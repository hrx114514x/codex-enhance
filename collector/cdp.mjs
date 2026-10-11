// CDP transport and DOM attribute discovery adapted from Codex Usage Monitor,
// Copyright (c) 2026 contributors, MIT; original notice in licenses/.
import fs from 'node:fs';
import path from 'node:path';

export class CdpSession {
  constructor(target) { this.target = target; this.pending = new Map(); this.nextId = 1; this.closed = false; }
  async open() {
    this.ws = new WebSocket(this.target.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.ws.close(); reject(new Error('CDP connection timeout')); }, 2500);
      this.ws.addEventListener('open', () => { clearTimeout(timer); resolve(); }, { once: true });
      this.ws.addEventListener('error', () => { clearTimeout(timer); reject(new Error('CDP unavailable')); }, { once: true });
    });
    this.ws.addEventListener('message', e => {
      let data; try { data = JSON.parse(String(e.data)); } catch { return; }
      const request = this.pending.get(data.id);
      if (!request) return;
      clearTimeout(request.timer); this.pending.delete(data.id);
      data.error ? request.reject(new Error(data.error.message)) : request.resolve(data.result);
    });
    this.ws.addEventListener('close', () => this.fail());
    this.ws.addEventListener('error', () => this.fail());
    return this;
  }
  fail() {
    this.closed = true;
    for (const request of this.pending.values()) { clearTimeout(request.timer); request.reject(new Error('CDP disconnected')); }
    this.pending.clear();
  }
  request(method, params = {}, timeoutMs = 2500) {
    if (this.closed) return Promise.reject(new Error('CDP disconnected'));
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('CDP request timeout')); }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      try { this.ws.send(JSON.stringify({ id, method, params })); } catch (e) { clearTimeout(timer); this.pending.delete(id); reject(e); }
    });
  }
  async evaluate(expression, timeoutMs = 2500) {
    const r = await this.request('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture: false }, timeoutMs);
    if (r.exceptionDetails) throw new Error('Codex runtime layout is unavailable');
    return r.result?.value;
  }
  close() { this.fail(); try { this.ws?.close(); } catch {} }
}

export function selectCurrentThread(doc, route = '') {
  const uuid = /[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}/i;
  const result = new Map();
  const active = doc.activeElement;
  const modal = [...doc.querySelectorAll('[role="dialog"],[role="alertdialog"]')].some(node => {
    const rect = node.getBoundingClientRect(); return rect.width > 0 && rect.height > 0;
  });
  for (const attribute of ['data-above-composer-conversation-id', 'data-thread-id', 'data-conversation-id']) {
    for (const node of doc.querySelectorAll(`[${attribute}]`)) {
      const value = String(node.getAttribute(attribute) ?? '');
      if (/^chatgpt:/i.test(value) || node.closest?.('[hidden]') || (!modal && node.closest?.('[aria-hidden="true"]'))) continue;
      const id = value.match(uuid)?.[0]?.toLowerCase();
      if (!id) continue;
      const parent = node.parentElement;
      const rect = node.getBoundingClientRect();
      const parentRect = parent?.getBoundingClientRect?.();
      if (!(rect.width > 0 && rect.height > 0) && !(parentRect?.width > 0 && parentRect?.height > 0)) continue;
      const focused = Boolean(active && active !== doc.body && (node.contains?.(active) || (parent !== doc.body && parent?.contains?.(active))));
      result.set(id, { threadId: id, focused: focused || result.get(id)?.focused });
    }
    // Composer markers describe the underlying task. Search-result rows must
    // not replace it while a native command palette/dialog is open.
    if (result.size) break;
  }
  const candidates = [...result.values()];
  const focused = candidates.filter(c => c.focused);
  if (focused.length === 1) return { threadId: focused[0].threadId, ambiguous: false };
  const routeId = route.match(/(?:thread|task|conversation)s?[\/:]([0-9a-f-]{36})/i)?.[1]?.toLowerCase();
  if (routeId && result.has(routeId)) return { threadId: routeId, ambiguous: false };
  return { threadId: candidates.length === 1 ? candidates[0].threadId : null, ambiguous: candidates.length > 1 };
}

