const time=n=>typeof n==='number'&&Number.isFinite(n)&&n>=0;
const count=n=>Number.isSafeInteger(n)&&n>=0;
const metrics=turn=>turn.outputMetrics??={items:new Map(),requests:new Map(),boundaries:[],trimmedBeforeMs:null,requestsBeforeMs:null};
function retain(map,limit,stamp) {
 if(map.size<=limit)return [];
 const removed=[...map.values()].sort((a,b)=>stamp(a)-stamp(b)).slice(0,map.size-limit);
 for(const item of removed)map.delete(item.id);
 return removed;
}
export function recordOutputItem(turn,{id,startedAtMs,completedAtMs,explicit=false}) {
 if(!turn||!id||!time(completedAtMs))return;
 if(startedAtMs!=null&&(!time(startedAtMs)||startedAtMs>completedAtMs))return;
 const m=metrics(turn),old=m.items.get(id);
 const start=startedAtMs??old?.startedAtMs??null;
 const end=old?.explicit&&!explicit?old.completedAtMs:completedAtMs;
 if(time(start)&&start>end)return;
 m.items.set(id,{id,startedAtMs:start,completedAtMs:end,explicit:explicit||old?.explicit===true});
 const removed=retain(m.items,512,item=>item.completedAtMs);
 if(removed.length)m.trimmedBeforeMs=Math.max(m.trimmedBeforeMs??0,...removed.map(item=>item.completedAtMs));
}
export function recordOutputBoundary(turn,at) {
 if(!turn||!time(at))return;
 const m=metrics(turn);if(!m.boundaries.includes(at))m.boundaries.push(at);
 m.boundaries.sort((a,b)=>a-b);m.boundaries=m.boundaries.slice(-128);
}
export function recordOutputUsage(turn,payload,at) {
 const id=payload.response_id,usage=payload.usage;
 if(!turn||typeof id!=='string'||!id||!time(at)||!count(usage?.output_tokens))return;
 const m=metrics(turn),old=m.requests.get(id);if(old&&old.completedAtMs>at)return;
 const reasoning=usage.reasoning_output_tokens??usage.output_tokens_details?.reasoning_tokens;
 m.requests.set(id,{id,completedAtMs:at,outputTokens:usage.output_tokens,
  reasoningTokens:count(reasoning)&&reasoning<=usage.output_tokens?reasoning:null});
 const removed=retain(m.requests,32,request=>request.completedAtMs);
 if(removed.length)m.requestsBeforeMs=Math.max(m.requestsBeforeMs??0,...removed.map(r=>r.completedAtMs));
}
export function outputSpeedView(turn,now) {
 const m=turn?.outputMetrics;
 if(!m?.requests.size)return {state:'pending',tokensPerSecond:null,samples:[]};
 const requests=[...m.requests.values()].filter(r=>r.completedAtMs<=now).sort((a,b)=>a.completedAtMs-b.completedAtMs);
 if(!requests.length)return {state:'pending',tokensPerSecond:null,samples:[]};
 const items=[...m.items.values()];let previous=Math.max(turn.startedAtMs??0,m.requestsBeforeMs??0);
 const samples=[];
 for(const request of requests) {
  const available=items.filter(item=>item.completedAtMs>previous&&item.completedAtMs<=request.completedAtMs);
  const completedAtMs=available.length?Math.max(...available.map(i=>i.completedAtMs)):null;
  // A completed client tool/compaction separates generation windows. If one
  // usage record straddles a tool, its token count cannot be split reliably.
  // Fast tools can finish before the usage
  // log is flushed: boundaries after the last generated item belong next time.
  const boundary=Math.max(previous,...m.boundaries.filter(at=>completedAtMs!==null&&at<completedAtMs));
  const candidates=available.filter(item=>item.completedAtMs>boundary);
  const starts=candidates.map(i=>i.startedAtMs).filter(time);
  const startedAtMs=starts.length?Math.min(...starts):null;
  const durationMs=startedAtMs!==null&&completedAtMs!==null?completedAtMs-startedAtMs:null;
  const partial=m.trimmedBeforeMs!==null&&m.trimmedBeforeMs>boundary;
  const crossedTool=available.some(item=>item.completedAtMs<=boundary);
  const missing=startedAtMs===null||startedAtMs<boundary||completedAtMs===null||request.completedAtMs-completedAtMs>5000||partial||crossedTool||candidates.some(item=>item.completedAtMs<startedAtMs);
  const reason=missing?'missing_timing':request.outputTokens===0?'no_output':durationMs<250?'short_sample':null;
  samples.push({state:reason?'unavailable':'measured',reason,approximate:true,
   tokensPerSecond:reason?null:request.outputTokens*1000/durationMs,
   outputTokens:request.outputTokens,reasoningTokens:request.reasoningTokens,startedAtMs,completedAtMs,durationMs,recordedAtMs:request.completedAtMs});
  previous=request.completedAtMs;
 }
 const latest=samples.at(-1);
 return {...latest,samples:samples.slice(-8)};
}
