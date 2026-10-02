using System.Windows;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views;

public partial class MaterialsReviewWindow : Window
{
    public MaterialsReviewWindow(MaterialsReviewViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += OnClose;
        Closed += (_, _) => vm.CloseRequested -= OnClose;
        Loaded += (_, _) => vm.Start();
    }

    private void OnClose(bool ok)
    {
        try { DialogResult = ok; } catch (InvalidOperationException) { }
        Close();
    }

    /// <summary>Shows the review modally over the main window; true when the user accepted.</summary>
    public static bool Show(MaterialsReviewViewModel vm)
    {
        var w = new MaterialsReviewWindow(vm) { Owner = Application.Current?.MainWindow };
        return w.ShowDialog() == true;
    }
}