// Read only application-owned in-memory state. Never resume/start a turn,
// patch application functions, or alter user content. The separate capability
// monitor permits only listMcpServers, a read-only directory request.
export function readRuntimeStore(threadId) {
  if (!threadId) return null;
  const cacheKey = '__codexEnhanceReadCache';
  const cache = globalThis[cacheKey] ??= { store: null, lastProbe: 0 };
  let store = cache.store;
  const usableStore = candidate => candidate?.conversations instanceof Map && candidate.disposed !== true
    && (candidate.hostId == null || candidate.hostId === 'local');
  const containsThread = candidate => usableStore(candidate) && (candidate.conversations.has(threadId)
    || candidate.conversations.has(`local:${threadId}`) || [...candidate.conversations.keys()].some(key => String(key).endsWith(`:${threadId}`)));
  // Keep the account-capable manager while a newly selected thread hydrates.
  if (store && !usableStore(store)) { store = null; cache.store = null; }
  if (!store && Date.now() - cache.lastProbe > 8000) {
    cache.lastProbe = Date.now();
    const root = globalThis.__codexRoot?._internalRoot?.current;
    // State providers and hooks are direct owners. Walking their values avoids
    // traversing hundreds of thousands of unrelated DOM/React element objects.
    const fibers = root ? [root] : [], visitedFibers = new WeakSet(), seeds = [], seedSet = new WeakSet();
    const seed = value => {
      if (store) return;
      if (!value || typeof value !== 'object' || seedSet.has(value)) return;
      seedSet.add(value); seeds.push(value);
      if (containsThread(value)) { store = value; return; }
      // New clients keep host managers inside hook arrays. Inspect small
      // containers immediately, before unrelated React state fills the queue.
      const values = Array.isArray(value) && value.length <= 64 ? value
        : value instanceof Map && value.size <= 64 ? value.values()
        : value instanceof Set && value.size <= 64 ? value.values() : [];
      for (const child of values) if (containsThread(child)) { store = child; return; }
    };
    const fiberDeadline = performance.now() + 18;
    for (let index = 0; !store && index < fibers.length && index < 30000 && performance.now() < fiberDeadline; index++) {
      const fiber = fibers[index];
      if (!fiber || typeof fiber !== 'object' || visitedFibers.has(fiber)) continue;
      visitedFibers.add(fiber);
      if (fiber.child) fibers.push(fiber.child);
      if (fiber.sibling) fibers.push(fiber.sibling);
      let dependency = fiber.dependencies?.firstContext;
      for (let n = 0; !store && dependency && n < 30; n++, dependency = dependency.next) seed(dependency.memoizedValue);
      seed(fiber.memoizedProps?.value);
      let hook = fiber.memoizedState;
      for (let n = 0; !store && hook && n < 30 && Object.hasOwn(hook, 'memoizedState'); n++, hook = hook.next) seed(hook.memoizedState);
    }
    store ??= seeds.find(containsThread) ?? null;
    if (store) cache.store = store;
    const queue = store ? [] : [...new Set([...seeds, ...(root ? [root] : [])])], seen = new WeakSet();
    const deadline = performance.now() + 20;
    for (let index = 0; index < queue.length && index < 30000 && performance.now() < deadline; index++) {
      const obj = queue[index];
      if (!obj || typeof obj !== 'object' || seen.has(obj) || obj === globalThis || (typeof Node !== 'undefined' && obj instanceof Node)) continue;
      seen.add(obj);
      try {
        if (containsThread(obj)) {
          store = obj; cache.store = obj; break;
        }
        for (const [key, d] of Object.entries(Object.getOwnPropertyDescriptors(obj))) if (key !== 'children' && 'value' in d && d.value && typeof d.value === 'object') queue.push(d.value);
        if (obj instanceof Map) for (const val of obj.values()) queue.push(val);
        if (obj instanceof Set) for (const val of obj.values()) queue.push(val);
      } catch {}
    }
  }
  if (!store) return null;
  let conversation = store.conversations.get(threadId) ?? store.conversations.get(`local:${threadId}`);
  if (!conversation) for (const [key, value] of store.conversations) if (String(key).endsWith(threadId)) { conversation = value; break; }
  if (!conversation) return null;
  const turns = [...(conversation.turns ?? [])];
  const entities = conversation.turnHistory?.history?.entitiesByKey;
  if (entities) for (const value of Object.values(entities)) if (value?.turnId && Array.isArray(value.items)) turns.push(value);
  const byId = new Map();
  for (const t of turns) byId.set(t.turnId ?? t.id, t);
  const ordered = [...byId.values()].sort((a, b) => (a.turnStartedAtMs ?? a.startedAtMs ?? 0) - (b.turnStartedAtMs ?? b.startedAtMs ?? 0)).slice(-3);
  const firstReplyStarted = turn => {
    const start = turn.turnStartedAtMs ?? turn.startedAtMs;
    if (!Number.isFinite(start)) return null;
    const times = (turn.items ?? []).filter(i => i.type === 'agentMessage' && typeof i.text === 'string' && i.text.trim().length > 0)
      .map(i => turn.aeonAssistantMessageStartedAtMsById?.[i.id])
      .filter(time => Number.isFinite(time) && time >= start && time <= Date.now());
    return times.length ? Math.min(...times) : null;
  };
  const safeItem = (item, turn) => {
    const type=String(item.type).toLowerCase()==='extension'&&['clock.sleep','sleep'].includes(item.kind)?'sleep':item.type;
    if (!/mcpToolCall|commandExecution|webSearch|dynamicToolCall|fileChange|contextCompaction|collabToolCall|imageGeneration|imageView|sleep/.test(type ?? '')) return null;
    return { id: item.id, type, tool: item.tool, server: item.server, name: item.name,
      status: item.status, completed: item.completed, exitCode: item.exitCode,
      startedAtMs: item.startedAtMs ?? turn.commandExecutionStartedAtMsById?.[item.id], completedAtMs: item.completedAtMs, durationMs: item.durationMs,
      error: item.error || item.failure ? { message: String(item.error?.message ?? '调用异常').slice(0, 160) } : null,
      result: item.result?.isError ? { isError: true } : null };
  };
  return { title: typeof (conversation.name ?? conversation.title) === 'string' ? (conversation.name ?? conversation.title).slice(0, 160) : '', model: conversation.latestModel ?? conversation.model,
    effort: conversation.latestReasoningEffort, threadStatus: conversation.threadRuntimeStatus,
    turns: ordered.map(t => ({ id: t.turnId ?? t.id, status: t.status,
      requestedModel: typeof t.params?.model==='string'?t.params.model:null,
      modelRoutes: (t.items??[]).filter(i=>i.type==='modelRerouted').slice(-12)
        .map(i=>({id:i.id,fromModel:i.fromModel,toModel:i.toModel})),
      progressSignature: (t.items ?? []).slice(-8).map(i => [i.id, i.type, i.status, typeof i.text === 'string' ? i.text.length : 0, typeof i.aggregatedOutput === 'string' ? i.aggregatedOutput.length : 0].join(':')).join('|'),
      hasUserInput: (t.items ?? []).some(i => i.type === 'userMessage'),
      hasReply: (t.items ?? []).some(i => i.type === 'agentMessage' && typeof i.text === 'string' && i.text.trim().length > 0),
      firstReplyStartedAtMs: firstReplyStarted(t),
      hasModelActivity: Number.isFinite(t.firstTurnWorkItemStartedAtMs) || (t.items ?? []).some(i => i.type === 'reasoning' && [...(Array.isArray(i.summary) ? i.summary : []), ...(Array.isArray(i.content) ? i.content : [])].some(part => typeof part === 'string' ? part.trim().length > 0 : typeof part?.text === 'string' && part.text.trim().length > 0)),
      startedAtMs: t.turnStartedAtMs ?? t.startedAtMs ?? t.startTime ?? t.firstTurnWorkItemStartedAtMs,
      completedAtMs: t.completedAtMs, items: (t.items ?? []).map(item => safeItem(item, t)).filter(Boolean) })) };
}

