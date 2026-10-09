import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {EventEmitter} from 'node:events';
import {ConversationUsage} from '../collector/conversation-usage.mjs';
import {ConversationCost,conversationCostView} from '../collector/conversation-cost.mjs';
import {BUNDLED_PRICING as pricing} from '../collector/price-catalog.mjs';

const at=Date.parse('2026-10-09T10:00:00Z'),thread='test-thread';
const row=(type,payload,ordinal=10)=>({timestamp:new Date(at).toISOString(),type,payload,ordinal});
const meta=(id=thread,extra={})=>row('session_meta',{id,model_provider:'openai',...extra},0);
const context=(turn='turn')=>row('turn_context',{turn_id:turn,model:'gpt-6-astra'});
const usage={input_tokens:300000,cached_input_tokens:100000,output_tokens:1000};
const ledger=(id='r1',extra={})=>row('token_usage_record',{turn_id:'turn',response_id:id,usage,service_tier:'priority',...extra});
const counter=()=>row('event_msg',{type:'token_count',info:{last_token_usage:usage,total_token_usage:{...usage,input_tokens:900000}}});
const write=(file,rows)=>fs.writeFileSync(file,rows.map(r=>JSON.stringify(r)).join('\n')+'\n');
function fixture(t){const dir=fs.mkdtempSync(path.join(os.tmpdir(),'conversation-cost-'));t.after(()=>fs.rmSync(dir,{recursive:true,force:true}));return dir;}

