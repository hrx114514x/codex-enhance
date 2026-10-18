import test from 'node:test';
import assert from 'node:assert/strict';
import {observeNetworkLog,probeSummary,networkView,NetworkMonitor} from '../collector/network.mjs';

const id='11111111-1111-4111-8111-111111111111',other='22222222-2222-4222-8222-222222222222';
const epoch=1800000000000;
function fixture(){return {latestTurnId:id,turns:new Map([[id,{startedAtMs:epoch}],[other,{startedAtMs:epoch}]])};}
function row(target,tail,ms=0,turn=id){return {target,ts:Math.floor((epoch+ms)/1000),ts_nanos:((epoch+ms)%1000)*1e6,feedback_log_body:`session_loop{thread_id=${other}}:turn{turn.id=${turn}}: ${tail}`};}
const ok=(atMs=epoch,kind='codex',totalMs=500)=>({atMs,kind,state:'response',status:kind==='codex'?401:204,expected:true,totalMs,tlsMs:240,error:null});
const fail=(atMs=epoch,kind='codex')=>({...ok(atMs,kind),state:'failed',status:null,expected:false,totalMs:8000,error:'timeout'});
const probes=(c=[ok()],r=[ok(epoch,'reference')],now=epoch)=>({enabled:true,checkedAtMs:now,route:{mode:'tun'},codex:probeSummary(c,now),reference:probeSummary(r,now)});
const snap={phase:'working',effort:'medium',tools:{running:0}};

