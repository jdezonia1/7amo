using System.Windows;
using System.Windows.Controls;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views;

/// <summary>Saved Aconex logins. The password goes straight from the PasswordBox to the DPAPI vault (never bound) and the box is cleared.</summary>
public partial class AconexAccountsCard : UserControl
{
    public AconexAccountsCard() => InitializeComponent();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PasswordBox box, DataContext: AconexAccountRow row }) return;
        if (DataContext is AconexAccountsViewModel vm) vm.Save(row, box.Password);
        box.Clear();
    }
}