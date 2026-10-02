using System.Windows.Controls;
using System.Windows.Input;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

/// <summary>[phase4] Invoice status board.</summary>
public partial class AconexWorkflowsBoardView : UserControl
{
    public AconexWorkflowsBoardView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is AconexBoardViewModel vm && vm.OpenRowCommand.CanExecute(vm.Selected)) vm.OpenRowCommand.Execute(vm.Selected);
    }
}