test('read exact request and stream metadata, scoped to turn, without persisting credentials or content',()=>{
 const state=fixture();
 observeNetworkLog(state,row('feedback_tags','model="gpt-6-astra" effort=Some(High) tags_json={}',100));
 observeNetworkLog(state,row('codex_http_client::client','Request completed method=POST url=https://chatgpt.com/backend-api/codex/responses status=200 OK headers={"set-cookie":"SECRET"}',1600));
 observeNetworkLog(state,row('codex_core::stream_events_utils','Output item item_type="reasoning" item_id="rs_test"',2200));
 const n=state.turns.get(id).networkObservation;
 assert.equal(n.headersMs,1500);assert.equal(n.actualEffort,'high');assert.equal(n.lastEventAtMs,epoch+2200);
 assert.ok(!JSON.stringify(n).includes('SECRET'));assert.equal(state.turns.get(other).networkObservation,undefined);
 const view=networkView(state,snap,probes([ok(epoch+2300)],[ok(epoch+2300,'reference')],epoch+2400),epoch+2400);
 assert.equal(view.status,'receiving');assert.equal(view.stream.effortMismatch,true);
});
test('ignore tool output, warmup, foreign turns and older turns; headers for search do not count',()=>{
 const state=fixture();
 observeNetworkLog(state,row('codex_core::session::handlers','Output item item_type="reasoning"',100));
 observeNetworkLog(state,row('codex_core::stream_events_utils','ToolCall: exec text=Output item item_type="reasoning"',200));
 observeNetworkLog(state,row('feedback_tags','websocket.warmup=true model="x" effort=Some(High)',300));
 observeNetworkLog(state,row('codex_core::stream_events_utils','Output item item_type="reasoning"',400,other));
 assert.equal(state.turns.get(id).networkObservation?.lastEventAtMs,undefined);
 observeNetworkLog(state,row('feedback_tags','model="x" effort=Some(Medium) tags_json={}',500));
 observeNetworkLog(state,row('codex_http_client::client','Request completed method=POST url=https://chatgpt.com/backend-api/codex/alpha/search status=200 OK headers={}',600));
 assert.equal(state.turns.get(id).networkObservation.headersMs,null);
 state.turns.get(id).completedAtMs=epoch+700;
 observeNetworkLog(state,row('codex_core::stream_events_utils','Output item item_type="reasoning"',900));
 assert.equal(state.turns.get(id).networkObservation?.lastEventAtMs,undefined);
});
test('stale prior request events never imply current request still receiving',()=>{
 const state=fixture();
 observeNetworkLog(state,row('codex_core::stream_events_utils','Output item item_type="reasoning"',100));
 observeNetworkLog(state,row('feedback_tags','model="x" effort=Some(High) tags_json={}',1000));
 assert.equal(networkView(state,snap,probes([ok(epoch+1100)],[ok(epoch+1100,'reference')],epoch+1100),epoch+1100).status,'reachable');
});
test('two consecutive failures are path evidence; one failure only indicates fluctuation',()=>{
 const state=fixture(),now=epoch+20000;
 assert.equal(networkView(state,snap,probes([fail(now)],[fail(now,'reference')],now),now).status,'fluctuation');
 assert.equal(networkView(state,snap,probes([fail(epoch),fail(now)],[fail(epoch,'reference'),fail(now,'reference')],now),now).status,'path_suspect');
 assert.equal(networkView(state,snap,probes([fail(epoch),fail(now)],[ok(now,'reference')],now),now).status,'openai_path_suspect');
});
test('HTTP refusal and real model rate limits are not transport failure',()=>{
 const state=fixture();
 const p=probes([{...ok(),status:403,expected:false}]);
 assert.equal(p.codex.failures,0);assert.equal(networkView(state,snap,p,epoch).status,'http_issue');
 state.turns.get(id).networkObservation={requestAtMs:epoch,statusCode:429};
 assert.equal(networkView(state,snap,probes(),epoch).status,'request_issue');
});
test('stale probes and unavailable local curl never report healthy',()=>{
 const state=fixture();
 assert.equal(networkView(state,snap,probes([ok()],[ok(epoch,'reference')],epoch+60000),epoch+60000).status,'unknown');
 const p=probes([{...ok(),state:'unavailable',expected:false}]);
 assert.equal(networkView(state,snap,p,epoch).status,'unknown');
 assert.equal(networkView(state,snap,{...probes(),enabled:false},epoch).status,'paused');
});
test('continuous tool work is distinct from a silent model request',()=>{
 const state=fixture();state.turns.get(id).networkObservation={requestAtMs:epoch,lastEventAtMs:epoch+1000};
 const now=epoch+90000,p=probes([ok(now)],[ok(now,'reference')],now);
 assert.equal(networkView(state,snap,p,now).status,'quiet');
 assert.equal(networkView(state,{...snap,tools:{running:1}},p,now).status,'tool');
 assert.equal(networkView(state,{...snap,phase:'idle'},p,now).status,'reachable');
});
test('network errors recognized only on internal network sources and expire',()=>{
 const state=fixture();
 observeNetworkLog(state,row('codex_core::stream_events_utils','ToolCall: exec stream disconnected',100));
 assert.equal(state.turns.get(id).networkObservation.errors.length,0);
 observeNetworkLog(state,row('codex_core::session::turn','stream disconnected before completion',200));
 assert.equal(networkView(state,snap,probes(),epoch+300).status,'stream_issue');
 const now=epoch+400000;
 assert.equal(networkView(state,snap,probes([ok(now)],[ok(now,'reference')],now),now).stream.errors,0);
});
test('slow thresholds and rolling windows do not count old failures forever',()=>{
 const samples=[ok(epoch-400000),fail(epoch-350000),ok(epoch-30000),ok(epoch-15000,'codex',4000),ok(epoch,'codex',5000)];
 const p=probeSummary(samples,epoch);assert.equal(p.count,3);assert.equal(p.failures,0);assert.equal(p.unstable,true);
});
test('probes are async, bounded to one batch, route changes reset old samples, pause cancels late results',async()=>{
 let now=epoch,routeLabel='a',resolveProbe,calls=0;
 const m=new NetworkMonitor({now:()=>now,route:async()=>({mode:'tun',label:routeLabel}),probe:kind=>{calls++;return new Promise(r=>{const old=resolveProbe;resolveProbe=()=>{old?.();r(ok(now,kind));};});}});
 assert.equal(m.sample().checking,true);await Promise.resolve();m.sample();assert.equal(calls,2);
 resolveProbe();await m.pending;assert.equal(m.sample().codex.count,1);
 now+=16000;routeLabel='b';m.sample();await Promise.resolve();assert.equal(m.samples.codex.length,0);
 const pending=m.pending;m.setEnabled(false);resolveProbe();await pending;
 assert.equal(m.sample().enabled,false);assert.equal(m.samples.codex.length,0);m.close();
});
