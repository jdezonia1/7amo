using System.Windows;
using System.Windows.Input;
using Raffaello.App.ViewModels;

namespace Raffaello.App.Views;

public partial class ContractPdfImportWindow : Window
{
    public ContractPdfImportWindow(ContractPdfImportViewModel vm)
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

    private void OnBoxClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PdfReviewField f } && DataContext is ContractPdfImportViewModel vm)
        {
            vm.SelectedField = f;
            vm.Tab = "FIELDS";
            FieldGrid.ScrollIntoView(f);
        }
    }

    /// <summary>Shows the review modally; true when the user saved.</summary>
    public static bool Show(ContractPdfImportViewModel vm)
    {
        var w = new ContractPdfImportWindow(vm) { Owner = Application.Current?.MainWindow };
        return w.ShowDialog() == true;
    }
}
