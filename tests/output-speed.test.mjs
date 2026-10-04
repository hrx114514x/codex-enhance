import test from'node:test';import assert from'node:assert/strict';
import{ThreadState}from'../collector/state.mjs';
import{recordOutputItem,recordOutputUsage,outputSpeedView}from'../collector/output-speed.mjs';
const epoch=Date.parse('2026-10-04T00:00:00Z');
function setup(){const s=new ThreadState('task');const record=(ms,type,payload)=>s.record({type,timestamp:new Date(epoch+ms).toISOString(),payload});record(0,'event_msg',{type:'task_started',turn_id:'turn'});return {s,record};}
const generated=(record,id,start,end,type='Reasoning')=>record(end,'event_msg',{type:'item_completed',turn_id:'turn',item:{id,type},started_at_ms:epoch+start,completed_at_ms:epoch+end});
const usage=(record,id,at,tokens,reasoning=0,turn='turn')=>record(at,'token_usage_record',{thread_id:'task',turn_id:turn,response_id:id,usage:{input_tokens:900000,output_tokens:tokens,reasoning_output_tokens:reasoning,total_tokens:900000+tokens}});
const speed=(s,at=30000)=>s.snapshot(epoch+at,true).outputSpeed;
test('per-request output rate uses real output tokens and generation timestamps, excluding prefill and tool wait',()=>{
 const {s,record}=setup();generated(record,'thinking',2000,4500);record(5000,'response_item',{type:'custom_tool_call',id:'call',input:'PRIVATE'});usage(record,'response-1',5100,100,80);
 let v=speed(s,6000);assert.equal(v.durationMs,3000);assert.equal(v.tokensPerSecond,100/3);assert.equal(v.reasoningTokens,80);assert.equal(v.outputTokens,100);
 record(15000,'event_msg',{type:'item_completed',turn_id:'turn',item:{id:'tool',type:'CommandExecution',status:'completed'},started_at_ms:epoch+5100,completed_at_ms:epoch+15000});
 generated(record,'reply',17000,19000,'AgentMessage');usage(record,'response-2',19200,60,0);v=speed(s);assert.equal(v.tokensPerSecond,30);assert.equal(v.durationMs,2000);assert.equal(v.samples.length,2);
 assert.ok(!JSON.stringify(v).includes('PRIVATE'));assert.ok(!JSON.stringify(v).includes('response-2'));
});
test('duplicate ledger and item records or UI token mirrors do not duplicate speed samples',()=>{
 const {s,record}=setup();for(let n=0;n<2;n++){generated(record,'reply',1000,3000,'AgentMessage');usage(record,'response',3100,100);}
 record(3110,'event_msg',{type:'token_count',info:{last_token_usage:{output_tokens:100,input_tokens:1000,total_tokens:1100}}});
 assert.equal(speed(s).samples.length,1);assert.equal(speed(s).tokensPerSecond,50);
});
test('a very fast tool completing before the usage log flush does not erase the preceding generation',()=>{
 const {s,record}=setup();generated(record,'thinking',1000,3000);record(4000,'response_item',{type:'custom_tool_call',id:'call'});
 record(4100,'event_msg',{type:'item_completed',turn_id:'turn',item:{id:'patch',type:'FileChange',status:'completed'},started_at_ms:epoch+4001,completed_at_ms:epoch+4100});
 usage(record,'response',4200,90);assert.equal(speed(s).tokensPerSecond,30);
});
test('missing output timestamps never fall back to total turn duration or character count',()=>{
 const {s,record}=setup();record(1000,'response_item',{type:'message',role:'assistant',id:'reply',content:[{text:'PRIVATE'}]});usage(record,'response',1100,100);
 const v=speed(s);assert.equal(v.state,'unavailable');assert.equal(v.reason,'missing_timing');assert.equal(v.tokensPerSecond,null);
});
test('earlier output without a start cannot be timed from a later message to inflate speed',()=>{
 const {s,record}=setup();record(1000,'response_item',{type:'function_call',id:'early-call'});generated(record,'later',2000,3000,'AgentMessage');usage(record,'response',3100,100);
 assert.equal(speed(s).tokensPerSecond,null);assert.equal(speed(s).reason,'missing_timing');
});
test('model output across a tool boundary is never merged into one slow generation window',()=>{
 const {s,record}=setup();generated(record,'old',1000,2000);record(10000,'response_item',{type:'custom_tool_call_output',call_id:'tool'});
 generated(record,'new',11000,12000);usage(record,'response',12100,20);
 assert.equal(speed(s).tokensPerSecond,20);assert.equal(speed(s).durationMs,1000);
});
test('ambiguous overlapping output and compaction leave speed unavailable rather than inflating the number',()=>{
 const {s,record}=setup();generated(record,'crossing',1000,8000);record(5000,'response_item',{type:'function_call_output',call_id:'tool'});usage(record,'response',8100,100);
 assert.equal(speed(s).tokensPerSecond,null);
 record(9000,'event_msg',{type:'item_completed',turn_id:'turn',item:{id:'compact',type:'ContextCompaction'},started_at_ms:epoch+8200,completed_at_ms:epoch+9000});
 usage(record,'compacted',9100,3000);assert.equal(speed(s).tokensPerSecond,null);
 generated(record,'after',10000,12000);usage(record,'after-response',12100,100);assert.equal(speed(s).tokensPerSecond,50);
});
test('new turns do not borrow speed from the previous turn and historic rates do not tick while idle',()=>{
 const {s,record}=setup();generated(record,'reply',1000,3000);usage(record,'response',3100,60);assert.equal(speed(s,20000).tokensPerSecond,30);
 assert.equal(s.snapshot(epoch+40000,false).outputSpeed.tokensPerSecond,30);
 record(50000,'event_msg',{type:'task_started',turn_id:'next'});assert.equal(speed(s,60000).state,'pending');assert.equal(speed(s,60000).tokensPerSecond,null);
});
test('short, invalid, future, and late-unmatched samples cannot produce bogus throughput',()=>{
 const {s,record}=setup();generated(record,'short',1000,1100);usage(record,'short',1200,20);assert.equal(speed(s).reason,'short_sample');
 generated(record,'late',2000,3000);usage(record,'late',10000,20);assert.equal(speed(s).reason,'missing_timing');
 usage(record,'invalid',11000,-20);assert.equal(speed(s).samples.length,2);
 generated(record,'future',40000,42000);usage(record,'future',42100,20);assert.equal(speed(s,30000).recordedAtMs,epoch+10000);assert.equal(speed(s,43000).tokensPerSecond,10);
});
test('bounded retention keeps recent response boundaries and rejects a pruned partial generation',()=>{
 const turn={startedAtMs:epoch};
 for(let i=0;i<40;i++){recordOutputItem(turn,{id:`i${i}`,startedAtMs:epoch+i*3000+1000,completedAtMs:epoch+i*3000+2000,explicit:true});recordOutputUsage(turn,{response_id:`r${i}`,usage:{output_tokens:50}},epoch+i*3000+2100);}
 assert.equal(turn.outputMetrics.requests.size,32);assert.equal(outputSpeedView(turn,epoch+150000).tokensPerSecond,50);
 const long={startedAtMs:epoch};for(let i=0;i<520;i++)recordOutputItem(long,{id:`i${i}`,startedAtMs:epoch+i*1000,completedAtMs:epoch+i*1000+500,explicit:true});
 recordOutputUsage(long,{response_id:'r',usage:{output_tokens:50000}},epoch+520000);assert.equal(long.outputMetrics.items.size,512);assert.equal(outputSpeedView(long,epoch+521000).tokensPerSecond,null);
});
