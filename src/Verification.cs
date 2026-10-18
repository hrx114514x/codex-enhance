using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace CodexEnhance;
public static class Verification
{
    public static void Run(string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        var now = DateTimeOffset.UtcNow; var d = new Disclosure(false);
        d.Observe(Array.Empty<string>(), now); Check(!d.Expanded && !d.ToolsExpanded, "normal does not force expand");
        d.Observe(new[] { "a" }, now); Check(d.Expanded && d.ToolsExpanded && !d.UserExpanded, "new attention temporarily expands both layers");
        d.SetExpanded(false); d.Observe(new[] { "a" }, now); Check(!d.Expanded, "same attention does not reopen after dismissal");
        d.Observe(new[] { "a", "b" }, now); Check(d.Expanded && d.ToolsExpanded, "new distinct attention can be shown");
        d.Observe(Array.Empty<string>(), now); d.Observe(Array.Empty<string>(), now.AddSeconds(5)); Check(!d.Expanded && !d.ToolsExpanded, "recovery restores prior user state");
        d.SetExpanded(true); d.ToggleTools(); d.Observe(Array.Empty<string>(), now.AddSeconds(10)); Check(d.Expanded && d.ToolsExpanded, "manual expansion survives normal state");
        var escalation = new Disclosure(false); escalation.Observe(new[] { "call:warning" }, now); escalation.SetExpanded(false);
        escalation.Observe(new[] { "call:critical" }, now.AddSeconds(1)); Check(escalation.Expanded, "severity escalation is a new attention event");
        escalation.SetExpanded(false); escalation.Observe(new[] { "call:critical" }, now.AddSeconds(2)); Check(!escalation.Expanded, "dismissed critical does not repeatedly reopen");
        var settings = new Settings { Expanded = true, ManualThreadId = "test", TopOffset = 155.5, RightOffset = 18, FollowMode = false };
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Settings.JsonOptions), Settings.JsonOptions)!;
        Check(restored.Expanded && restored.TopOffset == 155.5 && restored.ManualThreadId == "test" && !restored.FollowMode, "settings round trip preserves selection and geometry");
        var oldSettings = JsonSerializer.Deserialize<Settings>("{}", Settings.JsonOptions)!;
        Check(!oldSettings.QuotaIncludeAstraLongContext && oldSettings.QuotaNormalizeFast, "quota migration defaults Astra off and Fast on");
        settings.QuotaIncludeAstraLongContext = true; settings.QuotaNormalizeFast = false;
        restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Settings.JsonOptions), Settings.JsonOptions)!;
        Check(restored.QuotaIncludeAstraLongContext && !restored.QuotaNormalizeFast, "quota switches survive settings round trip");
        var previousStateDirectory = Environment.GetEnvironmentVariable("CODEX_ENHANCE_STATE_DIR");
        try {
            Environment.SetEnvironmentVariable("CODEX_ENHANCE_STATE_DIR", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "verification-settings"));
            settings.Save(); var diskSettings = Settings.Load();
            Check(diskSettings.QuotaIncludeAstraLongContext && !diskSettings.QuotaNormalizeFast && diskSettings.TopOffset==155.5, "quota switches persist on disk without changing other preferences");
        } finally { Environment.SetEnvironmentVariable("CODEX_ENHANCE_STATE_DIR", previousStateDirectory); }
        var quota = JsonNode.Parse("""{"usedPercent":10,"quotaBaseUsd":0.53,"astraPremiumUsd":0.505,"quotaFastPremiumUsd":0.795,"astraFastPremiumUsd":0.7575,"estimateReasons":[]}""")!;
        foreach (var (astra, fast, expected) in new[] { (false,false,.53), (true,false,1.035), (false,true,1.325), (true,true,2.5875) }) {
            var f = MainWindow.QuotaFigures(quota,astra,fast,false);
            Check(Math.Abs(f.Used!.Value-expected)<1e-9 && Math.Abs(f.Total!.Value-expected*10)<1e-9 && Math.Abs(f.Remaining!.Value-expected*9)<1e-9, $"quota UI arithmetic Astra={astra} Fast={fast}");
        }
        Check(MainWindow.QuotaFigures(quota,false,true,true).Total is null, "quota UI withholds partial indexing projection");
        quota["estimateReasons"] = new JsonArray("parse_errors");
        Check(MainWindow.QuotaFigures(quota,false,true,false).Total is null, "quota UI withholds incomplete current-window projection");
        var secret = JsonNode.Parse("""{"title":"PRIVATE","threadId":"PRIVATE","context":{"used":100,"path":"PRIVATE"},"tools":{"items":[{"name":"PRIVATE","command":"PRIVATE","detail":"PRIVATE","severity":"error","exitCode":1,"durationMs":12}]},"connection":{"cdp":"connected","runtime":true,"message":"PRIVATE"}}""")!.AsObject();
        string report = DiagnosticReport.Create(secret).ToJsonString();
        Check(!report.Contains("PRIVATE") && report.Contains("exitCode") && report.Contains("100"), "diagnostic export contains allowed metrics without private snapshot fields");
        var identity=JsonNode.Parse("""{"model":"gpt-5.6-sol","modelIdentity":{"response":"gpt-6-sol","state":"different"}}""")!.AsObject();
        Check(MainWindow.CurrentModelText(identity)=="GPT-6 Sol","main footer prefers upstream response model including generation");
        identity["modelIdentity"]!["response"]=null;identity["modelIdentity"]!["routedModel"]="gpt-6-astra";
        Check(MainWindow.CurrentModelText(identity)=="GPT-5.6 Sol","main footer falls back to task model without treating routing as a completed response");
        identity["modelIdentity"]!["requested"]="gpt-6-sol";identity["model"]="gpt-6-astra";
        Check(MainWindow.CurrentModelText(identity)=="GPT-6 Sol","active turn request takes priority over the picker for the next turn");
        var activity = JsonNode.Parse("""{"activity":{"kind":"model","startedAtMs":10000,"observedAtMs":20000,"items":[]}}""")!.AsObject();
        var modelPhase = MainWindow.ActivityPresentation(activity,20000,false);
        Check(modelPhase.Title=="思考 / 响应"&&modelPhase.Timer=="00:10", "current model phase displays its own duration");
        activity["activity"] = JsonNode.Parse("""{"kind":"tools","startedAtMs":19000,"observedAtMs":20000,"items":[{"label":"终端执行"},{"label":"网页检索"}]}""");
        var toolPhase=MainWindow.ActivityPresentation(activity,20000,false);
        Check(toolPhase.Title=="调用工具"&&toolPhase.Timer=="00:01", "switching to tools resets the displayed stage duration");
        Check(MainWindow.ActivityPresentation(activity,22000,false).Detail.Contains("网页检索"), "parallel tool labels rotate to the next active item");
        activity["activity"]!["runningCount"]=20;
        Check(MainWindow.ActivityPresentation(activity,20000,false).Detail.StartsWith("20 项并行"), "parallel count uses the full running count rather than the truncated display list");
        Check(!MainWindow.ActivityPresentation(activity,30000,false).Active, "outdated observations cannot keep a live stage clock");
        Check(MainWindow.ActivityPresentation(activity,20000,true).Timer=="—", "stale collector hides the stage clock");
        activity["activity"] = new JsonObject { ["kind"]="complete" };
        Check(!MainWindow.ActivityPresentation(activity,30000,false).Active&&MainWindow.ActivityPresentation(activity,30000,false).Timer=="—", "completed tasks do not show accumulated phase time");
        var owner = new Window { Width = 200, Height = 100, ShowInTaskbar = false, ShowActivated = false };
        var attached = new Window { Width = 100, Height = 80, ShowInTaskbar = false, ShowActivated = false };
        var ownerHandle = new WindowInteropHelper(owner).EnsureHandle(); var attachedHandle = new WindowInteropHelper(attached).EnsureHandle();
        Native.MakeIndependent(attachedHandle);
        Check(Native.GetWindow(attachedHandle, 4) != ownerHandle, "overlay does not attach its input queue to the host owner");
        Check((Native.GetWindowLongPtr(attachedHandle, -20).ToInt64() & 8) == 0, "independent overlay is not globally topmost");
        owner.Show(); attached.Show();
        Check(Native.GetWindow(attachedHandle, 4) != ownerHandle, "WPF Show does not attach a host owner");
        attached.Hide(); attached.Show();
        Check(Native.GetWindow(attachedHandle, 4) != ownerHandle, "WPF hide/show stays independent of the host");
        Check(Native.ShouldShowNearHost(ownerHandle, false), "visible restored host allows independent overlay visibility");
        Check(!Native.ShouldShowNearHost(ownerHandle, true), "user can hide the independent overlay");
        var other = new Window { Width = 200, Height = 100, ShowInTaskbar = false, ShowActivated = false }; other.Show(); other.Activate();
        Check(Native.IsWindowVisible(attachedHandle), "switching to another window keeps the independent overlay available");
        owner.WindowState = WindowState.Minimized;
        Check(Native.IsWindowVisible(attachedHandle), "host minimization cannot change an independent window through ownership");
        Check(!Native.ShouldShowNearHost(ownerHandle, false), "visibility polling hides the overlay when host is minimized");
        owner.Hide(); Check(!Native.ShouldShowNearHost(ownerHandle, false), "visibility polling hides the overlay when host is hidden");
        other.Close();
        attached.Close(); owner.Close();
        {
            var node = Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe");
            var fixture = new ProcessStartInfo(File.Exists(node) ? node : "node.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            fixture.ArgumentList.Add("-e"); fixture.ArgumentList.Add("setInterval(()=>{},1000)");
            using var legacy = Process.Start(fixture)!;
            var blockedWrite = Task.Run(() => {
                try { legacy.StandardInput.WriteLine(new string('x', 1024 * 1024)); legacy.StandardInput.Flush(); } catch { }
            });
            try { Check(!blockedWrite.Wait(250), "legacy synchronous pipe write reproduces a blocked caller when collector does not read"); }
            finally { if (!legacy.HasExited) legacy.Kill(entireProcessTree: true); legacy.WaitForExit(); blockedWrite.Wait(2000); }
        }
        var waitChain = JsonNode.Parse(JsonSerializer.Serialize(WaitChainProbe.Read(Native.GetCurrentThreadId())))!;
        Check(waitChain["available"] is not null, "wait-chain diagnostics safely report availability");
        if (waitChain["available"]!.GetValue<bool>())
            Check(waitChain["nodes"]![0]!["processId"]!.GetValue<int>() == Environment.ProcessId && waitChain["nodes"]![0]!["threadId"]!.GetValue<uint>() == Native.GetCurrentThreadId(), "wait-chain native layout returns the current process and GUI thread");
        using (var client = new CollectorClient()) {
            var node = Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe");
            var fixture = new ProcessStartInfo(File.Exists(node) ? node : "node.exe");
            fixture.ArgumentList.Add("-e"); fixture.ArgumentList.Add("setInterval(()=>{},1000)");
            var child = client.StartProcess(fixture);
            using var observer = Process.GetProcessById(child.Id);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 12; i++) client.Send(new JsonObject { ["type"] = "blocked-fixture", ["payload"] = new string('x', 65536) });
            Check(watch.ElapsedMilliseconds < 500, "sending into a non-reading collector never waits for pipe backpressure on the caller");
            watch.Restart(); var stopped = client.StopAsync();
            Check(watch.ElapsedMilliseconds < 200, "collector disposal returns without waiting on the caller");
            Check(stopped.Wait(TimeSpan.FromSeconds(6)), "unresponsive collector cleanup has a bounded deadline");
            Check(observer.WaitForExit(1000), "unresponsive collector process is terminated without leaving an orphan");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var healthDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "verification-health-" + Environment.ProcessId);
        var priorHealthDirectory = Environment.GetEnvironmentVariable("CODEX_ENHANCE_STATE_DIR");
        try {
            Environment.SetEnvironmentVariable("CODEX_ENHANCE_STATE_DIR", healthDirectory);
            using var health = new UiHealth(); health.Enter("verification:blocked-ui");
            System.Threading.Thread.Sleep(6200);
            var healthPath = Path.Combine(healthDirectory, "ui-health.jsonl");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(healthPath) && deadline.ElapsedMilliseconds < 3000) System.Threading.Thread.Sleep(50);
            Check(File.Exists(healthPath) && File.ReadAllText(healthPath).Contains("verification:blocked-ui") && File.ReadAllText(healthPath).Contains("waitChain"), "background watchdog records an unresponsive GUI stage and wait-chain availability");
            health.Beat(); System.Threading.Thread.Sleep(1300);
            Check(File.ReadAllText(healthPath).Contains("recovered"), "watchdog records recovery after the GUI heartbeat resumes");
        } finally { Environment.SetEnvironmentVariable("CODEX_ENHANCE_STATE_DIR", priorHealthDirectory); }
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = checks.Count, checks, waitChain }, Settings.JsonOptions));
    }
}