export function runtimeExpression(lockedId = null) {
  return `(() => { const selection = (${selectCurrentThread.toString()})(document, location.href);
    const selected = ${JSON.stringify(lockedId)} || selection.threadId;
    return { ...selection, documentFocused: document.hasFocus(), runtime: (${readRuntimeStore.toString()})(selected) }; })()`;
}

export class DesktopLink {
  constructor(stateDir, { fallbackPorts = [9336, 9335, 9222], includeAppPort = true } = {}) {
    this.stateDir = stateDir; this.fallbackPorts = fallbackPorts; this.includeAppPort = includeAppPort;
    this.sessions = new Map(); this.lastDiscover = 0; this.connected = false;
  }
  async discover() {
    const ports = new Set(this.fallbackPorts);
    const configFiles = [path.join(this.stateDir, 'connection.json')];
    if (this.includeAppPort) configFiles.push(path.join(process.env.APPDATA ?? '', 'Codex', 'DevToolsActivePort'));
    for (const file of configFiles) {
      try { const raw = fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''); const port = Number(file.endsWith('.json') ? JSON.parse(raw).port : raw.split(/\r?\n/)[0]); if (port >= 1024 && port <= 65535) ports.add(port); } catch {}
    }
    const settled = await Promise.allSettled([...ports].map(async port => {
      const response = await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(600) });
      if (!response.ok) return [];
      const targets = await response.json();
      return targets.filter(t => { try { const u = new URL(t.url); return u.protocol === 'app:' && u.pathname === '/index.html' && !u.searchParams.has('initialRoute') && t.webSocketDebuggerUrl?.startsWith('ws://127.0.0.1:'); } catch { return false; } });
    }));
    const targets = settled.flatMap(r => r.status === 'fulfilled' ? r.value : []);
    const ids = new Set(targets.map(t => t.id));
    for (const [id, session] of this.sessions) if (!ids.has(id) || session.closed) { session.close(); this.sessions.delete(id); }
    for (const target of targets) if (!this.sessions.has(target.id)) {
      try { this.sessions.set(target.id, await new CdpSession(target).open()); } catch {}
    }
    this.connected = this.sessions.size > 0;
  }
  async poll(lockedId) {
    if (Date.now() - this.lastDiscover > 5000) { this.lastDiscover = Date.now(); await this.discover(); }
    const sessions = [...this.sessions.values()];
    const values = await Promise.allSettled(sessions.map(s => s.evaluate(runtimeExpression(lockedId))));
    const usable = values.flatMap((r, index) => r.status === 'fulfilled' && r.value ? [{ ...r.value, session: sessions[index] }] : []);
    this.connected = usable.length > 0;
    const focused = usable.filter(r => r.documentFocused);
    const result = focused.length === 1 ? focused[0] : usable.length === 1 ? usable[0] : null;
    this.activeSession = result?.session ?? null;
    if (result) { const { session, ...data } = result; return data; }
    return { threadId: null, ambiguous: usable.length > 1, runtime: null };
  }
  close() { for (const s of this.sessions.values()) s.close(); this.sessions.clear(); }
}
