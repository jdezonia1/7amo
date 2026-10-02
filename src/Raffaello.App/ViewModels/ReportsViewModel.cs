using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Analytics;
using Raffaello.Core.Chain;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Rules;

namespace Raffaello.App.ViewModels;

public sealed record ReportCard(string Name, string Description, string Kind);
public sealed record SummaryLine(string Label, string Value, string Status);
public sealed record ProjectReportCard(Raffaello.Core.Reports.ReportDefinition Def)
{
    public string Name => Def.Name;
    public string Description => Def.Description;
    public string XlsxParam => Def.Key + "|XLSX";
    public string PdfParam => Def.Key + "|PDF";
}

/// <summary>Weekly progress report (Excel) and a printable one-page summary.</summary>
public sealed partial class ReportsViewModel : PageViewModel
{
    private readonly Raffaello.Core.Materials.IMaterialsStore _materials;
    private readonly Raffaello.Core.Materials.MaterialsSettings _materialsSettings;
    private readonly AconexAutomationService _aconex;

    public ReportsViewModel(PageContext ctx, Raffaello.Core.Materials.IMaterialsStore materials, Raffaello.Core.Materials.MaterialsSettings materialsSettings, AconexAutomationService aconex,
        Insights.InsightsReportsPanel insightsReports /* [insights] */) : base(ctx)
    {
        _materials = materials; _materialsSettings = materialsSettings; _aconex = aconex;
        InsightsReports = insightsReports;   // [insights]
    }

    /// <summary>[insights] Insight report cards (anomalies, material reconciliation, rate benchmark, cash flow, earned value).</summary>
    public Insights.InsightsReportsPanel InsightsReports { get; }

    /// <summary>[phase6] Management reports from the real data (ledger, invoices, materials, variations, Aconex).</summary>
    public ProjectReportCard[] ProjectCards { get; } = Raffaello.Core.Reports.ProjectReports.All.Select(d => new ProjectReportCard(d)).ToArray();

    private Raffaello.Core.Reports.ReportInputs Inputs()
    {
        Raffaello.Core.Materials.MaterialsSnapshot? mats = null;
        try { mats = _materials.Load(); } catch (Exception) { /* materials not available */ }
        List<Raffaello.Core.Variations.Variation> vos = new(); List<Raffaello.Core.Variations.VariationLine> voLines = new();
        List<Raffaello.Core.AconexWeb.StatusBoardRow> board;
        try { vos = _aconex.Variations.Variations(); voLines = _aconex.Variations.AllLines(); } catch (Exception) { }
        try { board = Raffaello.Core.AconexWeb.StatusBoard.Build(Project.Snapshot.SubInvoices, _aconex.Store.ActiveLinks(), _aconex.Store.LatestChecks(), Today, includeClosed: false); }
        catch (Exception) { board = Raffaello.Core.AconexWeb.StatusBoard.Build(Project.Snapshot.SubInvoices, Array.Empty<Raffaello.Core.AconexWeb.AconexWorkflowLink>(), new Dictionary<string, Raffaello.Core.AconexWeb.AconexWorkflowCheck>(), Today, false); }
        return new Raffaello.Core.Reports.ReportInputs
        {
            Project = Project.Snapshot, Materials = mats, MaterialsSettings = _materialsSettings, Variations = vos, VariationLines = voLines, InvoiceBoard = board, Building = BuildingFilter, AsOf = DateTime.Today,
        };
    }

    /// <summary>Parameter "KEY|XLSX" or "KEY|PDF".</summary>
    [RelayCommand]
    private async Task ProjectReport(string? param)
    {
        var parts = (param ?? "").Split('|');
        if (parts.Length != 2) return;
        var def = Raffaello.Core.Reports.ProjectReports.All.First(r => r.Key == parts[0]);
        var pdf = parts[1] == "PDF";
        var name = $"RAFFAELLO_{def.Key}_{DateTime.Now:yyyyMMdd}" + (pdf ? ".pdf" : ".xlsx");
        var path = Ctx.Dialogs.SaveFile($"{def.Name} ({(pdf ? "PDF" : "Excel")})", name, pdf ? "PDF|*.pdf" : "Excel workbook|*.xlsx");
        if (path is null) return;
        try
        {
            var inputs = Inputs();
            await Task.Run(() =>
            {
                var sheets = Raffaello.Core.Reports.ProjectReports.Build(def.Key, inputs);
                if (pdf) Raffaello.Core.Reports.ProjectReports.ExportPdf(path, $"Raffles Hotel & Branded Residence - {def.Name}", sheets);
                else ExcelExporter.Export(path, sheets);
            });
            Ctx.Toasts.Show("REPORT READY", System.IO.Path.GetFileName(path), ToastKind.Good);
            DialogService.OpenWithShell(path);
        }
        catch (System.IO.IOException ex) { Ctx.Toasts.Show("REPORT FAILED", ex.Message + " (is the file open?)", ToastKind.Error); }
        catch (Exception ex) { Ctx.Toasts.Show("REPORT FAILED", ex.Message, ToastKind.Error); }
    }

