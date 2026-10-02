using System.Collections.ObjectModel;
using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.App.Views;
using Raffaello.Core.Contracts;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;

namespace Raffaello.App.ViewModels;

/// <summary>
/// Contracts page, smart-reader part: IMPORT CONTRACT PDF (signed scan -> header, clauses, schedule, rules; cross-check with the Excel;
/// review; record + evidence), contract terms / rules (editable) / bypass history / obligations calendar, side-by-side comparison.
/// </summary>
public sealed partial class ContractsViewModel
{
    private readonly SmartReading _reading;
    private readonly IPageRenderer _renderer;

    public bool IsTerms => Tab == "TERMS, RULES & OBLIGATIONS";
    public bool IsCompare => Tab == "COMPARE CONTRACTS";

    [ObservableProperty] private string _readingBusy = "";
    [ObservableProperty] private string _termsText = "";
    [ObservableProperty] private ContractRule? _selectedRule;
    [ObservableProperty] private DateTime? _handoverDate;
    [ObservableProperty] private DateTime? _completionDate;
    [ObservableProperty] private DataView? _compareTerms;
    [ObservableProperty] private DataView? _compareRates;
    public ObservableCollection<ContractRule> RuleRows { get; } = new();
    public ObservableCollection<RuleBypass> BypassRows { get; } = new();
    public ObservableCollection<Obligation> ObligationRows { get; } = new();
    public ObservableCollection<ContractClause> ClauseRows { get; } = new();
    public string ReaderStatus => _reading.Status;

    private IDocumentStore Docs => _reading.Documents;

