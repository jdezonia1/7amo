using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace Raffaello.App.Services;

/// <summary>
/// Chart styling from the design tokens: series order accent, yellow, graphite, good; rounded bar tops;
/// no gridlines beyond a faint baseline. Colours are read from the live theme so charts follow theme / accent.
/// </summary>
public static class ChartKit
{
    public static SKColor Sk(string token, byte alpha = 255)
    {
        var c = ThemeService.Get(token);
        return new SKColor(c.R, c.G, c.B, alpha);
    }

    public static SKColor Accent => Sk("Accent");
    public static SKColor Yellow => Sk("Yellow");
    public static SKColor Graphite => Sk("Graphite");
    public static SKColor Good => Sk("Good");
    public static SKColor Muted => Sk("Muted");
    public static SKColor LineColor => Sk("Line");
    public static SKColor Ink => Sk("Ink");

    public static SKColor[] Series => new[] { Accent, Yellow, Graphite, Good, Sk("Ink2"), Sk("AccentSoft") };

    public static SolidColorPaint Fill(SKColor c) => new(c);
    public static SolidColorPaint Stroke(SKColor c, float w = 2) => new(c, w);
    public static SolidColorPaint Dashed(SKColor c, float w = 2) => new(c, w) { PathEffect = new DashEffect(new float[] { 6, 5 }) };
    public static SolidColorPaint Text => new(Muted);

    public static Axis XLabels(IEnumerable<string> labels, double rotation = 0) => new()
    {
        Labels = labels.ToArray(),
        LabelsPaint = new SolidColorPaint(Muted),
        TextSize = 11,
        LabelsRotation = rotation,
        SeparatorsPaint = null,
        ZeroPaint = null,
        MinStep = 1,
        ForceStepToMin = true,
    };

    public static Axis YValues(Func<double, string>? labeler = null, double? min = 0, double? max = null) => new()
    {
        Labeler = labeler ?? (v => v.ToString("N0")),
        // Whole-number labels need whole-number steps; small counts (MIR ageing: 0..1) showed "1 1 1 1 0 0 0".
        MinStep = labeler is null ? 1 : 0,
        LabelsPaint = new SolidColorPaint(Muted),
        TextSize = 11,
        MinLimit = min,
        MaxLimit = max,
        SeparatorsPaint = new SolidColorPaint(LineColor.WithAlpha(110), 1),
    };

    public static Axis YPercent(double max = 1) => YValues(v => v.ToString("P0"), 0, max);

    public static Axis Hidden() => new() { IsVisible = false, SeparatorsPaint = null, ZeroPaint = null };

    public static ColumnSeries<double> Columns(string name, IEnumerable<double> values, SKColor color, double maxWidth = 26) => new()
    {
        Name = name,
        Values = values.ToArray(),
        Fill = Fill(color),
        Stroke = null,
        Rx = 6,
        Ry = 6,
        MaxBarWidth = maxWidth,
        Padding = 3,
    };

    public static RowSeries<double> Rows(string name, IEnumerable<double> values, SKColor color) => new()
    {
        Name = name,
        Values = values.ToArray(),
        Fill = Fill(color),
        Stroke = null,
        Rx = 6,
        Ry = 6,
        MaxBarWidth = 18,
        Padding = 3,
    };

    public static LineSeries<double?> Line(string name, IEnumerable<double?> values, SKColor color, bool dashed = false, float width = 3, bool area = false) => new()
    {
        Name = name,
        Values = values.ToArray(),
        Stroke = dashed ? Dashed(color, width) : Stroke(color, width),
        Fill = area ? new SolidColorPaint(color.WithAlpha(28)) : null,
        GeometrySize = 0,
        GeometryFill = null,
        GeometryStroke = null,
        LineSmoothness = 0.35,
    };

    public static LineSeries<double> Spark(IEnumerable<double> values, SKColor color) => new()
    {
        Values = values.ToArray(),
        Stroke = Stroke(color, 2),
        Fill = new SolidColorPaint(color.WithAlpha(36)),
        GeometrySize = 0,
        GeometryFill = null,
        GeometryStroke = null,
        LineSmoothness = 0.4,
        IsHoverable = false,
    };

    public static PieSeries<double> Slice(string name, double value, SKColor color, double inner = 52) => new()
    {
        Name = name,
        Values = new[] { value },
        Fill = Fill(color),
        Stroke = null,
        InnerRadius = inner,
        HoverPushout = 4,
    };

    public static HeatSeries<WeightedPoint> Heat(IEnumerable<WeightedPoint> points) => new()
    {
        Values = points.ToArray(),
        HeatMap = new[]
        {
            ToLvc(Sk("Surface2")),
            ToLvc(Yellow),
            ToLvc(Accent),
        },
        Name = "% GIVEN",
    };

    public static LvcColor ToLvc(SKColor c) => new(c.Red, c.Green, c.Blue, c.Alpha);

    /// <summary>Colour for a slice / series index following the token order.</summary>
    public static SKColor At(int i)
    {
        var s = Series;
        if (i < s.Length) return s[i];
        var baseC = s[i % 4];
        return baseC.WithAlpha((byte)Math.Max(90, 255 - 50 * (i / 4)));
    }

    public static Brush BrushOf(SKColor c) => new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));

    public static LegendPosition Legend => LegendPosition.Top;
}
