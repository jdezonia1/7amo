using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Raffaello.App.Converters;

namespace Raffaello.App.Controls;

/// <summary>
/// Colours a row's left stripe from the row item's Status property - but only binds when the row type
/// has one. The shared CardGridRow template is used by every DataGrid, and most row types have no Status;
/// a plain {Binding Status} logs a binding path error for every such row.
/// </summary>
public static class RowStatusStripe
{
    private static readonly StatusToStripeConverter Converter = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(RowStatusStripe), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b) return;
        b.DataContextChanged -= OnDataContextChanged;
        if ((bool)e.NewValue) { b.DataContextChanged += OnDataContextChanged; Apply(b); }
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Apply((Border)sender);

    private static void Apply(Border b)
    {
        var item = b.DataContext;
        if (item != null && item.GetType().GetProperty("Status") != null)
            b.SetBinding(Border.BackgroundProperty, new Binding("Status") { Converter = Converter });
        else
        {
            BindingOperations.ClearBinding(b, Border.BackgroundProperty);
            b.Background = Brushes.Transparent;
        }
    }
}
