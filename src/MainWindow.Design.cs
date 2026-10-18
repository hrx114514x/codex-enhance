using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexEnhance;
public partial class MainWindow
{
    private void UpdateDesignSummary()
    {
        CostRefreshHint.Text=settings.ConversationCostAutoRefresh?"每 10 秒更新":"手动刷新";
        CostRefreshHint.ToolTip="点击查看累计、本轮和今日用量，以及计价设置。";
        var area=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        double scale=Math.Max(1,Native.GetDpiForWindow(handle)/96d);
        ExpandedScroll.MaxHeight=Math.Max(260,area.Height/scale-56);
    }
    private static string DetailSubtitle(string title) => title.Replace("Codex · ","") switch {
        "外观与配色"=>"调成适合你的工作界面",
        "费用与额度"=>"查看用量，理解每一次消耗",
        "性能详情"=>"从首字到完成，了解这一轮的耗时",
        "输出速度"=>"最近请求的生成速度与变化",
        "当前模型"=>"区分选择、请求与实际响应的模型",
        "每周记录"=>"按实际重置周期回看用量",
        "调用记录"=>"追踪工具执行与需要关注的记录",
        "网络与响应"=>"连接探测与响应过程的只读观测",
        "连接与诊断"=>"查看采集状态与连接信息",
        _=>"当前对话 · 详细观测"
    };
    private Button Segment(string title,string automation)
    {
        var button=new Button {Content=title,FontSize=12,FontWeight=FontWeights.Medium,Padding=new Thickness(18,9,18,9),HorizontalContentAlignment=HorizontalAlignment.Center};
        System.Windows.Automation.AutomationProperties.SetName(button,automation);return button;
    }
    private void SelectSegment(Button button,bool selected)
    {
        button.SetResourceReference(Control.BackgroundProperty,selected?"AccentSoft":"Panel");
        button.SetResourceReference(Control.ForegroundProperty,selected?"AccentInk":"Muted");
    }
}