    [RelayCommand]
    private async Task ImportContractPdf()
    {
        var file = Ctx.Dialogs.OpenFile("Signed contract PDF (scan or digital)", "PDF|*.pdf|All files|*.*");
        if (file is null) return;
        var no = ImportContractNo.Trim();
        // the Excel to cross-check against: the contract's items already in the app, else a contract Excel the user picks
        List<ContractItem>? excel = no.Length > 0 ? Project.Snapshot.ContractItems.Where(i => i.ContractNo == no).ToList() : null;
        if (excel is null || excel.Count == 0)
        {
            var x = Ctx.Dialogs.OpenFile("Contract Excel to cross-check against (cancel = no cross-check)");
            if (x != null)
            {
                try { excel = ContractScheduleExcel.Read(x, no).Items; }
                catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ EXCEL", ex.Message, ToastKind.Warn); excel = null; }
            }
        }
        ContractPdfReadResult res;
        try
        {
            ReadingBusy = "READING THE SIGNED CONTRACT (offline OCR, about a minute per scanned page)...";
            var progress = new Progress<string>(s => ReadingBusy = "READING  " + s);
            res = await Task.Run(() => ContractPdfImport.ReadAsync(file, _reading.Options(progress), excel, no.Length > 0 ? no : null, Docs));
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ CONTRACT PDF", ex.Message, ToastKind.Error, 10); return; }
        finally { ReadingBusy = ""; }
        var vm = new ContractPdfImportViewModel(res, _renderer);
        if (!ContractPdfImportWindow.Show(vm)) return;
        var sub = ImportSub.Trim().Length > 0 ? ImportSub.Trim() : res.Body.Terms.Subcontractor;
        var building = string.IsNullOrWhiteSpace(ImportBuilding) ? null : ImportBuilding;
        var user = Project.Store.User;
        var ok = await Ctx.Data.WriteAsync(p =>
        {
            // the signed PDF is kept as a contract document (shared folder, SHA-256) and indexed
            var att = p.Workflow.AddAttachment(AttachmentKinds.Contract, res.ContractNo, AttachmentKinds.Signed, file);
            ContractPdfImport.Commit(res, p.Store, Docs, sub, building, att?.FilePath ?? file, user);
            if (vm.Learn) ContractPdfImport.LearnTemplate(res, Docs);
        }, Ctx.Toasts, $"CONTRACT {res.ContractNo} SAVED FROM THE SIGNED PDF");
        if (ok) { SelectedContractNo = res.ContractNo; Tab = "TERMS, RULES & OBLIGATIONS"; }
    }

    private void FillTerms()
    {
        RuleRows.Clear(); BypassRows.Clear(); ObligationRows.Clear(); ClauseRows.Clear();
        try
        {
            var no = SelectedContractNo;
            var terms = Docs.All<ContractTerms>();
            var t = terms.FirstOrDefault(x => x.ContractNo == no);
            HandoverDate = t?.HandoverDate; CompletionDate = t?.CompletionDate;
            TermsText = t is null ? "No terms read for this contract yet - IMPORT CONTRACT PDF (the signed contract) to get its terms, clauses and rules."
                : $"{t.Subcontractor}  |  dated {t.ContractDate:dd-MMM-yyyy}  |  labour only {(t.LabourOnly ? "YES" : "NO")}  |  VAT {t.VatTreatment} {t.VatPct:P0}\n" +
                  $"Payment: {t.PaymentTerms}  |  tray / pulling / panels: {t.TrayPaymentTerms}\n" +
                  $"Retention {(t.RetentionPct is double r ? r.ToString("P0") : "-")}  |  penalty {(t.DelayPenaltyPerWeek is double pw ? $"SAR {pw:N0}/week" : "-")} cap {(t.DelayPenaltyCapPct is double c ? c.ToString("P0") : "-")}  |  warranty {(t.WarrantyMonths is int m ? m + " months" : "-")}  |  source {t.SourceFile}";
            foreach (var r in Docs.All<ContractRule>().Where(r => r.ContractNo == no).OrderBy(r => r.RuleType)) RuleRows.Add(r);
            foreach (var b in Docs.All<RuleBypass>().Where(b => b.ContractNo == no).OrderByDescending(b => b.BypassedAt)) BypassRows.Add(b);
            foreach (var c in Docs.All<ContractClause>().Where(c => c.ContractNo == no).OrderBy(c => int.TryParse(c.ClauseNo, out var v) ? v : 99)) ClauseRows.Add(c);
            var values = Project.Snapshot.ContractItems.GroupBy(i => i.ContractNo).ToDictionary(g => g.Key, g => g.Sum(i => i.Qty * i.Rate));
            foreach (var o in ObligationsCalendar.Build(terms, Docs.All<ContractRule>(), values)) ObligationRows.Add(o);
        }
        catch (Exception ex) { TermsText = "Contract terms unavailable: " + ex.Message; }
    }

    [RelayCommand]
    private void SaveRule()
    {
        if (SelectedRule is not { } r) return;
        try
        {
            _ = System.Text.Json.JsonDocument.Parse(r.ParamsJson);
            r.Origin = "USER";
            if (r.Id == 0) Docs.Insert(r, $"Contract rule added: {r.ContractNo} {r.RuleType}"); else Docs.Update(r, $"Contract rule edited: {r.ContractNo} {r.RuleType}");
            Ctx.Toasts.Show("RULE SAVED", r.Summary, ToastKind.Good);
            FillTerms();
        }
        catch (System.Text.Json.JsonException) { Ctx.Toasts.Show("PARAMETERS ARE NOT VALID JSON", "e.g. {\"stage\":\"1ST FIX\",\"pct\":0.9}", ToastKind.Warn); }
        catch (Exception ex) { Ctx.Toasts.Show("RULE NOT SAVED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void AddRule()
    {
        if (SelectedContractNo.Length == 0) return;
        var r = new ContractRule { ContractNo = SelectedContractNo, RuleType = RuleTypes.Retention, ParamsJson = "{\"pct\":0.1}", Summary = "Retention 10 %", Origin = "USER", Status = "CONFIRMED" };
        RuleRows.Add(r);
        SelectedRule = r;
    }

    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule is not { } r) return;
        if (r.Id == 0) { RuleRows.Remove(r); return; }
        if (!Ctx.Dialogs.Confirm("Delete rule", $"Delete '{r.Summary}'? (Disable it instead to keep it on record.)")) return;
        try { Docs.Delete(r, $"Contract rule deleted: {r.ContractNo} {r.RuleType}"); FillTerms(); }
        catch (Exception ex) { Ctx.Toasts.Show("RULE NOT DELETED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void SaveDates()
    {
        var t = Docs.All<ContractTerms>().FirstOrDefault(x => x.ContractNo == SelectedContractNo);
        if (t is null) { Ctx.Toasts.Show("NO TERMS YET", "Import the signed contract PDF first.", ToastKind.Warn); return; }
        t.HandoverDate = HandoverDate; t.CompletionDate = CompletionDate;
        try { Docs.Update(t, $"{t.ContractNo}: completion {CompletionDate:dd-MMM-yyyy}, handover {HandoverDate:dd-MMM-yyyy}"); FillTerms(); Ctx.Toasts.Show("DATES SAVED", "Obligations recalculated.", ToastKind.Good); }
        catch (Exception ex) { Ctx.Toasts.Show("DATES NOT SAVED", ex.Message, ToastKind.Error); }
    }

    private void FillCompare()
    {
        try
        {
            var terms = Docs.All<ContractTerms>().OrderBy(t => t.ContractNo).ToList();
            var dt = new DataTable();
            dt.Columns.Add("TERM");
            foreach (var t in terms) dt.Columns.Add(t.ContractNo);
            foreach (var row in ContractComparison.Terms(terms, Docs.All<ContractRule>()))
                dt.Rows.Add(new object[] { row.Term }.Concat(terms.Select(t => (object)row.ByContract.GetValueOrDefault(t.ContractNo, "-"))).ToArray());
            CompareTerms = dt.DefaultView;

            var items = Project.Snapshot.ContractItems.Where(i => i.Rate > 0).ToList();
            var nos = items.Select(i => i.ContractNo).Distinct().OrderBy(x => x).ToList();
            var rt = new DataTable();
            rt.Columns.Add("ITEM KIND"); rt.Columns.Add("UNIT");
            foreach (var n in nos) rt.Columns.Add(n, typeof(double));
            rt.Columns.Add("SPREAD %", typeof(double));
            foreach (var r in ContractComparison.Rates(items).Where(r => r.Rates.Count >= 2).Take(500))
            {
                var row = rt.NewRow();
                row[0] = r.Description.Length > 90 ? r.Description[..90] + "..." : r.Description; row[1] = r.Unit;
                foreach (var (c, v) in r.Rates) row[c] = v;
                row["SPREAD %"] = Math.Round(r.SpreadPct * 100, 1);
                rt.Rows.Add(row);
            }
            CompareRates = rt.DefaultView;
        }
        catch (Exception ex) { Ctx.Toasts.Show("COMPARISON UNAVAILABLE", ex.Message, ToastKind.Warn); }
    }
}
