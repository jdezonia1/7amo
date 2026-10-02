using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm, ImportViewModel import)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.ImportRequested += kind =>
        {
            import.Start(kind);
            var w = new ImportWindow(import) { Owner = this };
            w.ShowDialog();
        };
        vm.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && vm.Palette.IsOpen)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { PaletteInput.Focus(); Keyboard.Focus(PaletteInput); });
        };
        // [assistant] focus and scrolling of the chat are handled by Views/AssistantPanel
        Closing += OnClosingWindow;
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e) => _vm.OnClosing();

    private void AskBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => _vm.Ask.IsOpen = false;
    private void PaletteBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => _vm.Palette.IsOpen = false;

    private void PaletteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: _vm.Palette.Move(1); PaletteList.ScrollIntoView(_vm.Palette.Selected); e.Handled = true; break;
            case Key.Up: _vm.Palette.Move(-1); PaletteList.ScrollIntoView(_vm.Palette.Selected); e.Handled = true; break;
            case Key.Enter: _vm.Palette.Execute(null); e.Handled = true; break;
            case Key.Escape: _vm.Palette.IsOpen = false; e.Handled = true; break;
        }
    }

    private void PaletteList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Palette.Selected != null) _vm.Palette.Execute(_vm.Palette.Selected);
    }
}
