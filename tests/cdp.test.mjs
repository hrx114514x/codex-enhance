import test from 'node:test';
import assert from 'node:assert/strict';
import { selectCurrentThread,readRuntimeStore } from '../collector/cdp.mjs';
const a = '11111111-1111-1111-1111-111111111111', b = '22222222-2222-2222-2222-222222222222';
function doc(ids, focused) {
  const active = {};
  const nodes = ids.map(id => ({ getAttribute: () => id, closest: () => null, contains: () => false, getBoundingClientRect: () => ({ width: 350, height: 1 }), parentElement: { contains: e => e === active && id === focused, getBoundingClientRect: () => ({ width: 350, height: 80 }) } }));
  return { activeElement: active, body: {}, querySelectorAll: selector => selector.includes('role=') ? [] : nodes };
}
test('multiple visible tasks use focused composer rather than last DOM candidate', () => { assert.equal(selectCurrentThread(doc([a,b], a)).threadId, a); });
test('ambiguous visible tasks do not silently select a background task', () => { assert.deepEqual(selectCurrentThread(doc([a,b])), { threadId: null, ambiguous: true }); });
test('single task and explicitly matched route identify selection', () => { assert.equal(selectCurrentThread(doc([a])).threadId, a); assert.equal(selectCurrentThread(doc([a,b]), `app://-/thread/${b}`).threadId, b); });
test('ChatGPT conversation references are not treated as local task identifiers', () => { assert.equal(selectCurrentThread(doc([`chatgpt:${a}`])).threadId, null); });
test('native search dialog keeps the visible underlying composer even when aria-hidden', () => {
  const composer = { getAttribute: () => a, closest: selector => selector.includes('aria-hidden') ? {} : null,
    getBoundingClientRect: () => ({ width: 500, height: 32 }), contains: () => false };
  const row = { ...composer, getAttribute: () => b, closest: () => null };
  const document = { activeElement: {}, body: {}, querySelectorAll: selector => selector.includes('role=') ? [{ getBoundingClientRect: () => ({ width: 400, height: 500 }) }] : selector.includes('above-composer') ? [composer] : [row] };
  assert.equal(selectCurrentThread(document).threadId, a);
});
test('CSS-hidden composers are never revived by a dialog', () => {
  const hidden = { getAttribute: () => a, closest: () => ({}), getBoundingClientRect: () => ({ width: 0, height: 0 }) };
  const document = { body: {}, querySelectorAll: selector => selector.includes('role=') ? [{ getBoundingClientRect: () => ({ width: 400, height: 500 }) }] : [hidden] };
  assert.equal(selectCurrentThread(document).threadId, null);
});

test('manager hook arrays are found before large unrelated React state fills the search queue',t=>{
  const savedRoot=globalThis.__codexRoot,savedCache=globalThis.__codexEnhanceReadCache;
  t.after(()=>{globalThis.__codexRoot=savedRoot;globalThis.__codexEnhanceReadCache=savedCache;});
  const local={hostId:'local',conversations:new Map([[a,{title:'local',turns:[]}]])};
  const remote={hostId:'remote',conversations:new Map([[a,{title:'wrong host',turns:[]}]])};
  const disposed={hostId:'local',disposed:true,conversations:local.conversations};
  const root={};let fiber=root;
  for(let f=0;f<1600;f++){
    let hook={memoizedState:f===15?[remote,disposed,local]:{unrelated:f}};fiber.memoizedState=hook;
    for(let h=1;h<30;h++){hook.next={memoizedState:{unrelated:[f,h]}};hook=hook.next;}
    fiber.child={};fiber=fiber.child;
  }
  globalThis.__codexRoot={_internalRoot:{current:root}};globalThis.__codexEnhanceReadCache=null;
  assert.equal(readRuntimeStore(a).title,'local');
  assert.equal(readRuntimeStore(b),null);assert.equal(globalThis.__codexEnhanceReadCache.store,local);
  local.conversations.set(b,{title:'newly hydrated',turns:[]});assert.equal(readRuntimeStore(b).title,'newly hydrated');
  local.disposed=true;globalThis.__codexEnhanceReadCache.lastProbe=0;assert.equal(readRuntimeStore(a),null);
});
