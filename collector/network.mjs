import fs from 'node:fs/promises';
import http from 'node:http';
import {execFile} from 'node:child_process';

const WINDOW_MS=300000, INTERVAL_MS=15000;
export const endpoints={codex:'https://chatgpt.com/backend-api/codex/models',reference:'https://www.gstatic.com/generate_204'};
const median=values=>{const a=[...values].sort((a,b)=>a-b);return a.length?(a[Math.floor((a.length-1)/2)]+a[Math.floor(a.length/2)])/2:null;};
const age=(now,at)=>Number.isFinite(at)?Math.max(0,now-at):null;

// Keep timestamps and fixed metadata only. Never retain headers, cookies, messages,
// reasoning text or commands. The target and event grammar are both allowlisted.
export function observeNetworkLog(state,row) {
 const body=row.feedback_log_body;if(typeof body!=='string'||body.length>1048576)return;
 const turnId=body.match(/\bturn(?:\.id|_id)="?([0-9a-f-]{36})\b/i)?.[1];
 const turn=state.turns.get(turnId);if(!turn)return;
 const at=row.ts*1000+Math.floor((row.ts_nanos??0)/1e6);
 if(!Number.isFinite(at)||at<(turn.startedAtMs??0)||turn.completedAtMs&&at>turn.completedAtMs)return;
 if(/websocket\.warmup=true|warmup=true|run_auto_compact|session_task\.compact/.test(body))return;
 const tail=body.slice(body.lastIndexOf('}:')+2).trim();
 const n=turn.networkObservation??={errors:[],eventCount:0};
 if(row.target==='feedback_tags'&&/^model="[^"\n]+" .*effort=/.test(tail)) {
  if(at<(n.requestAtMs??0))return;
  n.requestAtMs=at;n.headersAtMs=null;n.headersMs=null;n.statusCode=null;
  n.actualEffort=tail.match(/effort=Some\((\w+)\)/)?.[1]?.toLowerCase()??null;
 }
 if(row.target==='codex_http_client::client') {
  const response=body.match(/Request completed method=POST url=https:\/\/chatgpt\.com\/backend-api\/codex\/responses status=(\d{3})\b/);
  if(response&&n.requestAtMs&&at>=n.requestAtMs) {
   n.headersAtMs=at;n.headersMs=at-n.requestAtMs;n.statusCode=Number(response[1]);
  }
 }
 if(row.target==='codex_core::stream_events_utils'&&/^(?:handle_output_item_done: )?Output item item_type="(?:reasoning|message|function_call|custom_tool_call)"/.test(tail)) {
  n.lastEventAtMs=Math.max(n.lastEventAtMs??0,at);n.eventCount++;
  n.lastEventKind=tail.match(/item_type="([^"]+)"/)[1];
 }
 const errorTargets=['codex_api::sse::responses','codex_api::endpoint::responses','codex_api::endpoint::responses_websocket','codex_core::client','codex_core::client::stream','codex_core::session::turn','codex_http_client::client'];
 if(errorTargets.includes(row.target)) {
  const type=/stream disconnected|connection reset|connection closed/i.test(tail)?'disconnect':/timed out|timeout/i.test(tail)?'timeout':/retrying|reconnecting/i.test(tail)?'retry':null;
  if(type) {n.errors.push({atMs:at,type});n.errors=n.errors.slice(-30);}
 }
}

function controllerGet(socketPath,url) {
 return new Promise((resolve,reject)=>{
  const req=http.get({socketPath,path:url,headers:{Connection:'close'}},res=>{
   let raw='',size=0;
   res.on('data',data=>{size+=data.length;if(size>4*1024*1024)req.destroy(new Error('size'));else raw+=data;});
   res.on('end',()=>{try{if(res.statusCode!==200)throw new Error('status');resolve(JSON.parse(raw));}catch(e){reject(e);}});
   res.on('error',reject);
  });
  const deadline=setTimeout(()=>req.destroy(new Error('timeout')),1500);
  req.on('close',()=>clearTimeout(deadline));req.on('error',reject);
 });
}
export async function readRoute() {
 try {
  const pipes=(await fs.readdir('\\\\.\\pipe\\')).filter(x=>x.startsWith('verge-mihomo-production-'));
  if(pipes.length!==1)return {mode:'unknown',label:'代理路径未验证'};
  const socket='\\\\.\\pipe\\'+pipes[0];
  const [config,connections]=await Promise.all([controllerGet(socket,'/configs'),controllerGet(socket,'/connections')]);
  const nodes=[...new Set((connections.connections??[]).filter(c=>c.metadata?.host==='chatgpt.com').map(c=>c.chains?.[0]).filter(Boolean))].sort();
  const tun=config.tun?.enable===true,port=Number(config['mixed-port']);
  return {mode:tun?'tun':port>0&&port<65536?'proxy':'unknown',port:port>0&&port<65536?port:null,
   label:nodes.length===1?nodes[0]:nodes.length>1?'检测到多条 ChatGPT 路径':'尚无 ChatGPT 路由记录'};
 }catch{return {mode:'unknown',label:'代理路径未验证'};}
}

