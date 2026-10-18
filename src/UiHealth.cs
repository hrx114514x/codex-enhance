using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CodexEnhance;
internal static class DiagnosticFiles
{
    private static readonly Channel<(string Path, string Text, bool Append)> files = Channel.CreateBounded<(string, string, bool)>(new BoundedChannelOptions(16) {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false
    });
    static DiagnosticFiles()
    {
        _ = Task.Run(async () => {
            await foreach (var file in files.Reader.ReadAllAsync()) {
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                    if (file.Append) {
                        if (File.Exists(file.Path) && new FileInfo(file.Path).Length > 1024 * 1024) File.Move(file.Path, file.Path + ".previous", true);
                        await File.AppendAllTextAsync(file.Path, file.Text + Environment.NewLine);
                    } else await File.WriteAllTextAsync(file.Path, file.Text);
                } catch { }
            }
        });
    }
    internal static void Write(string name, string text, bool append = false) => files.Writer.TryWrite((Path.Combine(Settings.StateDirectory, name), text, append));
}
internal sealed class UiHealth : IDisposable
{
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private readonly uint threadId = GetCurrentThreadId();
    private readonly Timer timer;
    private long pulse = Stopwatch.GetTimestamp(), lastCheck = Stopwatch.GetTimestamp();
    private long lastReport;
    private string stage = "startup";
    private bool reported;
    private int checking;
    internal UiHealth() => timer = new Timer(Check, null, 1000, 1000);
    internal void Enter(string value) => Volatile.Write(ref stage, value);
    internal void Beat() { Volatile.Write(ref pulse, Stopwatch.GetTimestamp()); Enter("idle"); }
    private void Check(object? state)
    {
        if (Interlocked.Exchange(ref checking, 1) != 0) return;
        try {
        long now = Stopwatch.GetTimestamp();
        // Do not call a system sleep/resume gap an application hang.
        if (Stopwatch.GetElapsedTime(Interlocked.Exchange(ref lastCheck, now), now).TotalSeconds > 3) {
            Volatile.Write(ref pulse, now); reported = false; return;
        }
        double gap = Stopwatch.GetElapsedTime(Volatile.Read(ref pulse), now).TotalMilliseconds;
        if (gap < 5000) {
            if (reported) Write("recovered", gap);
            reported = false; return;
        }
        if (Stopwatch.GetElapsedTime(lastReport, now).TotalSeconds < 15) return;
        lastReport = now; reported = true; Write("ui_unresponsive", gap);
        } finally { Volatile.Write(ref checking, 0); }
    }
    private void Write(string kind, double gap) => DiagnosticFiles.Write("ui-health.jsonl", JsonSerializer.Serialize(new {
        kind, sampledAt = DateTimeOffset.UtcNow, processId = Environment.ProcessId, threadId,
        stage = Volatile.Read(ref stage), heartbeatGapMs = Math.Round(gap), windowMode = "independent",
        waitChain = kind == "ui_unresponsive" ? WaitChainProbe.Read(threadId) : null
    }), append: true);
    public void Dispose() => timer.Dispose();
}
internal static class WaitChainProbe
{
    [StructLayout(LayoutKind.Explicit, Size = 280)]
    private struct Node {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Status;
        [FieldOffset(8)] public uint ProcessId;
        [FieldOffset(12)] public uint ThreadId;
    }
    [DllImport("advapi32.dll")] private static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);
    [DllImport("advapi32.dll")] private static extern void CloseThreadWaitChainSession(IntPtr session);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetThreadWaitChain(IntPtr session, IntPtr context, uint flags, uint threadId, ref uint count, [Out] Node[] nodes, [MarshalAs(UnmanagedType.Bool)] out bool cycle);
    internal static object Read(uint threadId)
    {
        var session = OpenThreadWaitChainSession(0, IntPtr.Zero);
        if (session == IntPtr.Zero) return new { available = false };
        try {
            uint count = 16; var nodes = new Node[16];
            if (!GetThreadWaitChain(session, IntPtr.Zero, 0, threadId, ref count, nodes, out bool cycle))
                return new { available = false, error = Marshal.GetLastWin32Error() };
            var summary = new object[Math.Min(count, 16)];
            for (int i = 0; i < summary.Length; i++) summary[i] = new {
                objectType = nodes[i].Type, status = nodes[i].Status,
                processId = nodes[i].Type == 8 ? nodes[i].ProcessId : (uint?)null,
                threadId = nodes[i].Type == 8 ? nodes[i].ThreadId : (uint?)null
            };
            return new { available = true, cycle, nodes = summary };
        } finally { CloseThreadWaitChainSession(session); }
    }
}
