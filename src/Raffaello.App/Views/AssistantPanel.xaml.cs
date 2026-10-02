using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views;

/// <summary>[assistant] The chat: Enter sends, Shift+Enter is a new line; keeps the newest message in view.</summary>
public partial class AssistantPanel : UserControl
{
    private AskViewModel? _vm;

    public AssistantPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook(DataContext as AskViewModel);
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) FocusInput(); };
    }

    public void FocusInput() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { Input.Focus(); Keyboard.Focus(Input); });

    private void Hook(AskViewModel? vm)
    {
        if (_vm != null)
        {
            ((INotifyCollectionChanged)_vm.Messages).CollectionChanged -= OnMessages;
            _vm.PropertyChanged -= OnVm;
        }
        _vm = vm;
        if (vm is null) return;
        ((INotifyCollectionChanged)vm.Messages).CollectionChanged += OnMessages;
        vm.PropertyChanged += OnVm;
    }

    private void OnMessages(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroll.ScrollToEnd());
        if (e.NewItems != null)
            foreach (var m in e.NewItems.OfType<AskMessage>())
                m.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroll.ScrollToEnd());
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AskViewModel.IsOpen) && _vm?.IsOpen == true) FocusInput();
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (_vm?.SendCommand.CanExecute(null) == true) _vm.SendCommand.Execute(null);
        }
        else if (e.Key == Key.Escape && _vm is { IsPageMode: false })
        {
            e.Handled = true;
            _vm.IsOpen = false;
        }
    }
}