export function runProbe(kind,route,signal) {
 return new Promise(resolve=>{
  const args=['--disable','--silent','--show-error','--connect-timeout','5','--max-time','8','--max-filesize','65536','--output',process.platform==='win32'?'NUL':'/dev/null',
   '--write-out','%{json}'];
  if(route.mode==='proxy')args.push('--noproxy','','--proxy',`http://127.0.0.1:${route.port}`);
  else args.push('--noproxy','*'); // TUN remains active; this does NOT bypass TUN.
  args.push(endpoints[kind]);
  execFile(process.platform==='win32'?'curl.exe':'curl',args,{windowsHide:true,timeout:10000,maxBuffer:65536,signal},(error,stdout)=>{
   let d;try{d=JSON.parse(stdout);}catch{}
   const code=Number(d?.exitcode??error?.code??0);
   const status=Number(d?.http_code??0);
   const local=!d||[2,3,4,23,26,27].includes(code);
   const reached=status>=100&&Number(d?.time_starttransfer)>0;
   resolve({kind,atMs:Date.now(),state:local?'unavailable':reached?'response':'failed',status:status||null,
    expected:reached&&(kind==='codex'?status===401||status===200:status===204),
    totalMs:d&&Number.isFinite(d.time_total)?Math.round(d.time_total*1000):null,
    tlsMs:d&&Number.isFinite(d.time_appconnect)?Math.round(d.time_appconnect*1000):null,
    error:local?'probe_unavailable':!reached?code===28?'timeout':code===6?'dns':code===35||code===60?'tls':'connection':null});
  });
 });
}

export function probeSummary(samples,now,staleMs=45000) {
 const recent=samples.filter(s=>now-s.atMs<=WINDOW_MS&&now>=s.atMs);
 const last=recent.at(-1)??null,healthy=recent.filter(s=>s.expected&&s.totalMs!==null);
 const base=median(recent.slice(0,-2).filter(s=>s.expected&&s.totalMs!==null).map(s=>s.totalMs));
 const slow=s=>s.state==='response'&&s.totalMs>Math.max(2500,(base??0)*4);
 const failures=recent.filter(s=>s.state==='failed').length;
 const streak=recent.slice(-2);
 const unstable=streak.length===2&&streak.every(s=>s.state==='failed'||slow(s));
 return {last,samples:recent.slice(-24),count:recent.length,failures,timeouts:recent.filter(s=>s.error==='timeout').length,
  medianMs:median(healthy.map(s=>s.totalMs)),unstable,slow:last?slow(last):false,stale:!last||now-last.atMs>staleMs};
}

