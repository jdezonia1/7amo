using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Raffaello.App.Services;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;

namespace Raffaello.App.ViewModels;

public sealed class ContractRow
{
    public required Contract Contract { get; init; }
    public double Claimed { get; init; }
    public double Certified { get; init; }
    public double CertifiedPct => Contract.Value <= 0 ? 0 : Certified / Contract.Value;
    public double Retention => Certified * Contract.RetentionPct;
    public string Status => CertifiedPct > 1 ? "OVER" : "OK";
}

public sealed partial class BoqRow : ObservableObject
{
    public required BoqItem Item { get; init; }
    public double QsTotal { get; init; }
    public double Claimed { get; init; }
    [ObservableProperty] private double? _projectQty;
    [ObservableProperty] private double _rate;
    [ObservableProperty] private bool _isDirty;
    public string ItemCode => Item.ItemCode;
    public string Bill => Item.Bill;
    public string Description => Item.Description;
    public string System => Item.System;
    public string Stage => Item.Stage;
    public string Unit => Item.Unit;
    public double BoqQty => Item.BoqQty;
    public double Value => (ProjectQty ?? BoqQty) * Rate;
    public string Status => ProjectQty is null ? "DUE" : Claimed > ProjectQty + 0.0001 ? "OVER" : "OK";
    partial void OnProjectQtyChanged(double? value) { IsDirty = true; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Value)); }
    partial void OnRateChanged(double value) { IsDirty = true; OnPropertyChanged(nameof(Value)); }
}

/// <summary>Subcontracts and the BOQ: rates and PROJECT QTY (the cap on subcontractor claims), editable with concurrency checks.</summary>
public sealed partial class ContractsViewModel : PageViewModel
{
    public ContractsViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "Contracts";
    public override string Title => "CONTRACTS & BOQ";
    public override string Subtitle => "Rates and PROJECT QTY - the cap on every subcontractor claim";
    protected override bool HasCharts => true;

    public ObservableCollection<ContractRow> Contracts { get; } = new();
    public ObservableCollection<BoqRow> Boq { get; } = new();

    [ObservableProperty] private string _coverage = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ISeries[] _coverageSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ISeries[] _boqSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _boqX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _boqY = Array.Empty<Axis>();

    partial void OnSearchChanged(string value) => Refresh();

