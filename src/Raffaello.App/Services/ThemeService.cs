using System.Windows;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;

namespace Raffaello.App.Services;

/// <summary>
/// Swaps the Light / Dark colour dictionary and the accent colour at runtime. Every component reads
/// its colours through DynamicResource, so the whole UI follows without a restart.
/// </summary>
public sealed class ThemeService
{
    public static readonly IReadOnlyDictionary<string, Color> Accents = new Dictionary<string, Color>
    {
        ["Red"] = (Color)ColorConverter.ConvertFromString("#5C0000"),
    };

    /// <summary>
    /// Dark theme depth presets (window / panels / zebra / table header / borders). Near-black is Themes/Dark.xaml itself;
    /// the others override those few colours. Text #F2F2F2, secondary #A6A6A6, oxblood accents and yellow warnings stay.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Bg, string Surface, string Surface2, string RowHover, string Line, string InputBg)> DarkDepths =
        new Dictionary<string, (string, string, string, string, string, string)>
        {
            ["Soft"] = ("#1C1C1C", "#242424", "#262626", "#2C2C2C", "#383838", "#1F1F1F"),
            ["Near-black"] = ("#0E0E0E", "#161616", "#181818", "#1E1E1E", "#2A2A2A", "#121212"),
            ["True black"] = ("#000000", "#0A0A0A", "#0D0D0D", "#141414", "#2A2A2A", "#0A0A0A"),
        };

    public string DarkDepth { get; set; } = "Near-black";
    public string Theme { get; private set; } = "Light";
    public string Accent { get; private set; } = "Red";
    public bool IsDark => Theme == "Dark";

    public event Action? Changed;

    public void Apply(string theme, string accent)
    {
        Theme = theme == "Dark" ? "Dark" : "Light";
        Accent = Accents.ContainsKey(accent) ? accent : "Red";
        var app = Application.Current;
        var dicts = app.Resources.MergedDictionaries;
        var colours = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{Theme}.xaml", UriKind.Absolute) };
        var idx = -1;
        for (var i = 0; i < dicts.Count; i++)
            if (dicts[i].Source?.OriginalString.Contains("/Themes/Light.xaml") == true || dicts[i].Source?.OriginalString.Contains("/Themes/Dark.xaml") == true) { idx = i; break; }
        if (idx >= 0) dicts[idx] = colours; else dicts.Insert(1, colours);

        if (IsDark && DarkDepths.TryGetValue(DarkDepth, out var d) && DarkDepth != "Near-black")
        {
            colours["BgColor"] = Parse(d.Bg); colours["BgBrush"] = Brush(d.Bg);
            colours["SurfaceColor"] = Parse(d.Surface); colours["SurfaceBrush"] = Brush(d.Surface);
            colours["Surface2Color"] = Parse(d.Surface2); colours["Surface2Brush"] = Brush(d.Surface2);
            colours["RowHoverColor"] = Parse(d.RowHover); colours["RowHoverBrush"] = Brush(d.RowHover);
            colours["LineColor"] = Parse(d.Line); colours["LineBrush"] = Brush(d.Line);
            colours["InputBgColor"] = Parse(d.InputBg); colours["InputBgBrush"] = Brush(d.InputBg);
            colours["SidebarBgColor"] = Parse(d.Surface); colours["SidebarBgBrush"] = Brush(d.Surface); colours["SidebarBgHoverBrush"] = Brush(d.RowHover);
        }
        var accentColor = Accents[Accent];
        if (IsDark && Accent == "Red") accentColor = (Color)ColorConverter.ConvertFromString("#7A0000");   // oxblood; on black only thin lines / fills with white text
        var surface = (Color)colours["SurfaceColor"];
        var soft = Mix(accentColor, surface, IsDark ? 0.72 : 0.86);
        var onAccent = Accent == "Yellow" ? (Color)ColorConverter.ConvertFromString("#231A00") : Colors.White;
        Set(app, "Accent", accentColor);
        Set(app, "AccentSoft", soft);
        Set(app, "AccentSubtle", soft);
        Set(app, "OnAccent", onAccent);
        Set(app, "Selection", Mix(accentColor, surface, IsDark ? 0.78 : 0.9));

        LiveCharts.Configure(c =>
        {
            if (IsDark) c.AddDarkTheme(); else c.AddLightTheme();
        });
        Changed?.Invoke();
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush(Parse(hex)); b.Freeze(); return b; }

    private static void Set(Application app, string name, Color c)
    {
        app.Resources[name + "Color"] = c;
        var b = new SolidColorBrush(c);
        b.Freeze();
        app.Resources[name + "Brush"] = b;
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color Get(string key) =>
        Application.Current.TryFindResource(key + "Color") is Color c ? c : Colors.Gray;
}
