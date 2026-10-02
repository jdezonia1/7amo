using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Raffaello.App.Converters;

/// <summary>Status text (OVER / CHECK / DUE / OPEN / OK ...) to the 4px row stripe brush.</summary>
public sealed class StatusToStripeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "").ToUpperInvariant() switch
        {
            "OVER" or "COPY" or "REJECTED" or "ERROR" => "AccentBrush",
            "CHECK" or "WARN" or "REDO" or "HOLD" => "YellowBrush",
            "DUE" => "YellowSoftBrush",
            "OK" or "APPROVED" or "CERTIFIED" or "DOWNLOADED" => "GoodBrush",
            "OPEN" or "RECEIVED" or "QUEUED" => "GraphiteBrush",
            _ => null,
        };
        return key is null ? Brushes.Transparent : Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value switch { bool x => x, int i => i != 0, string s => s.Length > 0, null => false, _ => true };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility v && (v == Visibility.Visible) != Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is not null && !(value is string s && s.Length == 0);
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>0..1 value times the ConverterParameter (max width) for inline bar cells.</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var pct = value is double d ? d : 0;
        var max = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 80;
        return Math.Max(0, Math.Min(1, pct)) * max;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class UpperConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString()?.ToUpperInvariant() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Formats numbers with a format string parameter; nulls render as an em dash.</summary>
public sealed class NumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        var fmt = parameter as string ?? "N0";
        return value switch
        {
            double d when double.IsNaN(d) => "—",
            IFormattable f => f.ToString(fmt, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