test('conversation totals deduplicate canonical records and mirrors in either order, preserve distinct equal requests',async t=>{
  const dir=fixture(t),a=path.join(dir,'a.jsonl'),b=path.join(dir,'b.jsonl');
  for(const reversed of [false,true]){
    write(a,[meta(),context(),...(reversed?[ledger(),counter()]:[counter(),ledger()]),ledger('r2')]);
    write(b,[meta(),context(),ledger(),ledger('r2')]);
    const state=new ConversationUsage(thread,'salt');await state.scan([a,b,a]);
    const totals=state.totals(pricing,at);assert.equal(totals.requests,2);assert.equal(totals.tokens,602000);assert.equal(totals.fastRequests,2);
    const offsets=[...state.files.values()].map(s=>s.tail.offset);await state.scan([a,b]);
    assert.equal(state.totals(pricing,at).requests,2);assert.deepEqual([...state.files.values()].map(s=>s.tail.offset),offsets);
    assert.equal(totals.quotaBaseUsd>0,true);assert.equal(totals.astraPremiumUsd>0,true);
    for(const astra of [true,false])for(const fast of [true,false]){
      const view=conversationCostView(totals,{includeAstraLongContext:astra,normalizeFast:fast});
      assert.equal(view.estimatedUsd,totals.quotaBaseUsd+(astra?totals.astraPremiumUsd:0)+(fast?totals.quotaFastPremiumUsd+(astra?totals.astraFastPremiumUsd:0):0));
    }
  }
});
test('conversation excludes foreign and inherited history and respects cutoff, incomplete lines, removal and rotation',async t=>{
  const dir=fixture(t),a=path.join(dir,'a.jsonl'),b=path.join(dir,'b.jsonl');
  write(a,[meta(thread,{subagent_history_start_ordinal:5}),{...context(),ordinal:6},{...ledger('inherited'),ordinal:3},ledger()]);
  write(b,[meta('another'),context(),ledger('foreign')]);
  const state=new ConversationUsage(thread,'salt');await state.scan([a,b]);assert.equal(state.totals(pricing,at).requests,1);
  const later={...ledger('later'),timestamp:new Date(at+1000).toISOString()};
  fs.appendFileSync(a,JSON.stringify(later));await state.scan([a,b]);assert.equal(state.totals(pricing,at+1000).requests,1);
  fs.appendFileSync(a,'\n');await state.scan([a,b]);assert.equal(state.totals(pricing,at+1000).requests,2);assert.equal(state.totals(pricing,at).requests,1);
  write(a,[meta(),context(),ledger('replacement')]);await state.scan([a]);assert.equal(state.totals(pricing,at+1000).requests,1);
  await state.scan([]);assert.equal(state.totals(pricing,at).state,'unavailable');
});
test('unknown price, provider, missing file and malformed rows do not appear as a complete zero',async t=>{
  const dir=fixture(t),a=path.join(dir,'a.jsonl');write(a,[meta(),row('turn_context',{turn_id:'turn',model:'not-priced'}),ledger()]);
  const state=new ConversationUsage(thread,'salt');await state.scan([a,path.join(dir,'missing.jsonl')]);
  let totals=state.totals(pricing,at);assert.equal(totals.readErrors,1);assert.equal(totals.unpricedRequests,1);assert.equal(conversationCostView(totals).estimatedUsd,null);assert.equal(conversationCostView(totals).partial,true);
  fs.appendFileSync(a,'{"type":"token_usage_record","timestamp":"bad"\n');await state.scan([a]);assert.equal(state.totals(pricing,at).parseErrors,1);
  write(a,[meta(thread,{model_provider:'other'}),context(),ledger()]);await state.scan([a]);assert.equal(state.totals(pricing,at).reason,'unsupported_provider');
});
class WorkerFake extends EventEmitter {sent=[];postMessage(message){this.sent.push(message);}async terminate(){}reply(i,data={}){this.emit('message',{...this.sent[i],complete:true,state:'ready',requests:1,quotaBaseUsd:2,astraPremiumUsd:3,quotaFastPremiumUsd:4,astraFastPremiumUsd:6,models:[],pricingRevision:pricing.revision,...data});}}
test('10 second scheduler isolates conversations and ignores late replies; manual mode and pricing switches work independently',async t=>{
  const dir=fixture(t),worker=new WorkerFake();let now=at;const cost=new ConversationCost(dir,{salt:'salt',now:()=>now,workerFactory:()=>worker});t.after(()=>cost.close());
  assert.equal(cost.sample('a',[],pricing).state,'loading');worker.reply(0);assert.equal(cost.sample('a',[],pricing).estimatedUsd,6);
  now+=9999;cost.sample('a',[],pricing);assert.equal(worker.sent.length,1);now++;cost.sample('a',[],pricing);assert.equal(worker.sent.length,2);
  cost.setAutomatic(false);worker.reply(1,{quotaBaseUsd:100});now+=20000;assert.equal(cost.sample('a',[],pricing).estimatedUsd,6);assert.equal(worker.sent.length,2);
  cost.setOptions({includeAstraLongContext:true,normalizeFast:false});assert.equal(cost.sample('a',[],pricing).estimatedUsd,5);
  cost.refresh('a');cost.sample('a',[],pricing);assert.equal(worker.sent.length,3);
  assert.equal(cost.sample('b',[],pricing).estimatedUsd,null);worker.reply(2);assert.equal(cost.sample('b',[],pricing).estimatedUsd,null);worker.reply(3,{quotaBaseUsd:10});assert.equal(cost.sample('b',[],pricing).estimatedUsd,13);
  assert.equal(cost.sample(null,[],pricing).reason,'no_thread');assert.equal(cost.sample('a',[],pricing).estimatedUsd,5);
});
test('manual preference survives restart and switching auto off before first result does not restart reads',async t=>{
  const dir=fixture(t);fs.writeFileSync(path.join(dir,'settings.json'),'\uFEFF{"conversationCostAutoRefresh":false}');const worker=new WorkerFake();
  const cost=new ConversationCost(dir,{salt:'salt',workerFactory:()=>worker});t.after(()=>cost.close());
  assert.equal(cost.sample('a',[],pricing).automatic,false);cost.setAutomatic(false);worker.reply(0);cost.sample('a',[],pricing);assert.equal(worker.sent.length,1);
  cost.refresh();cost.sample('a',[],pricing);worker.reply(1);assert.equal(cost.sample('a',[],pricing).state,'ready');
  cost.setAutomatic(true);cost.sample('a',[],pricing);worker.emit('error',Error('worker failure'));assert.equal(cost.sample('a',[],pricing).error,'refresh_failed');
});
test('real worker returns numeric totals without log contents',async t=>{
  const dir=fixture(t),a=path.join(dir,'a.jsonl');write(a,[meta(),context(),row('response_item',{secret:'PRIVATE TEXT'}),ledger()]);
  const cost=new ConversationCost(dir,{salt:'salt'});t.after(()=>cost.close());let view;
  for(let i=0;i<100;i++){view=cost.sample(thread,[a],pricing);if(view.state==='ready')break;await new Promise(resolve=>setTimeout(resolve,20));}
  assert.equal(view.state,'ready');assert.equal(view.requests,1);assert.equal(JSON.stringify(view).includes('PRIVATE TEXT'),false);assert.equal(JSON.stringify(view).includes(a),false);
});
