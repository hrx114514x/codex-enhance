import { createHash } from 'node:crypto';
import { classifyFailure } from './diagnostics.mjs';
import { performanceView, turnProblemNotice, firstOutputView } from './performance.mjs';
import { addTimingRange, observeTiming, elapsedTurnMs } from './timing.mjs';
import {observeActivity,activityView} from './activity.mjs';
import {modelId,observeReroute,modelIdentityView} from './models.mjs';
import {recordOutputItem,recordOutputUsage,recordOutputBoundary,outputSpeedView} from './output-speed.mjs';

const finite = value => typeof value === 'number' && Number.isFinite(value) ? value : null;
export function milliseconds(value) {
  if (typeof value === 'string') { const n = Date.parse(value); return Number.isFinite(n) ? n : null; }
  if (finite(value) !== null) return value < 100000000000 ? value * 1000 : value;
  return null;
}
const hash = value => createHash('sha256').update(JSON.stringify(value)).digest('hex').slice(0, 20);
const text = (value, max = 160) => String(value ?? '').replace(/[\r\n\t]+/g, ' ').slice(0, max);
export function normalizeUsage(u = {}) {
  // Field compatibility based on codex-monitor session-parser.js (MIT, licenses/).
  const input = finite(u.input_tokens ?? u.inputTokens);
  const output = finite(u.output_tokens ?? u.outputTokens);
  return { input, output, cached: finite(u.cached_input_tokens ?? u.cachedInputTokens ?? u.input_tokens_details?.cached_tokens),
    total: finite(u.total_tokens ?? u.totalTokens) ?? (input !== null && output !== null ? input + output : null) };
}

export function toolLabel(name, type = '') {
  const s = `${name} ${type}`.toLowerCase();
  if (/read_thread|thread\/read/.test(s)) return '会话读取';
  if (/imagegen|image_gen|generate.*image/.test(s)) return '图像生成';
  if (/exec_command|commandexecution|write_stdin|shell/.test(s)) return '终端执行';
  if (/web[._/]|websearch|search_query/.test(s)) return '网页检索';
  if (/browser|cua|playwright|sky\./.test(s)) return '浏览器操作';
  if (/apply_patch|filechange|read_file|write_file|view_image|imageview/.test(s)) return '文件操作';
  if (/sleep/.test(s)) return '等待';
  if (/mcp/.test(type.toLowerCase())) return '连接器调用';
  if (/collab|spawn_agent/.test(s)) return '协作任务';
  return '工具调用';
}

export function readThreadQuality(result) {
  let body = result?.structuredContent;
  if (!body) for (const part of result?.content ?? []) {
    if ((part.type === 'text' || part.type === 'Text') && typeof part.text === 'string' && part.text.length < 8 * 1024 * 1024) {
      try { const value = JSON.parse(part.text); if (Array.isArray(value.turns)) { body = value; break; } } catch {}
    }
  }
  if (!body || !Array.isArray(body.turns)) return null;
  const turns = body.turns;
  const unknown = turns.filter(t => !Array.isArray(t.items));
  const empty = turns.filter(t => Array.isArray(t.items) && t.items.length === 0);
  const count = turns.reduce((sum, t) => sum + (t.items?.length ?? 0), 0);
  if (empty.length) return { severity: 'warning', reason: '返回内容待核实',
    detail: `已完成，${turns.length} 轮中有 ${empty.length} 轮内容为空。`,
    resultSummary: `${turns.length} 轮 · ${count} 项`, quality: 'partial' };
  if (!turns.length) return { severity: 'warning', reason: '未返回对话轮次', detail: '调用已完成，返回为空；可能没有可读取的记录。', resultSummary: '0 轮', quality: 'empty' };
  return { resultSummary: `${turns.length} 轮 · ${count} 项`, quality: unknown.length ? 'unknown' : 'available' };
}

