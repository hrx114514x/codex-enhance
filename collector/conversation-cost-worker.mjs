import {parentPort,workerData} from 'node:worker_threads';
import {ConversationUsage} from './conversation-usage.mjs';
import {validatePricing} from './price-catalog.mjs';

const states=new Map();let wanted=null,running=false;
async function pump() {
  if(running)return;running=true;
  try {while(wanted) {
    const query=wanted;wanted=null;
    try {
      const pricing=validatePricing(query.pricing);if(!pricing)throw Error('Invalid prices');
      let usage=states.get(query.threadId);if(!usage){usage=new ConversationUsage(query.threadId,workerData.salt);states.set(query.threadId,usage);}
      // Recent conversations keep incremental offsets; old tasks can be replayed.
      states.delete(query.threadId);states.set(query.threadId,usage);
      while(states.size>4)states.delete(states.keys().next().value);
      let publishedAt=0;
      const complete=await usage.scan(query.files,{cancelled:()=>!!wanted,progress:value=>{
        if(Date.now()-publishedAt<400)return;publishedAt=Date.now();
        parentPort.postMessage({queryId:query.queryId,threadId:query.threadId,complete:false,progress:value});
      }});
      if(!complete)continue;
      parentPort.postMessage({queryId:query.queryId,threadId:query.threadId,complete:true,...usage.totals(pricing,query.cutoff,query),
        sampledAtMs:query.cutoff,pricingDate:pricing.verifiedAt,pricingRevision:pricing.revision});
    } catch {parentPort.postMessage({queryId:query.queryId,threadId:query.threadId,error:true});}
  }} finally {running=false;}
}
parentPort.on('message',message=>{if(message.type==='query'){wanted=message;void pump();}});
