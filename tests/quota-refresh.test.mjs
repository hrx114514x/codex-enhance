import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {EventEmitter} from 'node:events';
import {WeeklyQuota,readAccountQuota} from '../collector/quota.mjs';
import {BUNDLED_PRICING} from '../collector/price-catalog.mjs';

class WorkerFake extends EventEmitter {
  sent=[];postMessage(q){this.sent.push(q);}async terminate(){}
  reply(base){const q=this.sent.at(-1);this.emit('message',{queryId:q.queryId,accountKey:q.quota.accountKey,complete:true,pricingDate:BUNDLED_PRICING.verifiedAt,windows:[{minutes:10080,requests:1,tokens:1000,usd:base,quotaBaseUsd:base,models:[]}]});}
}
function fixture(t){
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),'quota-refresh-')),worker=new WorkerFake();let now=Date.now();
  const prices={catalog:BUNDLED_PRICING,sample:()=>({}),refresh(){},observeMissing(){},close(){}};
  const quota=new WeeklyQuota(dir,dir,{pricingUpdates:prices,workerFactory:()=>worker,now:()=>now});
  t.after(async()=>{await quota.close();fs.rmSync(dir,{recursive:true,force:true});});
  const reset=Math.floor(now/1000)+5*86400;
  return {quota,worker,advance:ms=>now+=ms,raw:(pct=50,id='a')=>({authType:'chatgpt',accountId:id,plan:'pro',checkedAtMs:now,windows:[{minutes:10080,usedPercent:pct,resetsAt:reset}]})};
}
test('account reads run together, allow cold-start latency and never refresh credentials',async t=>{
  const previous=globalThis.__codexEnhanceReadCache;t.after(()=>globalThis.__codexEnhanceReadCache=previous);
  const calls=[],pending=[];
  globalThis.__codexEnhanceReadCache={store:{sendRequest(method,params,options){calls.push({method,params,options});return new Promise(resolve=>pending.push(resolve));}}};
  const reading=readAccountQuota();assert.equal(calls.length,2);
  assert.equal(calls[1].params.refreshToken,false);assert.equal(calls[0].options.timeoutMs,8000);
  pending[0]({rateLimits:{limitId:'codex',planType:'pro',primary:{usedPercent:20,windowDurationMins:10080,resetsAt:1792000000}}});pending[1]({account:{type:'chatgpt'}});
  assert.equal((await reading).windows[0].usedPercent,20);
});
test('quota refresh preserves matched percent and amount until both new readings are ready',async t=>{
  const f=fixture(t);let pct=50,account='a';const session={evaluate:async(_,timeout)=>{assert.equal(timeout,10000);return f.raw(pct,account);}};
  f.quota.sample(session);await f.quota.pending;f.worker.reply(50);
  let view=f.quota.sample(session);assert.equal(view.windows[0].estimatedTotalUsd,100);
  pct=60;f.advance(60000);f.quota.sample(session);await f.quota.pending;
  view=f.quota.sample(session);assert.equal(view.refreshingUsage,true);assert.equal(view.indexing,false);assert.equal(view.windows[0].usedPercent,50);assert.equal(view.windows[0].estimatedTotalUsd,100);
  f.worker.reply(66);view=f.quota.sample(session);assert.equal(view.refreshingUsage,false);assert.equal(view.windows[0].usedPercent,60);assert.equal(view.windows[0].estimatedTotalUsd,110);
  account='b';f.quota.refresh();f.quota.sample(session);await f.quota.pending;view=f.quota.sample(session);
  assert.equal(view.indexing,true);assert.equal(view.refreshingUsage,false);assert.equal(view.windows[0].estimatedTotalUsd,null);
});
test('failed reads retry after 15 seconds and manual refresh does not wait for the old timer',async t=>{
  const f=fixture(t);let count=0,fail=true;const session={evaluate:async()=>{count++;if(fail)throw Error('Timeout');return f.raw();}};
  f.quota.sample(session);await f.quota.pending;assert.equal(f.quota.sample(session).state,'unavailable');
  f.advance(14999);f.quota.sample(session);assert.equal(count,1);f.advance(1);f.quota.sample(session);await f.quota.pending;assert.equal(count,2);
  fail=false;f.quota.refresh();f.quota.sample(session);await f.quota.pending;assert.equal(count,3);f.worker.reply(50);assert.equal(f.quota.sample(session).state,'ready');
});
