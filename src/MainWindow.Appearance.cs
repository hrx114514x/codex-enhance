using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms=System.Windows.Forms;

namespace CodexEnhance;
public partial class MainWindow
{
    private sealed class ColorDialogOwner : Forms.IWin32Window
    {
        public IntPtr Handle { get; }
        public ColorDialogOwner(IntPtr handle){Handle=handle;}
    }
    private void OpenAppearance(object sender,RoutedEventArgs e)
    {
        var stack=DetailStack();
        var themeRow=new Grid {Margin=new Thickness(0,0,0,22)};
        for(int i=0;i<3;i++)themeRow.ColumnDefinitions.Add(new ColumnDefinition());
        int column=0;
        foreach(var (label,mode) in new[]{("跟随系统","system"),("浅色","light"),("深色","dark")}) {
            var button=Segment(label,"主题 · "+label);button.Margin=new Thickness(column==0?0:4,0,column==2?0:4,0);Grid.SetColumn(button,column++);SelectSegment(button,settings.Theme==mode);themeRow.Children.Add(button);
            button.Click+=(_,_)=>{settings.Theme=mode;Save();ApplyTheme(true);OpenAppearance(this,new RoutedEventArgs());};
        }
        stack.Children.Add(themeRow);
        bool light=appliedTheme=="light";
        var colors=light?(settings.CustomLightColors??=new()):(settings.CustomDarkColors??=new());
        var fields=new Dictionary<string,TextBox>();
        Section(stack,"色彩预设");
        var presets=new Grid();for(int i=0;i<4;i++)presets.ColumnDefinitions.Add(new ColumnDefinition());column=0;
        foreach(var (label,day,night) in new[]{("青瓷","#177D8A","#79CDD4"),("海蓝","#326EC2","#8AB8FF"),("紫藤","#7954B3","#C1A4EE"),("暖砂","#92662D","#E4BC80")}) {
            string hex=light?day:night;var content=new StackPanel {Orientation=Orientation.Horizontal};
            content.Children.Add(new Border {Width=11,Height=11,CornerRadius=new CornerRadius(5.5),Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),Margin=new Thickness(0,0,7,0)});content.Children.Add(Text(label,"Text",11));
            var button=new Button {Content=content,Padding=new Thickness(8,11,8,11),Margin=new Thickness(column==0?0:3,0,column==3?0:3,0),Background=BrushFor("Panel")};Grid.SetColumn(button,column++);presets.Children.Add(button);
            AutomationProperties.SetName(button,"配色预设 · "+label);
            button.Click+=(_,_)=>{colors.Clear();colors["Accent"]=hex;Save();ApplyTheme(true);OpenAppearance(this,new RoutedEventArgs());};
        }
        stack.Children.Add(presets);
        Section(stack,(light?"浅色":"深色")+"主题 · 自定义");
        Note(stack,"输入 #RRGGBB 色值，或点击右侧色块选色。深浅主题分别保存。");
        foreach(var (label,key) in new[]{("强调色","Accent"),("窗口背景","Surface"),("卡片背景","Panel"),("主要文字","Text"),("次要文字","Muted")}) {
            var row=new Grid {Margin=new Thickness(0,7,0,0)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(108)});row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(44)});
            var text=Text(label,"Muted",12);text.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(text);
            var input=new TextBox {Text=((SolidColorBrush)BrushFor(key)).Color.ToString().Replace("#FF","#"),FontFamily=new FontFamily("Cascadia Mono, Consolas"),FontSize=12,Padding=new Thickness(8),BorderThickness=new Thickness(1),MaxLength=7,VerticalContentAlignment=VerticalAlignment.Center};
            input.SetResourceReference(Control.BackgroundProperty,"Panel");input.SetResourceReference(Control.ForegroundProperty,"Text");input.SetResourceReference(Control.BorderBrushProperty,"Stroke");input.SetResourceReference(TextBox.CaretBrushProperty,"Text");AutomationProperties.SetName(input,label+"色值");Grid.SetColumn(input,1);row.Children.Add(input);fields[key]=input;
            var swatch=new Button {Style=(Style)FindResource("ColorSwatch"),Width=34,Height=34,Margin=new Thickness(10,0,0,0),ToolTip="选择"+label,Background=BrushFor(key)};Grid.SetColumn(swatch,2);row.Children.Add(swatch);
            AutomationProperties.SetName(swatch,"选择"+label);
            input.TextChanged+=(_,_)=>{if(DesignTokens.TryColor(input.Text,out var hex))swatch.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));};
            swatch.Click+=(_,_)=>{
                using var dialog=new Forms.ColorDialog {FullOpen=true};
                if(DesignTokens.TryColor(input.Text,out var hex))dialog.Color=System.Drawing.ColorTranslator.FromHtml(hex);
                if(dialog.ShowDialog(new ColorDialogOwner(new WindowInteropHelper(detailWindow!).Handle))==Forms.DialogResult.OK)input.Text=$"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            };
            stack.Children.Add(row);
        }
        var feedback=Text("","Muted",11);feedback.Margin=new Thickness(0,12,0,0);stack.Children.Add(feedback);
        var actions=new Grid {Margin=new Thickness(0,17,0,0)};actions.ColumnDefinitions.Add(new ColumnDefinition());actions.ColumnDefinitions.Add(new ColumnDefinition());
        var apply=Segment("应用并保存","应用自定义配色");apply.Background=BrushFor("AccentSoft");apply.Margin=new Thickness(0,0,4,0);actions.Children.Add(apply);
        var reset=Segment("恢复默认配色","恢复默认配色");reset.Margin=new Thickness(4,0,0,0);Grid.SetColumn(reset,1);actions.Children.Add(reset);stack.Children.Add(actions);
        apply.Click+=(_,_)=>{
            var values=new Dictionary<string,string>();foreach(var pair in fields){if(!DesignTokens.TryColor(pair.Value.Text,out var hex)){feedback.Text="请填写有效的六位色值，例如 #177D8A。";pair.Value.Focus();return;}values[pair.Key]=hex;}
            colors.Clear();foreach(var pair in values)colors[pair.Key]=pair.Value;Save();ApplyTheme(true);
            double contrast=DesignTokens.Contrast(values["Text"],values["Surface"]);feedback.Text=contrast<4.5?$"已保存 · 文字对比度 {contrast:0.0}:1，可加深文字或调亮背景以便阅读。":"已保存，主浮窗与详情页已同步更新。";
        };
        reset.Click+=(_,_)=>{colors.Clear();Save();ApplyTheme(true);OpenAppearance(this,new RoutedEventArgs());};
        Detail("Codex · 外观与配色",new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
    }
}
