using System.Windows;
using System.Windows.Controls;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

/// <summary>[cables] Panel &amp; cable register.</summary>
public partial class CablesView : UserControl
{
    public CablesView() => InitializeComponent();

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is CablesViewModel vm) vm.SelectedNode = e.NewValue as CablePanelNode;
    }
}
