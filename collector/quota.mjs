import { createHash, randomBytes } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { Worker } from 'node:worker_threads';
import { quotaOptions, quotaAmount } from './pricing.mjs';
import { QuotaHistory } from './quota-history.mjs';
import { PricingUpdates } from './pricing-updates.mjs';

// Two read-only account methods; never refresh auth, buy credits or consume resets.
export async function readAccountQuota() {
  const store = globalThis.__codexEnhanceReadCache?.store;
  if (typeof store?.sendRequest !== 'function') return null;
  const [limits,auth] = await Promise.all([
    store.sendRequest('account/rateLimits/read', {}, { priority: 'background', timeoutMs: 8000 }),
    store.sendRequest('account/read', { refreshToken: false }, { priority: 'background', timeoutMs: 8000 })
  ]);
  const bucket = limits.rateLimitsByLimitId?.codex ?? (limits.rateLimits?.limitId == null || limits.rateLimits?.limitId === 'codex' ? limits.rateLimits : null);
  return { accountId: typeof limits.accountId === 'string' ? limits.accountId : null, authType: auth.account?.type ?? null,
    plan: bucket?.planType ?? auth.account?.planType ?? null,
    windows: [bucket?.primary, bucket?.secondary].filter(Boolean).map(w => ({ usedPercent: w.usedPercent, minutes: w.windowDurationMins, resetsAt: w.resetsAt })), checkedAtMs: Date.now() };
}
export function normalizeQuota(raw, salt, now = Date.now()) {
  if (!raw || !Array.isArray(raw.windows)) return { state: 'unavailable', reason: 'quota_unavailable', windows: [] };
  if (raw.authType !== 'chatgpt') return { state: 'unavailable', reason: 'not_subscription', windows: [] };
  const plan = String(raw.plan ?? '').toLowerCase();
  const windows = raw.windows.filter(w => Number.isFinite(w.usedPercent) && w.usedPercent >= 0 && Number.isFinite(w.minutes) && w.minutes > 0 && Number.isFinite(w.resetsAt))
    .filter(w => plan !== 'pro' || w.minutes !== 300)
    .map(w => ({ ...w, resetsAtMs: w.resetsAt * 1000, startMs: (w.resetsAt - w.minutes * 60) * 1000, remainingPercent: Math.max(0, 100 - w.usedPercent),
      label: w.minutes === 10080 ? '7d' : w.minutes === 300 ? '5h' : `${w.minutes}m` }))
    .filter(w => w.resetsAtMs > now && w.startMs <= now + 60000)
    .filter((w, i, a) => a.findIndex(x => x.minutes === w.minutes && x.resetsAt === w.resetsAt) === i);
  const accountKey = raw.accountId ? createHash('sha256').update(salt + '\0' + raw.accountId).digest('hex') : null;
  return { state: 'ready', accountKey, plan: raw.plan, checkedAtMs: raw.checkedAtMs, windows: windows.map(w => ({ ...w, accountIdentified: !!accountKey })) };
}
export function equivalentWindow(window, usage, ready, options) {
  const reasons = [];
  if (!ready) reasons.push('indexing');
  if (usage.unpricedRequests > 0) reasons.push('unpriced');
  if (usage.parseErrors > 0) reasons.push('parse_errors');
  if (usage.scanErrors > 0) reasons.push('index_error');
  if (window.usedPercent < 3) reasons.push('small_sample');
  if (!(usage.usd > 0)) reasons.push('no_usage');
  if (window.resetDetected) reasons.push('quota_rebounded');
  if (window.accountIdentified === false) reasons.push('account_unknown');
  if (quotaOptions(options).normalizeFast && usage.unnormalizedRequests > 0) reasons.push('speed_weight_unknown');
  const amount = quotaAmount(usage, options), ordinary = amount.ordinaryQuotaUsd;
  const total = reasons.length ? null : ordinary * 100 / window.usedPercent;
  return { ...window, ...usage, ...amount, estimateBasis: 'selected_equivalent', estimatedTotalUsd: total, estimatedRemainingUsd: total == null ? null : Math.max(0, total - ordinary),
    unknownSpeedTotalUsd: total == null || !amount.unknownSpeedUsd ? null : (ordinary + amount.unknownSpeedUsd) * 100 / window.usedPercent,
    estimateReasons: reasons, confidence: total == null ? 'insufficient' : usage.unattributedRequests || usage.assumedTierRequests ? 'low' : 'local_estimate' };
}
export class WeeklyQuota {
  constructor(home, stateDir, {pricingUpdates,now=Date.now,workerFactory} = {}) {
    this.now=now;this.workerFactory=workerFactory;this.displayPair=null;
    this.options = quotaOptions();
    this.home = home; this.stateDir = stateDir; this.view = { state: 'checking', windows: [] }; this.nextAt = 0; this.pending = null; this.revision = 0; this.session = null; this.worker = null; this.queryId = 0;
    fs.mkdirSync(stateDir, { recursive: true }); const file = path.join(stateDir, 'usage-salt');
    this.history = new QuotaHistory(stateDir);
    this.pricing=pricingUpdates??new PricingUpdates(stateDir);this.pricingRevision=this.pricing.catalog.revision;
    try { this.salt = fs.readFileSync(file, 'utf8').trim(); } catch { this.salt = randomBytes(24).toString('hex'); fs.writeFileSync(file, this.salt); }
    this.observationFile = path.join(stateDir, 'quota-observation.json');
    try { const saved = JSON.parse(fs.readFileSync(this.observationFile, 'utf8')); if (Array.isArray(saved.windows) && /^[a-f0-9]{64}$/.test(saved.accountKey ?? '')) this.rawQuota = saved; } catch {}
  }
  refresh() { this.nextAt = 0; this.pricing.refresh(); }
  setOptions(options) { this.options = quotaOptions(options); }
  ensureWorker() {
    if (this.worker) return;
    this.worker = this.workerFactory?.() ?? new Worker(new URL('./weekly-worker.mjs', import.meta.url), { workerData: { home: this.home, stateDir: this.stateDir, salt: this.salt } });
    this.worker.on('message', data => {
      if (data.queryId !== this.queryId || this.rawQuota?.accountKey !== data.accountKey) return;
      this.aggregate = data;
      if (data.complete) {
        this.displayPair={quota:this.rawQuota,aggregate:data};
        this.history.record(this.rawQuota,data);
        this.pricing.observeMissing(data.windows?.flatMap(w=>w.models??[])??[]);
      }
    });
    this.worker.on('error', () => { this.aggregate = { error: true }; this.worker = null; });
  }
  sample(session) {
    const now = this.now();
    const pricing=this.pricing.sample();
    if(this.pricingRevision!==this.pricing.catalog.revision) {
      this.pricingRevision=this.pricing.catalog.revision;
      // Reprice the same timestamped token ledger; do not re-read old logs or
      // mix a newer numerator with an older percentage.
      this.aggregate=null;
      if(this.view.state==='ready'&&this.rawQuota?.windows?.length) this.queryUsage(this.rawQuota);
    }
    if (session !== this.session) { this.session = session; this.revision++; this.pending = null; this.nextAt = 0; this.displayPair=null; this.view = { state: 'checking', windows: [] }; }
    if (!session) return { state: 'unavailable', reason: 'client_disconnected', windows: [] };
    if (!this.pending && now >= this.nextAt) {
      const revision = this.revision; this.nextAt = now + 60000;
      this.pending = session.evaluate(`(${readAccountQuota.toString()})()`, 10000).then(raw => {
        if (revision !== this.revision) return;
        const quota = normalizeQuota(raw, this.salt,this.now());
        const previous = this.rawQuota;
        if (previous?.accountKey === quota.accountKey) for (const w of quota.windows) {
          const old = previous.windows.find(p => p.minutes === w.minutes && p.resetsAt === w.resetsAt);
          if (old && (old.resetDetected || w.usedPercent < old.usedPercent - 1)) w.resetDetected = true;
        }
        this.rawQuota = quota; this.view = quota; this.aggregate = null;
        if(quota.state!=='ready')this.nextAt=this.now()+15000;
        if (quota.state === 'ready') {
          try { const temp = this.observationFile + `.${process.pid}.tmp`; fs.writeFileSync(temp, JSON.stringify(quota)); fs.renameSync(temp, this.observationFile); } catch {}
        }
        if (quota.state === 'ready' && quota.windows.length) {
          this.queryUsage(quota);
        }
      }).catch(() => { if (revision === this.revision) { this.view = { state: 'unavailable', reason: 'quota_unavailable', windows: [] }; this.nextAt=this.now()+15000; } })
        .finally(() => { if (revision === this.revision) this.pending = null; });
    }
    let quota = this.view;
    if (quota.state !== 'ready') return { ...quota, checking: !!this.pending, nextRefreshAtMs:this.nextAt, pricing };
    if (now - quota.checkedAtMs > 180000 || quota.windows.some(w => w.resetsAtMs <= now)) return { state: 'unavailable', reason: 'stale', windows: [] };
    let a = this.aggregate;
    const pair=this.displayPair;
    // Render only matched percentage/usage samples. Keep the previous pair
    // during a refresh so the money does not flash blank or use a new divisor.
    const retaining=!a?.complete&&!!quota.accountKey&&pair?.quota.accountKey===quota.accountKey
      &&now-pair.quota.checkedAtMs<=180000&&pair.quota.windows.length===quota.windows.length
      &&pair.quota.windows.every(w=>w.resetsAtMs>now&&quota.windows.some(next=>next.minutes===w.minutes&&next.resetsAt===w.resetsAt));
    if(retaining){quota=pair.quota;a=pair.aggregate;}
    return { state: 'ready', plan: quota.plan, checkedAtMs: quota.checkedAtMs, indexing: !a?.complete, indexProgress: this.aggregate?.progress ?? 0, checking: !!this.pending, refreshingUsage:retaining,
      windows: quota.windows.map(w => equivalentWindow(w, a?.windows?.find(x => x.minutes === w.minutes) ?? { usd: null, requests: 0, tokens: 0, unpricedRequests: 0, parseErrors: a?.error ? 1 : 0 }, !!a?.complete, this.options)),
      options: this.options, pricingDate: a?.pricingDate??this.pricing.catalog.verifiedAt, pricing, scope: 'local_openai', indexError: !!this.aggregate?.error,
      history: this.history.view(quota.accountKey,equivalentWindow,this.options,now) };
  }
  queryUsage(quota) {
    this.ensureWorker();this.worker.postMessage({type:'query',queryId:++this.queryId,quota,pricing:this.pricing.catalog});
  }
  async close() { this.revision++; this.session = null; this.pricing.close(); await this.worker?.terminate(); this.worker = null; }
}
