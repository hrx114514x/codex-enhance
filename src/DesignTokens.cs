using System.Collections.Generic;
using System;
using System.Globalization;

namespace CodexEnhance;
// Shared semantic palette for the overlay, dialogs, menus and charts.
internal static class DesignTokens
{
    internal static readonly string[] Editable={"Accent","Surface","Panel","Text","Muted"};
    internal static bool TryColor(string? input,out string value)
    {
        value=(input??"").Trim().ToUpperInvariant();if(value.Length==6)value="#"+value;
        return value.Length==7&&value[0]=='#'&&uint.TryParse(value.AsSpan(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out _);
    }
    private static string Mix(string a,string b,double ratio)
    {
        string result="#";for(int i=1;i<7;i+=2)result+=((int)Math.Round(Convert.ToInt32(a.Substring(i,2),16)*(1-ratio)+Convert.ToInt32(b.Substring(i,2),16)*ratio)).ToString("X2");return result;
    }
    internal static Dictionary<string,string> Resolve(bool light,Dictionary<string,string>? custom)
    {
        var palette=Palette(light);if(custom is null||custom.Count==0)return palette;
        foreach(var key in Editable)if(custom.TryGetValue(key,out var raw)&&TryColor(raw,out var color))palette[key]=color;
        palette["Raised"]=Mix(palette["Panel"],palette["Text"],.03);
        palette["Stroke"]=Mix(palette["Surface"],palette["Text"],.18);palette["Line"]=Mix(palette["Surface"],palette["Text"],.10);
        palette["Track"]=Mix(palette["Panel"],palette["Text"],.16);palette["Hover"]=Mix(palette["Panel"],palette["Text"],.05);palette["Pressed"]=Mix(palette["Panel"],palette["Text"],.10);
        palette["Faint"]=Mix(palette["Muted"],palette["Surface"],.12);palette["AccentSoft"]=Mix(palette["Surface"],palette["Accent"],light?.10:.15);palette["AccentInk"]=palette["Accent"];
        return palette;
    }
    internal static double Contrast(string a,string b)
    {
        double L(string hex){double c(int offset){double v=Convert.ToInt32(hex.Substring(offset,2),16)/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}return .2126*c(1)+.7152*c(3)+.0722*c(5);}
        double x=L(a),y=L(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
    internal static Dictionary<string,string> Palette(bool light) => light
        ? new() { ["Surface"]="#FAFBFC",["Panel"]="#F0F4F6",["Raised"]="#FFFFFF",["Stroke"]="#D7E1E7",["Text"]="#192D38",["Muted"]="#536B79",["Faint"]="#6B808D",["Line"]="#E0E8ED",["Track"]="#D9E4E9",["Hover"]="#E7EFF2",["Pressed"]="#DCE8ED",["Accent"]="#177D8A",["AccentSoft"]="#E5F2F3",["AccentInk"]="#126572",["Attention"]="#93651B",["Error"]="#AC493D",["Critical"]="#B62D4C" }
        : new() { ["Surface"]="#141F28",["Panel"]="#1D2C37",["Raised"]="#233642",["Stroke"]="#344956",["Text"]="#ECF3F7",["Muted"]="#B4C7D2",["Faint"]="#91A8B6",["Line"]="#2B404D",["Track"]="#354E5D",["Hover"]="#29404E",["Pressed"]="#304B5B",["Accent"]="#79CDD4",["AccentSoft"]="#203C45",["AccentInk"]="#A5E2E7",["Attention"]="#E8BF7E",["Error"]="#ECA598",["Critical"]="#FF9AB1" };
}
