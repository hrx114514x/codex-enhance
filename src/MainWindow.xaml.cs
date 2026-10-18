using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexEnhance;
public partial class MainWindow : Window
{
    private readonly string[] args;
    private readonly bool preview;
    private readonly Settings settings;
    private Disclosure disclosure;
    private CollectorClient? collector;
    private Forms.NotifyIcon? tray;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private JsonObject snapshot = new();
    private string? currentThread;
    private string issuesKey = "";
    private IntPtr handle, host;
    private bool hiddenByUser, dragging;
    private Native.Point dragStart;
    private Native.Rect dragWindow;
    private Window? detailWindow;
    private DropShadowEffect? detailShadow;
    private DateTimeOffset lastDiagnostic;
    private string appliedTheme = "";
    private bool renderMode;
    private bool capturedLive;
    private DateTimeOffset lastWindowDiagnostic;
    private DateTimeOffset lastSnapshotReceived = DateTimeOffset.UtcNow;
    private bool collectorFailed;
    private UIElement? dragSurface;
    private bool dragMoved;
    private Action? refreshDetail;
    private IntPtr lastForeground;
    private bool lastForegroundIsCodex;
    private DateTimeOffset lastHostSearch;

    public MainWindow(string[] arguments)
    {
        args = arguments; preview = args.Contains("--preview") || args.Contains("--render"); renderMode = args.Contains("--render");
        settings = preview ? new Settings { Expanded = true } : Settings.Load();
        disclosure = new Disclosure(settings.Expanded);
        InitializeComponent();
        if (preview && !renderMode) { ShowInTaskbar = true; ShowActivated = true; }
        CollapseButton.Content = Icons.Create("chevron-up");
        MenuButton.Content = Icons.Create("more", 18);
        QuotaIcon.Content = Icons.Create("data-usage", 17, "Text"); QuotaChevron.Content = Icons.Create("chevron-right", 16);
        PillChevron.Content = Icons.Create("chevron-down", 15); ToolsIcon.Content = Icons.Create("wrench", 17, "Text");
        ToolsChevron.Content = Icons.Create("chevron-right", 16); HistoryArrow.Content = Icons.Create("arrow-right", 16, "Accent");
        ModelIcon.Content = Icons.Create("sparkle", 16); FollowIcon.Content = Icons.Create("link", 16);
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle); source?.AddHook(WindowMessage);
            long style = Native.GetWindowLongPtr(handle, -20).ToInt64();
            if (!preview) Native.SetWindowLongPtr(handle, -20, new IntPtr(style | 0x08000000L | 0x80L)); // NOACTIVATE + TOOLWINDOW
        };
        Loaded += OnLoaded;
        SizeChanged += (_, _) => { if (!preview && IsVisible && host != IntPtr.Zero) PositionNearHost(); };
        MouseRightButtonUp += (_, e) => { OpenMenu(); e.Handled = true; };
        timer.Tick += (_, _) => Tick();
        Closed += (_, _) => { timer.Stop(); collector?.Dispose(); tray?.Dispose(); detailWindow?.Close(); Application.Current.Shutdown(); };
    }
    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x21 && !preview) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE, click still works
        if (msg == 0x1A) ApplyTheme();
        return IntPtr.Zero;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        if (preview)
        {
            Left = 880; Top = 160;
            ApplySnapshot(Demo(args.Contains("--attention")));
            if (renderMode) { await Dispatcher.InvokeAsync(RenderAll, DispatcherPriority.ApplicationIdle); return; }
            Title = "Codex 状态浮窗 · 界面预览";
            ConnectionText.Text = "界面预览 · 示例数据，未接入实时采集";
            ConnectionNotice.Visibility = Visibility.Visible;
            if (Arg("--capabilities") is { } capabilityPath)
            {
                snapshot["toolHealth"] = JsonNode.Parse(File.ReadAllText(capabilityPath));
                ApplySnapshot(snapshot); OpenCapabilities(this, new RoutedEventArgs());
            }
            if (Arg("--quota") is { } quotaPath)
            {
                snapshot["quota"] = JsonNode.Parse(File.ReadAllText(quotaPath));
                ApplySnapshot(snapshot); OpenQuota(this, new RoutedEventArgs());
            }
            if (Arg("--metrics") is { } metricsPath) {
                snapshot=JsonNode.Parse(File.ReadAllText(metricsPath))!.AsObject();
                ApplySnapshot(snapshot);OpenMetrics(this,new RoutedEventArgs());
            }
            if (args.Contains("--weekly-history")) OpenQuotaHistory(this,new RoutedEventArgs());
            if (args.Contains("--picker"))
            {
                var threads = new JsonArray();
                for (int i = 1; i <= 35; i++) threads.Add(new JsonObject { ["id"] = $"00000000-0000-0000-0000-{i:000000000000}", ["title"] = $"示例任务 {i:00} · 验证长列表滚动与标题截断", ["createdAtMs"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
                snapshot["recentThreads"] = threads;
                OpenTaskPicker(this, new RoutedEventArgs());
            }

        }
        else StartCollector();
        BuildTray(); timer.Start(); Tick();
    }
    private string? Arg(string flag) { int index = Array.IndexOf(args, flag); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    private void StartCollector()
    {
        collector?.Dispose();
        collector = new CollectorClient();
        collector.Snapshot += data => Dispatcher.BeginInvoke(() => { if (!preview) { lastSnapshotReceived = DateTimeOffset.UtcNow; collectorFailed = false; ApplySnapshot(data); } });
        collector.Error += error => Dispatcher.BeginInvoke(() => {
            collectorFailed = true;
            ConnectionText.Text = error; ConnectionNotice.Visibility = Visibility.Visible;
            Directory.CreateDirectory(Settings.StateDirectory);
            File.AppendAllText(Path.Combine(Settings.StateDirectory, "collector-error.log"), DateTimeOffset.Now + " " + error + Environment.NewLine);
        });
        try
        {
            collector.Start(settings, Arg("--thread") ?? settings.LockedThreadId ?? settings.ManualThreadId);
            SendQuotaOptions();
            if (settings.LockedThreadId is not null) Send("lock", settings.LockedThreadId);
            else if (!settings.FollowMode && settings.ManualThreadId is not null) Send("select", settings.ManualThreadId);
        }
        catch (Exception e) { ConnectionText.Text = "采集未启动：" + e.Message; ConnectionNotice.Visibility = Visibility.Visible; }
    }
    private static string S(JsonNode? n, string fallback = "") => n is JsonValue v && v.TryGetValue<string>(out var value) ? value : fallback;
    private static double? N(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var value)) return value;
        if (v.TryGetValue<int>(out var integer)) return integer;
        if (v.TryGetValue<long>(out var big)) return big;
        return null;
    }
    private static bool B(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var value) && value;
    private Brush BrushFor(string key) => (Brush)FindResource(key);
    private static string SeverityColor(string level) => level switch { "critical" => "Critical", "error" => "Error", "warning" => "Attention", _ => "Muted" };
    private static string SeverityName(string level) => level switch { "critical" => "Critical", "error" => "Error", "warning" => "Warning", "info" => "Info", _ => "" };
    private void ApplySnapshot(JsonObject data)
    {
        string? id = S(data["threadId"], null!);
        if (id != currentThread)
        {
            currentThread = id; disclosure = new Disclosure(settings.Expanded); issuesKey = "";
            detailWindow?.Close(); detailWindow = null;
        }
        snapshot = data;
        if (B(data["connection"]?["selectionReset"]))
        { settings.LockedThreadId = null; settings.ManualThreadId = null; settings.FollowMode = true; Save(); }
        var issues = ActiveIssues();
        if (!B(data["historyLoading"])) disclosure.Observe(issues.Where(i => S(i["severity"]) == "critical").Select(i => S(i["id"]) + ":critical"), DateTimeOffset.UtcNow);
        TaskTitle.Text = S(data["title"], "选择一个任务"); TaskTitle.ToolTip = TaskTitle.Text;
        PhaseText.Text = S(data["phase"]) switch { "working" => "正在处理", "compacting" => "正在压缩", "idle" => "已完成", "interrupted" => "已中断", "failed" => "本轮未完成", "waiting" => "等待输入", _ => currentThread is null ? "等待任务" : "状态待确认" };
        if (currentThread is not null && data["performance"]?["stage"] is { } stage) PhaseText.Text = S(stage, PhaseText.Text);
        var active = S(data["phase"]) is "working" or "compacting";
        PhaseDot.Fill = BrushFor(active ? "Accent" : "Faint");
        ContextText.Text = Percent(N(data["context"]?["percent"])); CacheText.Text = Percent(N(data["cacheHit"]));
        ContextFill.Width = 136 * Math.Clamp(N(data["context"]?["percent"]) ?? 0, 0, 100) / 100;
        ContextText.ToolTip = "最近一次上下文采样，不是累计 token 消耗";
        CacheText.ToolTip = "最近一次请求的缓存输入占比";
        ModelText.Text = CurrentModelText(data);
        string modelSource=string.IsNullOrEmpty(S(data["modelIdentity"]?["response"]))?"任务设置，尚未取得上游返回标识":"上游响应";
        ModelButton.ToolTip = $"来源：{modelSource}\n选定模型：{S(data["model"])} · {S(data["effort"])}\n点击查看记录";
        var attention = (int)(N(data["tools"]?["attention"]) ?? 0);
        var running = (int)(N(data["tools"]?["running"]) ?? 0);
        var completed = (int)(N(data["tools"]?["completed"]) ?? 0);
        var notes = (int)(N(data["tools"]?["notes"]) ?? 0);
        var realtime = B(data["tools"]?["runtimeAvailable"]);
        var level = S(data["tools"]?["highestSeverity"], attention > 0 ? "warning" : notes > 0 ? "info" : "");
        var highestCount = (int)(N(data["tools"]?["levelCounts"]?[level]) ?? attention);
        var criticalCount = (level == "critical" ? highestCount : 0) + ((data["toolHealth"]?["issues"] as JsonArray)?.Count ?? 0);
        var records = (snapshot["tools"]?["items"] as JsonArray)?.OfType<JsonObject>().Count(t => S(t["turnId"]) == S(snapshot["turnId"])) ?? completed;
        ToolsSummary.Text = criticalCount > 0 ? $"Critical · {criticalCount}" : active && realtime && running > 0 ? $"{running} 项进行中" : records > 0 ? $"本轮 · {records} 项" : active ? "等待调用记录" : "本轮暂无调用";
        if (criticalCount == 0 && S(data["toolHealth"]?["state"]) == "ready" &&
            (S(data["toolHealth"]?["watched"]?["state"]) is "missing" or "server_absent" || (data["toolHealth"]?["changes"] as JsonArray)?.Count > 0))
            ToolsSummary.Text = $"目录变化 · {records} 项调用";
        ToolsSummary.Foreground = BrushFor(criticalCount > 0 ? "Critical" : "Muted");
        CapabilitySummary.Text = CapabilityLabel();
        UpdateQuotaSummary();
        ToolsButton.ToolTip = attention + notes > 0 ? $"有 {attention + notes} 条分级记录，点击查看。只有 Critical 主动展开。" : realtime ? "当前回合的工具调用，点击查看详情" : "已完成调用来自本地记录；实时调用状态尚未接通";
        var connection = data["connection"];
        var selection = S(connection?["selection"]);
        FollowText.Text = selection switch { "locked" or "manual" => "固定此对话", "auto" => "跟随对话", _ => "选择对话" };
        FollowText.Foreground = BrushFor(selection == "auto" ? "Accent" : "Muted");
        FollowIcon.Content = Icons.Create("link", 16, selection == "auto" ? "Accent" : "Muted");
        FollowButton.ToolTip = selection == "auto" ? "跟随对话已开启：随 Codex 页面切换。点击固定当前对话。" : "当前固定查看此对话。点击恢复随 Codex 页面切换；此开关只控制查看哪个对话。";
        FollowButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, selection == "auto" ? "关闭跟随对话，固定当前对话" : "开启跟随对话");
        var message = S(connection?["message"]);
        if (B(data["historyLoading"])) message = "正在读取这个任务的历史记录…";
        ConnectionText.Text = message;
        ConnectionNotice.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        string newKey = (data["tools"]?["issues"]?.ToJsonString() ?? "") + (data["toolHealth"]?["issues"]?.ToJsonString() ?? "") + disclosure.ToolsExpanded + (disclosure.ToolsExpanded ? data["tools"]?["items"]?.ToJsonString() : "");
        if (newKey != issuesKey) { issuesKey = newKey; PopulateTools(); }
        UpdateDisclosure(); UpdateTime();
        PillPhase.Text = criticalCount > 0 ? ToolsSummary.Text : PhaseText.Text;
        PillDot.Fill = BrushFor(criticalCount > 0 ? "Critical" : active ? "Accent" : "Faint");
        PillMetric.Text = ElapsedText.Text;
        Pill.ToolTip = TaskTitle.Text + " · 点击展开，拖动调整位置";
        UpdateFreshness(); refreshDetail?.Invoke();
        if (!preview && DateTimeOffset.UtcNow - lastDiagnostic > TimeSpan.FromSeconds(5))
        {
            lastDiagnostic = DateTimeOffset.UtcNow;
            try { Directory.CreateDirectory(Settings.StateDirectory); File.WriteAllText(Path.Combine(Settings.StateDirectory, "latest-state.json"), data.ToJsonString(Settings.JsonOptions)); } catch { }
        }
    }
    private void UpdateTime()
    {
        UpdateActivity(!preview && (collectorFailed || (DateTimeOffset.UtcNow-lastSnapshotReceived).TotalMilliseconds>6000));
        UpdateFirstToken();
    }
    private void UpdateFirstToken()
    {
        var first = snapshot["firstOutput"]; var kind = S(first?["state"]);
        bool active = S(snapshot["phase"]) is "working" or "compacting" or "waiting";
        string label = "首字", value = "—", source = "本轮暂无记录", tip = "本轮首字时点未取得，不使用上轮数据代替正在运行的这一轮。";
        if (N(snapshot["ttftMs"]) is { } exact)
        { label = "首字（日志）"; value = Duration(exact); source = "本轮记录"; tip = "本轮日志中的 time_to_first_token_ms，不等同屏幕实际绘制时间。"; }
        else if (kind == "observed" && N(first?["ms"]) is { } observed)
        { label = "首字（观测）"; value = "≈" + Duration(observed); source = "本轮首条回复"; tip = "从本轮开始到客户端首条助手消息的启动时间，属于首条回复观测值，不等同日志首 token 耗时；日志到达后会切换口径。"; }
        else if (active)
        {
            source = "本轮状态";
            switch (kind)
            {
                case "output_seen": value = "已输出"; source = "时点未记录"; break;
                case "activity_seen": value = "已响应"; source = "首字待记录"; break;
                case "waiting_input": value = "待输入"; source = "等待确认/输入"; break;
                case "compacting": value = "压缩中"; break;
                case "waiting":
                    double age = preview ? 0 : Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (N(snapshot["updatedAtMs"]) ?? 0));
                    if (!collectorFailed && age < 6000)
                    { value = "等 " + Duration((N(first?["ms"]) ?? 0) + age); source = "本轮等待中"; tip = "这是本轮目前已等待的时间，尚不是测得的首 token 耗时。观察到响应后停止等待显示。"; }
                    else source = "等待数据更新";
                    break;
            }
        }
        else if (N(snapshot["performance"]?["recentTtftMs"]) is { } recent)
        { label = "首字（日志）"; value = Duration(recent); source = "最近记录"; tip = "本轮没有首 token 日志；这里是最近有记录的完成轮次。"; }
        FirstTokenLabel.Text = label; FirstTokenText.Text = value; FirstTokenSource.Text = source;
        FirstTokenText.FontSize = value.Length > 6 ? 16 : 20; FirstTokenText.ToolTip = tip;
    }
    private static string Percent(double? value) => value is null ? "—" : value.Value.ToString("0.0") + "%";
    private static string Clock(double ms) { var span = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes:00}:{span.Seconds:00}"; }
    private static string Duration(double? ms) => ms is null ? "—" : ms < 60000 ? (ms.Value / 1000).ToString("0.0") + " s" : Clock(ms.Value);
    private void UpdateDisclosure()
    {
        ExpandedContent.Visibility = disclosure.Expanded ? Visibility.Visible : Visibility.Collapsed;
        Pill.Visibility = disclosure.Expanded ? Visibility.Collapsed : Visibility.Visible;
        ToolsDetails.Visibility = disclosure.ToolsExpanded ? Visibility.Visible : Visibility.Collapsed;
        ToolsChevron.Content = Icons.Create(disclosure.ToolsExpanded ? "chevron-down" : "chevron-right", 16);
        ToolsButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, disclosure.ToolsExpanded ? "收起工具调用" : "展开工具调用");
        Width = disclosure.Expanded ? 386 : 292;
        UpdateDesignSummary();
    }
    private void PopulateTools()
    {
        IssueList.Children.Clear();
        var issues = ActiveIssues();
        if (issues.Length > 0) foreach (var group in issues.GroupBy(i => S(i["severity"]) + "|" + S(i["label"]) + "|" + S(i["reason"])).Take(6))
        {
            var issue = group.First().DeepClone().AsObject();
            if (group.Count() > 1) { issue["reason"] = S(issue["reason"]) + $" · {group.Count()} 次"; issue["detail"] = $"本轮有 {group.Count()} 次同类记录，可在调用记录中逐项查看。"; }
            IssueList.Children.Add(ToolView(issue, true));
        }
        else
        {
            var items = (snapshot["tools"]?["items"] as JsonArray)?.OfType<JsonObject>().Where(t => S(t["turnId"]) == S(snapshot["turnId"])).Take(5).ToArray() ?? Array.Empty<JsonObject>();
            if (items.Length == 0) IssueList.Children.Add(Text("暂无需要关注的调用", "Muted", 13));
            else foreach (var item in items) IssueList.Children.Add(ToolView(item, false));
        }
    }
    private TextBlock Text(string value, string color = "Text", double size = 13) => new() { Text = value, Foreground = BrushFor(color), FontSize = size, TextWrapping = TextWrapping.Wrap };
    private FrameworkElement ToolView(JsonObject item, bool issue)
    {
        var outer = new StackPanel();
        var line = new DockPanel();
        var level = S(item["severity"]);
        if (issue) { var icon = Icons.Create("info", 17, SeverityColor(level)); icon.Margin = new Thickness(0, 2, 10, 0); DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon); }
        var title = Text((string.IsNullOrEmpty(level) ? "" : SeverityName(level) + " · ") + S(item["label"], "工具调用") + (issue ? "：" + S(item["reason"], "需要关注") : ""), "Text", 13);
        title.FontWeight = FontWeights.Medium; line.Children.Add(title); outer.Children.Add(line);
        var details = S(item["detail"]);
        if (!string.IsNullOrWhiteSpace(details)) { var detail = Text(details, "Muted", 12); detail.Margin = new Thickness(issue ? 27 : 0, 6, 0, 0); outer.Children.Add(detail); }
        var state = S(item["status"]) switch { "running" => "进行中", "completed" => "完成", "failed" => "异常", "interrupted" => "中断", _ => "状态未知" };
        var exit = N(item["exitCode"]);
        var meta = Text(S(item["name"]) + " · " + Duration(N(item["durationMs"])) + (exit is null ? "" : $" · 退出码 {exit:0}") + (issue ? "" : " · " + (S(item["reason"]) is { Length: > 0 } reason ? reason : state)), "Faint", 12);
        meta.Margin = new Thickness(issue ? 27 : 0, 5, 0, 0); outer.Children.Add(meta);
        return new Border { Background=BrushFor("Panel"),CornerRadius=new CornerRadius(10),Padding=new Thickness(12),Margin=new Thickness(0,3,0,5),Child=outer };
    }
    private void ToggleTools(object sender, RoutedEventArgs e) { disclosure.ToggleTools(); PopulateTools(); UpdateDisclosure(); }
    private void Collapse(object sender, RoutedEventArgs e) { disclosure.SetExpanded(false); settings.Expanded = false; Save(); UpdateDisclosure(); }
    private void PillClick(object sender, MouseButtonEventArgs e) { disclosure.SetExpanded(true); settings.Expanded = true; Save(); UpdateDisclosure(); e.Handled = true; }
    private void Follow(object sender, RoutedEventArgs e)
    {
        if (S(snapshot["connection"]?["cdp"]) != "connected") { OpenTaskPicker(sender, e); return; }
        bool following = S(snapshot["connection"]?["selection"]) == "auto";
        if (following && currentThread is not null)
        { settings.LockedThreadId = currentThread; settings.FollowMode = true; Save(); Send("lock", currentThread); }
        else { settings.LockedThreadId = null; settings.ManualThreadId = null; settings.FollowMode = true; Save(); Send("follow"); }
        if (preview) { snapshot["connection"]!["selection"] = following ? "locked" : "auto"; ApplySnapshot(snapshot); }
    }
    private void Send(string type, string? id = null) => collector?.Send(new JsonObject { ["type"] = type, ["threadId"] = id });
    private void Save() { if (!preview) settings.Save(); }
    private void OpenTaskPicker(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Width = 304, MaxHeight = Math.Min(440, SystemParameters.WorkArea.Height - 40) };
        var follow = new MenuItem { Header = "跟随对话（随 Codex 页面切换）", IsCheckable = true, IsChecked = S(snapshot["connection"]?["selection"]) == "auto" };
        follow.Click += (_, _) => { settings.LockedThreadId = null; settings.ManualThreadId = null; settings.FollowMode = true; Save(); Send("follow"); };
        menu.Items.Add(follow); menu.Items.Add(new Separator());
        foreach (var thread in (snapshot["recentThreads"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            string id = S(thread["id"]), title = S(thread["title"], "未命名任务");
            if (title == "未命名任务" && N(thread["createdAtMs"]) is { } created) title += " · " + DateTimeOffset.FromUnixTimeMilliseconds((long)created).ToLocalTime().ToString("MM-dd HH:mm");
            var label = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 245, FontSize = 12 };
            var item = new MenuItem { Header = label, ToolTip = title, IsCheckable = true, IsChecked = id == currentThread };
            item.Click += (_, _) => { settings.ManualThreadId = id; settings.LockedThreadId = null; settings.FollowMode = false; Save(); Send("select", id); };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = TaskPicker; menu.IsOpen = true;
    }
    private Window Detail(string title, FrameworkElement content)
    {
        detailWindow?.Close();
        var window = new Window { Title = title, Width = 540, Height = 700, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Foreground = BrushFor("Text"), WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = false, FontFamily = FontFamily, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = preview, Owner = this };
        var frame = new Border { Margin=new Thickness(16), CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), BorderBrush = BrushFor("Stroke"), Background = BrushFor("Surface") };
        detailShadow=new DropShadowEffect { Color=Colors.Black, BlurRadius=28, ShadowDepth=5, Direction=270, Opacity=appliedTheme=="light"?.12:.28 };
        var shell=new Grid();shell.Children.Add(new Border { Margin=new Thickness(16),CornerRadius=new CornerRadius(20),Background=BrushFor("Surface"),Effect=detailShadow,IsHitTestVisible=false });shell.Children.Add(frame);
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Margin = new Thickness(24, 22, 20, 18), Background = Brushes.Transparent };
        var close = new Button { Width = 32, Height = 32, Content = Icons.Create("dismiss", 16), ToolTip = "关闭" }; close.Click += (_, _) => window.Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var heading=new StackPanel();
        var headingText=Text(title.Replace("Codex · ", ""), "Text", 22);headingText.FontWeight=FontWeights.SemiBold;heading.Children.Add(headingText);
        var subtitle=Text(DetailSubtitle(title),"Muted",11);subtitle.Margin=new Thickness(0,6,0,0);heading.Children.Add(subtitle);header.Children.Add(heading);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is TextBlock) window.DragMove(); };
        layout.Children.Add(header); Grid.SetRow(content, 1); layout.Children.Add(content); frame.Child = layout; window.Content = shell;
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        detailWindow = window; window.Closed += (_, _) => { if (detailWindow == window) { detailWindow = null; refreshDetail = null; } };
        window.Loaded += (_, _) => {
            var area = Forms.Screen.FromHandle(handle).WorkingArea;
            double scale = Math.Max(1, Native.GetDpiForWindow(handle) / 96d);
            window.Height = Math.Min(720, area.Height / scale - 16);
            window.Left = Math.Clamp(Left - window.Width - 12, area.Left / scale + 12, Math.Max(area.Left / scale + 12, area.Right / scale - window.Width - 12));
            window.Top = Math.Clamp(Top, area.Top / scale + 12, Math.Max(area.Top / scale + 12, area.Bottom / scale - window.Height - 12));
        };
        window.Show(); return window;
    }
    private void Tick()
    {
        if (preview) { refreshDetail?.Invoke(); return; }
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        if (foreground != lastForeground) { lastForeground = foreground; lastForegroundIsCodex = Native.IsCodex(foreground); }
        if (lastForegroundIsCodex)
        {
            var candidate = Native.GetAncestor(foreground, 3); // Root owner: ignore native dialogs/tooltips.
            if (candidate != IntPtr.Zero && candidate != host && Native.IsCodex(candidate))
            { AttachHost(candidate); }
        }
        if ((host == IntPtr.Zero || !Native.IsWindow(host)) && DateTimeOffset.UtcNow - lastHostSearch > TimeSpan.FromSeconds(5))
        {
            lastHostSearch = DateTimeOffset.UtcNow;
            var candidate = Native.FindSingleHost();
            if (candidate != IntPtr.Zero) AttachHost(candidate);
        }
        bool show = !hiddenByUser && host != IntPtr.Zero && Native.IsWindow(host) && Native.IsWindowVisible(host) && !Native.IsIconic(host);
        if (show && !IsVisible) Show(); else if (!show && IsVisible) Hide();
        if (show && !dragging) PositionNearHost();
        UpdateTime(); UpdateFreshness(); refreshDetail?.Invoke();
        if (!B(snapshot["historyLoading"])) disclosure.Observe(ActiveIssues().Where(i => S(i["severity"]) == "critical").Select(i => S(i["id"]) + ":critical"), DateTimeOffset.UtcNow);
        UpdateDisclosure();
        if (DateTimeOffset.UtcNow - lastWindowDiagnostic > TimeSpan.FromSeconds(3))
        {
            lastWindowDiagnostic = DateTimeOffset.UtcNow;
            Native.GetWindowRect(handle, out var bounds);
            try
            {
                Directory.CreateDirectory(Settings.StateDirectory);
                File.WriteAllText(Path.Combine(Settings.StateDirectory, "window-state.json"), System.Text.Json.JsonSerializer.Serialize(new {
                    visible = Native.IsWindowVisible(handle), wpfVisible = IsVisible, foregroundProcess,
                    hostWindow = host.ToInt64(), ownerWindow = Native.GetWindow(handle, 4).ToInt64(), topmost = (Native.GetWindowLongPtr(handle, -20).ToInt64() & 8) != 0, window = handle.ToInt64(), threadId = currentThread,
                    foregroundAboveOverlay = Native.IsAbove(foreground, handle), overlayAboveHost = Native.IsAbove(handle, host),
                    left = bounds.Left, top = bounds.Top, width = bounds.Width, height = bounds.Height,
                    dpi = Native.GetDpiForWindow(handle), sampledAt = DateTimeOffset.UtcNow }, Settings.JsonOptions));
            } catch { }
        }
        if (!capturedLive && show && !B(snapshot["historyLoading"]) && currentThread is not null && Arg("--capture-once") is { } capturePath)
        { capturedLive = true; Capture(capturePath); }
    }
    private void PositionNearHost()
    {
        if (host == IntPtr.Zero || handle == IntPtr.Zero || !Native.GetWindowRect(host, out var rect)) return;
        if (!IsVisible || hiddenByUser) return;
        double scale = Math.Max(1, Native.GetDpiForWindow(host) / 96d);
        int w = (int)Math.Round(ActualWidth * scale), h = (int)Math.Round(ActualHeight * scale);
        var screen = Forms.Screen.FromHandle(host).WorkingArea;
        var top = settings.TopOffset ?? Math.Max(95, (rect.Height - h) / scale - 28);
        int x = (int)(rect.Right - w - settings.RightOffset * scale), y = (int)(rect.Top + top * scale);
        x = Math.Clamp(x, screen.Left + 6, Math.Max(screen.Left + 6, screen.Right - w - 6));
        y = Math.Clamp(y, screen.Top + 6, Math.Max(screen.Top + 6, screen.Bottom - h - 6));
        if (!Native.GetWindowRect(handle, out var previous) || previous.Left != x || previous.Top != y)
            Native.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0010 | 0x0001 | 0x0004); // No activation, size or z-order change.
    }
    private void AttachHost(IntPtr candidate)
    {
        host = candidate;
        // Keep WPF's cached owner in sync; a raw HWND assignment is overwritten by Show().
        new WindowInteropHelper(this).Owner = host;
        Native.AttachToHost(handle, host);
    }
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
            for (var p = source; p is not null; p = VisualTreeHelper.GetParent(p)) if (p is Button) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Native.GetCursorPos(out dragStart); Native.GetWindowRect(handle, out dragWindow); dragging = true; dragMoved = false; dragSurface = (UIElement)sender; dragSurface.CaptureMouse();
    }
    private void DragMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        Native.GetCursorPos(out var point);
        if (Math.Abs(point.X - dragStart.X) + Math.Abs(point.Y - dragStart.Y) < 5 && !dragMoved) return;
        dragMoved = true;
        Native.SetWindowPos(handle, IntPtr.Zero, dragWindow.Left + point.X - dragStart.X, dragWindow.Top + point.Y - dragStart.Y, 0, 0, 0x0010 | 0x0001 | 0x0004);
    }
    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return; dragging = false; dragSurface?.ReleaseMouseCapture();
        if (sender == Pill && !dragMoved) { PillClick(sender, e); return; }
        if (host != IntPtr.Zero && Native.GetWindowRect(host, out var parent) && Native.GetWindowRect(handle, out var rect))
        { double scale = Math.Max(1, Native.GetDpiForWindow(host) / 96d); settings.RightOffset = (parent.Right - rect.Right) / scale; settings.TopOffset = (rect.Top - parent.Top) / scale; Save(); }
    }
    private void BuildTray()
    {
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "Codex 状态浮窗", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => Dispatcher.Invoke(() => { hiddenByUser = !hiddenByUser; if (preview) { if (hiddenByUser) Hide(); else Show(); } else Tick(); }));
        menu.Items.Add("重新连接", null, (_, _) => Dispatcher.Invoke(() => { if (!preview) StartCollector(); }));
        if (preview)
        {
            menu.Items.Add("演示正常状态", null, (_, _) => Dispatcher.Invoke(() => { disclosure = new Disclosure(true); ApplySnapshot(Demo(false)); }));
            menu.Items.Add("演示异常状态", null, (_, _) => Dispatcher.Invoke(() => { disclosure = new Disclosure(true); ApplySnapshot(Demo(true)); }));
        }
        menu.Items.Add("退出浮窗", null, (_, _) => Dispatcher.Invoke(Close));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { hiddenByUser = false; if (preview) Show(); else Tick(); });
    }
    private void ShowMenu(object sender, RoutedEventArgs e) => OpenMenu();
    private void OpenMenu()
    {
        var menu = new ContextMenu();
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("选择监视任务", () => OpenTaskPicker(this, new RoutedEventArgs()));
        Add("连接与诊断", () => OpenHealth(this, new RoutedEventArgs()));
        Add("查看调用记录", () => OpenHistory(this, new RoutedEventArgs()));
        Add("工具可用性检查", () => OpenCapabilities(this, new RoutedEventArgs()));
        Add("账号额度与等效金额", () => OpenQuota(this, new RoutedEventArgs()));
        Add("语音连接", () => OpenVoice(this, new RoutedEventArgs()));
        Add("性能与压缩详情", () => OpenMetrics(this, new RoutedEventArgs()));
        Add("当前模型", () => OpenModels(this, new RoutedEventArgs()));
        Add("恢复默认位置", () => { settings.RightOffset = 22; settings.TopOffset = null; Save(); PositionNearHost(); });
        menu.Items.Add(new Separator());
        Add("外观与配色…", () => OpenAppearance(this,new RoutedEventArgs()));
        Add("跟随系统主题", () => { settings.Theme = "system"; ApplyTheme(); Save(); });
        Add("深色外观", () => { settings.Theme = "dark"; ApplyTheme(); Save(); });
        Add("浅色外观", () => { settings.Theme = "light"; ApplyTheme(); Save(); });
        menu.Items.Add(new Separator()); Add("退出浮窗", Close);
        menu.PlacementTarget = MenuButton; menu.IsOpen = true;
    }
    private void ApplyTheme(bool force=false)
    {
        var theme = settings.Theme;
        if (theme == "system") { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); theme = (key?.GetValue("AppsUseLightTheme") as int? ?? 0) == 0 ? "dark" : "light"; }
        if (args.Contains("--light")) theme = "light";
        if (theme == appliedTheme && !force) return; appliedTheme = theme;
        var colors = DesignTokens.Resolve(theme=="light",theme=="light"?settings.CustomLightColors:settings.CustomDarkColors);
        var oldColors=colors.Keys.Select(key=>(key,brush:Application.Current.Resources[key] as SolidColorBrush)).Where(p=>p.brush is not null).GroupBy(p=>p.brush!.Color).ToDictionary(g=>g.Key,g=>g.First().key);
        foreach (var pair in colors) Application.Current.Resources[pair.Key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
        if(detailWindow is not null) RethemeVisual(detailWindow,oldColors);
        SurfaceShadow.Opacity=theme=="light"?.12:.28;if(detailShadow is not null)detailShadow.Opacity=SurfaceShadow.Opacity;
        if (snapshot.Count > 0) { issuesKey = ""; ApplySnapshot(snapshot); }
    }
    private static void RethemeVisual(DependencyObject root,IReadOnlyDictionary<Color,string> palette)
    {
        void Bind(FrameworkElement element,DependencyProperty property) {
            if(element.ReadLocalValue(property) is SolidColorBrush brush&&palette.TryGetValue(brush.Color,out var key))element.SetResourceReference(property,key);
        }
        if(root is Control control){Bind(control,Control.ForegroundProperty);Bind(control,Control.BackgroundProperty);Bind(control,Control.BorderBrushProperty);}
        if(root is TextBlock text)Bind(text,TextBlock.ForegroundProperty);
        if(root is Border border){Bind(border,Border.BackgroundProperty);Bind(border,Border.BorderBrushProperty);}
        if(root is Panel panel)Bind(panel,Panel.BackgroundProperty);
        if(root is System.Windows.Shapes.Shape shape){Bind(shape,System.Windows.Shapes.Shape.FillProperty);Bind(shape,System.Windows.Shapes.Shape.StrokeProperty);}
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)RethemeVisual(VisualTreeHelper.GetChild(root,i),palette);
    }
    public static JsonObject Demo(bool attention, string level = "warning") => JsonNode.Parse("""
      {"threadId":"demo","title":"优化 Codex 状态浮窗","model":"gpt-6-astra","effort":"xhigh","phase":"working","turnId":"demo-turn","elapsedMs":86000,"ttftMs":2305,"context":{"used":100236,"limit":828400,"percent":12.1},"cacheHit":95.2,"totalTokens":22020349,"compactions":1,"lastCompaction":{"before":598000,"after":41000,"durationMs":175374},"tools":{"running":2,"completed":18,"attention":0,"issues":[],"items":[],"runtimeAvailable":true},"connection":{"cdp":"connected","runtime":true,"selection":"auto","message":""},"recentThreads":[],"performance":{"stage":"工具执行中","progressGapMs":2300,"logGapMs":1100,"baselineMs":3800,"baselineSamples":5,"recentTimings":[{"ttftMs":5100,"completedAtMs":1790416100000},{"ttftMs":4200,"completedAtMs":1790416200000},{"ttftMs":3800,"completedAtMs":1790416300000},{"ttftMs":2700,"completedAtMs":1790416400000},{"ttftMs":6200,"completedAtMs":1790416500000},{"ttftMs":2305,"completedAtMs":1790416600000}],"hints":[]},"updatedAtMs":0}
      """)!.AsObject().WithAttention(attention, level).WithDemoActivity();
    private void RenderAll()
    {
        string output = Path.GetFullPath(Arg("--render") ?? Path.Combine(AppContext.BaseDirectory, "renders")); Directory.CreateDirectory(output);
        settings.Theme = "dark"; ApplyTheme();
        disclosure = new Disclosure(false); ApplySnapshot(Demo(true, "warning")); bool warningQuiet = !disclosure.Expanded;
        ApplySnapshot(Demo(true, "error")); bool errorQuiet = !disclosure.Expanded;
        ApplySnapshot(Demo(true, "critical")); bool criticalOpens = disclosure.Expanded && disclosure.ToolsExpanded;
        File.WriteAllText(Path.Combine(output, "critical-only-check.json"), System.Text.Json.JsonSerializer.Serialize(new { warningQuiet, errorQuiet, criticalOpens, passed = warningQuiet && errorQuiet && criticalOpens }, Settings.JsonOptions));
        var missingDirectory = Demo(false);
        missingDirectory["toolHealth"] = JsonNode.Parse("""{"state":"ready","watched":{"state":"missing"},"issues":[],"servers":[]}""");
        disclosure = new Disclosure(false); ApplySnapshot(missingDirectory); bool directoryMissingQuiet = !disclosure.Expanded;
        var failedService = Demo(false);
        failedService["toolHealth"] = JsonNode.Parse("""{"state":"ready","watched":{"state":"unverified"},"issues":[{"id":"service-failure","severity":"critical","label":"工具服务","name":"codex_app","reason":"服务状态失败","detail":"当前服务状态为 failed"}],"servers":[]}""");
        disclosure = new Disclosure(false); ApplySnapshot(failedService); bool serviceFailureOpens = disclosure.Expanded && disclosure.ToolsExpanded && ToolsSummary.Text.Contains("Critical");
        File.WriteAllText(Path.Combine(output, "capability-disclosure-check.json"), System.Text.Json.JsonSerializer.Serialize(new { directoryMissingQuiet, serviceFailureOpens, passed = directoryMissingQuiet && serviceFailureOpens }, Settings.JsonOptions));
        var waitingOutput = Demo(false); waitingOutput["ttftMs"] = null; waitingOutput["elapsedMs"] = 4200;
        waitingOutput["tools"]!["running"] = 0; waitingOutput["performance"]!["stage"] = "正在处理";
        waitingOutput["performance"]!["recentTtftMs"] = 98765;
        waitingOutput["firstOutput"] = new JsonObject { ["state"] = "waiting", ["ms"] = 4200 };
        disclosure = new Disclosure(true); ApplySnapshot(waitingOutput); bool waitingIsLive = FirstTokenText.Text == "等 4.2 s" && FirstTokenSource.Text == "本轮等待中";
        Capture(Path.Combine(output, "first-output-waiting.png"));
        waitingOutput["firstOutput"] = new JsonObject { ["state"] = "observed", ["ms"] = 2300 };
        ApplySnapshot(waitingOutput); bool observedCurrent = FirstTokenText.Text == "≈2.3 s" && FirstTokenLabel.Text == "首字（观测）";
        Capture(Path.Combine(output, "first-output-observed.png"));
        waitingOutput["firstOutput"] = new JsonObject { ["state"] = "output_seen" };
        ApplySnapshot(waitingOutput); bool noInventedDuration = FirstTokenText.Text == "已输出";
        waitingOutput["ttftMs"] = 1200; ApplySnapshot(waitingOutput); bool logWins = FirstTokenText.Text == "1.2 s" && FirstTokenLabel.Text == "首字（日志）";
        File.WriteAllText(Path.Combine(output, "first-output-check.json"), System.Text.Json.JsonSerializer.Serialize(new { waitingIsLive, observedCurrent, noInventedDuration, logWins, passed = waitingIsLive && observedCurrent && noInventedDuration && logWins }, Settings.JsonOptions));
        var quotaDemo = Demo(false); quotaDemo["quota"] = JsonNode.Parse("""{"state":"ready","plan":"pro","windows":[{"minutes":10080,"remainingPercent":93}]}""");
        ApplySnapshot(quotaDemo); bool proWeeklyOnly = QuotaSummary.Text == "周剩余 93%";
        Capture(Path.Combine(output, "quota-pro-main.png"));
        quotaDemo["quota"]!["plan"] = "plus"; quotaDemo["quota"]!["windows"]!.AsArray().Add(new JsonObject { ["minutes"] = 300, ["remainingPercent"] = 20 });
        ApplySnapshot(quotaDemo); bool plusBoth = QuotaSummary.Text == "5h余 20% · 周余 93%";
        Capture(Path.Combine(output, "quota-plus-main.png"));
        File.WriteAllText(Path.Combine(output, "quota-display-check.json"), System.Text.Json.JsonSerializer.Serialize(new { proWeeklyOnly, plusBoth, passed = proWeeklyOnly && plusBoth }, Settings.JsonOptions));
        disclosure = new Disclosure(true); ApplySnapshot(Demo(false)); Capture(Path.Combine(output, "normal.png"));
        ApplySnapshot(Demo(true)); Capture(Path.Combine(output, "attention.png"));
        disclosure = new Disclosure(true); ApplySnapshot(Demo(true, "error")); Capture(Path.Combine(output, "error.png"));
        disclosure = new Disclosure(true); ApplySnapshot(Demo(true, "critical")); Capture(Path.Combine(output, "critical.png"));
        disclosure = new Disclosure(true); ApplySnapshot(Demo(true, "info")); Capture(Path.Combine(output, "info.png"));
        disclosure.SetExpanded(false); UpdateDisclosure(); Capture(Path.Combine(output, "collapsed.png"));
        settings.Theme = "light"; ApplyTheme(); disclosure = new Disclosure(true); ApplySnapshot(Demo(false)); Capture(Path.Combine(output, "light.png"));
        settings.Theme = "dark"; ApplyTheme(); disclosure = new Disclosure(true); ApplySnapshot(Demo(false));
        FollowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool fixedConversation = FollowText.Text == "固定此对话" && S(snapshot["connection"]?["selection"]) == "locked";
        Capture(Path.Combine(output, "fixed-conversation.png"));
        FollowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool following = FollowText.Text == "跟随对话" && S(snapshot["connection"]?["selection"]) == "auto";
        File.WriteAllText(Path.Combine(output, "follow-control-check.json"), System.Text.Json.JsonSerializer.Serialize(new { fixedConversation, following, passed = fixedConversation && following }, Settings.JsonOptions));
        RenderGallery(Path.Combine(output, "screenshots"));
        Close();
    }
    private void Capture(string file)
    {
        UpdateLayout(); SurfaceRoot.Measure(new Size(Width, double.PositiveInfinity)); SurfaceRoot.Arrange(new Rect(0, 0, Width, SurfaceRoot.DesiredSize.Height)); SurfaceRoot.UpdateLayout();
        double scale = 2; var bitmap = new RenderTargetBitmap((int)Math.Ceiling(SurfaceRoot.ActualWidth * scale), (int)Math.Ceiling(SurfaceRoot.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(SurfaceRoot);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(file); encoder.Save(output);
    }
}
internal static class DemoExtensions
{
    public static JsonObject WithDemoActivity(this JsonObject obj) {
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        obj["activity"]=new JsonObject { ["kind"]="tools",["startedAtMs"]=now-20000,["observedAtMs"]=now,["elapsedMs"]=20000,["approximate"]=false,["items"]=JsonNode.Parse("""[{"id":"tool-a","label":"终端执行"},{"id":"tool-b","label":"网页检索"}]""") };
        return obj;
    }
    public static JsonObject WithAttention(this JsonObject obj, bool attention, string level = "warning")
    {
        if (!attention) return obj;
        obj["tools"]!["attention"] = level == "info" ? 0 : 1;
        obj["tools"]!["notes"] = level == "info" ? 1 : 0;
        obj["tools"]!["highestSeverity"] = level;
        obj["tools"]!["levelCounts"] = new JsonObject { ["info"] = level == "info" ? 1 : 0, ["warning"] = level == "warning" ? 1 : 0, ["error"] = level == "error" ? 1 : 0, ["critical"] = level == "critical" ? 1 : 0 };
        var issue = JsonNode.Parse("""{"id":"demo-read","turnId":"demo-turn","label":"会话读取","name":"read_thread","status":"completed","severity":"warning","reason":"返回内容不完整","detail":"已完成，但 3 轮内容为空。","durationMs":106,"resolved":false}""")!;
        issue["severity"] = level;
        if (level == "critical") { issue["label"] = "工具服务"; issue["reason"] = "启动失败"; issue["detail"] = "启动目录无效（系统错误 267），需要检查服务路径。"; issue["name"] = "codex_app"; }
        if (level == "error") { issue["label"] = "终端执行"; issue["reason"] = "访问权限不足"; issue["detail"] = "目标服务拒绝了本次请求，工具服务仍可连接。"; issue["name"] = "exec_command"; issue["exitCode"] = 1; }
        if (level == "info") { issue["label"] = "终端执行"; issue["reason"] = "未找到匹配"; issue["detail"] = "检索正常结束，没有匹配项。"; issue["name"] = "exec_command"; issue["exitCode"] = 1; }
        if (level != "info") obj["tools"]!["issues"]!.AsArray().Add(issue.DeepClone());
        obj["tools"]!["items"]!.AsArray().Add(issue.DeepClone()); return obj;
    }
}
