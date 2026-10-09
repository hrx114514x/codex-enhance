import fs from 'node:fs';import path from 'node:path';
import {Worker} from 'node:worker_threads';
import {quotaAmount,quotaOptions} from './pricing.mjs';

export const CONVERSATION_REFRESH_MS=10000;
export function conversationCostView(data,options) {
  if(!data)return {state:'loading',estimatedUsd:null};
  const amount=quotaAmount(data,options).ordinaryQuotaUsd;
  const partial=data.unpricedRequests>0||data.parseErrors>0||data.readErrors>0||data.unsupportedFiles>0||quotaOptions(options).normalizeFast&&data.unnormalizedRequests>0;
  return {...data,estimatedUsd:data.state==='ready'&&!(data.requests>0&&data.requests===data.unpricedRequests)?amount:null,partial,
    models:(data.models??[]).map(model=>({...model,estimatedUsd:quotaAmount(model,options).ordinaryQuotaUsd}))};
}
export class ConversationCost {
  constructor(stateDir,{salt,now=Date.now,workerFactory,onMissing=()=>{}}={}) {
    this.now=now;this.salt=salt;this.workerFactory=workerFactory??(()=>new Worker(new URL('./conversation-cost-worker.mjs',import.meta.url),{workerData:{salt:this.salt}}));
    this.onMissing=onMissing;this.options=quotaOptions();this.automatic=true;this.currentId=null;this.pending=null;this.sequence=0;this.nextAt=0;this.cache=new Map();this.worker=null;this.force=false;this.error=null;
    try {this.automatic=JSON.parse(fs.readFileSync(path.join(stateDir,'settings.json'),'utf8').replace(/^\uFEFF/,''))?.conversationCostAutoRefresh!==false;}catch{}
  }
  ensureWorker() {
    if(this.worker)return;const worker=this.workerFactory();this.worker=worker;
    worker.on('message',message=>{
      if(this.worker!==worker||message.queryId!==this.pending?.queryId||message.threadId!==this.currentId)return;
      if(message.error){this.pending=null;this.error='refresh_failed';return;}
      if(!message.complete){this.progress=message.progress;return;}
      this.pending=null;this.error=null;this.cache.delete(message.threadId);this.cache.set(message.threadId,message);
      while(this.cache.size>8)this.cache.delete(this.cache.keys().next().value);
      this.onMissing(message.models??[]);
    });
    worker.on('error',()=>{if(this.worker===worker){this.worker=null;this.pending=null;this.error='refresh_failed';}});
    worker.on('exit',()=>{if(this.worker===worker){this.worker=null;this.pending=null;this.error='refresh_failed';}});
  }
  setOptions(options){this.options=quotaOptions(options);}
  setAutomatic(value) {
    this.automatic=value!==false;
    if(this.automatic)this.nextAt=0;
    else {this.pending=null;this.force=false;this.sequence++;}
  }
  refresh(threadId) {if(!threadId||threadId===this.currentId){this.force=true;this.nextAt=0;}}
  sample(threadId,files,pricing) {
    const now=this.now();
    if(threadId!==this.currentId){this.currentId=threadId;this.pending=null;this.sequence++;this.nextAt=0;this.force=true;this.error=null;}
    const control={automatic:this.automatic,intervalMs:CONVERSATION_REFRESH_MS};
    if(!threadId)return {state:'unavailable',reason:'no_thread',estimatedUsd:null,...control};
    const data=this.cache.get(threadId);
    const changed=!!data&&data.pricingRevision!==pricing.revision;
    if(!this.pending&&(this.force||this.automatic&&(now>=this.nextAt||changed))) {
      this.ensureWorker();const queryId=++this.sequence;this.pending={queryId,threadId};this.progress=0;this.force=false;this.error=null;this.nextAt=now+CONVERSATION_REFRESH_MS;
      this.worker.postMessage({type:'query',queryId,threadId,files:files??[],cutoff:now,pricing});
    }
    return {...conversationCostView(data,this.options),...control,threadId,refreshing:!!this.pending,progress:this.progress??0,error:this.error,
      options:this.options,nextRefreshAtMs:this.automatic?this.nextAt:null};
  }
  async close(){this.pending=null;const worker=this.worker;this.worker=null;await worker?.terminate();}
}
