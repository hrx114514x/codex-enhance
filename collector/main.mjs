import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import readline from 'node:readline';
import { Catalog } from './catalog.mjs';
import { DesktopLink } from './cdp.mjs';
import { CapabilityMonitor } from './capabilities.mjs';
import { WeeklyQuota } from './quota.mjs';
import {ConversationCost} from './conversation-cost.mjs';
import {VoiceUsage} from './voice.mjs';

const args = process.argv.slice(2);
const value = (flag, fallback) => args.includes(flag) ? args[args.indexOf(flag) + 1] : fallback;
const home = value('--home', process.env.CODEX_HOME ?? path.join(os.homedir(), '.codex'));
const stateDir = value('--state-dir', path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'CodexEnhance'));
const seed = value('--thread', null);
const catalog = new Catalog(home), desktop = new DesktopLink(stateDir);
const capabilities = new CapabilityMonitor();
const weekly = new WeeklyQuota(home, stateDir);
const conversationCost = new ConversationCost(stateDir,{salt:weekly.salt,onMissing:models=>weekly.pricing.observeMissing(models)});
const voiceUsage = new VoiceUsage(stateDir);
let manualId = seed, lockedId = null, follow = true, lastId = null, stopping = false;
let timedState = null;
const pauseTiming = (preserveActivity=false) => { timedState?.pauseTiming(preserveActivity); timedState = null; };
const write = data => { if (!process.stdout.destroyed) process.stdout.write(`${JSON.stringify(data)}\n`); };
const rl = readline.createInterface({ input: process.stdin });
rl.on('line', line => {
  try {
    const command = JSON.parse(line);
    if (command.type === 'select' && /^[0-9a-f-]{36}$/i.test(command.threadId ?? '')) { manualId = command.threadId; follow = false; lockedId = null; }
    if (command.type === 'follow') { follow = true; lockedId = null; manualId = null; }
    if (command.type === 'lock') { lockedId = command.threadId || lastId; }
    if (command.type === 'unlock') { lockedId = null; follow = true; manualId = null; }
    if (command.type === 'stop') stopping = true;
    if (command.type === 'checkTools') capabilities.refresh(command.threadId ?? lastId);
    if (command.type === 'refreshQuota') weekly.refresh();
    if (command.type === 'checkVoice') voiceUsage.inspect();
    if (command.type === 'quotaOptions') { weekly.setOptions(command.options); conversationCost.setOptions(command.options); }
    if (command.type === 'conversationCostAutomatic') conversationCost.setAutomatic(command.enabled);
    if (command.type === 'refreshConversationCost') { conversationCost.refresh(command.threadId); weekly.pricing.refresh(); }
  } catch {}
});
rl.on('close', () => { stopping = true; });
process.on('SIGTERM', () => { stopping = true; });
process.on('SIGINT', () => { stopping = true; });

while (!stopping) {
  const start = Date.now();
  try {
    const selected = lockedId ?? manualId;
    const selectionReset = Boolean(selected && catalog.isInternal(selected));
    if (selectionReset) { lockedId = null; manualId = null; follow = true; }
    const current = await desktop.poll(lockedId ?? (!follow ? manualId : null));
    const id = lockedId ?? (!follow ? manualId : current.threadId ?? (!desktop.connected ? manualId : null));
    if(timedState&&id&&timedState.id!==id)pauseTiming(true);
    else if(!id||!current.runtime)pauseTiming(true);
    const selection = lockedId ? 'locked' : !follow || (!desktop.connected && manualId) ? 'manual' : id ? 'auto' : 'none';
    const toolHealth = capabilities.sample(desktop.activeSession, id, Boolean(current.runtime));
    const quota = weekly.sample(desktop.activeSession);
    const voice = voiceUsage.sample(desktop.activeSession,id);
    const connection = { cdp: desktop.connected ? 'connected' : 'waiting', runtime: Boolean(current.runtime), selection, selectionReset,
      message: !desktop.connected ? '下次正常启动 Codex 后启用自动跟随' : current.ambiguous ? '多个会话可见，请点击要跟随的输入区' : !id ? '当前页面没有可识别的本地任务' : '' };
    const recentThreads = catalog.recent();
    if (id) {
      const data = catalog.sample(id);
      const cost = conversationCost.sample(id,data?.files,weekly.pricing.catalog);
      if (data) {
        timedState = data.state;
        if (current.runtime && (lockedId || id === current.threadId || !follow)) data.state.runtime(current.runtime);
        const snapshot = data.state.snapshot(Date.now(), desktop.connected && Boolean(current.runtime));
        lastId = id;
        write({ schemaVersion: 1, ...snapshot, connection, toolHealth, quota, voice, conversationCost:cost, recentThreads, historyLoading: !data.caughtUp, readErrors: data.errors });
      } else { pauseTiming(); write({ schemaVersion: 1, threadId: id, phase: 'unknown', connection: { ...connection, message: '未找到这个任务的本地记录' }, quota, voice, conversationCost:cost, recentThreads, updatedAtMs: Date.now() }); }
    } else write({ schemaVersion: 1, phase: 'unknown', connection, quota, voice, conversationCost:conversationCost.sample(null,[],weekly.pricing.catalog), recentThreads, updatedAtMs: Date.now() });
  } catch (e) { pauseTiming(true); write({ schemaVersion: 1, phase: 'unknown', error: String(e.message).slice(0, 180), connection: { cdp: 'waiting', selection: 'none', message: '采集暂不可用，正在重连' }, updatedAtMs: Date.now() }); }
  if (args.includes('--once')) break;
  await new Promise(r => setTimeout(r, Math.max(100, 900 - (Date.now() - start))));
}
voiceUsage.close(); desktop.close(); catalog.close(); rl.close(); await conversationCost.close(); await weekly.close();
