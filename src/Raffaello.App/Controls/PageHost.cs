using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Raffaello.App.Controls;

/// <summary>
/// Hosts the current page view model in a ContentControl without the page-switch crash.
/// With a plain Content="{Binding Current}", the outgoing view is detached while its selectors
/// (ListBox / ComboBox / DataGrid) still hold TwoWay SelectedItem bindings. Detaching clears their
/// ItemsSource, the selector writes SelectedItem back through a binding that is in the middle of
/// re-resolving its source, and WPF throws a NullReferenceException in
/// PropertyPathWorker.DetermineWhetherDBNullIsValid (seen leaving PLAN VIEW).
/// The outgoing view is thrown away anyway, so its selection bindings are dropped first (nothing is
/// written to the view model), the content is cleared, and the new page gets a fresh view.
/// </summary>
public static class PageHost
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.RegisterAttached(
        "Page", typeof(object), typeof(PageHost), new PropertyMetadata(null, OnPageChanged));

    public static object? GetPage(DependencyObject d) => d.GetValue(PageProperty);
    public static void SetPage(DependencyObject d, object? value) => d.SetValue(PageProperty, value);

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl host) return;
        if (e.OldValue != null && !ReferenceEquals(e.OldValue, e.NewValue))
        {
            DetachSelections(host);
            host.Content = null;
        }
        host.Content = e.NewValue;
    }

    private static void DetachSelections(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Selector s)
            {
                BindingOperations.ClearBinding(s, Selector.SelectedItemProperty);
                BindingOperations.ClearBinding(s, Selector.SelectedValueProperty);
                BindingOperations.ClearBinding(s, Selector.SelectedIndexProperty);
            }
            DetachSelections(child);
        }
    }
}
