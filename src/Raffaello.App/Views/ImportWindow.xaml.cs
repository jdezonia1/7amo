using System.Windows;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views;

public partial class ImportWindow : Window
{
    private readonly ImportViewModel _vm;

    public ImportWindow(ImportViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.CloseRequested += OnClose;
        Closed += (_, _) => vm.CloseRequested -= OnClose;
    }

    private void OnClose(bool ok)
    {
        try { DialogResult = ok; } catch (InvalidOperationException) { }
        Close();
    }
}
