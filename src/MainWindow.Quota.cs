using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CodexEnhance;
public partial class MainWindow
{
    private static string Money(double? value) => value is null ? "—" : "$" + value.Value.ToString("N2");
    private static string CompactNumber(double? n) => n is null ? "—" : n >= 1e9 ? (n.Value/1e9).ToString("0.00")+"B" : n >= 1e6 ? (n.Value/1e6).ToString("0.0")+"M" : n >= 1e3 ? (n.Value/1e3).ToString("0.0")+"K" : n.Value.ToString("0");
    private void SendQuotaOptions() => collector?.Send(new JsonObject { ["type"] = "quotaOptions", ["options"] = new JsonObject {
        ["includeAstraLongContext"] = settings.QuotaIncludeAstraLongContext, ["normalizeFast"] = settings.QuotaNormalizeFast } });
    // Recalculate cached components immediately; collector publishes the same projection.
    internal static (double? Used, double? Total, double? Remaining, double? UnknownTotal) QuotaFigures(JsonNode w, bool astra, bool fast, bool indexing)
    {
        double? used = N(w["quotaBaseUsd"]);
        if (used is not null) used += (astra ? N(w["astraPremiumUsd"]) ?? 0 : 0) + (fast ? (N(w["quotaFastPremiumUsd"]) ?? 0) + (astra ? N(w["astraFastPremiumUsd"]) ?? 0 : 0) : 0);
        bool blocked = indexing || (w["estimateReasons"] as JsonArray ?? new()).Any(x => S(x) != "speed_weight_unknown" || fast);
        double pct = N(w["usedPercent"]) ?? 0;
        double? total = blocked || pct < 3 ? null : used * 100 / pct;
        double unknown = fast ? (N(w["unknownSpeedPremiumUsd"]) ?? 0) + (astra ? N(w["astraUnknownSpeedPremiumUsd"]) ?? 0 : 0) : 0;
        return (indexing ? null : used, total, total is null ? null : Math.Max(0, (total-used)!.Value), total is null || unknown <= 0 ? null : total + unknown * 100 / pct);
    }
    private void UpdateQuotaSummary()
    {
        UpdateConversationCostSummary();
        var q = snapshot["quota"];
        var week = (q?["windows"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(w => N(w["minutes"]) == 10080);
        var shortWindow = (q?["windows"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(w => N(w["minutes"]) == 300);
        QuotaSummary.Text = S(q?["state"]) == "ready" && week is not null ? "周剩余 " + (N(week["remainingPercent"])?.ToString("0.#") ?? "—") + "%"
            : S(q?["state"]) == "checking" ? "正在读取" : S(q?["reason"]) == "not_subscription" ? "非订阅账号" : "额度待确认";
        if (S(q?["state"]) == "ready" && S(q?["plan"]) == "plus" && shortWindow is not null && week is not null)
            QuotaSummary.Text = "5h余 " + N(shortWindow["remainingPercent"])?.ToString("0.#") + "% · 周余 " + N(week["remainingPercent"])?.ToString("0.#") + "%";
    }
    private string QuotaCountdown(double? reset)
    {
        if (reset is null) return "重置时间未知";
        var seconds = (reset.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000;
        if (seconds <= 0) return "等待额度刷新";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1 ? $"{span.Days} 天 {span.Hours} 小时后重置" : span.TotalHours >= 1 ? $"{span.Hours} 小时 {span.Minutes} 分后重置" : $"{Math.Ceiling(span.TotalMinutes):0} 分钟后重置";
    }
    private void OpenQuota(object sender, RoutedEventArgs e)
    {
        var stack = DetailStack();
        var updateConversation = AddConversationCost(stack);
        var top = new DockPanel();
        var refresh = new Button { Content = "刷新", FontSize = 11, Padding = new Thickness(9,4,9,4), Foreground = BrushFor("Muted") };
        DockPanel.SetDock(refresh, Dock.Right); top.Children.Add(refresh);
        var subtitle = Text("", "Muted", 12); subtitle.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(subtitle); stack.Children.Add(top);
        var panels = new StackPanel(); stack.Children.Add(panels);
        var status = Text("", "Muted", 12); status.Margin = new Thickness(0,12,0,0); stack.Children.Add(status);
        stack.Children.Add(new Border { Height = 1, Background = BrushFor("Line"), Margin = new Thickness(0,20,0,14) });
        bool detailsOpen = false;
        void Switch(string label, string hint, bool value, Action<bool> changed)
        {
            var row = new Grid { Margin = new Thickness(0,5,0,9), ToolTip = hint };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = Text(label,"Text",12); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text);
            var toggle = new ToggleButton { Style = (Style)FindResource("QuotaSwitch"), IsChecked = value, ToolTip = hint };
            AutomationProperties.SetName(toggle,label); Grid.SetColumn(toggle,1); row.Children.Add(toggle); stack.Children.Add(row);
            toggle.Click += (_,_) => { changed(toggle.IsChecked == true); Save(); SendQuotaOptions(); UpdateConversationCostSummary(); refreshDetail?.Invoke(); };
        }
        Switch("计入 Astra 长上下文加价", "仅调整等效估算。超过 272K 输入时，输入/缓存 ×2、输出 ×1.5；Codex Astra 默认不计入。", settings.QuotaIncludeAstraLongContext, v => settings.QuotaIncludeAstraLongContext=v);
        Switch("计入已记录的 Fast 倍率", "只换算有 Fast 记录的用量；未记录速度的请求按普通速度估算。此开关不会开启 Codex 的 Fast 模式。", settings.QuotaNormalizeFast, v => settings.QuotaNormalizeFast=v);
        var disclosure = new Button { Content = "计算明细  ›", FontSize = 11, Foreground = BrushFor("Muted"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,10,0,0), Padding = new Thickness(0,5,6,5) };
        var links=new DockPanel(); stack.Children.Add(links);
        var historyButton=new Button { Content="每周记录  ›", FontSize=11, Foreground=BrushFor("Muted"), Margin=new Thickness(0,10,0,0), Padding=new Thickness(6,5,0,5) };
        AutomationProperties.SetName(historyButton,"查看每周记录"); historyButton.Click+=OpenQuotaHistory;
        DockPanel.SetDock(historyButton,Dock.Right);links.Children.Add(historyButton);
        AutomationProperties.SetName(disclosure,"展开计算明细"); links.Children.Add(disclosure);
        var details = new StackPanel { Visibility = Visibility.Collapsed }; stack.Children.Add(details);
        disclosure.Click += (_,_) => { detailsOpen=!detailsOpen; details.Visibility=detailsOpen?Visibility.Visible:Visibility.Collapsed; disclosure.Content=detailsOpen?"计算明细  ⌄":"计算明细  ›"; AutomationProperties.SetName(disclosure,detailsOpen?"收起计算明细":"展开计算明细"); refreshDetail?.Invoke(); };
        DateTimeOffset queuedUntil = DateTimeOffset.MinValue;
        refresh.Click += (_,_) => { if (!preview && collector?.IsRunning != true) StartCollector(); Send("refreshQuota"); queuedUntil=DateTimeOffset.UtcNow.AddSeconds(4); };
        var dialog = Detail("Codex · 费用与额度", new ScrollViewer { Content=stack, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        string key = ""; var tickers = new List<Action>();
        refreshDetail = () =>
        {
            updateConversation();
            var q = snapshot["quota"]; bool ready=S(q?["state"])=="ready", indexing=B(q?["indexing"]);
            subtitle.Text = S(q?["plan"]) switch { "pro"=>"Pro · 周额度", "plus"=>"Plus · 5 小时 / 周额度", ""=>"账号额度", _=>S(q?["plan"])+" · 账号额度" };
            if (preview) subtitle.Text += " · 示例数据";
            subtitle.ToolTip=preview?"界面预览 · 示例数据":"更新于 "+LocalTime(N(q?["checkedAtMs"]));
            bool queued=DateTimeOffset.UtcNow<queuedUntil;
            bool updating=B(q?["checking"])||B(q?["refreshingUsage"])&&!B(q?["indexError"]);
            refresh.Content=updating?"刷新中…":queued?"已请求":"刷新";
            refresh.IsEnabled=!updating&&!queued&&!preview;
            status.Text = ready ? (q?["windows"] as JsonArray)?.Count==0?"暂无可用额度数据":B(q?["refreshingUsage"])?B(q?["indexError"])?"更新失败，保留上次完整采样；可点击重试":"正在更新统计 · 显示上次完整采样":indexing?B(q?["indexError"])?"统计暂不可用，可稍后刷新":$"正在整理用量 · {100*(N(q?["indexProgress"])??0):0}%":""
                : S(q?["reason"]) switch { "not_subscription"=>"当前未使用订阅账号", "stale"=>"额度已过期，等待刷新", "client_disconnected"=>"等待连接 Codex", _=>B(q?["checking"])||S(q?["state"])=="checking"?"正在读取额度…":"额度暂时读取失败，将自动重试；也可点击刷新。" };
            status.Visibility=status.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
            string next=(q?["windows"]?.ToJsonString()??"[]")+ready+indexing+settings.QuotaIncludeAstraLongContext+settings.QuotaNormalizeFast;
            if (next!=key)
            {
                key=next; panels.Children.Clear(); details.Children.Clear(); tickers.Clear();
                foreach (var w in (q?["windows"] as JsonArray ?? new()).OfType<JsonObject>())
                {
                    bool week=N(w["minutes"])==10080;
                    var section=new StackPanel { Margin=new Thickness(0,20,0,0) };
                    var heading=new DockPanel();
                    var used=Text("已用 "+(N(w["usedPercent"])?.ToString("0.#")??"—")+"%","Muted",12); used.VerticalAlignment=VerticalAlignment.Bottom; used.Margin=new Thickness(0,0,0,3); DockPanel.SetDock(used,Dock.Right); heading.Children.Add(used);
                    var remaining=Text((N(w["remainingPercent"])?.ToString("0.#")??"—")+"%","Text",30); remaining.FontWeight=FontWeights.SemiBold;
                    var line=new StackPanel { Orientation=Orientation.Horizontal }; line.Children.Add(remaining);
                    var caption=Text(week?"本周剩余":N(w["minutes"])==300?"5 小时剩余":"本周期剩余","Muted",12); caption.VerticalAlignment=VerticalAlignment.Bottom; caption.Margin=new Thickness(9,0,0,5); line.Children.Add(caption); heading.Children.Add(line); section.Children.Add(heading);
                    double fraction=Math.Clamp(N(w["remainingPercent"])??0,0,100);
                    var track=new Grid { Height=5, Margin=new Thickness(0,11,0,9) }; track.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(fraction,GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(100-fraction,GridUnitType.Star) });
                    var bg=new Border { Background=BrushFor("Track"), CornerRadius=new CornerRadius(2.5) }; Grid.SetColumnSpan(bg,2); track.Children.Add(bg);
                    track.Children.Add(new Border { Background=BrushFor("Accent"), CornerRadius=new CornerRadius(2.5) }); section.Children.Add(track);
                    var timing=Text("","Faint",11); timing.ToolTip="周期起点 "+LocalTime(N(w["startMs"]))+"\n重置时间 "+LocalTime(N(w["resetsAtMs"])); section.Children.Add(timing); tickers.Add(()=>timing.Text=QuotaCountdown(N(w["resetsAtMs"])));
                    var figures=QuotaFigures(w,settings.QuotaIncludeAstraLongContext,settings.QuotaNormalizeFast,indexing);
                    var tiles=new Grid { Margin=new Thickness(0,20,0,0) }; for(int i=0;i<3;i++) tiles.ColumnDefinitions.Add(new ColumnDefinition());
                    var values=new[]{ figures.Total,figures.Used,figures.Remaining }; var labels=new[]{ week?"周总额 · 估算":"总额 · 估算","已用等效","剩余等效" };
                    for(int i=0;i<3;i++) {
                        var content=new StackPanel(); content.Children.Add(Text(labels[i],"Muted",11));
                        var amount=values[i] is null?"—":"≈$"+values[i]!.Value.ToString(i==1?"N2":"N0");
                        var value=Text(indexing?"…":amount,"Text",18); value.FontWeight=FontWeights.SemiBold; value.Margin=new Thickness(0,7,0,0); content.Children.Add(value);
                        var tile=new Border { Background=BrushFor("Hover"), CornerRadius=new CornerRadius(8), Padding=new Thickness(10,12,5,12), Margin=new Thickness(i==0?0:5,0,0,0), Child=content }; Grid.SetColumn(tile,i); tiles.Children.Add(tile);
                    }
                    section.Children.Add(tiles);
                    var reasons=(w["estimateReasons"] as JsonArray ?? new()).Select(x=>S(x)).ToArray();
                    string hint=reasons.Contains("quota_rebounded")?"额度有调整，暂不估算总额":reasons.Contains("account_unknown")?"等待确认账号":reasons.Contains("unpriced")?"部分模型价格待补全":reasons.Contains("parse_errors")?"本周期记录不完整，暂不估算总额":reasons.Contains("index_error")?"用量读取不完整，请刷新":reasons.Contains("small_sample")?"已用不足 3%，暂不估算总额":reasons.Contains("no_usage")?"本周期暂无本机用量":reasons.Contains("speed_weight_unknown")&&settings.QuotaNormalizeFast?"部分 Fast 倍率待确认":"";
                    if (!indexing && hint.Length>0) Note(section,hint);
                    panels.Children.Add(section);
                    Section(details,week?"本周计算明细":"本周期计算明细");
                    MetricRow(details,"本机用量")(CompactNumber(N(w["requests"]))+" 次 · "+CompactNumber(N(w["tokens"]))+" tokens");
                    MetricRow(details,"任务普通计价")(Money(N(w["quotaBaseUsd"])));
                    MetricRow(details,"Astra 长上下文")(settings.QuotaIncludeAstraLongContext?"+"+Money(N(w["astraPremiumUsd"])):"未计入");
                    MetricRow(details,"Fast 用量折算")((N(w["fastRequests"])??0)<=0?"未记录 Fast 用量":settings.QuotaNormalizeFast?"+"+Money((N(w["quotaFastPremiumUsd"])??0)+(settings.QuotaIncludeAstraLongContext?N(w["astraFastPremiumUsd"])??0:0)):"未计入");
                    MetricRow(details,"API 标价等效")(Money(N(w["usd"])));
                    Note(details,"总额 ≈ 已用等效 ÷ 已用比例。金额为本机模型用量估算，非官方余额；不含语音或其他设备。");
                    if (N(w["unattributedRequests"])>0) Note(details,$"本周期 {N(w["unattributedRequests"]):0} 条记录未标明账号，已纳入本机统计。");
                    if (N(w["assumedTierRequests"])>0) Note(details,$"本周期 {N(w["assumedTierRequests"]):0} 条未记录速度，按普通速度估算，未计入 Fast 加价。");
                    if (N(w["unscopedParseErrors"])>0) Note(details,$"另有 {N(w["unscopedParseErrors"]):0} 条记录无法确定时间，未算作本周期异常。");
                    if (N(w["excludedAccountRequests"])>0) Note(details,$"已排除 {N(w["excludedAccountRequests"]):0} 条其他账号记录。");
                    Note(details,"统计区间 "+LocalTime(N(w["startMs"]))+" — "+LocalTime(N(q?["checkedAtMs"])));
                }
                Note(details,"价格参考 "+S(q?["pricingDate"],"当前")+" · 两个开关仅影响本地等效估算。");
            }
            var area=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            double scale=Math.Max(1,Native.GetDpiForWindow(handle)/96d);
            dialog.Height=Math.Min(742,area.Height/scale-16);
            dialog.Top=Math.Clamp(dialog.Top,area.Top/scale+12,Math.Max(area.Top/scale+12,area.Bottom/scale-dialog.Height-12));
            foreach(var tick in tickers) tick();
        };
        refreshDetail();
    }
}
