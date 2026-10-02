using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Chain;
using Raffaello.Core.Domain;

namespace Raffaello.App.ViewModels;

public sealed class ChainLink
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>OK / OVER / CHECK / DUE / OPEN for the node colour.</summary>
    public string Status { get; init; } = "OK";
    public double Pct { get; init; }
    public bool IsLast { get; init; }
}

/// <summary>"THE CHAIN": the five links of the selected line with a verdict, findings and quick edits.</summary>
public sealed partial class ChainPanelViewModel : ObservableObject
{
    private readonly PageContext _ctx;

    [ObservableProperty] private ChainRow? _row;
    [ObservableProperty] private string _projectQtyText = "";
    [ObservableProperty] private string _sitePctText = "";

    public ObservableCollection<ChainLink> Links { get; } = new();
    public ObservableCollection<string> Findings { get; } = new();

    public event Action? AskRequested;

    public ChainPanelViewModel(PageContext ctx) => _ctx = ctx;

    public bool HasRow => Row != null;
    public string Title => Row is null ? "SELECT A LINE" : $"{Row.Level} {Row.Room}";
    public string SubTitle => Row is null ? "Pick a row to see its chain" : $"{Row.Building} / {Row.System} / {Row.Stage} / {Row.ItemCode}";
    public string Verdict => Row?.Status ?? "";
    public string Certifiable => Row is null ? "" : $"{Row.CertifiableQty:N0} {Row.Unit}";
    public string CertifiableValue => Row is null ? "" : $"SAR {Row.CertifiableQty * Row.Rate:N2}";

    partial void OnRowChanged(ChainRow? value)
    {
        Links.Clear();
        Findings.Clear();
        OnPropertyChanged(nameof(HasRow));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SubTitle));
        OnPropertyChanged(nameof(Verdict));
        OnPropertyChanged(nameof(Certifiable));
        OnPropertyChanged(nameof(CertifiableValue));
        _ctx.Selection.SelectedLine = value;
        if (value is null) return;
        var r = value;
        ProjectQtyText = r.ProjectQty?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        SitePctText = (r.SitePct * 100).ToString("0", CultureInfo.InvariantCulture);
        var givenOver = r.Given > r.Qs + 0.0001;
        var doneCheck = r.Done > r.Given + 0.0001 || r.Findings.Any(f => f.RuleCode == "SITE%");
        var claimOver = r.Claimed > r.Cap + 0.0001;
        var claimCheck = r.Claimed > r.Done + 0.0001;
        Links.Add(new ChainLink
        {
            Index = 1, Label = "QS / PROJECT QTY", Value = $"{r.Qs:N0}", Pct = 1,
            Detail = r.ProjectQty.HasValue ? $"PROJECT QTY {r.ProjectQty:N0} (cap)" : "PROJECT QTY empty - QS is the cap",
            Status = r.ProjectQty.HasValue ? "OK" : "DUE",
        });
        Links.Add(new ChainLink
        {
            Index = 2, Label = "GIVEN", Value = $"{r.Given:N0}", Pct = r.GivenPct,
            Detail = $"{(string.IsNullOrEmpty(r.Subcontractors) ? "not given" : r.Subcontractors)}  |  REMAINING {r.Remaining:N0}",
            Status = givenOver ? "OVER" : r.Remaining > 0 ? "OPEN" : "OK",
        });
        Links.Add(new ChainLink
        {
            Index = 3, Label = "DONE (WIR)", Value = $"{r.Done:N0}", Pct = r.WirPct,
            Detail = $"WIR {r.WirPct:P0}  |  SITE {r.SitePct:P0}{(r.OpenWirs > 0 ? $"  |  {r.OpenWirs} OPEN WIR ({r.OldestOpenWirDays} d)" : "")}{(r.Rework > 0 ? $"  |  EMT {r.Rework:N0}" : "")}",
            Status = doneCheck ? "CHECK" : r.OpenWirs > 0 ? "DUE" : r.Done + 0.0001 < r.Given ? "OPEN" : "OK",
        });
        Links.Add(new ChainLink
        {
            Index = 4, Label = "CLAIMED", Value = $"{r.Claimed:N0}", Pct = r.ClaimedPct,
            Detail = $"{(string.IsNullOrEmpty(r.LastInvoiceNo) ? "no statement" : r.LastInvoiceNo)}  |  CERTIFIED {r.Certified:N0}",
            Status = claimOver ? "OVER" : claimCheck ? "CHECK" : "OK",
        });
        Links.Add(new ChainLink
        {
            Index = 5, Label = "DELIVERED", Value = r.Delivered.HasValue ? $"{r.Delivered:N0}" : "—", Pct = r.Delivered.HasValue && r.Qs > 0 ? r.Delivered.Value / r.Qs : 0,
            Detail = r.Delivered.HasValue ? "issued to this line (DN)" : "not tracked at this stage",
            Status = r.Delivered.HasValue && r.Delivered < r.Done ? "CHECK" : "OK", IsLast = true,
        });
        foreach (var f in r.Findings.OrderByDescending(f => f.Severity)) Findings.Add($"{VerdictText.Of(f.Severity)}  {f.Message}");
        if (r.Findings.Count == 0) Findings.Add("OK  Chain consistent: every link agrees.");
    }

    [RelayCommand]
    private void Ask() => AskRequested?.Invoke();

    [RelayCommand]
    private async Task SaveEdits()
    {
        if (Row is null) return;
        var line = _ctx.Project.GetLine(Row.Id);
        if (line is null) return;
        if (line.RowVersion != Row.Line.RowVersion)
        {
            _ctx.Toasts.Show("CHANGED BY SOMEONE ELSE", $"{line.UpdatedBy} changed this line at {line.UpdatedAt:HH:mm}. Reloaded.", ToastKind.Warn);
            await _ctx.Data.ReloadAsync();
            return;
        }
        double? pq = string.IsNullOrWhiteSpace(ProjectQtyText) ? null :
            double.TryParse(ProjectQtyText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : line.ProjectQty;
        var site = double.TryParse(SitePctText, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s / 100.0, 0, 1) : line.SitePct;
        if (pq == line.ProjectQty && Math.Abs(site - line.SitePct) < 0.0001) { _ctx.Toasts.Show("NOTHING TO SAVE"); return; }
        line.ProjectQty = pq;
        line.SitePct = site;
        var id = line.Id;
        await _ctx.Data.WriteAsync(p => p.UpdateLine(line, $"{line.Level} {line.Room} {line.System} {line.Stage}: PROJECT QTY {pq?.ToString("N0") ?? "-"}, SITE {site:P0}"),
            _ctx.Toasts, "LINE SAVED");
        if (_ctx.Project.ChainById.TryGetValue(id, out var fresh)) Row = fresh;
    }
}
