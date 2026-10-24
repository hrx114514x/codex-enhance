// Isolated CDP protocol fixture. It never attaches to the user's Codex process.
import inspector from 'node:inspector';
import http from 'node:http';
const id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const active = {};
const element = { getAttribute: () => id, closest: () => null, contains: e => e === active,
  getBoundingClientRect: () => ({ width: 350, height: 1 }), parentElement: { contains: e => e === active, getBoundingClientRect: () => ({ width: 350, height: 90 }) } };
globalThis.document = { activeElement: active, body: {}, querySelectorAll: selector => selector.includes('role=') ? [] : [element], hasFocus: () => true };
globalThis.location = { href: `app://-/index.html#/thread/${id}` };
const turn = { turnId: 'cdp-turn', status: 'inProgress', turnStartedAtMs: Date.now(), items: [{ id: 'live-a', type: 'mcpToolCall', tool: 'web.search', status: 'inProgress' }, { id: 'live-b', type: 'commandExecution', status: 'inProgress' }] };
const store = { conversations: new Map([[id, { title: '协议验证任务', latestModel: 'gpt-6-astra', turns: [], turnHistory: { kind: 'canonical', history: { entitiesByKey: { 'tail:live': turn } } } }]]), updateConversationState() {} };
let directoryTools = { read_thread: { name: 'read_thread' } };
store.listMcpServers = async params => {
  if (params.threadId !== id || params.detail !== 'toolsAndAuthOnly') throw new Error('unexpected capability request');
  return { data: [{ name: 'codex_app', runtimeStatus: 'connected', tools: directoryTools, toolsError: null, authStatus: 'unsupported' }], nextCursor: null };
};
if(process.argv.includes('--signal-store')) {
  store.hostId='local';store.sendRequest=async(method,params)=>{
    if(method==='account/read'){if(params.refreshToken!==false)throw Error('Auth refresh forbidden');return {account:{type:'chatgpt',planType:'pro'}};}
    if(method==='account/rateLimits/read')return {rateLimits:{limitId:'codex',planType:'pro',primary:{usedPercent:10,windowDurationMins:10080,resetsAt:Math.floor(Date.now()/1000)+86400}}};
    throw Error('Unexpected request');
  };
  const atom={read(){},write(){}},values=new WeakMap([[atom,store]]),reader={get:key=>values.get(key)};
  const node={store:reader,familyBindings:new Map([[{kind:'signal-family'},new Map([['local',{value:{atom,store:reader}}]])]])};
  globalThis.__codexRoot={_internalRoot:{current:{memoizedState:{memoizedState:{current:{node}}}}}};
}else globalThis.__codexRoot = { _internalRoot: { current: { memoizedState: { memoizedState: store, next: null } } } };
inspector.open(0, '127.0.0.1');
const server = http.createServer((req, res) => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify([{ id: 'isolated-fixture', type: 'page', url: 'app://-/index.html', webSocketDebuggerUrl: inspector.url() }])); });
server.listen(0, '127.0.0.1', () => process.send?.({ port: server.address().port, id }));
process.on('message', message => {
  if (message.type === 'complete') { turn.items[0].status = 'completed'; process.send?.({ type: 'completed' }); }
  if (message.type === 'sleep-history') { turn.items.push(...Array.from({length:17},(_,i)=>({id:`sleep-${i}`,type:'sleep',durationMs:20000}))); process.send?.({type:'sleep-history-ready'}); }
  if (message.type === 'remove-tool') { directoryTools = {}; process.send?.({ type: 'removed' }); }
  if (message.type === 'first-reply') {
    const atMs = Date.now(); turn.items.push({ id: 'reply', type: 'agentMessage', text: 'Fixture output' });
    turn.aeonAssistantMessageStartedAtMsById = { reply: atMs };
    process.send?.({ type: 'replied', expectedMs: atMs - turn.turnStartedAtMs });
  }
  if (message.type === 'stop') { server.close(); inspector.close(); process.exit(0); }
});
