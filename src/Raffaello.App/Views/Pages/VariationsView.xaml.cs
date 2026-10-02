using System.Windows.Controls;
using System.Windows.Input;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

/// <summary>[phase4] Variations / EI register.</summary>
public partial class VariationsView : UserControl
{
    public VariationsView() => InitializeComponent();

    private void OnDocDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is VariationsViewModel vm && vm.OpenDocCommand.CanExecute(vm.SelectedDoc)) vm.OpenDocCommand.Execute(vm.SelectedDoc);
    }
}
