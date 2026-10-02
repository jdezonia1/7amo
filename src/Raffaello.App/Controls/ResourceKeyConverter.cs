using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Raffaello.App.Controls;

/// <summary>Looks up an application resource by key (used to bind icon names to geometries).</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public static readonly ResourceKeyConverter Instance = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Boolean negation for IsEnabled bindings.</summary>
public sealed class NotConverter : IValueConverter
{
    public static readonly NotConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
