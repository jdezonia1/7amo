using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Raffaello.App.Views.Pages;

public partial class TrackingView : UserControl
{
    public TrackingView() => InitializeComponent();

    /// <summary>ENTRY: Enter (or Down) in a CLAIMED NOW box moves to the box of the next line, Up to the previous one.</summary>
    private void ClaimedNow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || e.Key is not (Key.Enter or Key.Down or Key.Up)) return;
        var row = FindParent<DataGridRow>(box);
        if (row is null) return;
        var index = EntryGrid.ItemContainerGenerator.IndexFromContainer(row) + (e.Key == Key.Up ? -1 : 1);
        if (index < 0 || index >= EntryGrid.Items.Count) return;
        e.Handled = true;
        EntryGrid.ScrollIntoView(EntryGrid.Items[index]);
        EntryGrid.UpdateLayout();
        if (EntryGrid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow next && FindChild<TextBox>(next) is { } nextBox)
        {
            nextBox.Focus();
            nextBox.SelectAll();
        }
    }

    /// <summary>PLANS: Ctrl + wheel zooms, a click on a shape opens the ROOM view.</summary>
    private void PlanScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || DataContext is not Raffaello.App.ViewModels.TrackingViewModel vm) return;
        vm.PlanZoom = Math.Clamp(vm.PlanZoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.03, 4);
        e.Handled = true;
    }

    private void PlanShape_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Raffaello.App.ViewModels.TrkPlanShape s } && DataContext is Raffaello.App.ViewModels.TrackingViewModel vm)
            vm.ShapeClickCommand.Execute(s.Room);
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    /// <summary>Wide tracker grids (PROJECT QTY, SUMMARY, DASHBOARD, ROOM): header from the column caption, numbers right-aligned.</summary>
    private void AutoCol(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is not DataGrid g || g.ItemsSource is not DataView v || v.Table is null || !v.Table.Columns.Contains(e.PropertyName)) return;
        var col = v.Table.Columns[e.PropertyName]!;
        e.Column.Header = col.Caption;
        if (e.Column is DataGridTextColumn t)
        {
            if (col.DataType == typeof(double) || col.DataType == typeof(int))
            {
                if (t.Binding is Binding b) b.StringFormat = col.DataType == typeof(int) ? "#,0" : "#,0.##";
                t.ElementStyle = (System.Windows.Style)FindResource("CellNum");
                t.Width = new DataGridLength(Math.Max(56, col.Caption.Length * 7 + 12));
            }
            else
            {
                t.ElementStyle = (System.Windows.Style)FindResource(col.Ordinal == 0 ? "CellKey" : "CellText");
                t.Width = new DataGridLength(col.Caption is "LOCATION" or "KEY" or "SUBCONTRACTOR" ? 150 : Math.Max(70, col.Caption.Length * 7 + 12));
            }
        }
    }
}