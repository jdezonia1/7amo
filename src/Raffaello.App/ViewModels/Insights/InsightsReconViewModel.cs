using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Raffaello.App.Services;
using Raffaello.Core.Export;
using Raffaello.Core.Insights;

namespace Raffaello.App.ViewModels.Insights;

public sealed record UnmatchedRow(string Description, double Qty, string Unit);

/// <summary>[insights] Materials delivered vs installed (theoretical, from consumption norms) vs invoiced / paid; MOS release candidates.</summary>
public sealed partial class InsightsReconViewModel : PageViewModel
{
    private readonly InsightsHub _hub;
    private ReconResult? _last;
    private int _run;

    public InsightsReconViewModel(PageContext ctx, InsightsHub hub) : base(ctx) => _hub = hub;

    public override string Key => "MaterialRecon";
    public override string Title => "MATERIAL RECONCILIATION";
    public override string Subtitle => "Delivered (DN) vs installed (ledger points x consumption norms) vs invoiced / paid - stock on site, wastage, over-delivery, MOS release";
    public override bool ShowFilterBar => false;
    protected override bool HasCharts => true;

    public ObservableCollection<ReconRow> Rows { get; } = new();
    public ObservableCollection<MosReleaseCandidate> Mos { get; } = new();
    public ObservableCollection<UnmatchedRow> Unmatched { get; } = new();
    public ObservableCollection<InsightNorm> Norms { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty] private ReconRow? _selected;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _mosReleaseValue;
    [ObservableProperty] private int _overDelivered;
    [ObservableProperty] private ISeries[] _chart = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _chartX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _chartY = Array.Empty<Axis>();

    protected override void Refresh() => _ = LoadAsync(reloadNorms: true);

    private async Task LoadAsync(bool reloadNorms)
    {
        var run = ++_run;
        StatusText = "Reconciling...";
        try
        {
            var p = Project; var building = BuildingFilter;
            var data = await Task.Run(() => _hub.LoadData());
            var mats = await Task.Run(() => _hub.LoadMaterials());
            var norms = reloadNorms ? data.Norms : Norms.Where(n => n.Material.Trim().Length > 0).ToList();
            var r = await Task.Run(() => MaterialReconciliation.Build(p.Snapshot, mats, norms, building, _hub.Consumption(p, mats, building)));
            if (run != _run) return;
            _last = r;
            if (reloadNorms) { Norms.Clear(); foreach (var n in data.Norms) Norms.Add(n); }
            Rows.Clear(); foreach (var x in r.Rows) Rows.Add(x);
            Mos.Clear(); foreach (var x in r.Mos) Mos.Add(x);
            Unmatched.Clear(); foreach (var u in r.Unmatched.OrderByDescending(u => u.Qty)) Unmatched.Add(new UnmatchedRow(u.Description, u.Qty, u.Unit));
            Notes.Clear(); foreach (var n in r.Notes) Notes.Add(n);
            if (mats is null) Notes.Add("Materials tables could not be read - deliveries are empty.");
            MosReleaseValue = r.Mos.Sum(m => m.ReleaseValue);
            OverDelivered = r.Rows.Count(x => x.Status == "OVER-DELIVERED");
            Selected = Rows.FirstOrDefault();
            BuildChart(r);
            StatusText = $"{r.Rows.Count} materials  |  {(r.UsingDefaultNorms ? "DEFAULT norms" : $"{data.Norms.Count} norms")}{(r.UsingAssemblies ? " + ASSEMBLY templates" : "")}  |  {building ?? "ALL BUILDINGS"}";
        }
        catch (Exception ex) { StatusText = "Reconciliation failed: " + ex.Message; }
    }