    public override string Key => "Reports";
    public override string Title => "REPORTS";
    public override string Subtitle => "Weekly progress report, chain exports, printable summary";

    public ObservableCollection<SummaryLine> Summary { get; } = new();
    public ObservableCollection<ChainTotals> Stages { get; } = new();
    public ReportCard[] Cards { get; } =
    {
        new("WEEKLY PROGRESS REPORT", "Summary, by stage, by system, S-curve, needs-today, OVER/CHECK lines, open WIRs, materials. One workbook.", "WEEKLY"),
        new("CHAIN - ALL LINES", "Every line in the current filter with QS, GIVEN, DONE, CLAIMED, DELIVERED and the verdict.", "CHAIN"),
        new("OVER / CHECK REGISTER", "Only the lines that need action, with the reason.", "FLAGS"),
        new("MATERIALS - PO vs DN", "One sheet per PO, one column per DN, value per DN and the PO total check.", "MATERIALS"),
        new("PRINTABLE SUMMARY", "One-page summary for the weekly meeting (print or save as PDF).", "PRINT"),
    };

    [ObservableProperty] private string _scopeText = "";

    protected override void Refresh()
    {
        var p = Project;
        var rows = Scoped();
        ScopeText = $"SCOPE: {Spec.Describe()}  |  {rows.Count:N0} LINES  |  {Today:dd MMM yyyy}";
        var curve = ProjectAnalytics.SCurve(p.Snapshot, rows, p.ProjectStart, p.PlannedFinish, Today);
        var fc = ProjectAnalytics.ForecastCompletion(curve);
        var now = curve.LastOrDefault(c => c.Actual.HasValue);
        Summary.Clear();
        Summary.Add(new("ACTUAL PROGRESS", now?.Actual?.ToString("P1") ?? "-", (now?.Actual ?? 0) + 0.05 < (now?.Planned ?? 0) ? "CHECK" : "OK"));
        Summary.Add(new("PLANNED PROGRESS", now?.Planned.ToString("P1") ?? "-", "OPEN"));
        Summary.Add(new("FORECAST COMPLETION", fc.CompletionDate?.ToString("dd MMM yyyy") ?? "n/a", fc.CompletionDate > p.PlannedFinish ? "OVER" : "OK"));
        Summary.Add(new("CERTIFIED TO DATE", $"SAR {rows.Sum(r => r.CertifiedValue):N0}", "OK"));
        Summary.Add(new("CLAIMED TO DATE", $"SAR {rows.Sum(r => r.ClaimedValue):N0}", "OPEN"));
        Summary.Add(new("OVER LINES", rows.Count(r => r.Verdict == Verdict.Over).ToString("N0"), rows.Any(r => r.Verdict == Verdict.Over) ? "OVER" : "OK"));
        Summary.Add(new("CHECK LINES", rows.Count(r => r.Verdict == Verdict.Check).ToString("N0"), rows.Any(r => r.Verdict == Verdict.Check) ? "CHECK" : "OK"));
        Summary.Add(new("OPEN WIRS", p.Snapshot.Wirs.Count(w => w.Kind == "WIR" && w.Status == WirStatus.Open).ToString("N0"), "DUE"));
        Summary.Add(new("NEEDS TODAY", p.Queue.Count.ToString("N0"), p.Queue.Count > 0 ? "CHECK" : "OK"));
        Stages.Clear();
        foreach (var t in ChainMath.TotalsByStage(rows).Values) Stages.Add(t);
    }

    [RelayCommand]
    private void Run(string? kind)
    {
        var p = Project;
        var rows = Scoped();
        switch (kind)
        {
            case "WEEKLY":
                Ctx.Exports.Export("RAFFAELLO_WEEKLY_REPORT", ReportBuilder.WeeklyReport(p.Snapshot, rows, p.Options, p.ProjectStart, p.PlannedFinish, p.Settings.EffectiveUserName));
                break;
            case "CHAIN":
                Ctx.Exports.Export("RAFFAELLO_CHAIN", new[] { ReportBuilder.ChainSheet("CHAIN", rows, $"CHAIN - {Spec.Describe()}") });
                break;
            case "FLAGS":
                Ctx.Exports.Export("RAFFAELLO_OVER_CHECK", new[] { ReportBuilder.ChainSheet("OVER-CHECK", rows.Where(r => r.Verdict is Verdict.Over or Verdict.Check).OrderByDescending(r => r.Verdict), "OVER / CHECK REGISTER") });
                break;
            case "MATERIALS":
                Ctx.Exports.Export("RAFFAELLO_MATERIALS", MaterialAnalysis.All(p.Snapshot).Select(ReportBuilder.PoProgressSheet));
                break;
            case "PRINT":
                Print();
                break;
        }
    }

