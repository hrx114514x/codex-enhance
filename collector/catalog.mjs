import fs from 'node:fs';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { JsonlTail } from './tail.mjs';
import { ThreadState } from './state.mjs';
import {observeModelLog} from './models.mjs';

// Filter before LIMIT so a burst of worker threads cannot displace user conversations.
const userThreads = `CASE WHEN json_valid(source) THEN json_type(source, '$.subagent') IS NULL
  ELSE lower(coalesce(source,'')) NOT LIKE 'subagent%' AND lower(coalesce(source,'')) <> 'guardian' END
  AND coalesce(agent_path,'/root') IN ('','/root')`;

export function cleanTitle(value) {
  const raw = String(value ?? '');
  const source = raw.includes('## My request:') ? raw.split('## My request:').at(-1) : raw;
  return source.split(/\r?\n/).map(s => s.trim()).find(s => s && !s.startsWith('#') && !s.startsWith('<'))?.slice(0, 80) ?? '未命名任务';
}
export class Catalog {
  constructor(home) { this.home = home; this.db = null; this.logs = null; this.states = new Map(); this.tails = new Map(); this.metaCache = new Map(); this.lastMetaAt = 0; this.lastFileScan = 0; this.fileIndex = new Map(); this.logCursor = new Map(); this.activities = new Map(); }
  connect() {
    if (!this.db) { this.db = new DatabaseSync(path.join(this.home, 'state_5.sqlite'), { readOnly: true, timeout: 300 }); this.db.exec('PRAGMA query_only=ON'); }
  }
  recent() {
    this.connect();
    if (Date.now() - this.lastMetaAt > 5000) {
      const rows = this.db.prepare(`SELECT id, name, title, rollout_path, model, reasoning_effort, created_at, updated_at FROM threads WHERE archived=0 AND ${userThreads} ORDER BY updated_at DESC LIMIT 35`).all();
      this.metaCache = new Map(rows.map(r => [r.id, { ...r, title: cleanTitle(r.name || r.title) }]));
      this.lastMetaAt = Date.now();
    }
    return [...this.metaCache.values()].map(r => ({ id: r.id, title: r.title, createdAtMs: r.created_at * 1000 }));
  }
  isInternal(id) {
    this.connect();
    const row = this.db.prepare(`SELECT CASE WHEN ${userThreads} THEN 0 ELSE 1 END AS internal FROM threads WHERE id=?`).get(id);
    return Boolean(row?.internal);
  }
  metadata(id) {
    this.connect(); this.recent();
    return this.metaCache.get(id) ?? this.db.prepare('SELECT id, name, title, rollout_path, model, reasoning_effort FROM threads WHERE id=?').get(id);
  }
  discoverFiles(id, primary) {
    if (Date.now() - this.lastFileScan > 30000 || !this.fileIndex.has(id)) {
      const files = [];
      for (const folder of ['sessions', 'archived_sessions']) {
        const root = path.join(this.home, folder);
        if (!fs.existsSync(root)) continue;
        for (const entry of fs.readdirSync(root, { recursive: true, withFileTypes: true })) if (entry.isFile() && entry.name.endsWith('.jsonl') && entry.name.includes(id)) files.push(path.join(entry.parentPath, entry.name));
      }
      this.fileIndex.set(id, files); this.lastFileScan = Date.now();
    }
    const paths = [primary, ...(this.fileIndex.get(id) ?? [])].filter(Boolean).map(file => file.startsWith('\\\\?\\') ? file.slice(4) : file);
    return [...new Map(paths.map(file => [path.resolve(file).toLowerCase(), file])).values()];
  }
  sample(id) {
    const meta = this.metadata(id);
    if (!meta) return null;
    let state = this.states.get(id);
    if (!state) { state = new ThreadState(id,this.activities.get(id)); this.activities.delete(id); this.states.set(id, state); }
    state.title = cleanTitle(meta.name || meta.title); state.model ||= meta.model; state.effort ||= meta.reasoning_effort;
    const files = this.discoverFiles(id, meta.rollout_path);
    let caughtUp = true, errors = 0;
    // Process physical files in timestamp order; reducer uses turn ids and item ids to deduplicate.
    files.sort();
    for (const file of files) {
      const key = `${id}:${file}`;
      let tail = this.tails.get(key);
      if (!tail) {
        let own = null, inheritedBoundary = 0;
        tail = new JsonlTail(file, row => {
          if (row.type === 'session_meta') { if (own === null) { own = row.payload?.id; inheritedBoundary = row.payload?.subagent_history_start_ordinal ?? 0; } return; }
          if (own !== id || (row.ordinal != null && row.ordinal <= inheritedBoundary && inheritedBoundary > 0)) return;
          state.record(row);
        });
        this.tails.set(key, tail);
      }
      try { const r = tail.read(); caughtUp &&= r.caughtUp; errors += r.malformed + r.dropped; }
      catch { errors++; }
    }
    this.readCompaction(id, state);
    // Bound memory when the user navigates across many tasks.
    if (this.states.size > 8) {
      const old = [...this.states.keys()].find(k => k !== id);
      const checkpoint=this.states.get(old)?.activityCheckpoint();
      if(checkpoint)this.activities.set(old,checkpoint);
      while(this.activities.size>128)this.activities.delete(this.activities.keys().next().value);
      this.states.delete(old);
      this.logCursor.delete(old);
      for (const key of this.tails.keys()) if (key.startsWith(`${old}:`)) this.tails.delete(key);
    }
    return { state, caughtUp, errors, files };
  }
  readCompaction(id, state) {
    try {
      this.logs ??= new DatabaseSync(path.join(this.home, 'logs_2.sqlite'), { readOnly: true, timeout: 200 });
      const turn = state.turns.get(state.latestTurnId);
      if (!turn) return;
      const rows = this.logs.prepare('SELECT id, ts, ts_nanos, target, feedback_log_body FROM logs WHERE thread_id=? AND ts>=? AND id>? ORDER BY id LIMIT 300').all(id, Math.floor((turn.startedAtMs ?? Date.now()) / 1000), this.logCursor.get(id) ?? 0);
      for (const row of rows) {
        this.logCursor.set(id, Math.max(this.logCursor.get(id) ?? 0, row.id));
        observeModelLog(state,row);
        if(turn.completedAtMs)continue;
        if (/op: Compact|response\.compaction\.compacting|session_task\.compact/.test(row.feedback_log_body)) { turn.phase = 'compacting'; turn.wasCompaction = true; turn.compactionStartedAtMs ??= row.ts * 1000; }
        if (/response\.compaction\.compacting/.test(row.feedback_log_body)) turn.compactionHeartbeatAtMs = row.ts * 1000;
      }
    } catch { /* Diagnostic logs are optional; do not invent a phase on missing evidence. */ }
  }
  close() { this.db?.close(); this.logs?.close(); }
}