    private void BuildChart(ReconResult r)
    {
        var rows = r.Rows.Where(x => x.Delivered > 0 || x.Theoretical > 0).ToList();
        Chart = new ISeries[]
        {
            ChartKit.Columns("THEORETICAL USE", rows.Select(x => x.Theoretical), ChartKit.Graphite, 22),
            ChartKit.Columns("DELIVERED", rows.Select(x => x.Delivered), ChartKit.Accent, 22),
            ChartKit.Columns("INVOICED", rows.Select(x => x.Invoiced), ChartKit.Yellow, 22),
        };
        ChartX = new[] { ChartKit.XLabels(rows.Select(x => x.Material), rows.Count > 6 ? 30 : 0) };
        ChartY = new[] { ChartKit.YValues(v => v >= 10000 ? $"{v / 1000:N0}K" : v.ToString("N0")) };
    }

    [RelayCommand] private Task Recalculate() => LoadAsync(reloadNorms: false);

    [RelayCommand]
    private void LoadDefaults()
    {
        foreach (var n in MaterialReconciliation.DefaultNorms())
            if (!Norms.Any(x => x.Material.Equals(n.Material, StringComparison.OrdinalIgnoreCase) && x.System == n.System && x.Stage == n.Stage)) Norms.Add(n);
    }

    /// <summary>Learn the norm from history: scale the material's norms so the theoretical use equals what was delivered (use once the work is complete).</summary>
    [RelayCommand]
    private void AdoptObserved()
    {
        if (Selected is not { InstalledPoints: > 0 } r || r.Theoretical <= 0) { Ctx.Toasts.Show("NOTHING TO LEARN", "Pick a material with installed points and deliveries.", ToastKind.Warn); return; }
        var factor = r.Delivered / r.Theoretical;
        var mine = Norms.Where(n => n.Material.Trim().Equals(r.Material, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0)
            foreach (var d in MaterialReconciliation.DefaultNorms().Where(n => n.Material.Equals(r.Material, StringComparison.OrdinalIgnoreCase))) { Norms.Add(d); mine.Add(d); }
        foreach (var n in mine)
        {
            n.PerPoint = Math.Round(n.PerPoint * factor, 2);
            n.Source = "LEARNED";
            n.Note = $"learned {DateTime.Today:dd-MMM-yy}: delivered {r.Delivered:N0} / {r.InstalledPoints:N0} installed points (includes wastage + stock)";
        }
        var copy = Norms.ToList(); Norms.Clear(); foreach (var n in copy) Norms.Add(n);
        Ctx.Toasts.Show("NORM LEARNED", $"{r.Material}: x{factor:0.00} - SAVE NORMS to keep it.", ToastKind.Good);
    }

    [RelayCommand]
    private async Task SaveNorms()
    {
        try
        {
            var rows = Norms.Where(n => n.Material.Trim().Length > 0 && n.PerPoint > 0).ToList();
            foreach (var n in rows) { n.Material = n.Material.Trim().ToUpperInvariant(); if (n.Source == "DEFAULT") n.Source = "MANUAL"; }
            await Task.Run(() => _hub.Store.ReplaceAll(rows, $"Consumption norms saved ({rows.Count})"));
            Ctx.Toasts.Show("NORMS SAVED", $"{rows.Count} consumption norms.", ToastKind.Good);
            await Ctx.Data.ReloadAsync();
        }
        catch (Exception ex) { Ctx.Toasts.Show("COULD NOT SAVE", ex.Message, ToastKind.Error); }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        if (_last is null) return Array.Empty<ExportSheet>();
        var scope = $"{BuildingFilter ?? "ALL BUILDINGS"}  |  {DateTime.Today:dd MMM yyyy}";
        return new[]
        {
            InsightsEngine.ReconSheet(_last, scope), InsightsEngine.MosSheet(_last, scope),
            new ExportSheet { Name = "UNMATCHED", Title = "DELIVERED MATERIALS WITHOUT A NORM", Columns = new() { new("DESCRIPTION", ColumnKind.Text, 60), new("QTY", ColumnKind.Number), new("UNIT") },
                Rows = _last.Unmatched.Select(u => new object?[] { u.Description, u.Qty, u.Unit }).ToList() },
        };
    }

    [RelayCommand] private void Export() => Ctx.Exports.Export("RAFFAELLO_MATERIAL_RECON", ExportCurrentView());
}