export function normalizeTool(item, meta = {}) {
  const originalType = item?.type ?? '';
  const extensionKind=String(item.kind??'').toLowerCase();
  const type = originalType.toLowerCase()==='extension'&&['sleep','clock.sleep'].includes(extensionKind)?'sleep':originalType.toLowerCase();
  if (!/mcptoolcall|commandexecution|dynamictoolcall|websearch|filechange|collabtoolcall|imagegeneration|imageview|sleep/.test(type) && !(type === 'extension' && /web|image|browser|tool/i.test(item.kind ?? ''))) return null;
  const name = type==='sleep'?'sleep':item.tool ?? item.name ?? item.kind ?? ({ commandexecution: 'exec_command', websearch: 'web.search', filechange: 'apply_patch' }[type]) ?? originalType;
  const args = item.arguments ?? item.args ?? {};
  const identityArgs = /read_thread/.test(name) ? { threadId: args.threadId, hostId: args.hostId ?? 'local' } : args;
  const matchKey = hash([name, type === 'commandexecution' ? item.command : identityArgs]);
  const reported=String(item.status??'').toLowerCase();
  const statusMissing=!reported&&typeof item.completed!=='boolean'&&typeof meta.completed!=='boolean';
  let status=/fail|error/.test(reported)?'failed':/cancel|abort|interrupt/.test(reported)?'interrupted'
    :/complete|succeed|success|done/.test(reported)||meta.completed===true||item.completed===true?'completed'
    :/inprogress|in_progress|running|started/.test(reported)||meta.completed===false||item.completed===false?'running':'unknown';
  const result = item.result;
  const error = item.error;
  const exitCode = item.exit_code ?? item.exitCode;
  const executionStatus = status;
  let severity = null, reason = '', detail = '', quality = 'unknown', resultSummary = '', category = 'normal';
  if (result?.isError === true || error || status === 'failed' || (Number.isInteger(exitCode) && exitCode !== 0)) {
    ({ status, severity, reason, detail, quality = 'unknown', resultSummary = '', category } = classifyFailure(item, exitCode));
  } else if (status === 'interrupted') {
    reason = '已中断'; detail = '执行已被中断。';
  } else if (status === 'completed' && /read_thread/.test(name)) {
    const check = readThreadQuality(result);
    if (check) { ({ severity = null, reason = '', detail = '', quality = 'unknown', resultSummary = '' } = check); if (severity) category = 'result_quality'; }
  }
  const duration = finite(item.durationMs ?? item.duration_ms) ??
    (typeof item.duration === 'object' ? (item.duration.secs ?? 0) * 1000 + (item.duration.nanos ?? 0) / 1e6 : finite(item.duration));
  let start = finite(meta.startedAtMs ?? item.startedAtMs) ?? (!meta.completed && meta.source === 'log' && status === 'running' ? finite(meta.timestamp) : null);
  const terminal=['completed','failed','interrupted'].includes(status);
  const anchoredEnd = finite(meta.completedAtMs ?? item.completedAtMs) ?? (terminal && meta.source === 'log' ? finite(meta.timestamp) : null);
  const timingStart = start ?? (anchoredEnd !== null && duration !== null && duration >= 0 ? anchoredEnd-duration : null);
  const timingEnd = anchoredEnd ?? (start !== null && duration !== null && duration >= 0 ? start+duration : null);
  const end = finite(meta.completedAtMs ?? item.completedAtMs) ?? (terminal ? finite(meta.timestamp) : null);
  if (start === null && end !== null && duration !== null) start = end - duration;
  const actualDuration = duration ?? (start !== null && end !== null ? Math.max(0, end - start) : null);
  return { id: String(item.id ?? hash([name, meta.turnId, start, end])), turnId: meta.turnId ?? '', name, label: toolLabel(`${item.server ?? ''}.${name}`, originalType),
    status, executionStatus, server: item.server ?? null, exitCode: Number.isInteger(exitCode) ? exitCode : null, category,
    startedAtMs: start, completedAtMs: end, durationMs: actualDuration, severity, reason, detail, quality, resultSummary,
    timingStartAtMs: timingStart, timingEndAtMs: terminal ? timingEnd : null,
    statusMissing, reportedAtMs: finite(meta.timestamp),
    seenRunningAtMs: status === 'running' ? finite(meta.timestamp) : null,
    observedStartedAtMs: status === 'running' ? finite(meta.timestamp) : null,
    timingApproximate: meta.source === 'runtime' && anchoredEnd === null,
    matchKey, resolved: false, source: meta.source ?? 'log' };
}

