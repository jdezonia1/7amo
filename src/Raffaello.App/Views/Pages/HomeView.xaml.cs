using System.Windows.Controls;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views.Pages;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void Links_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Links.SelectedItem is AreaLink link && DataContext is HomeViewModel vm)
        {
            Links.SelectedItem = null;
            vm.OpenLinkCommand.Execute(link);
        }
    }
}