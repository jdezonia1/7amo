using System.Windows.Controls;
using Raffaello.App.ViewModels;
using Raffaello.Core.Queue;

namespace Raffaello.App.Views.Pages;

public partial class WelcomeView : UserControl
{
    public WelcomeView() => InitializeComponent();

    private void Queue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Queue.SelectedItem is QueueItem item && DataContext is WelcomeViewModel vm)
        {
            Queue.SelectedItem = null;
            vm.OpenQueueCommand.Execute(item);
        }
    }
}