    protected override void Refresh()
    {
        var p = Project;
        var s = p.Snapshot;
        Contracts.Clear();
        foreach (var c in s.Contracts.OrderBy(c => c.ContractNo))
        {
            var mine = p.Chain.Where(r => r.Subcontractors.Contains(c.Subcontractor)).ToList();
            Contracts.Add(new ContractRow { Contract = c, Claimed = mine.Sum(r => r.ClaimedValue), Certified = mine.Sum(r => r.CertifiedValue) });
        }

        var spec = Spec;
        var rows = Scoped();
        Boq.Clear();
        foreach (var b in s.BoqItems.Where(b => (spec.System is null || b.System == spec.System) && (spec.Stage is null || b.Stage == spec.Stage)
                                                && (spec.Building is null || b.ItemCode.StartsWith(spec.Building == Buildings.Hotel ? "H-" : "B-")))
                     .Where(b => string.IsNullOrWhiteSpace(Search) || b.ItemCode.Contains(Search, StringComparison.OrdinalIgnoreCase) || b.Description.Contains(Search, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(b => b.ItemCode))
        {
            var building = b.ItemCode.StartsWith("H-") ? Buildings.Hotel : Buildings.Branded;
            var code = b.ItemCode[2..];
            var lines = p.Chain.Where(r => r.Building == building && r.ItemCode == code).ToList();
            var row = new BoqRow { Item = b, QsTotal = lines.Sum(r => r.Qs), Claimed = lines.Sum(r => r.Claimed) };
            row.ProjectQty = b.ProjectQty;
            row.Rate = b.Rate;
            row.IsDirty = false;
            Boq.Add(row);
        }
        var filled = rows.Count(r => r.ProjectQty.HasValue);
        Coverage = $"PROJECT QTY FILLED ON {filled:N0} OF {rows.Count:N0} LINES ({(rows.Count == 0 ? 0 : (double)filled / rows.Count):P0})  |  BOQ ITEMS WITH PROJECT QTY {s.BoqItems.Count(b => b.ProjectQty.HasValue)}/{s.BoqItems.Count}";
        CoverageSeries = new ISeries[]
        {
            ChartKit.Slice("FILLED", filled, ChartKit.Accent, 46),
            ChartKit.Slice("EMPTY (QS USED)", rows.Count - filled, ChartKit.Graphite, 46),
        };
        var bySys = Boq.GroupBy(b => b.System).OrderBy(g => Array.IndexOf(Raffaello.Core.Domain.Systems.Main, g.Key)).ToList();
        BoqSeries = new ISeries[]
        {
            ChartKit.Columns("BOQ VALUE", bySys.Select(g => g.Sum(b => b.BoqQty * b.Rate)), ChartKit.Graphite, 18),
            ChartKit.Columns("PROJECT VALUE", bySys.Select(g => g.Sum(b => b.Value)), ChartKit.Accent, 18),
            ChartKit.Columns("CLAIMED", bySys.Select(g => g.Sum(b => b.Claimed * b.Rate)), ChartKit.Yellow, 18),
        };
        BoqX = new[] { ChartKit.XLabels(bySys.Select(g => g.Key)) };
        BoqY = new[] { ChartKit.YValues(v => $"{v / 1000:N0}K") };
    }

    [RelayCommand]
    private async Task Save()
    {
        var dirty = Boq.Where(b => b.IsDirty).ToList();
        if (dirty.Count == 0) { Ctx.Toasts.Show("NOTHING TO SAVE"); return; }
        foreach (var b in dirty)
        {
            b.Item.ProjectQty = b.ProjectQty;
            b.Item.Rate = b.Rate;
            var item = b.Item;
            if (!await Ctx.Data.WriteAsync(p => p.UpdateBoq(item), Ctx.Toasts)) return;
        }
        Ctx.Toasts.Show("BOQ SAVED", $"{dirty.Count} items", ToastKind.Good);
    }

    [RelayCommand]
    private async Task ApplyToLines()
    {
        // pushes each BOQ item's PROJECT QTY down to its lines pro-rata to QS, filling empty caps only
        var targets = Boq.Where(b => b.ProjectQty.HasValue && b.QsTotal > 0).ToList();
        var updates = new List<QtyLine>();
        foreach (var b in targets)
        {
            var building = b.ItemCode.StartsWith("H-") ? Buildings.Hotel : Buildings.Branded;
            var code = b.ItemCode[2..];
            foreach (var r in Project.Chain.Where(r => r.Building == building && r.ItemCode == code && !r.ProjectQty.HasValue))
            {
                r.Line.ProjectQty = Math.Floor(r.Qs * b.ProjectQty!.Value / b.QsTotal);
                updates.Add(r.Line);
            }
        }
        if (updates.Count == 0) { Ctx.Toasts.Show("NOTHING TO APPLY", "Every line already has a PROJECT QTY."); return; }
        if (!Ctx.Dialogs.Confirm("Apply PROJECT QTY", $"Fill PROJECT QTY on {updates.Count} lines that are empty, pro-rata to QS?")) { await Ctx.Data.ReloadAsync(); return; }
        await Ctx.Data.WriteAsync(p => p.FillProjectQty(updates), Ctx.Toasts, $"PROJECT QTY filled on {updates.Count} lines");
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        yield return new ExportSheet
        {
            Name = "BOQ", Title = "BOQ - RATES AND PROJECT QTY",
            Columns = new() { new("ITEM"), new("BILL"), new("DESCRIPTION", Width: 40), new("SYSTEM"), new("STAGE"), new("UNIT"), new("BOQ QTY", ColumnKind.Integer), new("QS (LINES)", ColumnKind.Integer), new("PROJECT QTY", ColumnKind.Integer), new("CLAIMED", ColumnKind.Integer), new("RATE", ColumnKind.Money), new("VALUE", ColumnKind.Money), new("STATUS") },
            Rows = Boq.Select(b => new object?[] { b.ItemCode, b.Bill, b.Description, b.System, b.Stage, b.Unit, b.BoqQty, b.QsTotal, b.ProjectQty, b.Claimed, b.Rate, b.Value, b.Status }).ToList(),
            TotalRow = new object?[] { "TOTAL", null, null, null, null, null, null, null, null, null, null, Boq.Sum(b => b.Value), null },
        };
        yield return new ExportSheet
        {
            Name = "CONTRACTS", Title = "SUBCONTRACTS",
            Columns = new() { new("CONTRACT"), new("SUBCONTRACTOR"), new("SCOPE", Width: 40), new("VALUE", ColumnKind.Money), new("CLAIMED", ColumnKind.Money), new("CERTIFIED", ColumnKind.Money), new("CERTIFIED %", ColumnKind.Percent), new("RETENTION", ColumnKind.Money) },
            Rows = Contracts.Select(c => new object?[] { c.Contract.ContractNo, c.Contract.Subcontractor, c.Contract.Scope, c.Contract.Value, c.Claimed, c.Certified, c.CertifiedPct, c.Retention }).ToList(),
        };
    }
}
