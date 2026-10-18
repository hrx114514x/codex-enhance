using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace CodexEnhance;
public partial class MainWindow
{
    private void UpdateFreshness()
    {
        double age = preview ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - lastSnapshotReceived).TotalMilliseconds);
        bool stale = collectorFailed || age > 6000;
        bool connected = S(snapshot["connection"]?["cdp"]) == "connected";
        FreshnessText.Text = preview ? "预览" : stale ? "更新延迟" : B(snapshot["historyLoading"]) ? "读取中" : connected && B(snapshot["connection"]?["runtime"]) ? "已连接" : connected ? "本地记录" : "未连接";
        FreshnessText.ToolTip = preview ? "示例数据" : $"{Math.Floor(age / 1000):0} 秒前更新";
        UpdateActivity(stale);
        if(stale) { NetworkSummary.Text="采集已过期";NetworkSummary.Foreground=BrushFor("Faint");NetworkCaption.Text="等待新的网络与响应记录"; }
        if (stale) { PhaseText.Text = "状态待更新"; PillPhase.Text = "状态待更新"; PhaseDot.Fill = PillDot.Fill = BrushFor("Faint"); }
    }

    private StackPanel DetailStack() => new() { Margin = new Thickness(24, 0, 24, 24) };
    private void Section(StackPanel stack, string title)
    {
        var label = Text(title, "Text", 13); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0, 23, 0, 11); stack.Children.Add(label);
    }
    private Action<string> MetricRow(StackPanel stack, string title)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        var label = Text(title, "Muted", 12); label.Margin = new Thickness(0, 0, 12, 0); grid.Children.Add(label);
        var value = Text("—", "Text", 12); value.TextAlignment = TextAlignment.Right; Grid.SetColumn(value, 1); grid.Children.Add(value); stack.Children.Add(new Border { Background=BrushFor("Panel"),CornerRadius=new CornerRadius(8),Padding=new Thickness(12,10,12,10),Margin=new Thickness(0,2,0,2),Child=grid });
        return text => value.Text = text;
    }
    private void Note(StackPanel stack, string text)
    { var label = Text(text, "Faint", 11); label.Margin = new Thickness(0, 12, 0, 0); label.LineHeight=17; stack.Children.Add(label); }

    private void OpenMetrics(object sender, RoutedEventArgs e)
    {
        var stack = DetailStack();
        Section(stack, "本轮进度");
        var phase = MetricRow(stack, "当前阶段"); var elapsed = MetricRow(stack, "本轮耗时");
        var progress = MetricRow(stack, "距可见进度变化"); var log = MetricRow(stack, "距最新日志");
        Section(stack, "本轮耗时拆解");
        var breakdown = new StackPanel(); stack.Children.Add(breakdown);
        Section(stack, "首字与近期表现");
        var liveOutput = MetricRow(stack, "本轮输出观测");
        var ttft = MetricRow(stack, "首字 · 本轮日志"); var baseline = MetricRow(stack, "同模型 / 强度中位数");
        var trend = new Grid { Height = 80, Margin = new Thickness(0, 10, 0, 0) }; stack.Children.Add(trend);
        var trendNote = Text("", "Faint", 11); trendNote.Margin = new Thickness(0, 7, 0, 0); stack.Children.Add(trendNote);
        Section(stack, "上下文与缓存");
        var context = MetricRow(stack, "上下文 · 最近采样"); var sampled = MetricRow(stack, "上下文采样时间");
        var cache = MetricRow(stack, "缓存命中 · 最近请求"); var total = MetricRow(stack, "累计 token");
        Section(stack, "上下文压缩");
        var compact = MetricRow(stack, "次数 / 最近耗时"); var saved = MetricRow(stack, "最近压缩前 → 后");
        var hints = Text("", "Muted", 12); hints.Margin = new Thickness(0, 15, 0, 0); stack.Children.Add(hints);
        Note(stack, "趋势仅比较相同模型与思考强度，数值越低表示首字等待越短。");
        Detail("Codex · 性能详情", new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        string trendKey = "", breakdownKey = "";
        refreshDetail = () =>
        {
            var p = snapshot["performance"];
            string timingKey=p?["breakdown"]?.ToJsonString()??"";
            if (timingKey!=breakdownKey || breakdown.Children.Count==0) {
                breakdownKey=timingKey; breakdown.Children.Clear();
                RenderBreakdown(breakdown,p?["breakdown"]);
            }
            phase(PhaseText.Text); elapsed(Duration(N(snapshot["elapsedMs"]))); progress(Duration(N(p?["progressGapMs"]))); log(Duration(N(p?["logGapMs"])));
            ttft(Duration(N(snapshot["ttftMs"]))); baseline($"{Duration(N(p?["baselineMs"]))} · {N(p?["baselineSamples"]) ?? 0} 轮");
            liveOutput(S(snapshot["firstOutput"]?["state"]) == "observed" ? "≈" + Duration(N(snapshot["firstOutput"]?["ms"])) + " · 首条回复" : FirstTokenText.Text);
            context($"{N(snapshot["context"]?["used"])?.ToString("N0") ?? "—"} / {N(snapshot["context"]?["limit"])?.ToString("N0") ?? "—"}");
            sampled(LocalTime(N(snapshot["context"]?["sampledAtMs"]))); cache(CacheText.Text); total(N(snapshot["totalTokens"])?.ToString("N0") ?? "—");
            compact($"{N(snapshot["compactions"]) ?? 0} / {Duration(N(snapshot["lastCompaction"]?["durationMs"]))}");
            saved($"{N(snapshot["lastCompaction"]?["before"])?.ToString("N0") ?? "—"} → {N(snapshot["lastCompaction"]?["after"])?.ToString("N0") ?? "—"}");
            hints.Text = string.Join("\n\n", (p?["hints"] as JsonArray ?? new JsonArray()).Select(h => S(h)));
            string key = p?["recentTimings"]?.ToJsonString() ?? "[]";
            if (key != trendKey)
            {
                trendKey = key; trend.Children.Clear(); trend.ColumnDefinitions.Clear();
                var values = (p?["recentTimings"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
                trendNote.Text = values.Length == 0 ? "尚无可比较的首字记录" : $"最近 {values.Length} 轮 · 从左到右由早到晚 · 悬停查看数值";
                trend.Visibility = values.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                double max = Math.Max(1, values.Select(v => N(v["ttftMs"]) ?? 0).DefaultIfEmpty(1).Max());
                for (int i = 0; i < values.Length; i++)
                {
                    trend.ColumnDefinitions.Add(new ColumnDefinition());
                    var bar = new Border { Background = BrushFor("Accent"), Opacity = i == values.Length - 1 ? 1 : .5, CornerRadius = new CornerRadius(3), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(3, 72 * (N(values[i]["ttftMs"]) ?? 0) / max), ToolTip = Duration(N(values[i]["ttftMs"])) + " · " + LocalTime(N(values[i]["completedAtMs"])) };
                    Grid.SetColumn(bar, i); trend.Children.Add(bar);
                }
            }
        };
        refreshDetail();
    }

    private static string LocalTime(double? value) => value is null or <= 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value).ToLocalTime().ToString("MM-dd HH:mm:ss");

    private void OpenHistory(object sender, RoutedEventArgs e)
    {
        var layout = new DockPanel { Margin = new Thickness(22, 10, 22, 20) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        var filters = new StackPanel { Orientation = Orientation.Horizontal }; top.Children.Add(filters);
        var summary = Text("", "Faint", 11); summary.Margin = new Thickness(0, 12, 0, 14); top.Children.Add(summary);
        var entries = new StackPanel(); layout.Children.Add(new ScrollViewer { Content = entries, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        string selected = "本轮", last = ""; var buttons = new List<Button>();
        foreach (string label in new[] { "本轮", "需关注", "最近记录" })
        {
            var b = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 6, 0), FontSize = 12 };
            b.Click += (_, _) => { selected = label; last = ""; refreshDetail?.Invoke(); }; filters.Children.Add(b); buttons.Add(b);
        }
        Detail("Codex · 调用记录", layout);
        refreshDetail = () =>
        {
            var all = (snapshot["tools"]?["items"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
            var items = all.Where(t => selected == "最近记录" || S(t["turnId"]) == S(snapshot["turnId"]))
                .Where(t => selected != "需关注" || (S(t["severity"]) is "warning" or "error" or "critical") && !B(t["resolved"])).ToArray();
            string key = selected + string.Join("", items.Select(t => t.ToJsonString()));
            if (key == last) return; last = key;
            foreach (var b in buttons) { b.Background = BrushFor((string)b.Content == selected ? "Hover" : "Surface"); b.Foreground = BrushFor((string)b.Content == selected ? "Accent" : "Muted"); }
            summary.Text = $"{items.Length} 项 · 自动更新 · 仅 Critical 主动展开";
            entries.Children.Clear();
            if (items.Length == 0) { entries.Children.Add(Text(selected == "需关注" ? "本轮暂无需要关注的记录" : "暂无调用记录", "Muted")); return; }
            foreach (var item in items)
            {
                entries.Children.Add(ToolView(item, (S(item["severity"]) is "warning" or "error" or "critical") && !B(item["resolved"])));
                entries.Children.Add(new Border { Height = 1, Background = BrushFor("Line"), Margin = new Thickness(0, 0, 0, 12) });
            }
        };
        refreshDetail();
    }

    private void OpenHealth(object sender, RoutedEventArgs e)
    {
        var stack = DetailStack(); Section(stack, "采集状态");
        var link = MetricRow(stack, "Codex 连接"); var runtime = MetricRow(stack, "实时状态"); var age = MetricRow(stack, "距采集更新");
        var history = MetricRow(stack, "历史读取"); var errors = MetricRow(stack, "读取错误");
        Section(stack, "显示与跟随");
        MetricRow(stack, "显示方式")("独立浮窗 · 跟随位置和最小化");
        var selection = MetricRow(stack, "观察对象");
        Note(stack, "浮窗随 Codex 窗口显示。“跟随对话”开启时，观察对象随页面切换。");
        Section(stack, "本地诊断");
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var reconnect = new Button { Content = "重新连接", Padding = new Thickness(12, 8, 12, 8), Background = BrushFor("Hover") };
        reconnect.Click += (_, _) => { if (!preview) StartCollector(); };
        var export = new Button { Content = "导出诊断摘要", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 0, 0, 0), Background = BrushFor("Hover") };
        actions.Children.Add(reconnect); actions.Children.Add(export); stack.Children.Add(actions);
        var feedback = Text("只导出指标和状态；不含对话正文、标题、命令、路径或账号凭据。", "Faint", 11); feedback.Margin = new Thickness(0, 12, 0, 0); stack.Children.Add(feedback);
        export.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { FileName = "Codex-诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", Filter = "JSON 诊断摘要|*.json", DefaultExt = ".json" };
            if (dialog.ShowDialog(detailWindow) != true) return;
            try { File.WriteAllText(dialog.FileName, DiagnosticReport.Create(snapshot).ToJsonString(Settings.JsonOptions)); feedback.Text = "已保存诊断摘要。"; }
            catch (Exception) { feedback.Text = "保存失败，请选择一个可写入的位置。"; }
        };
        Detail("Codex · 连接与诊断", new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        refreshDetail = () =>
        {
            link(S(snapshot["connection"]?["cdp"]) == "connected" ? "已连接" : "未连接");
            runtime(B(snapshot["connection"]?["runtime"]) ? "可读取" : "暂不可读");
            age(preview ? "示例数据" : Duration(Math.Max(0, (DateTimeOffset.UtcNow - lastSnapshotReceived).TotalMilliseconds)));
            history(B(snapshot["historyLoading"]) ? "正在读取" : "已追平"); errors((N(snapshot["readErrors"]) ?? 0).ToString("0")); selection(FollowText.Text);
        };
        refreshDetail();
    }
}
