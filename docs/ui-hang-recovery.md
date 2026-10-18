# Windows 浮窗与 Codex 输入卡顿排查（2026-09-30）

## 现场证据概要

- 窗口能响应 Windows 消息探测，但鼠标/键盘仍受影响；Responding=true 不足以证明可操作。
- 旧版将 Codex 主窗设为浮窗 owner，两个 GUI 线程共享焦点/捕获状态。
- 只结束浮窗后输入恢复，Codex 主进程及运行任务保持。
- 以下说明区分现场证据、隔离实验和无法确认的触发细节。

## 能确定的机制与仍未知的触发点

原实现通过 WindowInteropHelper.Owner 和 GWLP_HWNDPARENT 把其他进程的 Codex 窗口设为浮窗 owner。Windows 会自动连接 owner/owned 的输入队列，焦点、激活与鼠标捕获涉及的操作因而需要两边协同。此绑定是本次联动卡顿的强证据解释，但 owner 关系本身不意味着一定死锁，也不能仅凭菜单捕获状态断言菜单有错。

微软机制说明：https://devblogs.microsoft.com/oldnewthing/20130607-00/?p=4143/

源代码还存在独立的阻塞风险：CollectorClient.Send 在 WPF 调用线程上同步写入并 Flush 子进程 stdin；Dispose 在该线程上先写 stop，再关闭 stdin 和等待进程退出。管道容量不足、采集进程不读数据时，写入可以一直等待，原有 3500ms 的 WaitForExit 上限并不覆盖此前的写入。

隔离回归实验向不读 stdin 的 Node fixture 写入大于管道容量的数据，复现了旧同步写法阻塞调用方；这证明该风险存在，实验载荷不代表实际现场命令。旧进程结束前未取得线程栈/等待链，因此无法确认今天最先卡住的是管道、输入队列、WPF/WinForms 交互还是其他调用，不能把实验当作现场精确根因。

## 修复

- 主浮窗不再设置跨进程 owner/parent，也不调用 AttachThreadInput。详情窗口只保留本程序同线程的 owner。
- 通过几何/可见性轮询跟随 Codex；最小化或隐藏时主动隐藏浮窗，通过非激活的 z-order 操作保持在 Codex 上方，不全局置顶。
- 无标题托盘菜单不再作为位置跟随宿主。
- 命令使用有界后台队列，写入/Flush 单步 2 秒超时；退出与旧采集进程清理在后台执行，等待 1.5 秒后清理本工具的进程树。
- 重连丢弃旧采集器的迟到回调；状态更新合并为最新一条，在较低 dispatcher 优先级渲染。
- 状态诊断文件写入移至后台；托盘回调改为 BeginInvoke。
- 界面心跳超过 5 秒未更新时，在本工具状态目录的 ui-health.jsonl 记录最后阶段、GUI 线程、等待链可用性及恢复。休眠恢复的明显采样间隙不当作卡死。日志轮转，不包含窗口标题、对话正文、命令或凭据。等待链权限不足或内核不支持的等待会如实标为不可用/不完整。

## 验证

- Node 110 项测试通过，构建成功。
- 原生检查覆盖独立 owner、非 topmost、显示/隐藏/最小化条件、旧同步管道阻塞复现、新发送/退出立即返回及不留采集进程；还实测了界面心跳停顿和恢复记录。详见 artifacts/independent-window/native-checks.json。
- 本机新窗口状态 ownerWindow=0、windowMode=independent、topmost=false；与 Codex 的 GUI 输入状态独立。采集 connected/runtime/auto，readErrors=0。
- 用户确认新版 Codex 和浮窗两边都能正常点击。
- 当时保留 Codex 主进程、启动时间及原启动脚本。相关修复现已纳入 v0.5.0 发行包。

这次短期恢复与验证不等于已完成长时间稳定性测试。若再次发生，先读取 ui-health.jsonl 并取得仍存活的监测进程线程栈，再终止进程。
