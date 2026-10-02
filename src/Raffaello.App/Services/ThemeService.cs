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
        ["Red"] = (Color)ColorConverter.ConvertFromString("#8E1B22"),
        ["Yellow"] = (Color)ColorConverter.ConvertFromString("#C99A06"),
        ["Green"] = (Color)ColorConverter.ConvertFromString("#2E7D4F"),
        ["Blue"] = (Color)ColorConverter.ConvertFromString("#1F4E8C"),
        ["Purple"] = (Color)ColorConverter.ConvertFromString("#5B2C83"),
        ["Graphite"] = (Color)ColorConverter.ConvertFromString("#4A4445"),
    };

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

        var accentColor = Accents[Accent];
        var surface = (Color)colours["SurfaceColor"];
        var soft = Mix(accentColor, surface, IsDark ? 0.72 : 0.86);
        var onAccent = Accent == "Yellow" ? (Color)ColorConverter.ConvertFromString("#231A00") : Colors.White;
        Set(app, "Accent", accentColor);
        Set(app, "AccentSoft", soft);
        Set(app, "OnAccent", onAccent);
        Set(app, "Selection", Mix(accentColor, surface, IsDark ? 0.78 : 0.9));

        LiveCharts.Configure(c =>
        {
            if (IsDark) c.AddDarkTheme(); else c.AddLightTheme();
        });
        Changed?.Invoke();
    }

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
