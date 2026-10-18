using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace CodexEnhance;
public partial class MainWindow
{
    private void RenderNetworkGallery(string directory,JsonObject source)
    {
        var data=source.DeepClone().AsObject();long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        data["network"]=JsonNode.Parse("""
        {"enabled":true,"checking":false,"status":"receiving","label":"模型数据仍在更新","detail":"短连接探测可达，当前聊天仍有模型事件；长时间思考不能直接归因于节点。","intervalMs":15000,
         "route":{"mode":"tun","label":"示例节点 · 圣何塞"},
         "codex":{"last":{"state":"response","status":401,"totalMs":610},"count":20,"timeouts":0,"stale":false,"samples":[]},
         "reference":{"last":{"state":"response","status":204,"totalMs":340},"count":20,"timeouts":0,"stale":false,"samples":[]},
         "stream":{"eventGapMs":8000,"headersMs":4000,"errors":0,"actualEffort":"high","selectedEffort":"medium","effortMismatch":true}}
        """);
        var n=data["network"]!;n["checkedAtMs"]=now;
        for(int i=0;i<5;i++)foreach(string key in new[]{"codex","reference"})
            ((JsonArray)n[key]!["samples"]!).Add(new JsonObject {["state"]="response",["atMs"]=now-(4-i)*15000,["totalMs"]=key=="codex"?610+i*8:340+i*7});
        settings.Theme="dark";ApplyTheme();ApplySnapshot(data);Capture(Path.Combine(directory,"network-dark.png"));
        NetworkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));CaptureGalleryDetail(Path.Combine(directory,"network-detail-dark.png"));
        bool opened=detailWindow?.Title=="Codex · 网络与响应";
        var texts=GalleryNodes(detailWindow!).OfType<TextBlock>().Select(x=>x.Text).ToArray();
        bool mismatch=texts.Contains("中 / 高")&&texts.Any(t=>t.Contains("设置与请求记录不一致"));
        bool expectedHttp=texts.Contains("401 / 204")&&texts.Any(t=>t.Contains("401 是预期响应"));
        detailWindow?.Close();detailWindow=null;
        settings.Theme="light";ApplyTheme();ApplySnapshot(data);Capture(Path.Combine(directory,"network-light.png"));
        OpenNetwork(this,new RoutedEventArgs());CaptureGalleryDetail(Path.Combine(directory,"network-detail-light.png"));detailWindow?.Close();detailWindow=null;
        var faulty=data.DeepClone().AsObject();faulty["network"]!["status"]="path_suspect";faulty["network"]!["label"]="网络路径可疑";faulty["network"]!["detail"]="两个目标连续异常；尚不能区分本地网络与节点。";
        ApplySnapshot(faulty);bool bounded=NetworkSummary.Text=="网络路径可疑";Capture(Path.Combine(directory,"network-suspect.png"));
        var absent=data.DeepClone().AsObject();absent.Remove("network");ApplySnapshot(absent);bool clears=NetworkSummary.Text=="等待采样"&&NetworkCaption.Text.Contains("未记录");
        File.WriteAllText(Path.Combine(directory,"network-check.json"),System.Text.Json.JsonSerializer.Serialize(new {opened,mismatch,expectedHttp,bounded,clears,passed=opened&&mismatch&&expectedHttp&&bounded&&clears},Settings.JsonOptions));
        ApplySnapshot(source);
    }
}