export function networkView(state,snapshot,probe,now) {
 const n=state?.turns.get(state.latestTurnId)?.networkObservation;
 const active=['working','compacting'].includes(snapshot.phase),toolRunning=(snapshot.tools?.running??0)>0;
 const eventGapMs=age(now,n?.lastEventAtMs),errors=(n?.errors??[]).filter(e=>now-e.atMs<WINDOW_MS);
 const c=probe.codex,r=probe.reference;
 const fresh=c&&!c.stale&&r&&!r.stale;
 const flowing=active&&!toolRunning&&eventGapMs!==null&&eventGapMs<45000&&(!n.requestAtMs||n.lastEventAtMs>=n.requestAtMs);
 let status='unknown',label='等待网络采样',detail='正在读取网络探测和当前任务的响应记录。';
 if(probe.enabled===false){status='paused';label='网络探测已暂停';detail='仅保留被动响应记录；点击可恢复探测。';}
 else if(fresh) {
  if(c.unstable&&r.unstable){status='path_suspect';label='网络路径可疑';detail='两个目标连续超时或明显变慢；尚不能区分本地网络、节点和分流路径。';}
  else if(c.unstable){status='openai_path_suspect';label='OpenAI 路径可疑';detail='Codex 探测连续异常，对照未同时异常；可能是到 OpenAI 的线路或服务端。';}
  else if(errors.length){status='stream_issue';label='聊天连接有异常';detail='当前轮次有断流、超时或重试记录。短连接探测正常也不能证明长连接正常。';}
  else if(active&&n?.statusCode>=400){status='request_issue';label=n.statusCode===429?'模型请求被限流':'模型请求返回异常';detail=`真实模型请求返回 HTTP ${n.statusCode}，不应将服务端拒绝当作节点超时。`;}
  else if(c.last.state==='unavailable'||r.last.state==='unavailable'){label='探测暂不可用';detail='本机探测器不可用；不能判断节点状态。';}
  else if(!c.last.expected&&c.last.state==='response'){status='http_issue';label='接口返回异常';detail=`已收到 HTTP ${c.last.status}；访问拒绝、限流和服务端错误不等同于断网。`;}
  else if(c.last.state==='failed'||r.last.state==='failed'||c.slow||r.slow){status='fluctuation';label='检测到网络波动';detail='出现一次失败或延迟升高，等待下一次采样确认；尚不能归因于节点。';}
  else if(flowing){status='receiving';label='模型数据仍在更新';detail='短连接探测可达，当前聊天仍有模型事件；长时间思考不能直接归因于节点。';}
  else if(toolRunning){status='tool';label='工具执行中 · 探测可达';detail='当前有工具运行；这段等待不用于判定模型连接卡住。';}
  else if(active&&n?.requestAtMs&&now-Math.max(n.requestAtMs,n.lastEventAtMs??0)>60000){status='quiet';label='模型响应待确认';detail='探测可达，但聊天一段时间没有响应事件；可能是服务端等待或长连接停顿。';}
  else {status='reachable';label='短连接探测可达';detail='仅代表本次轻量请求可达，不是节点或模型速度保证。';}
 }
 if(probe.enabled!==false&&!fresh&&probe.checkedAtMs){label='网络采样已过期';detail='暂无新鲜探测结果，不沿用之前的正常状态。';}
 const effort=n?.actualEffort??null,selected=snapshot.effort?.toLowerCase()??null;
 return {...probe,status,label,detail,stream:{eventGapMs,lastEventAtMs:n?.lastEventAtMs??null,eventCount:n?.eventCount??0,
  requestAtMs:n?.requestAtMs??null,headersMs:n?.headersMs??null,statusCode:n?.statusCode??null,
  errors:errors.length,lastError:errors.at(-1)??null,actualEffort:effort,selectedEffort:selected,effortMismatch:Boolean(effort&&selected&&effort!==selected)}};
}

export class NetworkMonitor {
 constructor({probe=runProbe,route=readRoute,now=Date.now,intervalMs=INTERVAL_MS}={}) {
  this.probe=probe;this.readRoute=route;this.now=now;this.intervalMs=intervalMs;this.enabled=true;
  this.samples={codex:[],reference:[]};this.route={mode:'unknown',label:'路径待读取'};this.nextAt=0;this.generation=0;
 }
 setEnabled(enabled){this.enabled=Boolean(enabled);this.generation++;this.abort?.abort();this.pending=null;this.nextAt=0;}
 refresh(){if(!this.pending)this.nextAt=0;}
 sample(active=true) {
  const now=this.now();
  if(active&&!this.wasActive)this.nextAt=0;
  this.wasActive=active;
  if(this.enabled&&!this.pending&&now>=this.nextAt) {
   const generation=this.generation,abort=new AbortController();this.abort=abort;
   this.nextAt=now+(active?this.intervalMs:60000);
   this.pending=(async()=>{
    const route=await this.readRoute();if(generation!==this.generation)return;
    const key=JSON.stringify([route.mode,route.port,route.label]);
    if(this.routeKey&&this.routeKey!==key)this.samples={codex:[],reference:[]};
    this.routeKey=key;this.route=route;
    const results=await Promise.all(['codex','reference'].map(kind=>this.probe(kind,route,abort.signal)));
    if(generation!==this.generation)return;
    for(const result of results)this.samples[result.kind]=[...this.samples[result.kind],result].slice(-24);
    this.checkedAtMs=this.now();
   })().catch(()=>{if(generation===this.generation)this.checkedAtMs=null;}).finally(()=>{if(generation===this.generation)this.pending=null;});
  }
  return {enabled:this.enabled,checking:Boolean(this.pending),checkedAtMs:this.checkedAtMs??null,intervalMs:active?this.intervalMs:60000,
   route:this.route,codex:probeSummary(this.samples.codex,now,active?45000:120000),reference:probeSummary(this.samples.reference,now,active?45000:120000)};
 }
 close(){this.setEnabled(false);}
}
