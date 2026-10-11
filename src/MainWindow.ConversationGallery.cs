using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CodexEnhance;
public partial class MainWindow
{
    private void RenderConversationGallery(string directory,JsonObject data)
    {
        var sample=data.DeepClone().AsObject();
        var cost=JsonNode.Parse("""{"state":"ready","requests":128,"tokens":18200000,"quotaBaseUsd":12,"astraPremiumUsd":3,"quotaFastPremiumUsd":6,"astraFastPremiumUsd":1.5,"fastRequests":8,"assumedTierRequests":12,"unpricedRequests":0,"pricingDate":"2026-10-02","models":[]}""")!;
        cost["threadId"]=S(sample["threadId"]);cost["sampledAtMs"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        cost["turnId"]=S(sample["turnId"]);cost["dayStartMs"]=new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
        cost["turn"]=JsonNode.Parse("""{"state":"ready","requests":1,"quotaBaseUsd":1,"astraPremiumUsd":0.2,"quotaFastPremiumUsd":0.5,"astraFastPremiumUsd":0.1}""");
        cost["today"]=JsonNode.Parse("""{"state":"ready","requests":4,"quotaBaseUsd":4,"astraPremiumUsd":1,"quotaFastPremiumUsd":2,"astraFastPremiumUsd":0.5}""");
        sample["conversationCost"]=cost;
        bool astra=settings.QuotaIncludeAstraLongContext,fast=settings.QuotaNormalizeFast,automatic=settings.ConversationCostAutoRefresh;
        settings.QuotaIncludeAstraLongContext=false;settings.QuotaNormalizeFast=true;settings.ConversationCostAutoRefresh=true;
        settings.Theme="dark";ApplyTheme();ApplySnapshot(sample);
        ConversationCostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool entry=detailWindow?.Title=="Codex · 费用与额度"&&ConversationCostSummary.Text=="≈$18.00";
        UpdateLayout();CaptureScene(Path.Combine(directory,"conversation-main-dark.png"));CaptureGalleryDetail(Path.Combine(directory,"conversation-cost-dark.png"));
        var toggles=GalleryNodes(detailWindow!).OfType<ToggleButton>().ToArray();
        var astraToggle=toggles.Single(t=>AutomationProperties.GetName(t)=="计入 Astra 长上下文加价");
        astraToggle.IsChecked=true;astraToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool sharedSwitch=ConversationCostSummary.Text=="≈$22.50";
        var scopeTexts=GalleryNodes(detailWindow!).OfType<TextBlock>().Select(t=>t.Text).ToArray();
        bool scopes=scopeTexts.Contains("本轮  ≈$1.80")&&scopeTexts.Contains("今天  ≈$7.50");
        var autoToggle=toggles.Single(t=>AutomationProperties.GetName(t)=="自动刷新此对话费用");autoToggle.IsChecked=false;autoToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool manual=!settings.ConversationCostAutoRefresh;
        GalleryNodes(detailWindow!).OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="刷新此对话费用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool refresh=GalleryNodes(detailWindow!).OfType<Button>().Any(b=>Equals(b.Content,"已请求"));
        settings.Theme="light";ApplyTheme();refreshDetail?.Invoke();UpdateLayout();CaptureScene(Path.Combine(directory,"conversation-main-light.png"));CaptureGalleryDetail(Path.Combine(directory,"conversation-cost-light.png"));
        GalleryNodes(detailWindow!).OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="展开此对话用量明细").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));detailWindow!.UpdateLayout();
        var cachedRow=GalleryNodes(detailWindow!).OfType<TextBlock>().Single(t=>t.Text=="输入 / 缓存命中");refreshDetail?.Invoke();
        bool stableDetail=ReferenceEquals(cachedRow,GalleryNodes(detailWindow!).OfType<TextBlock>().Single(t=>t.Text=="输入 / 缓存命中"));
        cost["unpricedRequests"]=2;ApplySnapshot(sample);bool partial=ConversationCostSummary.Text.StartsWith("部分 ≈");
        sample["threadId"]="another-thread";ApplySnapshot(sample);bool switched=!ConversationCostSummary.Text.Contains("22.50");
        bool migration=JsonSerializer.Deserialize<Settings>("{}",Settings.JsonOptions)!.ConversationCostAutoRefresh;
        var saved=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings,Settings.JsonOptions),Settings.JsonOptions)!;bool persistence=!saved.ConversationCostAutoRefresh;
        File.WriteAllText(Path.Combine(directory,"conversation-cost-check.json"),JsonSerializer.Serialize(new {entry,sharedSwitch,scopes,stableDetail,manual,refresh,partial,switched,migration,persistence,passed=entry&&sharedSwitch&&scopes&&stableDetail&&manual&&refresh&&partial&&switched&&migration&&persistence},Settings.JsonOptions));
        detailWindow?.Close();detailWindow=null;settings.QuotaIncludeAstraLongContext=astra;settings.QuotaNormalizeFast=fast;settings.ConversationCostAutoRefresh=automatic;
    }
}
