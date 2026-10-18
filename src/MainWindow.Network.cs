using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace CodexEnhance;
public partial class MainWindow
{
    private static string NetworkLatency(JsonNode? probe)
    {
        var last=probe?["last"];
        if(last is null||B(probe?["stale"]))return "待采样";
        if(S(last["state"])=="unavailable")return "探测不可用";
        if(S(last["state"])=="failed")return S(last["error"])=="timeout"?"超时":"连接失败";
        return (N(last["totalMs"])/1000)?.ToString("0.00")+" 秒";
    }
    private static string EffortLabel(JsonNode? node)=>S(node) switch {"low"=>"低","medium"=>"中","high"=>"高","xhigh"=>"超高","max"=>"Max","ultra"=>"Ultra",""=>"未记录",_=>S(node)};
    private static string NetworkColor(JsonNode? node)=>S(node?["status"]) switch {"path_suspect" or "openai_path_suspect" or "stream_issue" or "fluctuation" or "http_issue" or "request_issue"=>"Attention","receiving"=>"Accent",_=>"Muted"};
    private void UpdateNetworkSummary()
    {
        var n=snapshot["network"];NetworkSummary.Text=S(n?["label"],"等待采样");NetworkSummary.Foreground=BrushFor(NetworkColor(n));
        var gap=N(n?["stream"]?["eventGapMs"]);
        string eventText=gap is null?"响应事件未记录":$"{Math.Floor(gap.Value/1000):0} 秒前收到事件";
        NetworkCaption.Text=$"Codex {NetworkLatency(n?["codex"])} · {eventText}";
        NetworkButton.ToolTip=S(n?["detail"],"点击查看网络探测与当前聊天的响应记录");
    }
    private void OpenNetwork(object sender,RoutedEventArgs e)
    {
        var stack=DetailStack();
        var summary=Text("等待采样","Text",18);summary.FontWeight=FontWeights.SemiBold;summary.Margin=new Thickness(0,8,0,6);stack.Children.Add(summary);
        var explanation=Text("","Muted",12);stack.Children.Add(explanation);
        Section(stack,"轻量连接探测");
        var route=MetricRow(stack,"当前 ChatGPT 路径");var mode=MetricRow(stack,"探测方式");
        var latency=MetricRow(stack,"Codex 接口");var reference=MetricRow(stack,"对照网站 · Google 静态服务");
        var failures=MetricRow(stack,"近 5 分钟超时 / 探测次数");var response=MetricRow(stack,"最近 HTTP 状态");var sampled=MetricRow(stack,"采样时间 / 周期");
        var controls=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,12,0,0)};
        var check=new Button {Content="立即探测",Padding=new Thickness(12,6,12,6)};check.Click+=(_,_)=>Send("checkNetwork");controls.Children.Add(check);
        var toggle=new Button {Content="暂停探测",Padding=new Thickness(12,6,12,6),Margin=new Thickness(10,0,0,0)};
        toggle.Click+=(_,_)=>{settings.NetworkProbeEnabled=!B(snapshot["network"]?["enabled"]);Save();collector?.Send(new JsonObject {["type"]="networkEnabled",["enabled"]=settings.NetworkProbeEnabled});};controls.Children.Add(toggle);stack.Children.Add(controls);
        Section(stack,"当前聊天 · 被动观测");
        var gap=MetricRow(stack,"距最近模型事件");var headers=MetricRow(stack,"最近请求至响应头");var errors=MetricRow(stack,"近 5 分钟连接异常记录");
        var effort=MetricRow(stack,"界面设置 / 最近请求强度");var mismatch=Text("","Attention",11);mismatch.Margin=new Thickness(0,8,0,0);stack.Children.Add(mismatch);
        Section(stack,"最近探测 · Codex / 对照");
        var history=Text("等待采样","Muted",11);stack.Children.Add(history);
        Note(stack,"Codex 探测不带登录凭据，HTTP 401 是预期响应。探测不调用模型、不消费模型 token。对照网站可能命中不同分流规则，不能据此锁定节点故障。");
        Note(stack,"响应事件来自本地日志，不是逐 token 心跳。请求至响应头包含上传、网络和服务端等待，不能当作纯网络延迟。探测正常也不保证长连接正常。");
        Detail("Codex · 网络与响应",new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        refreshDetail=()=>{
            var n=snapshot["network"];var c=n?["codex"];var r=n?["reference"];var s=n?["stream"];
            bool stale=!preview&&(collectorFailed||(DateTimeOffset.UtcNow-lastSnapshotReceived).TotalSeconds>6);
            summary.Text=stale?"采集已过期":S(n?["label"],"等待采样");summary.Foreground=BrushFor(stale?"Faint":NetworkColor(n));explanation.Text=stale?"等待新的采集结果，以下为最后一次记录。":S(n?["detail"]);
            route(S(n?["route"]?["label"],"路径未验证"));mode(S(n?["route"]?["mode"]) switch {"tun"=>"经本机 TUN","proxy"=>"经本机代理入口",_=>"系统网络 · 代理路径未验证"});
            latency(NetworkLatency(c));reference(NetworkLatency(r));
            failures($"Codex {N(c?["timeouts"])??0}/{N(c?["count"])??0} · 对照 {N(r?["timeouts"])??0}/{N(r?["count"])??0}");
            response($"{N(c?["last"]?["status"])?.ToString()??"—"} / {N(r?["last"]?["status"])?.ToString()??"—"}");
            sampled(LocalTime(N(n?["checkedAtMs"]))+" · "+(N(n?["intervalMs"])/1000)?.ToString("0")+" 秒");
            gap(Duration(N(s?["eventGapMs"])));headers(Duration(N(s?["headersMs"])));errors((N(s?["errors"])??0).ToString("0"));
            effort(EffortLabel(s?["selectedEffort"])+" / "+EffortLabel(s?["actualEffort"]));
            mismatch.Text=B(s?["effortMismatch"])?"设置与请求记录不一致；此处只提示，不修改任务。":"";
            toggle.Content=B(n?["enabled"])?"暂停探测":"恢复探测";check.IsEnabled=!stale&&B(n?["enabled"])&&!B(n?["checking"]);
            var samples=(c?["samples"] as JsonArray)?.OfType<JsonObject>().TakeLast(5).Reverse().ToArray()??Array.Empty<JsonObject>();
            var refs=(r?["samples"] as JsonArray)?.OfType<JsonObject>().ToArray()??Array.Empty<JsonObject>();
            history.Text=samples.Length==0?"等待采样":string.Join("\n",samples.Select(p=>{
                var other=refs.OrderBy(q=>Math.Abs((N(q["atMs"])??0)-(N(p["atMs"])??0))).FirstOrDefault();
                return LocalTime(N(p["atMs"]))+"   "+NetworkLatency(new JsonObject {["last"]=p.DeepClone()})+" / "+NetworkLatency(new JsonObject {["last"]=other?.DeepClone()});
            }));
        };refreshDetail();
    }
}
