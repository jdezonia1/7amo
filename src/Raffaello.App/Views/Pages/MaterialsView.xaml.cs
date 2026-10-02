using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

/// <summary>Builds the PO grid with one column per delivery note (the DN set differs per PO).</summary>
public partial class MaterialsView : UserControl
{
    private MaterialsViewModel? _vm;

    public MaterialsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
    }

    private void Attach()
    {
        if (DataContext is not MaterialsViewModel vm || ReferenceEquals(vm, _vm)) return;
        if (_vm != null) _vm.ColumnsChanged -= Build;
        _vm = vm;
        _vm.ColumnsChanged += Build;
        Build();
    }

    private Style S(string key) => (Style)FindResource(key);

    private DataGridTextColumn Text(string header, string path, double width, string style, string? format = null) => new()
    {
        Header = header,
        Binding = new Binding(path) { StringFormat = format, TargetNullValue = "" },
        Width = new DataGridLength(width),
        ElementStyle = S(style),
    };

    private void Build()
    {
        if (_vm is null) return;
        Grid.Columns.Clear();
        var status = new DataGridTemplateColumn { Header = "STATUS", Width = new DataGridLength(64), SortMemberPath = "Status" };
        var f = new FrameworkElementFactory(typeof(ContentControl));
        f.SetValue(StyleProperty, S("Tag"));
        f.SetBinding(ContentControl.ContentProperty, new Binding("Status"));
        status.CellTemplate = new DataTemplate { VisualTree = f };
        Grid.Columns.Add(status);
        Grid.Columns.Add(Text("LINE", "LineNo", 44, "CellNum"));
        Grid.Columns.Add(Text("DESCRIPTION", "Description", 210, "CellKey"));
        Grid.Columns.Add(Text("UNIT", "Unit", 46, "CellText"));
        Grid.Columns.Add(Text("PO QTY", "PoQty", 74, "CellNumStrong", "N0"));
        Grid.Columns.Add(Text("RATE", "Rate", 62, "CellNum", "N2"));
        foreach (var dn in _vm.DnColumns)
        {
            var col = Text("", $"Dn[{dn.Index}]", 86, "CellNum", "N0");
            col.Header = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = dn.Header },
                    new TextBlock { Text = dn.Detail, FontSize = 9, Opacity = 0.8 },
                },
            };
            Grid.Columns.Add(col);
        }
        Grid.Columns.Add(Text("DELIVERED", "Delivered", 84, "CellNumStrong", "N0"));
        Grid.Columns.Add(Text("REMAINING", "Remaining", 84, "CellNum", "N0"));
        Grid.Columns.Add(Text("DELIV %", "Pct", 64, "CellNum", "P0"));
        Grid.Columns.Add(Text("VALUE SAR", "Value", 96, "CellNumStrong", "N0"));
    }
}
