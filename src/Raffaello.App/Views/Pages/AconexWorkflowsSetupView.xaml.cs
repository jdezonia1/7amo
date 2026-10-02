using System.Windows;
using System.Windows.Controls;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

/// <summary>[phase4] Aconex setup. The password goes straight from the PasswordBox to the DPAPI vault (never bound).</summary>
public partial class AconexWorkflowsSetupView : UserControl
{
    public AconexWorkflowsSetupView() => InitializeComponent();

    private void OnStorePassword(object sender, RoutedEventArgs e)
    {
        if (DataContext is AconexSetupViewModel vm) vm.SaveCredential(Pwd.Password);
        Pwd.Clear();
    }
}