    private void Print()
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        var doc = BuildDocument();
        doc.PageWidth = dlg.PrintableAreaWidth;
        doc.PageHeight = dlg.PrintableAreaHeight;
        doc.ColumnWidth = dlg.PrintableAreaWidth;
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "Raffaello weekly summary");
        Ctx.Toasts.Show("SENT TO PRINTER", kind: ToastKind.Good);
    }

    public FlowDocument BuildDocument()
    {
        var red = new SolidColorBrush(Color.FromRgb(0x8E, 0x1B, 0x22));
        var grey = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xA6));
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 11, PagePadding = new Thickness(48) };
        doc.Blocks.Add(new Paragraph(new Run("RAFFAELLO  |  WEEKLY SUMMARY")) { FontSize = 22, FontWeight = FontWeights.Bold, Foreground = red, Margin = new Thickness(0) });
        doc.Blocks.Add(new Paragraph(new Run($"Every quantity. One chain.   {ScopeText}")) { Foreground = Brushes.DimGray });
        var t = new Table { CellSpacing = 0 };
        t.Columns.Add(new TableColumn { Width = new GridLength(220) });
        t.Columns.Add(new TableColumn { Width = new GridLength(200) });
        t.Columns.Add(new TableColumn { Width = new GridLength(80) });
        var g = new TableRowGroup();
        var header = new TableRow { Background = grey };
        foreach (var h in new[] { "ITEM", "VALUE", "FLAG" }) header.Cells.Add(new TableCell(new Paragraph(new Run(h)) { FontWeight = FontWeights.Bold }) { Padding = new Thickness(4) });
        g.Rows.Add(header);
        foreach (var s in Summary)
        {
            var r = new TableRow();
            r.Cells.Add(new TableCell(new Paragraph(new Run(s.Label))) { Padding = new Thickness(4) });
            r.Cells.Add(new TableCell(new Paragraph(new Run(s.Value)) { FontFamily = new FontFamily("Consolas") }) { Padding = new Thickness(4) });
            r.Cells.Add(new TableCell(new Paragraph(new Run(s.Status)) { Foreground = s.Status == "OVER" ? red : Brushes.Black, FontWeight = FontWeights.Bold }) { Padding = new Thickness(4) });
            g.Rows.Add(r);
        }
        t.RowGroups.Add(g);
        doc.Blocks.Add(t);
        doc.Blocks.Add(new Paragraph(new Run("BY STAGE (STAGES ARE NEVER SUMMED)")) { FontWeight = FontWeights.Bold, Foreground = red, Margin = new Thickness(0, 16, 0, 4) });
        var st = new Table { CellSpacing = 0 };
        foreach (var _ in Enumerable.Range(0, 6)) st.Columns.Add(new TableColumn { Width = new GridLength(95) });
        var sg = new TableRowGroup();
        var sh = new TableRow { Background = grey };
        foreach (var h in new[] { "STAGE", "QS", "GIVEN", "DONE", "CLAIMED", "DONE %" }) sh.Cells.Add(new TableCell(new Paragraph(new Run(h)) { FontWeight = FontWeights.Bold }) { Padding = new Thickness(4) });
        sg.Rows.Add(sh);
        foreach (var s in Stages)
        {
            var r = new TableRow();
            foreach (var v in new[] { s.Stage, s.Qs.ToString("N0"), s.Given.ToString("N0"), s.Done.ToString("N0"), s.Claimed.ToString("N0"), s.DonePct.ToString("P0") })
                r.Cells.Add(new TableCell(new Paragraph(new Run(v))) { Padding = new Thickness(4) });
            sg.Rows.Add(r);
        }
        st.RowGroups.Add(sg);
        doc.Blocks.Add(st);
        doc.Blocks.Add(new Paragraph(new Run("NEEDS YOU TODAY")) { FontWeight = FontWeights.Bold, Foreground = red, Margin = new Thickness(0, 16, 0, 4) });
        var list = new List();
        foreach (var q in Project.Queue.Take(12)) list.ListItems.Add(new ListItem(new Paragraph(new Run($"[{q.Tag}] {q.Title} - {q.Detail}"))));
        doc.Blocks.Add(list);
        return doc;
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        var p = Project;
        return ReportBuilder.WeeklyReport(p.Snapshot, Scoped(), p.Options, p.ProjectStart, p.PlannedFinish, p.Settings.EffectiveUserName);
    }
}
