using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CodexEnhance;
public partial class MainWindow
{
    private JsonNode? CurrentConversationCost => S(snapshot["conversationCost"]?["threadId"]) == S(snapshot["threadId"])
        ? snapshot["conversationCost"] : null;
    internal static double? ConversationAmount(JsonNode? cost, bool astra, bool fast)
    {
        if (S(cost?["state"]) != "ready" || N(cost?["requests"]) > 0 && N(cost?["requests"]) == N(cost?["unpricedRequests"])) return null;
        return N(cost?["quotaBaseUsd"]) + (astra ? N(cost?["astraPremiumUsd"]) ?? 0 : 0)
            + (fast ? (N(cost?["quotaFastPremiumUsd"]) ?? 0) + (astra ? N(cost?["astraFastPremiumUsd"]) ?? 0 : 0) : 0);
    }
    private bool ConversationPartial(JsonNode? cost) => N(cost?["unpricedRequests"]) > 0 || N(cost?["parseErrors"]) > 0
        || N(cost?["readErrors"]) > 0 || N(cost?["unsupportedFiles"]) > 0 || settings.QuotaNormalizeFast && N(cost?["unnormalizedRequests"]) > 0;
    private static string CostMoney(double? amount) => amount is > 0 and < .01 ? "$"+amount.Value.ToString("0.0000") : Money(amount);
    private string ConversationAmountText(JsonNode? cost)
    {
        double? amount = ConversationAmount(cost, settings.QuotaIncludeAstraLongContext, settings.QuotaNormalizeFast);
        return amount is null ? B(cost?["refreshing"]) ? "统计中…" : S(cost?["error"]) != "" ? "刷新失败" : "暂无估算"
            : (ConversationPartial(cost) ? "部分 ≈" : "≈") + CostMoney(amount);
    }
    private void UpdateConversationCostSummary()
    {
        ConversationCostSummary.Text = ConversationAmountText(CurrentConversationCost);
        ConversationCostSummary.ToolTip = "此对话本机模型用量 · 更新于 " + LocalTime(N(CurrentConversationCost?["sampledAtMs"]));
    }
    private Action AddConversationCost(StackPanel stack)
    {
        var heading = new DockPanel();
        var refresh = new Button { Content="刷新", FontSize=11, Padding=new Thickness(9,4,9,4), Foreground=BrushFor("Muted") };
        AutomationProperties.SetName(refresh,"刷新此对话费用"); DockPanel.SetDock(refresh,Dock.Right); heading.Children.Add(refresh);
        var label=Text("此对话费用估算","Text",14); label.FontWeight=FontWeights.SemiBold; label.VerticalAlignment=VerticalAlignment.Center; heading.Children.Add(label); stack.Children.Add(heading);
        var title=Text("","Muted",11); title.Margin=new Thickness(0,8,0,0); title.TextTrimming=TextTrimming.CharacterEllipsis; title.TextWrapping=TextWrapping.NoWrap; stack.Children.Add(title);
        var amount=Text("","Text",30); amount.FontWeight=FontWeights.SemiBold; amount.Margin=new Thickness(0,9,0,0); stack.Children.Add(amount);
        var scopeRow=new Grid { Margin=new Thickness(0,9,0,1) };scopeRow.ColumnDefinitions.Add(new ColumnDefinition());scopeRow.ColumnDefinitions.Add(new ColumnDefinition());
        var turnAmount=Text("","Muted",12);var todayAmount=Text("","Muted",12);Grid.SetColumn(todayAmount,1);scopeRow.Children.Add(turnAmount);scopeRow.Children.Add(todayAmount);stack.Children.Add(scopeRow);
        scopeRow.ToolTip="本轮为当前一轮任务；今天从本机零点起算。均只计已记录的用量。";
        var status=Text("","Faint",11); status.Margin=new Thickness(0,6,0,0); stack.Children.Add(status);
        var automaticRow=new Grid { Margin=new Thickness(0,14,0,0) };
        automaticRow.ColumnDefinitions.Add(new ColumnDefinition()); automaticRow.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var autoLabel=Text("自动刷新 · 每 10 秒","Muted",12); autoLabel.VerticalAlignment=VerticalAlignment.Center; automaticRow.Children.Add(autoLabel);
        var automatic=new ToggleButton { Style=(Style)FindResource("QuotaSwitch"), IsChecked=settings.ConversationCostAutoRefresh };
        AutomationProperties.SetName(automatic,"自动刷新此对话费用"); Grid.SetColumn(automatic,1); automaticRow.Children.Add(automatic); stack.Children.Add(automaticRow);
        automatic.Click+=(_,_)=>{
            settings.ConversationCostAutoRefresh=automatic.IsChecked==true; Save();
            collector?.Send(new JsonObject { ["type"]="conversationCostAutomatic",["enabled"]=settings.ConversationCostAutoRefresh });
            refreshDetail?.Invoke();
        };
        DateTimeOffset queuedUntil=DateTimeOffset.MinValue;
        refresh.Click+=(_,_)=>{
            if(!preview&&collector?.IsRunning!=true)StartCollector();
            Send("refreshConversationCost",S(snapshot["threadId"])); queuedUntil=DateTimeOffset.UtcNow.AddSeconds(2); refreshDetail?.Invoke();
        };
        bool expanded=false;string detailKey="";
        var disclosure=new Button { Content="用量明细  ›", FontSize=11, Foreground=BrushFor("Muted"), HorizontalAlignment=HorizontalAlignment.Left, Padding=new Thickness(0,9,6,3) };
        AutomationProperties.SetName(disclosure,"展开此对话用量明细"); stack.Children.Add(disclosure);
        var details=new StackPanel { Visibility=Visibility.Collapsed }; stack.Children.Add(details);
        disclosure.Click+=(_,_)=>{ expanded=!expanded; details.Visibility=expanded?Visibility.Visible:Visibility.Collapsed; disclosure.Content=expanded?"用量明细  ⌄":"用量明细  ›"; refreshDetail?.Invoke(); };
        stack.Children.Add(new Border { Height=1,Background=BrushFor("Line"),Margin=new Thickness(0,17,0,16) });
        return () => {
            var cost=CurrentConversationCost;
            title.Text=S(snapshot["title"],"未选择对话"); title.ToolTip=title.Text;
            amount.Text=ConversationAmountText(cost);
            var currentTurn=S(cost?["turnId"])==S(snapshot["turnId"])?cost?["turn"]:null;
            long dayStart=new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
            var today=N(cost?["dayStartMs"])==dayStart?cost?["today"]:null;
            turnAmount.Text="本轮  "+(currentTurn is null?"—":ConversationAmountText(currentTurn));
            todayAmount.Text="今天  "+(today is null?"—":ConversationAmountText(today));
            bool busy=B(cost?["refreshing"]), queued=DateTimeOffset.UtcNow<queuedUntil;
            refresh.Content=busy?"统计中…":queued?"已请求":"刷新";
            refresh.IsEnabled=!busy&&!queued&&S(snapshot["threadId"])!="";
            string counts=CompactNumber(N(cost?["requests"]))+" 次请求 · "+CompactNumber(N(cost?["tokens"]))+" tokens";
            status.Text=cost is null?"等待选择本地对话":S(cost["state"])=="ready"?counts+"\n"+(settings.ConversationCostAutoRefresh?"自动更新":"手动更新")+" · "+LocalTime(N(cost["sampledAtMs"]))
                :S(cost["reason"])=="unsupported_provider"?"暂不支持此模型服务商":S(cost["state"])=="unavailable"?"尚无可读取的本机用量记录":busy?"首次统计中 · "+(100*(N(cost["progress"])??0)).ToString("0")+"%":"点击刷新以读取用量";
            if(S(cost?["error"])!="")status.Text+="\n刷新失败，保留上次结果；可点击重试。";
            else if(settings.ConversationCostAutoRefresh&&N(cost?["sampledAtMs"]) is double sampled&&DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-sampled>30000)
                status.Text+="\n更新延迟，当前为上次结果。";
            if(!expanded)return;
            string next=(cost?.ToJsonString()??"")+settings.QuotaIncludeAstraLongContext+settings.QuotaNormalizeFast;
            if(next==detailKey)return;detailKey=next;
            details.Children.Clear();
            MetricRow(details,"输入 / 缓存命中")(CompactNumber(N(cost?["input"]))+" / "+CompactNumber(N(cost?["cached"])));
            MetricRow(details,"输出（含思考）")(CompactNumber(N(cost?["output"])));
            MetricRow(details,"普通计价")(Money(N(cost?["quotaBaseUsd"])));
            MetricRow(details,"Astra 长上下文")(settings.QuotaIncludeAstraLongContext?"+"+Money(N(cost?["astraPremiumUsd"])):"未计入");
            MetricRow(details,"Fast 用量折算")((N(cost?["fastRequests"])??0)<=0?"未记录 Fast 用量":settings.QuotaNormalizeFast?"+"+Money((N(cost?["quotaFastPremiumUsd"])??0)+(settings.QuotaIncludeAstraLongContext?N(cost?["astraFastPremiumUsd"])??0:0)):"未计入");
            foreach(var model in (cost?["models"] as JsonArray??new()).OfType<JsonObject>())
                MetricRow(details,S(model["model"]))((N(model["unpriced"])??0)>0?"价格待补全":Money((N(model["quotaBaseUsd"])??0)+(settings.QuotaIncludeAstraLongContext?N(model["astraPremiumUsd"])??0:0)+(settings.QuotaNormalizeFast?(N(model["quotaFastPremiumUsd"])??0)+(settings.QuotaIncludeAstraLongContext?N(model["astraFastPremiumUsd"])??0:0):0)));
            Note(details,"与下方两个计价开关联动。金额为等效用量估算，非实际扣款；仅此对话本机记录，不含子任务、语音或其他设备。");
            Note(details,"正在生成的请求，在用量写入后计入。本轮与今天的金额也遵循相同计价开关。");
            if(N(cost?["assumedTierRequests"])>0)Note(details,$"{N(cost?["assumedTierRequests"]):0} 条未记录速度，按普通速度估算。");
            if(ConversationPartial(cost))Note(details,"部分价格、倍率或记录缺失，当前仅显示可计算部分。");
            Note(details,"记录范围 "+LocalTime(N(cost?["fromAtMs"]))+" — "+LocalTime(N(cost?["toAtMs"]))+"\n价格参考 "+S(cost?["pricingDate"],"待确认"));
        };
    }
}
