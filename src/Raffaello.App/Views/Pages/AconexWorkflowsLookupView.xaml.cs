using System.Windows.Controls;
using System.Windows.Input;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

/// <summary>[phase4] Workflow lookup.</summary>
public partial class AconexWorkflowsLookupView : UserControl
{
    public AconexWorkflowsLookupView() => InitializeComponent();

    private void OnWfKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is AconexLookupViewModel vm && vm.LookUpCommand.CanExecute(null))
        {
            vm.LookUpCommand.Execute(null);
            e.Handled = true;
        }
    }
}
