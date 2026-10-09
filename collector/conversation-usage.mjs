import fs from 'node:fs';
import path from 'node:path';
import {JsonlTail} from './tail.mjs';
import {UsageLedger,keyed} from './usage-ledger.mjs';
import {priceUsage} from './pricing.mjs';

const priceFields=['quotaBaseUsd','astraPremiumUsd','quotaFastPremiumUsd','astraFastPremiumUsd'];
export class ConversationUsage {
  constructor(threadId,salt) {this.threadId=threadId;this.salt=salt;this.threadKey=keyed(salt,threadId);this.reset();}
  reset() {this.files=new Map();this.records=new Map();this.signatures=new Map();this.scopes=new Map();this.canonical=new Set();}
  remove(record) {
    this.records.delete(record.key);this.scopes.get(record.scopeKey)?.delete(record.key);
    if(this.signatures.get(record.signature)===record.key)this.signatures.delete(record.signature);
  }
  put(record) {
    if(!record)return;
    if(record.kind==='counter'&&this.canonical.has(record.scopeKey))return;
    if(record.kind==='ledger') {
      this.canonical.add(record.scopeKey);
      for(const key of [...(this.scopes.get(record.scopeKey)??[])]) {const old=this.records.get(key);if(old?.kind==='counter')this.remove(old);}
    }
    const same=this.records.get(record.key);
    if(same) {
      if(same.kind==='ledger'||record.kind==='counter')return;
      this.remove(same);
    }
    const duplicate=this.records.get(this.signatures.get(record.signature));
    if(record.kind==='counter'&&duplicate)return;
    if(record.kind==='ledger'&&duplicate?.kind==='counter')this.remove(duplicate);
    this.records.set(record.key,record);this.signatures.set(record.signature,record.key);
    if(!this.scopes.has(record.scopeKey))this.scopes.set(record.scopeKey,new Set());
    this.scopes.get(record.scopeKey).add(record.key);
  }
  async scan(paths,{progress=()=>{},cancelled=()=>false}={}) {
    const files=[...new Map(paths.map(file=>[path.resolve(file).toLowerCase(),path.resolve(file)])).values()].sort();
    let reset=[...this.files.keys()].some(file=>!files.includes(file));
    for(const file of files) {
      const old=this.files.get(file);if(!old)continue;
      try {const stat=fs.statSync(file);if(stat.size<old.tail.offset||old.tail.identity!==`${stat.dev}:${stat.ino}:${stat.birthtimeMs}`)reset=true;}
      catch {reset=true;}
    }
    if(reset)this.reset();let done=0,readErrors=0;
    for(const file of files) {
      if(cancelled())return false;
      let source=this.files.get(file);
      if(!source) {
        const parser=new UsageLedger(this.salt);source={parser,issues:[],tail:null};
        source.tail=new JsonlTail(file,row=>{
          const record=parser.accept(row);
          if(parser.state.meta?.key!==this.threadKey)return;
          this.put(record);
          if(parser.issue)source.issues.push({at:parser.issue.at,scopeKey:parser.issue.scopeKey});
        },prefix=>{
          const meta=parser.state.meta;
          if(meta&&(meta.key!==this.threadKey||meta.provider!=='openai'))return;
          const at=Date.parse(/"timestamp"\s*:\s*"([^"]+)"/.exec(prefix)?.[1]);
          source.issues.push({at:Number.isFinite(at)?at:null,scopeKey:null});
        });
        this.files.set(file,source);
      }
      try {
        for(;;) {
          const result=source.tail.read(4*1024*1024);
          progress(files.length?(done+(result.caughtUp?1:.5))/files.length:0);
          if(result.caughtUp)break;
          await new Promise(resolve=>setImmediate(resolve));if(cancelled())return false;
        }
      } catch {readErrors++;}
      done++;await new Promise(resolve=>setImmediate(resolve));
    }
    this.readErrors=readErrors;this.fileCount=files.length;return true;
  }
  totals(pricing,cutoff) {
    const out={requests:0,tokens:0,input:0,cached:0,output:0,usd:0,unpricedRequests:0,fastRequests:0,assumedTierRequests:0,unnormalizedRequests:0,
      quotaBaseUsd:0,astraPremiumUsd:0,quotaFastPremiumUsd:0,astraFastPremiumUsd:0,parseErrors:0,readErrors:this.readErrors??0,fromAtMs:null,toAtMs:null,models:[]};
    const models=new Map();let ownFiles=0,unsupportedFiles=0;
    for(const source of this.files.values()) {
      const meta=source.parser.state.meta;
      if(meta?.key===this.threadKey){ownFiles++;if(meta.provider!=='openai')unsupportedFiles++;}
      for(const issue of source.issues)if((issue.at===null||issue.at<=cutoff)&&(!issue.scopeKey||!this.canonical.has(issue.scopeKey)))out.parseErrors++;
      out.parseErrors+=source.tail.dropped;
    }
    for(const record of this.records.values()) {
      if(record.at>cutoff)continue;
      out.requests++;out.input+=record.input;out.cached+=record.cached??0;out.output+=record.output;out.tokens+=record.input+record.output;
      out.fromAtMs=Math.min(out.fromAtMs??record.at,record.at);out.toAtMs=Math.max(out.toAtMs??record.at,record.at);
      const model=models.get(record.model)??{model:record.model,requests:0,unpriced:0,quotaBaseUsd:0,astraPremiumUsd:0,quotaFastPremiumUsd:0,astraFastPremiumUsd:0};model.requests++;
      const price=priceUsage(record,pricing);
      if(!price){out.unpricedRequests++;model.unpriced++;}
      else {
        out.usd+=price.usd;for(const field of priceFields){out[field]+=price[field]??0;model[field]+=price[field]??0;}
        if(price.fast)out.fastRequests++;if(price.assumedTier)out.assumedTierRequests++;if(price.ordinaryQuotaUsd===null)out.unnormalizedRequests++;
      }
      models.set(record.model,model);
    }
    out.models=[...models.values()].sort((a,b)=>b.quotaBaseUsd-a.quotaBaseUsd).slice(0,32);
    out.unsupportedFiles=unsupportedFiles;
    out.state=!this.fileCount||!ownFiles?'unavailable':'ready';out.reason=!this.fileCount||!ownFiles?'no_records':unsupportedFiles===ownFiles?'unsupported_provider':null;
    if(out.reason==='unsupported_provider')out.state='unavailable';
    return out;
  }
}
