using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;

namespace CodexEnhance;
public partial class MainWindow
{
    private void RenderDesignGallery(string directory,JsonObject source)
    {
        var checks=new Dictionary<string,bool>();
        var oldLight=settings.CustomLightColors;var oldDark=settings.CustomDarkColors;string oldTheme=settings.Theme;
        settings.CustomLightColors=new();settings.CustomDarkColors=new();settings.Theme="light";ApplyTheme(true);ApplySnapshot(source);disclosure.SetExpanded(true);UpdateDisclosure();
        Button Named(string name)=>GalleryNodes(detailWindow!).OfType<Button>().Single(b=>AutomationProperties.GetName(b)==name);
        IEnumerable<TextBlock> VisibleText()=>GalleryNodes(detailWindow!).OfType<TextBlock>().Where(t=>t.IsVisible);
        ConversationCostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));detailWindow!.UpdateLayout();
        checks["conversationEntry"]=VisibleText().Any(t=>t.Text=="累计等效用量")&&!VisibleText().Any(t=>t.Text=="本周剩余");
        Named("查看账号额度分区").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));detailWindow!.UpdateLayout();
        checks["accountTab"]=VisibleText().Any(t=>t.Text=="本周剩余")&&!VisibleText().Any(t=>t.Text=="累计等效用量");
        checks["accountAmounts"]=VisibleText().Any(t=>t.Text=="周总额 · 估算"&&t.ActualHeight>0);
        CaptureGalleryDetail(Path.Combine(directory,"design-account-light.png"));
        var bounds=GalleryNodes(detailWindow!).OfType<FrameworkElement>().Where(x=>x is TextBlock||x is StackPanel||x is Grid).Select(x=>new {type=x.GetType().Name,text=(x as TextBlock)?.Text,visible=x.IsVisible,visibility=x.Visibility.ToString(),height=x.ActualHeight,desired=x.DesiredSize.Height}).ToArray();
        File.WriteAllText(Path.Combine(directory,"design-layout.json"),JsonSerializer.Serialize(bounds,Settings.JsonOptions));
        OpenAppearance(this,new RoutedEventArgs());detailWindow!.UpdateLayout();CaptureGalleryDetail(Path.Combine(directory,"appearance-light.png"));
        var accent=GalleryNodes(detailWindow!).OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="强调色色值");
        accent.Text="#7954B3";Named("应用自定义配色").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["customApplies"]=((SolidColorBrush)BrushFor("Accent")).Color==(Color)ColorConverter.ConvertFromString("#7954B3")&&settings.CustomLightColors["Accent"]=="#7954B3"&&settings.CustomDarkColors.Count==0;
        var restored=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings,Settings.JsonOptions),Settings.JsonOptions)!;checks["customPersists"]=restored.CustomLightColors["Accent"]=="#7954B3";
        accent.Text="oops";Named("应用自定义配色").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));checks["invalidRejected"]=settings.CustomLightColors["Accent"]=="#7954B3";
        ApplySnapshot(source);UpdateLayout();CaptureScene(Path.Combine(directory,"custom-purple-main.png"));
        Named("恢复默认配色").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));checks["reset"]=settings.CustomLightColors.Count==0&&((SolidColorBrush)BrushFor("Accent")).Color==(Color)ColorConverter.ConvertFromString(DesignTokens.Palette(true)["Accent"]);
        Named("主题 · 深色").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));detailWindow!.UpdateLayout();CaptureGalleryDetail(Path.Combine(directory,"appearance-dark.png"));
        checks["themeChoice"]=settings.Theme=="dark";
        Named("配色预设 · 暖砂").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));checks["preset"]=settings.CustomDarkColors["Accent"]=="#E4BC80";
        detailWindow?.Close();detailWindow=null;
        var longTitle=source.DeepClone().AsObject();longTitle["title"]=new string('长',80);ApplySnapshot(longTitle);UpdateLayout();
        var measured=new {titleWidth=TaskTitle.ActualWidth,cardWidth=Card.ActualWidth,dpi=Native.GetDpiForWindow(handle)};
        File.WriteAllText(Path.Combine(directory,"design-dimensions.json"),JsonSerializer.Serialize(measured,Settings.JsonOptions));
        checks["longTitleBounded"]=TaskTitle.ActualWidth<=233&&Card.ActualWidth<=355; // One DIP for device-pixel layout rounding.
        ExpandedScroll.MaxHeight=360;UpdateLayout();checks["smallScreenScrolls"]=ExpandedScroll.ScrollableHeight>0;ExpandedScroll.ScrollToEnd();UpdateLayout();
        CaptureScene(Path.Combine(directory,"small-screen-scroll.png"));ExpandedScroll.ScrollToHome();
        bool Hits(UIElement target)
        {
            var element=(FrameworkElement)target;
            var point=element.TranslatePoint(new Point(element.ActualWidth/2,element.ActualHeight/2),this);
            var hit=InputHitTest(point) as DependencyObject;
            while(hit is not null){if(ReferenceEquals(hit,target))return true;hit=hit is Visual?VisualTreeHelper.GetParent(hit):LogicalTreeHelper.GetParent(hit);}
            return false;
        }
        disclosure.SetExpanded(false);UpdateDisclosure();UpdateLayout();
        checks["collapsedReceivesMouse"]=Hits(Pill);
        disclosure.SetExpanded(true);UpdateDisclosure();UpdateLayout();
        checks["expandedReceivesMouse"]=Hits(ConversationCostButton);
        foreach(bool light in new[]{true,false}){var p=DesignTokens.Palette(light);checks[light?"lightContrast":"darkContrast"]=DesignTokens.Contrast(p["Text"],p["Panel"])>=4.5&&DesignTokens.Contrast(p["Muted"],p["Surface"])>=4.5;}
        settings.CustomLightColors=oldLight;settings.CustomDarkColors=oldDark;settings.Theme=oldTheme;ApplyTheme(true);ApplySnapshot(source);
        File.WriteAllText(Path.Combine(directory,"design-check.json"),JsonSerializer.Serialize(new {checks,passed=checks.Values.All(x=>x)},Settings.JsonOptions));
    }
}
