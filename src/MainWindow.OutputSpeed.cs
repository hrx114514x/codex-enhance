using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace CodexEnhance;
public partial class MainWindow
{
    internal static (string Value,string Source) OutputSpeedPresentation(JsonNode? speed)
    {
        double? rate=N(speed?["tokensPerSecond"]);
        if(S(speed?["state"])=="measured"&&rate is >0&&double.IsFinite(rate.Value))
            return ("≈"+rate.Value.ToString(rate>=1000?"0":"0.0"),"最近请求");
        return ("—",S(speed?["reason"]) switch {"missing_timing"=>"时点未记录","short_sample"=>"样本过短","no_output"=>"无输出",_=>"等待记录"});
    }
    private void UpdateOutputSpeed()
    {
        var view=OutputSpeedPresentation(snapshot["outputSpeed"]);
        OutputSpeedText.Text=view.Value;OutputSpeedSource.Text=view.Source;
        OutputSpeedButton.ToolTip="最近一次请求的平均输出速度，含思考 token。请求结束后更新，点击查看详情。";
    }
    private void OpenOutputSpeed(object sender,RoutedEventArgs e)
    {
        var stack=DetailStack();
        var hero=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,4)};
        var value=Text("—","Text",32);value.FontWeight=FontWeights.SemiBold;hero.Children.Add(value);
        var unit=Text("tok/s","Muted",13);unit.Margin=new Thickness(8,0,0,6);unit.VerticalAlignment=VerticalAlignment.Bottom;hero.Children.Add(unit);stack.Children.Add(hero);
        var state=Text("","Muted",12);stack.Children.Add(state);
        Section(stack,"最近请求");
        var tokens=MetricRow(stack,"输出 token");var reasoning=MetricRow(stack,"其中思考 token");
        var duration=MetricRow(stack,"生成时长");var sampled=MetricRow(stack,"记录时间");
        Section(stack,"本轮最近请求");
        var trend=new Grid {Height=82,Margin=new Thickness(0,3,0,2)};stack.Children.Add(trend);
        var caption=Text("","Faint",11);stack.Children.Add(caption);
        Note(stack,"按输出片段的起止时间估算，请求结束后更新。工具时间和输出开始前的等待不计入。");
        var dialog=Detail("Codex · 输出速度",new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});dialog.Height=Math.Min(dialog.Height,540);
        string previous="";
        refreshDetail=()=>{
            var speed=snapshot["outputSpeed"];var view=OutputSpeedPresentation(speed);value.Text=view.Value;
            state.Text=view.Source+" · 本地观测均速";
            tokens(N(speed?["outputTokens"])?.ToString("N0")??"—");reasoning(N(speed?["reasoningTokens"])?.ToString("N0")??"未单列");
            duration(S(speed?["state"])=="measured"?Duration(N(speed?["durationMs"])):"—");sampled(LocalTime(N(speed?["recordedAtMs"])));
            string key=speed?["samples"]?.ToJsonString()??"[]";if(previous==key)return;previous=key;
            trend.Children.Clear();trend.ColumnDefinitions.Clear();
            var samples=(speed?["samples"] as JsonArray)?.OfType<JsonObject>().TakeLast(8).ToArray()??Array.Empty<JsonObject>();
            double maximum=Math.Max(1,samples.Select(s=>N(s["tokensPerSecond"])??0).DefaultIfEmpty(1).Max());
            caption.Text=samples.Length==0?"本轮尚无生成记录":"从左到右由早到晚 · 单位 tok/s";
            trend.Visibility=samples.Length==0?Visibility.Collapsed:Visibility.Visible;
            for(int i=0;i<samples.Length;i++) {
                trend.ColumnDefinitions.Add(new ColumnDefinition());double? rate=N(samples[i]["tokensPerSecond"]);
                var item=new StackPanel {VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(4,0,4,0),ToolTip=LocalTime(N(samples[i]["recordedAtMs"]))+" · "+OutputSpeedPresentation(samples[i]).Value+" tok/s"};
                var label=Text(rate?.ToString("0.0")??"—","Muted",10);label.HorizontalAlignment=HorizontalAlignment.Center;item.Children.Add(label);
                item.Children.Add(new Border {Height=rate is null?3:Math.Max(3,52*rate.Value/maximum),Background=BrushFor(rate is null?"Track":"Accent"),Opacity=i==samples.Length-1?1:.55,CornerRadius=new CornerRadius(3),Margin=new Thickness(0,5,0,0)});
                Grid.SetColumn(item,i);trend.Children.Add(item);
            }
        };
        refreshDetail();
    }
}
