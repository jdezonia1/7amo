using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core;
using Raffaello.Core.Export;
using Raffaello.Core.Insights;

namespace Raffaello.App.ViewModels.Insights;

public sealed record InsightsReportCard(string Key, string Name, string Description);

/// <summary>[insights] Insight report cards on the Reports page (Excel in the house style).</summary>
public sealed partial class InsightsReportsPanel : ObservableObject
{
    private readonly InsightsHub _hub;
    private readonly ProjectService _project;
    private readonly ExportService _exports;
    private readonly ToastService _toasts;
    private readonly FilterState _filter;

    public InsightsReportsPanel(InsightsHub hub, ProjectService project, ExportService exports, ToastService toasts, FilterState filter)
    {
        _hub = hub; _project = project; _exports = exports; _toasts = toasts; _filter = filter;
    }

    public InsightsReportCard[] Cards { get; } =
    {
        new("ANOMALIES", "Claim anomalies", "Every warning with severity, explanation, evidence, suggested action and dismissal reasons"),
        new("RECON", "Material reconciliation", "Delivered vs installed vs invoiced / paid, wastage, stock on site, over-delivery, MOS release"),
        new("RATES", "Rate benchmark", "Same item across contracts, POs and owner BOQ: min / max / average, outliers, margin"),
        new("CASHFLOW", "Cash-flow forecast", "Monthly payables (subcontractors, suppliers) vs owner receipts, by party, assumptions"),
        new("EV", "Earned value & productivity", "Planned vs actual per stage / system, points per week, forecast finish per area, early warnings"),
        new("ALL", "All insights", "The five reports above in one workbook"),
    };

    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task Run(string? key)
    {
        if (IsBusy || key is null) return;
        IsBusy = true;
        try
        {
            var p = _project;
            var building = _filter.Spec.Building;
            var scope = $"{building ?? "ALL BUILDINGS"}  |  as of {p.Options.Today:dd MMM yyyy}";
            var sheets = await Task.Run(() =>
            {
                var data = _hub.LoadData();
                var mats = _hub.LoadMaterials();
                var list = new List<ExportSheet>();
                ReconResult? recon = null; EvResult? ev = null;
                if (key is "ANOMALIES" or "ALL")
                {
                    var a = _hub.Compute(p, building, false, out recon, out ev, data, mats);
                    list.Add(InsightsEngine.AnomalySheet(a, scope));
                }
                if (key is "RECON" or "ALL")
                {
                    recon ??= MaterialReconciliation.Build(p.Snapshot, mats, data.Norms, building, _hub.Consumption(p, mats, building));
                    list.Add(InsightsEngine.ReconSheet(recon, scope));
                    list.Add(InsightsEngine.MosSheet(recon, scope));
                }
                if (key is "RATES" or "ALL") list.AddRange(RateBenchmark.Sheets(RateBenchmark.Build(p.Snapshot, mats), scope));
                if (key is "CASHFLOW" or "ALL")
                    list.AddRange(CashFlowForecast.Sheets(CashFlowForecast.Build(new CashFlowInputs
                    { Project = p.Snapshot, Materials = mats, Data = data, Today = p.Options.Today, ProjectStart = p.ProjectStart, PlannedFinish = p.PlannedFinish, Building = building }), scope));
                if (key is "EV" or "ALL")
                {
                    ev ??= EarnedValue.Build(new EvInputs { Project = p.Snapshot, Data = data, Today = p.Options.Today, ProjectStart = p.ProjectStart, PlannedFinish = p.PlannedFinish, Building = building });
                    list.AddRange(EarnedValue.Sheets(ev, scope));
                }
                return list;
            });
            _exports.Export($"RAFFAELLO_INSIGHTS_{key}", sheets);
        }
        catch (Exception ex) { _toasts.Show("REPORT FAILED", ex.Message, ToastKind.Error); }
        finally { IsBusy = false; }
    }
}