export class ThreadState {
  constructor(id, activityCheckpoint = null) {
    this.id = id; this.title = ''; this.model = ''; this.effort = ''; this.turns = new Map();
    this.tools = new Map(); this.latestTurnId = ''; this.latestTurnAt = 0; this.lastSampleAt = 0;
    this.context = null; this.cacheHit = null; this.totalTokens = null; this.compactions = 0;
    this.lastCompaction = null; this.usageSeen = new Set(); this.lastCompactionAt = 0;
    this.lastRuntimeAt = 0; this.lastActivityAt = 0;
    this.modelAt = 0;
    this.savedActivity = activityCheckpoint;
  }
  turn(id, at = 0) {
    if (!id) return null;
    if (!this.turns.has(id)) this.turns.set(id, { id, startedAtMs: at || null, completedAtMs: null, durationMs: null, ttftMs: null, phase: 'working', model: this.model, effort: this.effort });
    if(this.savedActivity?.turnId===id) {
      this.turns.get(id).liveActivity=this.savedActivity.activity;
      this.turns.get(id).activityVerified=false;this.savedActivity=null;
    }
    if (at >= this.latestTurnAt) { this.latestTurnAt = at; this.latestTurnId = id; }
    return this.turns.get(id);
  }
  pauseTiming(preserveActivity = false) {
    const turn = this.turns.get(this.latestTurnId);
    if (turn) { turn.timingObservation = null; turn.activityVerified=false; if(!preserveActivity)turn.liveActivity = null; }
  }
  activityCheckpoint() {
    const turn=this.turns.get(this.latestTurnId);
    return turn?.liveActivity&&!turn.completedAtMs?{turnId:turn.id,activity:structuredClone(turn.liveActivity)}:null;
  }
  applyUsage(info, at) {
    const last = normalizeUsage(info.last_token_usage ?? info.last ?? info.usage);
    const limit = finite(info.model_context_window ?? info.modelContextWindow);
    if (at < this.lastSampleAt || last.total === null) return;
    const key = hash([at, last, info.total_token_usage ?? info.total]);
    if (this.usageSeen.has(key)) return;
    this.usageSeen.add(key);
    if (this.usageSeen.size > 5000) this.usageSeen.delete(this.usageSeen.values().next().value);
    this.lastSampleAt = at;
    const capacity = limit ?? this.context?.limit ?? null;
    this.context = { used: last.total, limit: capacity, percent: capacity > 0 ? Math.min(100, last.total / capacity * 100) : null, sampledAtMs: at };
    this.cacheHit = last.input > 0 && last.cached !== null ? Math.max(0, Math.min(100, last.cached / last.input * 100)) : null;
    this.totalTokens = normalizeUsage(info.total_token_usage ?? info.total ?? {}).total ?? this.totalTokens;
    if (this.lastCompaction && at > this.lastCompactionAt && !this.lastCompaction.after) this.lastCompaction.after = last.total;
  }
  addTool(tool) {
    if (!tool) return;
    const old = this.tools.get(tool.id);
    // Completed durable records win over stale in-progress runtime snapshots.
    const terminal=old&&['completed','failed','interrupted'].includes(old.status);
    if (terminal && ['running','unknown'].includes(tool.status)) return;
    if (old?.source === 'log' && terminal && tool.source === 'runtime') return;
    if (old?.source==='log'&&old.status==='running'&&tool.source==='runtime'&&tool.statusMissing) {
      // An explicit invocation remains pending until its completion. A sparse
      // runtime summary may confirm presence, but cannot invent a new start.
      old.seenRunningAtMs=tool.reportedAtMs??old.seenRunningAtMs;
      return;
    }
    if (old) tool = { ...old, ...tool, startedAtMs: tool.startedAtMs ?? old.startedAtMs,
      observedStartedAtMs: old.observedStartedAtMs ?? tool.observedStartedAtMs, seenRunningAtMs: tool.seenRunningAtMs ?? old.seenRunningAtMs,
      timingStartAtMs: tool.timingStartAtMs ?? old.timingStartAtMs,
      timingEndAtMs: tool.timingEndAtMs ?? old.timingEndAtMs ?? (tool.status !== 'running' && old.observedStartedAtMs ? tool.completedAtMs : null),
      durationMs: tool.durationMs ?? old.durationMs, resolved: old.resolved && tool.severity === old.severity };
    this.tools.set(tool.id, tool);
    if (tool.status === 'completed' && tool.executionStatus !== 'failed' && tool.server) for (const previous of this.tools.values()) {
      if (previous.id !== tool.id && previous.server === tool.server && ['tool_startup', 'tool_connection'].includes(previous.category) &&
          (previous.completedAtMs ?? 0) <= (tool.completedAtMs ?? 0)) previous.resolved = true;
    }
    if ((!tool.severity || tool.category === 'expected_result') && tool.status === 'completed') for (const previous of this.tools.values()) {
      if (previous.id !== tool.id && previous.severity && previous.matchKey === tool.matchKey &&
          (previous.completedAtMs ?? 0) <= (tool.completedAtMs ?? 0)) previous.resolved = true;
    }
    if (this.tools.size > 1000) {
      const removable = [...this.tools.values()].filter(t => t.status !== 'running').sort((a, b) => (a.completedAtMs ?? 0) - (b.completedAtMs ?? 0));
      for (const t of removable.slice(0, this.tools.size - 800)) {
        addTimingRange(this.turns.get(t.turnId),'tools',t.timingStartAtMs??t.observedStartedAtMs,t.timingEndAtMs);
        this.tools.delete(t.id);
      }
    }
  }
  record(row) {
    const p = row.payload ?? {}, at = milliseconds(row.timestamp) ?? 0;
    if (p.thread_id && p.thread_id !== this.id) return;
    if (row.type === 'turn_context') {
      if (at >= this.modelAt) { this.modelAt = at; this.model = p.model ?? this.model; this.effort = p.effort ?? this.effort; }
      const turn = this.turns.get(p.turn_id ?? this.latestTurnId); if (turn) { turn.model = p.model ?? turn.model; turn.effort = p.effort ?? turn.effort; }
      return;
    }
    if (row.type === 'compacted') {
      if (at >= this.lastCompactionAt) {
        const n = finite(p.window_number);
        this.compactions = n !== null ? Math.max(this.compactions, n) : this.compactions + (at > this.lastCompactionAt ? 1 : 0);
        this.lastCompactionAt = at; this.lastCompaction = { before: this.context?.used ?? null, after: null, durationMs: null, at };
      } return;
    }
    if (row.type === 'token_usage_record') {
      recordOutputUsage(this.turns.get(p.turn_id),p,at);
      this.applyUsage({ usage: p.usage, total: p.thread_token_usage ?? p.total_token_usage }, at); return;
    }
    if(row.type==='response_item') {
      const owner=this.turns.get(p.turn_id??this.latestTurnId);
      if(p.type==='reasoning'||p.type==='message'&&p.role==='assistant'||['function_call','custom_tool_call'].includes(p.type))
        recordOutputItem(owner,{id:p.id??p.call_id,completedAtMs:at});
      if(['function_call_output','custom_tool_call_output'].includes(p.type))recordOutputBoundary(owner,at);
      // Sleep summaries have no status in the desktop store. Pair the explicit
      // call/result lifecycle instead of treating every historical summary as live.
      if(p.type==='function_call'&&p.call_id&&(p.name==='clock.sleep'||p.namespace==='clock'&&p.name==='sleep')) {
        this.lastActivityAt=Math.max(this.lastActivityAt,at);
        this.addTool(normalizeTool({id:p.call_id,type:'sleep'},{source:'log',completed:false,timestamp:at,startedAtMs:at,turnId:p.turn_id??this.latestTurnId}));
      } else if(p.type==='function_call_output'&&p.call_id) {
        const old=this.tools.get(p.call_id);
        if(old?.name==='sleep'&&!['completed','failed','interrupted'].includes(old.status)) {
          this.lastActivityAt=Math.max(this.lastActivityAt,at);
          this.addTool(normalizeTool({id:p.call_id,type:'sleep'},{source:'log',completed:true,timestamp:at,startedAtMs:old.startedAtMs,completedAtMs:at,turnId:old.turnId}));
        }
      }
      return;
    }
    if (row.type !== 'event_msg') return;
    this.lastActivityAt = Math.max(this.lastActivityAt, at);
    const kind = p.type;
    if(kind==='model_reroute') {
      observeReroute(this.turns.get(p.turn_id??this.latestTurnId),p,at,'log');
    } else if (kind === 'thread_settings_applied') {
      if (at >= this.modelAt) { this.modelAt = at; this.model = p.thread_settings?.model ?? this.model; this.effort = p.thread_settings?.reasoning_effort ?? this.effort; }
    } else if (kind === 'task_started') {
      const turn = this.turn(p.turn_id, milliseconds(p.started_at) ?? at);
      if (turn && !turn.completedAtMs) turn.phase = 'working';
      if (p.model_context_window) this.context = { ...this.context, limit: p.model_context_window };
    } else if (kind === 'task_complete' || kind === 'turn_aborted') {
      const turn = this.turn(p.turn_id, milliseconds(p.started_at) ?? 0);
      if (turn) {
        turn.completedAtMs = milliseconds(p.completed_at) ?? at;
        turn.durationMs = finite(p.duration_ms) ?? (turn.startedAtMs ? Math.max(0, at - turn.startedAtMs) : null);
        turn.ttftMs = finite(p.time_to_first_token_ms);
        if (typeof p.last_agent_message === 'string' && p.last_agent_message.trim()) turn.hasReply = true;
        turn.phase = kind === 'turn_aborted' ? 'interrupted' : p.error ? 'failed' : 'idle';
        if (p.error) turn.problemNotice = turnProblemNotice(p.error);
        for (const tool of this.tools.values()) if (tool.turnId === p.turn_id && tool.status === 'running') tool.status = 'unknown';
      }
    } else if (kind === 'token_count') {
      if (p.info) this.applyUsage(p.info, at);
    } else if (kind === 'error') {
      const owner = this.turns.get(p.turn_id ?? this.latestTurnId);
      if (owner) owner.problemNotice = turnProblemNotice({ code: p.codex_error_info, message: p.message }) ?? owner.problemNotice;
    } else if (kind === 'user_message' || kind === 'agent_message') {
      const owner = this.turns.get(p.turn_id ?? this.latestTurnId);
      if (owner && kind === 'user_message') owner.hasUserInput = true;
      if (owner && kind === 'agent_message' && String(p.message ?? '').trim()) owner.hasReply = true;
    } else if (kind === 'item_completed' || kind === 'item_started') {
      const item = p.item ?? {}, completed = kind === 'item_completed';
      const owner = this.turns.get(p.turn_id ?? this.latestTurnId);
      if(completed&&/^(agentmessage|reasoning)$/i.test(item.type??''))
        recordOutputItem(owner,{id:item.id,startedAtMs:p.started_at_ms,completedAtMs:p.completed_at_ms,explicit:true});
      if (owner && String(item.type).toLowerCase() === 'usermessage') owner.hasUserInput = true;
      if (owner && String(item.type).toLowerCase() === 'agentmessage' && (String(item.text ?? '').trim() || (item.content ?? []).some(c => typeof c.text === 'string' && c.text.trim()))) owner.hasReply = true;
      if (/contextcompaction/i.test(item.type ?? '')) {
        const turn = this.turns.get(p.turn_id);
        if (turn) turn.wasCompaction = true;
        if (turn && !completed) { turn.phase = 'compacting'; turn.compactionStartedAtMs ??= p.started_at_ms ?? at; }
        if (completed) {
          if (turn) {
            turn.compactionCompletedAtMs = p.completed_at_ms ?? at;
            recordOutputBoundary(turn,turn.compactionCompletedAtMs);
            addTimingRange(turn,'compacting',p.started_at_ms??turn.compactionStartedAtMs,turn.compactionCompletedAtMs);
            if (turn.phase === 'compacting' && !turn.completedAtMs) turn.phase = 'working';
          }
          this.lastCompaction ??= { before: null, after: null, at };
          this.lastCompaction.durationMs = p.completed_at_ms != null && p.started_at_ms != null ? p.completed_at_ms - p.started_at_ms : null;
        }
      }
      const tool=normalizeTool(item, { completed, turnId: p.turn_id ?? this.latestTurnId,
        timestamp: at, startedAtMs: p.started_at_ms, completedAtMs: p.completed_at_ms, source: 'log' });
      if(completed&&tool)recordOutputBoundary(owner,tool.timingEndAtMs??at);
      this.addTool(tool);
    }
  }
  runtime(data, now = Date.now()) {
    if (!data?.turns) return;
    this.lastRuntimeAt = now;
    this.title = data.title || this.title; this.model = data.model || this.model; this.effort = data.effort || this.effort;
    for (const t of data.turns) {
      const at = finite(t.startedAtMs) ?? milliseconds(t.startedAt) ?? 0;
      const turn = this.turn(t.id ?? t.turnId, at);
      if (!turn) continue;
      if (t.progressSignature && t.progressSignature !== turn.progressSignature) { turn.progressSignature = t.progressSignature; turn.lastObservedProgressAt = now; }
      turn.model = modelId(t.requestedModel)??(turn.model||data.model); turn.effort ||= data.effort;
      for(const route of t.modelRoutes??[])observeReroute(turn,route,finite(route.atMs)??now,'runtime');
      if (t.hasUserInput) turn.hasUserInput = true;
      if (t.hasReply) turn.hasReply = true;
      turn.outputObserved = true;
      if (t.hasModelActivity) turn.hasModelActivity = true;
      const firstReply = finite(t.firstReplyStartedAtMs);
      if (firstReply !== null && turn.startedAtMs != null && firstReply >= turn.startedAtMs && firstReply <= now)
        turn.firstReplyStartedAtMs = Math.min(turn.firstReplyStartedAtMs ?? firstReply, firstReply);
      const status = String(t.status ?? '').toLowerCase();
      if (/inprogress|in_progress|active|running/.test(status) && !turn.completedAtMs) {
        const observedCompaction = turn.compactionHeartbeatAtMs && !turn.compactionCompletedAtMs && now - turn.compactionHeartbeatAtMs < 45000;
        turn.phase = observedCompaction ? 'compacting' : 'working';
      }
      if (/completed|failed|interrupted/.test(status)) {
        turn.phase = status === 'completed' ? 'idle' : status;
        turn.completedAtMs ??= finite(t.completedAtMs) ?? now;
      }
      for (const item of t.items ?? []) {
        if (item.type === 'contextCompaction') { turn.wasCompaction = true; if (item.completed === false) { turn.phase = 'compacting'; turn.compactionStartedAtMs ??= item.startedAtMs ?? now; } }
        this.addTool(normalizeTool(item, { turnId: turn.id, timestamp: now, startedAtMs: item.startedAtMs, completedAtMs: item.completedAtMs, source: 'runtime' }));
      }
      if(Array.isArray(t.items)) {
        const present=new Set(t.items.map(i=>String(i.id)));
        for(const tool of this.tools.values()) if(tool.turnId===turn.id&&tool.source==='runtime'&&tool.status==='running'&&!present.has(tool.id)) {
          tool.status='unknown';tool.executionStatus='unknown';
        }
      }
    }
    const latest = this.turns.get(this.latestTurnId);
    if (latest && !latest.completedAtMs && data.threadStatus?.activeFlags?.some(flag => /waiting/i.test(flag))) {
      latest.phase = 'waiting'; latest.waitReason = data.threadStatus.activeFlags.some(flag => /approval/i.test(flag)) ? '等待确认' : '等待输入';
    }
    const currentRuntime = latest && data.turns.find(t=>(t.id??t.turnId)===latest.id);
    if (latest && (currentRuntime || data.threadStatus?.activeFlags?.some(flag=>/waiting/i.test(flag)))) {
      const runningTools = [...this.tools.values()].some(t=>t.turnId===latest.id && (t.status==='running'||t.status==='unknown'&&t.observedStartedAtMs!=null));
      // This is time in an observed active model phase, including response and
      // provider/network waiting. It is not a measurement of pure reasoning.
      const modelActive = !!currentRuntime && /^(inprogress|in_progress|active|running)$/i.test(currentRuntime.status??'') && !runningTools;
      observeTiming(latest,now,{modelActive});
      // Streaming reply text changes within a phase. Only execution/compaction
      // items identify a boundary, using the runtime list before reducer pruning.
      const boundary=Array.isArray(currentRuntime?.items)?hash(currentRuntime.items
        .filter(i=>/toolcall|commandexecution|websearch|filechange|contextcompaction|imagegeneration|imageview|sleep/i.test(i.type??''))
        .map(i=>[i.id,i.type])):null;
      observeActivity(latest,[...this.tools.values()].filter(t=>t.turnId===latest.id),now,boundary);
    }
    else if (latest) {
      this.pauseTiming(true);
      for(const tool of this.tools.values())if(tool.turnId===latest.id&&tool.source==='runtime'&&tool.status==='running'){tool.status='unknown';tool.executionStatus='unknown';}
    }
  }
  snapshot(now = Date.now(), runtimeConnected = false) {
    const turn = this.turns.get(this.latestTurnId);
    if (!runtimeConnected) this.pauseTiming(true);
    const all = [...this.tools.values()].map(t => !runtimeConnected && t.status === 'running' && t.source === 'runtime' ? { ...t, status: 'unknown' } : t)
      .sort((a, b) => (b.startedAtMs ?? b.completedAtMs ?? 0) - (a.startedAtMs ?? a.completedAtMs ?? 0));
    const current = all.filter(t => t.turnId === this.latestTurnId);
    const priority = { critical: 3, error: 2, warning: 1, info: 0 };
    const attention = current.filter(t => ['warning', 'error', 'critical'].includes(t.severity) && !t.resolved)
      .sort((a, b) => priority[b.severity] - priority[a.severity] || (b.completedAtMs ?? 0) - (a.completedAtMs ?? 0));
    const notes = current.filter(t => t.severity === 'info' && !t.resolved);
    const highestSeverity = attention[0]?.severity ?? (notes.length ? 'info' : null);
    const levelCounts = Object.fromEntries(['info', 'warning', 'error', 'critical'].map(level => [level, current.filter(t => t.severity === level && !t.resolved).length]));
    const pending = current.filter(t => t.status === 'running');
    const running = pending.map(t => !runtimeConnected && t.source === 'runtime' ? { ...t, status: 'unknown' } : t);
    let phase = turn?.phase ?? 'unknown';
    if (phase === 'working' && !runtimeConnected && now - this.lastActivityAt > 120000) phase = 'unknown';
    return { threadId: this.id, title: this.title, model: this.model, effort: this.effort, turnId: this.latestTurnId,
      phase, startedAtMs: turn?.startedAtMs, elapsedMs: elapsedTurnMs(turn,now),
      ttftMs: turn?.ttftMs ?? null, firstOutput: firstOutputView(turn, now, runtimeConnected), context: this.context, cacheHit: this.cacheHit, totalTokens: this.totalTokens,
      compactions: this.compactions, lastCompaction: this.lastCompaction,
      performance: performanceView(this, current, now, runtimeConnected),
      activity: activityView(turn,current,now,runtimeConnected),
      modelIdentity: modelIdentityView(turn,this.model),
      outputSpeed: outputSpeedView(turn,now),
      tools: { running: running.filter(t => t.status === 'running').length, completed: current.filter(t => t.status === 'completed').length,
        attention: attention.length, notes: notes.length, highestSeverity, levelCounts, issues: attention.slice(0, 20), items: all.slice(0, 100), runtimeAvailable: runtimeConnected },
      updatedAtMs: now };
  }
}
