using System.Windows;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views;

public partial class FirstRunWindow : Window
{
    public FirstRunWindow(FirstRunViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += ok => { try { DialogResult = ok; } catch (InvalidOperationException) { } Close(); };
    }
}
