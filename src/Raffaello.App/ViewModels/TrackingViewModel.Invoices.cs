using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Domain;
using Raffaello.Core.Tracker;

namespace Raffaello.App.ViewModels;

/// <summary>TRACKING > INVOICES: one list per subcontractor x invoice - DRAWING DONE / DRAWING PENDING / DIFFERS FROM INVOICE.</summary>
public sealed partial class TrackingViewModel
{
    public ObservableCollection<InvoiceStatusRow> InvoiceRows { get; } = new();
    private List<InvoiceStatusRow> _invoiceAll = new();
    public string[] InvoiceStatusFilters { get; } = { "ALL", DrawingStatus.Done, DrawingStatus.Pending, DrawingStatus.Differs };
    [ObservableProperty] private string _invoiceFilter = "ALL";
    [ObservableProperty] private InvoiceStatusRow? _selectedInvoiceRow;
    [ObservableProperty] private string _invoicesInfo = "";
    partial void OnInvoiceFilterChanged(string value) { if (value != null) FilterInvoices(); }

    public static string ReferenceFolder => Path.Combine(Raffaello.Core.Settings.AppSettings.SettingsFolder, "invoice_reference");

    /// <summary>The reference check workbooks the recon work produced (offered by IMPORT REFERENCE).</summary>
    public static readonly Dictionary<string, string> ReferenceWorkbooks = new()
    {
        [Buildings.Hotel] = @"D:\RAFFLES MASTER FOLDER\HOTEL_RECON_WORK\09_INVOICE_CHECK\HOTEL_CLAIMS_vs_INVOICES.xlsx",
        [Buildings.Branded] = @"D:\RAFFLES MASTER FOLDER\BRANDED_RECON_WORK\09_INVOICE_CHECK\BRANDED_LEDGER_vs_INVOICES.xlsx",
    };

    private List<InvoiceStatusRow> InvoiceStatusRows()
    {
        var buildings = BuildingFilter is { } b ? new[] { b } : Buildings.All;
        return buildings.SelectMany(bd => InvoiceStatusList.Build(bd, Project.Snapshot.Claims.Where(c => string.Equals(c.Building, bd, StringComparison.OrdinalIgnoreCase)),
            InvoiceStatusList.LoadReference(ReferenceFolder, bd), Project.Snapshot.MappingRules)).ToList();
    }

    private void BuildInvoices()
    {
        _invoiceAll = InvoiceStatusRows();
        FilterInvoices();
    }

    private void FilterInvoices()
    {
        if (!_built.Contains(TInvoices)) return;
        Fill(InvoiceRows, InvoiceFilter is null or "ALL" ? _invoiceAll : _invoiceAll.Where(r => r.Status == InvoiceFilter));
        var refs = _invoiceAll.Count(r => r.Invoiced != null);
        InvoicesInfo = $"{_invoiceAll.Count} invoices  |  DONE {_invoiceAll.Count(r => r.Status == DrawingStatus.Done)}  |  PENDING {_invoiceAll.Count(r => r.Status == DrawingStatus.Pending)} (work in progress, not counted)  |  " +
                       $"DIFFERS {_invoiceAll.Count(r => r.Status == DrawingStatus.Differs)}  |  " + (refs > 0 ? $"{refs} with an invoice reference" : "no invoice reference imported - IMPORT REFERENCE");
    }

    [RelayCommand] private Task MarkPending() => SetInvoiceStatus("PENDING");
    [RelayCommand] private Task MarkDone() => SetInvoiceStatus("DONE");
    [RelayCommand] private Task MarkAuto() => SetInvoiceStatus("");

    private async Task SetInvoiceStatus(string status)
    {
        if (SelectedInvoiceRow is not { } r) { Ctx.Toasts.Show("PICK AN INVOICE FIRST", kind: ToastKind.Info); return; }
        await Ctx.Data.WriteAsync(p => p.Workflow.SetInvoiceDrawingStatus(r.Subcontractor, r.Building, r.InvoiceNo, status), Ctx.Toasts, status.Length == 0 ? "STATUS AUTOMATIC AGAIN" : $"MARKED {status}");
    }

    [RelayCommand]
    private void ImportReference()
    {
        var building = WorkingBuilding;
        var suggested = ReferenceWorkbooks.GetValueOrDefault(building);
        string? file = suggested != null && File.Exists(suggested) && Ctx.Dialogs.Confirm("Invoice reference", $"Use {suggested} as the {building} invoice reference (invoiced / certified per subcontractor x invoice, read-only)?\n\nNO = pick another file.")
            ? suggested : Ctx.Dialogs.OpenFile($"{building} invoice check workbook (sheet SUMMARY)");
        if (file is null) return;
        try
        {
            var (rows, issues) = InvoiceStatusList.ReadReference(file);
            if (rows.Count == 0) { Ctx.Toasts.Show("NO INVOICES FOUND", string.Join("\n", issues.Take(4)), ToastKind.Warn, 8); return; }
            InvoiceStatusList.SaveReference(ReferenceFolder, building, rows);
            Ctx.Toasts.Show("INVOICE REFERENCE LOADED", $"{rows.Count} invoices for {building} from {Path.GetFileName(file)} (kept beside the app, the data file is not changed)", ToastKind.Good, 6);
            _built.Remove(TInvoices); _built.Remove(TControl);
            EnsureTab();
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ REFERENCE", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void OpenInvoiceLines()
    {
        if (SelectedInvoiceRow is not { } r) return;
        LedgerSub = Subcontractors.Contains(r.Subcontractor) ? r.Subcontractor : "ALL";
        LedgerInvoice = r.InvoiceNo.ToString();
        Tab = TLedger;
        if (!LedgerInvoiceOptions.Contains(LedgerInvoice)) LedgerInvoice = "ALL";
    }
}