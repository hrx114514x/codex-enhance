using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Text;

namespace CodexEnhance;
public sealed class CollectorClient : IDisposable
{
    private Process? process;
    private int stopping;
    private readonly Channel<string> commands = Channel.CreateBounded<string>(new BoundedChannelOptions(64) {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource cancellation = new();
    private readonly object stopLock = new();
    private Task? shutdownTask;
    public event Action<JsonObject>? Snapshot;
    public event Action<string>? Error;
    public bool IsRunning { get { try { return Volatile.Read(ref stopping) == 0 && process is { HasExited: false }; } catch { return false; } } }
    public void Start(Settings settings, string? seed)
    {
        var node = Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe");
        if (!File.Exists(node)) node = settings.NodePath ?? "node.exe";
        var start = new ProcessStartInfo(node) { WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--no-warnings"); start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "collector", "main.mjs"));
        start.ArgumentList.Add("--state-dir"); start.ArgumentList.Add(Settings.StateDirectory);
        if (!settings.NetworkProbeEnabled) start.ArgumentList.Add("--network-paused");
        if (seed is not null) { start.ArgumentList.Add("--thread"); start.ArgumentList.Add(seed); }
        StartProcess(start);
    }
    internal Process StartProcess(ProcessStartInfo start)
    {
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = start.StandardErrorEncoding = Encoding.UTF8;
        start.StandardInputEncoding = new UTF8Encoding(false);
        var child = Process.Start(start) ?? throw new InvalidOperationException("采集进程未能启动");
        process = child;
        child.EnableRaisingEvents = true;
        child.Exited += (_, _) => ReportError("采集进程已退出，请从托盘重新连接");
        _ = Task.Run(() => ReadSnapshots(child));
        _ = Task.Run(() => ReadErrors(child));
        _ = Task.Run(async () => {
            try {
                await foreach (var command in commands.Reader.ReadAllAsync(cancellation.Token)) {
                    await child.StandardInput.WriteLineAsync(command.AsMemory(), cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));
                    await child.StandardInput.FlushAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));
                }
                child.StandardInput.Close();
            } catch (Exception e) {
                ReportError("采集命令通道已断开或超时：" + e.GetType().Name);
                Dispose();
            }
        });
        return child;
    }
    private async Task ReadSnapshots(Process child)
    {
        try {
            while (Volatile.Read(ref stopping) == 0 && await child.StandardOutput.ReadLineAsync(cancellation.Token) is { } line) {
                try { if (JsonNode.Parse(line) is JsonObject data) Snapshot?.Invoke(data); }
                catch (Exception e) { ReportError("采集结果读取失败：" + e.Message); }
            }
        } catch (Exception e) { ReportError("采集通道已断开：" + e.GetType().Name); }
    }
    private async Task ReadErrors(Process child)
    {
        try {
            while (Volatile.Read(ref stopping) == 0 && await child.StandardError.ReadLineAsync(cancellation.Token) is { } line)
                if (!line.Contains("ExperimentalWarning")) ReportError(line.Length > 150 ? line[..150] : line);
        } catch (Exception e) { ReportError("采集错误通道已断开：" + e.GetType().Name); }
    }
    private void ReportError(string message) { if (Volatile.Read(ref stopping) == 0) Error?.Invoke(message); }
    public void Send(JsonObject command)
    {
        if (!IsRunning) return;
        // Pipe writes must never run on WPF's dispatcher, including reconnect/exit.
        if (!commands.Writer.TryWrite(command.ToJsonString())) ReportError("采集命令队列已满，请重新连接");
    }
    public void Dispose()
    {
        lock (stopLock) {
        if (stopping != 0) return;
        Volatile.Write(ref stopping, 1);
        commands.Writer.TryWrite("{\"type\":\"stop\"}");
        commands.Writer.TryComplete();
        var child = process;
        shutdownTask = Task.Run(async () => {
            try {
                if (child is not null) {
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(1500)); }
                    catch (TimeoutException) { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                }
            } catch { }
            finally { cancellation.Cancel(); child?.Dispose(); }
        });
        }
    }
    internal Task StopAsync() { Dispose(); lock (stopLock) return shutdownTask ?? Task.CompletedTask; }
}
