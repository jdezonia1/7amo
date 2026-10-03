using System.Windows;
using System.Windows.Controls;

namespace Raffaello.App.Controls;

/// <summary>
/// Keeps fixed-width DataGrid columns at their designed width. When a grid has a "*" column and the window is
/// narrow (1366 x 768 laptop), WPF shrinks every pixel-width column towards MinWidth (20 px) instead of
/// scrolling, so QUANTITIES / STATEMENTS showed one-letter columns. With MinWidth = Width the grid scrolls
/// horizontally instead; the star column still takes whatever is left on a wide screen.
/// </summary>
public static class GridColumnGuard
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(GridColumnGuard), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid g) return;
        g.Loaded -= OnLoaded;
        if ((bool)e.NewValue) { g.Loaded += OnLoaded; if (g.IsLoaded) Apply(g); }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((DataGrid)sender);

    private static void Apply(DataGrid g)
    {
        foreach (var c in g.Columns)
            if (c.Width.IsAbsolute && c.MinWidth < c.Width.Value) c.MinWidth = c.Width.Value;
    }
}
