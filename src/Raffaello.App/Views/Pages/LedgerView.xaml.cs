using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

public partial class LedgerView : UserControl
{
    public LedgerView() => InitializeComponent();

    private void OnPolygonClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string room } && DataContext is LedgerViewModel vm) vm.SelectRoom(room);
    }
}
