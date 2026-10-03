using System.Globalization;
using System.Windows.Data;

namespace Raffaello.App.Converters;

/// <summary>Quantities on the tracker screens: whole numbers stay whole (5, 43, 104), fractions only where they exist.</summary>
public sealed class QtyFormatConverter : IValueConverter
{
    public static QtyFormatConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        double d => d.ToString("#,0.##", culture),
        int i => i.ToString("#,0", culture),
        null => "",
        _ => value.ToString() ?? "",
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
