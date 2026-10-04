using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexEnhance;
public partial class MainWindow
{
    // Export the actual WPF views with synthetic data only. Never read live state.
    private void RenderGallery(string directory)
    {
        if (!preview || !renderMode) throw new InvalidOperationException("Gallery requires render mode.");
        Directory.CreateDirectory(directory);
        bool limitedQueryWorks=HostProcessIdentity.ImagePath((uint)Environment.ProcessId)?.EndsWith("CodexEnhance.exe",StringComparison.OrdinalIgnoreCase)==true;
        bool hostIdentityScoped=HostProcessIdentity.IsCodexPath(@"C:\Program Files\WindowsApps\OpenAI.Codex_1_x64__test\app\ChatGPT.exe")&&
            !HostProcessIdentity.IsCodexPath(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1_x64__test\app\ChatGPT.exe")&&
            !HostProcessIdentity.IsCodexPath(@"C:\Program Files\WindowsApps\OpenAI.Codex_1_x64__test\app\resources\codex.exe");
        File.WriteAllText(Path.Combine(directory,"host-identity-check.json"),System.Text.Json.JsonSerializer.Serialize(new {limitedQueryWorks,hostIdentityScoped,passed=limitedQueryWorks&&hostIdentityScoped},Settings.JsonOptions));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var data = Demo(false);
        data["title"] = "示例任务 · 优化项目界面";
        data["outputSpeed"]=new JsonObject { ["state"]="measured",["tokensPerSecond"]=42.5,["outputTokens"]=1700,["reasoningTokens"]=1200,["durationMs"]=40000,["recordedAtMs"]=now,["approximate"]=true };
        var speedSamples=new JsonArray();var rates=new[]{38.2,41.8,35.4,44.1,42.5};
        for(int i=0;i<rates.Length;i++)speedSamples.Add(new JsonObject { ["state"]="measured",["tokensPerSecond"]=rates[i],["recordedAtMs"]=now-(rates.Length-1-i)*60000L });
        data["outputSpeed"]!["samples"]=speedSamples;
        data["connection"]!["message"] = "预览 · 示例数据";
        data["quota"] = JsonNode.Parse("""
          {"state":"ready","plan":"pro","pricingDate":"2026-09-27","indexing":false,"windows":[{"minutes":10080,"usedPercent":24,"remainingPercent":76,"quotaBaseUsd":60,"quotaFastPremiumUsd":24,"astraPremiumUsd":8,"astraFastPremiumUsd":3,"usd":102,"requests":128,"tokens":18200000,"estimateReasons":[]}]}
          """);
        data["quota"]!["checkedAtMs"] = now;
        data["quota"]!["windows"]![0]!["fastRequests"]=16;
        data["voice"]=new JsonObject { ["state"]="observed",["reportedPhase"]="active",["checkedAtMs"]=now,["billingVerified"]=false };
        data["quota"]!["windows"]![0]!["startMs"] = now - 3 * 86400000L;
        data["quota"]!["windows"]![0]!["resetsAtMs"] = now + 4 * 86400000L;
        data["performance"]!["breakdown"] = JsonNode.Parse("""{"state":"available","totalMs":86000,"classifiedMs":85000,"modelObserved":true,"missingToolTimings":0,"segments":[{"key":"model","label":"思考/响应（估算）","ms":50000},{"key":"tools","label":"工具调用","ms":20000},{"key":"waiting","label":"等待你操作","ms":11000},{"key":"compacting","label":"上下文整理","ms":4000},{"key":"unknown","label":"其他／未分类","ms":1000}]}""");
        var historyRows=new JsonArray();
        for(int i=0;i<3;i++) {
            var row=data["quota"]!["windows"]![0]!.DeepClone();
            row["startMs"]=now-(3+i*7)*86400000L;row["resetsAtMs"]=now+(4-i*7)*86400000L;
            row["checkedAtMs"]=i==0?now:now+(4-i*7)*86400000L-60000;
            row["closed"]=i>0;row["observationGapMs"]=i==0?0:60000;row["fastRequests"]=16;
            row["models"]=JsonNode.Parse("""[{"model":"gpt-6-astra","requests":100,"usd":90},{"model":"gpt-6-sol","requests":28,"usd":12}]""");
            historyRows.Add(row);
        }
        data["quota"]!["history"]=new JsonObject { ["state"]="ready",["entries"]=historyRows };
        data["toolHealth"] = JsonNode.Parse("""
          {"state":"ready","watched":{"state":"listed"},"issues":[],"changes":[],"observations":[],"servers":[{"name":"codex_app","runtimeStatus":"connected","authStatus":"unsupported","catalogComplete":true,"toolCount":3,"tools":["read_thread","list_threads","open_in_codex"]},{"name":"cua_repl","runtimeStatus":"connected","authStatus":"unsupported","catalogComplete":true,"toolCount":2,"tools":["js","js_reset"]}]}
          """);
        data["toolHealth"]!["checkedAtMs"] = now;
        data["modelIdentity"]=new JsonObject { ["selected"]="gpt-5.6-sol",["requested"]="gpt-5.6-sol",["response"]="gpt-6-sol",["source"]="response_model",["state"]="different",["observedAtMs"]=now,["records"]=new JsonArray(),["routes"]=new JsonArray() };
        data["model"]="gpt-5.6-sol";
        settings.Theme = "dark"; ApplyTheme(); disclosure = new Disclosure(true); ApplySnapshot(data);
        Capture(Path.Combine(directory, "overview.png"));
        bool speedVisible=OutputSpeedText.Text=="≈42.5"&&OutputSpeedSource.Text=="最近请求";
        OutputSpeedButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        bool speedOpens=detailWindow?.Title=="Codex · 输出速度";
        CaptureGalleryDetail(Path.Combine(directory,"output-speed.png"));detailWindow?.Close();detailWindow=null;
        var speedPending=data.DeepClone().AsObject();speedPending["outputSpeed"]=new JsonObject { ["state"]="pending" };
        ApplySnapshot(speedPending);bool newTurnClearsSpeed=OutputSpeedText.Text=="—"&&OutputSpeedSource.Text=="等待记录";
        speedPending["outputSpeed"]=new JsonObject { ["state"]="unavailable",["reason"]="missing_timing" };
        ApplySnapshot(speedPending);bool missingTimingClear=OutputSpeedText.Text=="—"&&OutputSpeedSource.Text=="时点未记录";
        ApplySnapshot(data);
        File.WriteAllText(Path.Combine(directory,"output-speed-check.json"),System.Text.Json.JsonSerializer.Serialize(new {speedVisible,speedOpens,newTurnClearsSpeed,missingTimingClear,passed=speedVisible&&speedOpens&&newTurnClearsSpeed&&missingTimingClear},Settings.JsonOptions));
        ModelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        bool modelEntryWorks=detailWindow?.Title=="Codex · 当前模型"&&ModelText.Text=="GPT-6 Sol";
        CaptureGalleryDetail(Path.Combine(directory,"upstream-model.png"));detailWindow?.Close();detailWindow=null;
        var unknownModel=data.DeepClone().AsObject();unknownModel["modelIdentity"]!["response"]=null;unknownModel["modelIdentity"]!["state"]="unavailable";ApplySnapshot(unknownModel);
        bool taskFallback=ModelText.Text=="GPT-5.6 Sol";
        Capture(Path.Combine(directory,"upstream-unknown.png"));ApplySnapshot(data);
        File.WriteAllText(Path.Combine(directory,"model-check.json"),System.Text.Json.JsonSerializer.Serialize(new {modelEntryWorks,taskFallback,passed=modelEntryWorks&&taskFallback},Settings.JsonOptions));
        bool currentToolPhase = PhaseText.Text == "调用工具" && ElapsedText.Text == "00:20";
        ActivityButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        bool timingOpensDetails = detailWindow?.Title == "Codex · 性能详情";
        detailWindow?.Close(); detailWindow = null;
        var model=data.DeepClone().AsObject(); model["activity"]=new JsonObject { ["kind"]="model",["startedAtMs"]=now-7000,["observedAtMs"]=now,["items"]=new JsonArray() };
        model["tools"]!["running"]=0;ApplySnapshot(model);Capture(Path.Combine(directory,"model-phase.png"));
        bool modelResets=PhaseText.Text=="思考 / 响应"&&ElapsedText.Text=="00:07";
        model["activity"]!["observedAtMs"]=now+4000;ApplySnapshot(model);bool ticks=ElapsedText.Text=="00:11";
        var completed=data.DeepClone().AsObject();completed["phase"]="idle";completed["activity"]=new JsonObject { ["kind"]="complete" };
        completed["tools"]!["running"]=0;completed["tools"]!["completed"]=2;
        completed["tools"]!["items"]=JsonNode.Parse("""[{"id":"a","turnId":"demo-turn","status":"completed","label":"终端执行"},{"id":"b","turnId":"demo-turn","status":"completed","label":"网页检索"}]""");
        ApplySnapshot(completed);Capture(Path.Combine(directory,"completed.png"));
        bool completedHidesTimer=ElapsedText.Visibility==Visibility.Collapsed&&PhaseText.Text=="已完成";
        ApplySnapshot(data);
        File.WriteAllText(Path.Combine(directory,"activity-check.json"),System.Text.Json.JsonSerializer.Serialize(new {currentToolPhase,modelResets,ticks,completedHidesTimer,timingOpensDetails,passed=currentToolPhase&&modelResets&&ticks&&completedHidesTimer&&timingOpensDetails},Settings.JsonOptions));
        disclosure.SetExpanded(false); UpdateDisclosure(); Capture(Path.Combine(directory, "compact.png"));
        disclosure.SetExpanded(true); UpdateDisclosure();
        OpenQuota(this, new RoutedEventArgs()); CaptureGalleryDetail(Path.Combine(directory, "quota.png"));
        OpenVoice(this,new RoutedEventArgs());
        bool voiceOpens=detailWindow?.Title=="Codex · 语音连接";
        bool mainVoiceRowRemoved=FindName("VoiceButton") is null;
        File.WriteAllText(Path.Combine(directory,"voice-control-check.json"),System.Text.Json.JsonSerializer.Serialize(new {voiceOpens,mainVoiceRowRemoved,passed=voiceOpens&&mainVoiceRowRemoved},Settings.JsonOptions));
        CaptureGalleryDetail(Path.Combine(directory,"voice.png"));
        settings.Theme="light";ApplyTheme();
        var openFrame=(System.Windows.Controls.Border)((System.Windows.Controls.Grid)detailWindow!.Content).Children[1];
        bool openDialogThemeUpdates=((SolidColorBrush)openFrame.Background).Color==Color.FromRgb(252,252,253);
        CaptureGalleryDetail(Path.Combine(directory,"voice-light.png"));
        File.WriteAllText(Path.Combine(directory,"theme-check.json"),System.Text.Json.JsonSerializer.Serialize(new {openDialogThemeUpdates,passed=openDialogThemeUpdates},Settings.JsonOptions));
        settings.Theme="dark";ApplyTheme();
        OpenQuotaHistory(this, new RoutedEventArgs()); CaptureGalleryDetail(Path.Combine(directory, "weekly-history.png"));
        OpenCapabilities(this, new RoutedEventArgs()); CaptureGalleryDetail(Path.Combine(directory, "tools.png"));
        OpenMetrics(this, new RoutedEventArgs()); CaptureGalleryDetail(Path.Combine(directory, "performance.png"));
        detailWindow?.Close(); detailWindow = null;
        var critical = data.DeepClone().AsObject().WithAttention(true, "critical");
        disclosure = new Disclosure(true); ApplySnapshot(critical); Capture(Path.Combine(directory, "critical.png"));
        settings.Theme = "light"; ApplyTheme(); disclosure = new Disclosure(true); ApplySnapshot(data);
        Capture(Path.Combine(directory, "light.png"));
        CaptureScene(Path.Combine(directory,"floating-light.png"));
        ApplySnapshot(completed);Capture(Path.Combine(directory,"completed-light.png"));ApplySnapshot(data);
        OpenMetrics(this,new RoutedEventArgs());CaptureGalleryDetail(Path.Combine(directory,"performance-light.png"));
        OpenOutputSpeed(this,new RoutedEventArgs());CaptureGalleryDetail(Path.Combine(directory,"output-speed-light.png"));
        OpenModels(this,new RoutedEventArgs());CaptureGalleryDetail(Path.Combine(directory,"upstream-model-light.png"));
        OpenQuotaHistory(this,new RoutedEventArgs());CaptureGalleryDetail(Path.Combine(directory,"weekly-history-light.png"));
        detailWindow?.Close();detailWindow=null;
        VerifyFastPresentation(data,directory);
    }
    private static IEnumerable<DependencyObject> GalleryNodes(DependencyObject node)
    {
        yield return node;
        foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach(var descendant in GalleryNodes(child))yield return descendant;
    }
    private void VerifyFastPresentation(JsonObject data,string directory)
    {
        bool savedFast=settings.QuotaNormalizeFast,savedAstra=settings.QuotaIncludeAstraLongContext;
        settings.QuotaNormalizeFast=true;settings.QuotaIncludeAstraLongContext=false;
        string[] OpenDetails(JsonObject sample)
        {
            ApplySnapshot(sample);OpenQuota(this,new RoutedEventArgs());
            var button=GalleryNodes(detailWindow!).OfType<System.Windows.Controls.Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="展开计算明细");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));detailWindow!.UpdateLayout();
            GalleryNodes(detailWindow).OfType<System.Windows.Controls.ScrollViewer>().First().ScrollToBottom();detailWindow.UpdateLayout();
            return GalleryNodes(detailWindow).OfType<System.Windows.Controls.TextBlock>().Select(t=>t.Text).ToArray();
        }
        var noFast=data.DeepClone().AsObject();var w=noFast["quota"]!["windows"]![0]!;
        w["fastRequests"]=0;w["quotaFastPremiumUsd"]=0;w["astraFastPremiumUsd"]=0;
        w["assumedTierRequests"]=20;w["unknownSpeedPremiumUsd"]=500;
        var none=OpenDetails(noFast);
        bool noFastClear=none.Contains("未记录 Fast 用量")&&none.Any(t=>t.Contains("20 条未记录速度"))&&!none.Any(t=>t.Contains("若这些请求"));
        CaptureGalleryDetail(Path.Combine(directory,"quota-no-fast.png"));
        var recorded=OpenDetails(data);bool recordedFastPreserved=recorded.Contains("+$24.00")&&!recorded.Contains("未记录 Fast 用量");
        CaptureGalleryDetail(Path.Combine(directory,"quota-recorded-fast.png"));
        settings.QuotaNormalizeFast=savedFast;settings.QuotaIncludeAstraLongContext=savedAstra;
        detailWindow?.Close();detailWindow=null;ApplySnapshot(data);
        File.WriteAllText(Path.Combine(directory,"fast-display-check.json"),System.Text.Json.JsonSerializer.Serialize(new {noFastClear,recordedFastPreserved,passed=noFastClear&&recordedFastPreserved},Settings.JsonOptions));
    }
    private void CaptureGalleryDetail(string file)
    {
        var window = detailWindow ?? throw new InvalidOperationException("No detail window to capture.");
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        visual.Measure(new Size(window.Width, window.Height));
        visual.Arrange(new Rect(0, 0, window.Width, window.Height)); visual.UpdateLayout();
        const double scale = 2;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); encoder.Save(output);
    }
    private void CaptureScene(string file)
    {
        // Paint the actual native control on a neutral surface so its transparent
        // shadow can be reviewed without the image viewer's black backdrop.
        var scene=new DrawingVisual();
        double w=SurfaceRoot.ActualWidth+40,h=SurfaceRoot.ActualHeight+40;
        using(var context=scene.RenderOpen()) {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(241,243,247)),null,new Rect(0,0,w,h));
            context.DrawRectangle(new VisualBrush(SurfaceRoot),null,new Rect(20,20,SurfaceRoot.ActualWidth,SurfaceRoot.ActualHeight));
        }
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(w*2),(int)Math.Ceiling(h*2),192,192,PixelFormats.Pbgra32);bitmap.Render(scene);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(file);encoder.Save(output);
    }
}
