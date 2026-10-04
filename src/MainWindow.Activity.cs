using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexEnhance;
public partial class MainWindow
{
    private string activityDetailKey = "";
    internal static (string Title, string Detail, string Timer, bool Active) ActivityPresentation(JsonObject data, double now, bool stale)
    {
        var a = data["activity"]; string kind = S(a?["kind"]);
        if (stale) return ("状态待更新", "等待最新状态", "—", false);
        if (kind is "complete" or "idle") return ("已完成", "查看本轮详情", "—", false);
        if (kind == "interrupted") return ("已停止", "查看本轮记录", "—", false);
        if (kind == "failed") return ("未完成", "点击查看本轮记录", "—", false);
        if (kind == "unknown" || N(a?["observedAtMs"]) is not { } observed || now-observed>5000)
            return ("状态待确认", "正在等待任务状态", "—", false);
        var title = kind switch { "tools"=>"调用工具", "model"=>"思考 / 响应", "waiting"=>"等待你操作", "compacting"=>"整理上下文", _=>"状态待确认" };
        double elapsed = Math.Max(0, now-(N(a?["startedAtMs"])??now));
        string detail = kind switch { "model"=>"正在处理你的请求", "waiting"=>"需要输入或确认后继续", "compacting"=>"正在整理当前对话", _=>"等待状态更新" };
        var items = (a?["items"] as JsonArray ?? new()).OfType<JsonObject>().ToArray();
        if(kind=="tools"&&items.Length>0) {
            int index=(int)(elapsed/3000)%items.Length;
            int count=(int)(N(a?["runningCount"])??items.Length);
            detail=count>1?$"{count} 项并行 · {S(items[index]["label"],"工具调用")}"+(count==items.Length?$"  {index+1}/{items.Length}":""):S(items[0]["label"],"工具调用")+" · 执行中";
        }
        return (title,detail,Clock(elapsed),true);
    }
    private void UpdateActivity(bool stale)
    {
        UpdateOutputSpeed();
        double now=renderMode?(N(snapshot["activity"]?["observedAtMs"])??0):DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if(preview&&!renderMode&&snapshot["activity"] is JsonObject sample)sample["observedAtMs"]=now;
        var view=ActivityPresentation(snapshot,now,stale);
        PhaseText.Text=view.Title; ElapsedText.Text=view.Timer; ElapsedText.Visibility=view.Active?Visibility.Visible:Visibility.Collapsed; PhaseTimeCaption.Visibility=view.Active?Visibility.Visible:Visibility.Collapsed;
        PhaseDot.Fill=BrushFor(view.Active?"Accent":"Faint"); PillPhase.Text=view.Title; PillMetric.Text=view.Timer=="—"?"":view.Timer; PillDot.Fill=PhaseDot.Fill;
        int critical=ActiveIssues().Count(i=>S(i["severity"])=="critical");if(critical>0){PillPhase.Text=$"Critical · {critical}";PillDot.Fill=BrushFor("Critical");}
        if(activityDetailKey!=view.Detail) {
            activityDetailKey=view.Detail; ActivityDetailText.Text=view.Detail;
            if(!renderMode&&SystemParameters.ClientAreaAnimation) {
                var shift=(TranslateTransform)ActivityDetailText.RenderTransform;
                shift.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(4,0,TimeSpan.FromMilliseconds(160)));
                ActivityDetailText.BeginAnimation(OpacityProperty,new DoubleAnimation(.35,1,TimeSpan.FromMilliseconds(160)));
            }
        }
        ActivityButton.ToolTip="查看本轮耗时与性能详情。模型阶段可能包含生成回复和等待。";
        ElapsedText.ToolTip="当前阶段持续时间，切换阶段后重新计时。";
    }
}
