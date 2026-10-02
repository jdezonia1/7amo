using System.Windows.Controls;
using Raffaello.App.ViewModels;
using Raffaello.Core.Queue;

namespace Raffaello.App.Views.Pages;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private void Queue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueItem item && DataContext is DashboardViewModel vm)
        {
            QueueList.SelectedItem = null;
            vm.OpenQueueCommand.Execute(item);
        }
    }
}
