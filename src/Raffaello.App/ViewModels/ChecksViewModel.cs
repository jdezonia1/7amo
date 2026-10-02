using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Ledger;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

public sealed class CheckRow
{
    public required ClaimLine Line { get; init; }
    public required bool IsHeight { get; init; }
    public string Sub => Line.Subcontractor;
    public int InvoiceNo => Line.InvoiceNo;
    public string Room => Line.Room;
    public string Stage => Line.Stage;
    public string Item => Line.Item;
    public double Qty => Line.Qty;
    public double Claimed => IsHeight ? Line.QtyAbove45 : Line.LengthClaimedQty;
    public double Decided => IsHeight ? HeightCheck.AcceptedHigh(Line) : LengthCheck.InvoiceBaseQty(Line);
    public string CheckStatusText => (IsHeight ? Line.HeightStatus : Line.LengthStatus) is { Length: > 0 } s ? s : CheckStatus.Pending;
    public bool Pending => IsHeight ? HeightCheck.IsPending(Line) : LengthCheck.IsPending(Line);
    public string Status => Pending ? "DUE" : CheckStatusText switch { CheckStatus.Rejected => "REJECTED", CheckStatus.Partly or CheckStatus.Revised => "CHECK", _ => "OK" };
    public string Note => IsHeight ? Line.HeightNote : Line.LengthNote;
    public string CheckedBy => IsHeight ? Line.HeightCheckedBy : Line.LengthCheckedBy;
}

public sealed class LengthExtraRow
{
    public string Sub { get; init; } = "";
    public int Lines { get; init; }
    public double Claimed { get; init; }
    public double Invoiced { get; init; }
    public double RejectedExtra { get; init; }
}

/// <summary>Checks queue: HEIGHT CHECK (&gt;4.5 m) and LENGTH CHECK (15 m rule). Pending lines are held out of invoices.</summary>
public sealed partial class ChecksViewModel : PageViewModel
{
    public ChecksViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "Checks";
    public override string Title => "CHECKS";
    public override string Subtitle => "HEIGHT CHECK (>4.5 m) and LENGTH CHECK (15 m rule) - pending lines are held out of invoicing";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => true;   // [phase6] the building switcher drives it

    public string[] Kinds { get; } = { "HEIGHT", "LENGTH" };
    public string[] Scopes { get; } = { "PENDING", "ALL" };
    public ObservableCollection<CheckRow> Rows { get; } = new();
    public ObservableCollection<LengthExtraRow> Extras { get; } = new();
    public ObservableCollection<string> Subcontractors { get; } = new();

    [ObservableProperty] private string _kind = "HEIGHT";
    [ObservableProperty] private string _scope = "PENDING";
    [ObservableProperty] private string _subFilter = "ALL";
    [ObservableProperty] private CheckRow? _selected;
    [ObservableProperty] private string _countText = "";

    // decision form
    [ObservableProperty] private string _acceptedQty = "";
    [ObservableProperty] private string _routeTotal = "";
    [ObservableProperty] private string _groups = "";
    [ObservableProperty] private string _revisedOverride = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _preview = "";

    public bool IsHeight => Kind == "HEIGHT";
    public bool IsLength => Kind == "LENGTH";

    partial void OnKindChanged(string value) { OnPropertyChanged(nameof(IsHeight)); OnPropertyChanged(nameof(IsLength)); Fill(); }
    partial void OnScopeChanged(string value) => Fill();
    partial void OnSubFilterChanged(string value) => Fill();
    partial void OnSelectedChanged(CheckRow? value) => LoadSelected();
    partial void OnRouteTotalChanged(string value) => UpdatePreview();
    partial void OnGroupsChanged(string value) => UpdatePreview();
    partial void OnRevisedOverrideChanged(string value) => UpdatePreview();

    protected override void Refresh()
    {
        var claims = Project.Snapshot.Claims;
        var subs = new[] { "ALL" }.Concat(claims.Where(c => c.QtyAbove45 != 0 || c.LengthApplies).Select(c => c.Subcontractor).Distinct().OrderBy(x => x)).ToList();
        if (!Subcontractors.SequenceEqual(subs)) { Subcontractors.Clear(); foreach (var s in subs) Subcontractors.Add(s); }
        if (!Subcontractors.Contains(SubFilter)) SubFilter = "ALL";
        Extras.Clear();
        foreach (var g in claims.Where(c => c.LengthApplies).GroupBy(c => c.Subcontractor).OrderBy(g => g.Key))
            Extras.Add(new LengthExtraRow
            {
                Sub = g.Key, Lines = g.Count(), Claimed = g.Sum(c => c.LengthClaimedQty), Invoiced = g.Sum(LengthCheck.InvoiceBaseQty), RejectedExtra = g.Sum(LengthCheck.RejectedExtra),
            });
        Fill();
    }

    private void Fill()
    {
        var keep = Selected?.Line.Id;
        var claims = Project.Snapshot.Claims;
        var height = IsHeight;
        var all = claims.Where(c => InBuilding(c.Building) && (height ? c.QtyAbove45 != 0 : c.LengthApplies)).Select(c => new CheckRow { Line = c, IsHeight = height }).ToList();
        var q = all.AsEnumerable();
        if (Scope == "PENDING") q = q.Where(r => r.Pending);
        if (SubFilter is { Length: > 0 } s && s != "ALL") q = q.Where(r => r.Sub == s);
        Rows.Clear();
        foreach (var r in q.OrderBy(r => r.Sub).ThenBy(r => r.InvoiceNo).ThenBy(r => r.Room).ThenBy(r => r.Item)) Rows.Add(r);
        var pending = all.Count(r => r.Pending);
        CountText = $"{Rows.Count} SHOWN  |  {pending} PENDING  |  {all.Count} {(height ? "HEIGHT" : "LENGTH")} LINES  |  pending lines are not invoiced";
        Selected = Rows.FirstOrDefault(r => r.Line.Id == keep) ?? Rows.FirstOrDefault();
    }

    private static double? D(string s) => double.TryParse((s ?? "").Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private void LoadSelected()
    {
        var l = Selected?.Line;
        if (l is null) { AcceptedQty = RouteTotal = Groups = RevisedOverride = Note = Preview = ""; return; }
        AcceptedQty = (l.HeightStatus.Length > 0 ? l.QtyAbove45Accepted : l.QtyAbove45).ToString("0.##", CultureInfo.InvariantCulture);
        RouteTotal = l.RouteLengthTotal > 0 ? l.RouteLengthTotal.ToString("0.##", CultureInfo.InvariantCulture) : "";
        Groups = l.LengthGroups;
        RevisedOverride = l.LengthRevisedOverride?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        Note = Selected!.IsHeight ? l.HeightNote : (l.LengthNote.StartsWith("plan qty") ? "" : l.LengthNote);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var l = Selected?.Line;
        if (l is null) { Preview = ""; return; }
        if (Selected!.IsHeight)
        {
            Preview = $"QTY {l.Qty:0.##}, of which >4.5 m claimed {l.QtyAbove45:0.##}. Accepted part is priced at the HIGH item, the rest at the normal item.";
            return;
        }
        var probe = LedgerRules.Copy(l);
        probe.RouteLengthTotal = D(RouteTotal) ?? 0;
        probe.LengthGroups = Groups.Trim();
        probe.LengthNote = "";
        var revised = LengthCheck.Recalculate(probe, Project.Settings.LengthRoundingDecimals);
        var ov = D(RevisedOverride);
        Preview = $"PLAN QTY {l.Qty:0.##}  |  CLAIMED {l.LengthClaimedQty:0.##}  |  REVISED {revised:0.##}{(ov.HasValue ? $"  |  OVERRIDE {ov:0.##}" : "")}\n" +
                  $"Per point max(1, L/15) rounded to {Project.Settings.LengthRoundingDecimals} dp. Groups like 10x20;5x35 are exact; a total length is exact only when every run is at least 15 m.";
    }

    private async Task DecideHeight(string status)
    {
        if (Selected is not { IsHeight: true } row) return;
        var line = row.Line;
        var qty = D(AcceptedQty);
        var note = Note.Trim();
        await Ctx.Data.WriteAsync(p => p.Workflow.DecideHeight(line, status, qty, note), Ctx.Toasts, $"HEIGHT {status}");
    }

    private async Task DecideLength(string status)
    {
        if (Selected is not { IsHeight: false } row) return;
        var line = row.Line;
        double? total = D(RouteTotal);
        var groups = Groups.Trim();
        var ov = status == CheckStatus.Revised ? D(RevisedOverride) : null;
        var note = Note.Trim();
        await Ctx.Data.WriteAsync(p => p.Workflow.DecideLength(line, status, total, groups, ov, note), Ctx.Toasts, $"LENGTH {status}");
    }

    [RelayCommand] private Task AcceptHeight() => DecideHeight(CheckStatus.Accepted);
    [RelayCommand] private Task PartlyHeight() => DecideHeight(CheckStatus.Partly);
    [RelayCommand] private Task RejectHeight() => DecideHeight(CheckStatus.Rejected);
    [RelayCommand] private Task AcceptLength() => DecideLength(CheckStatus.Accepted);
    [RelayCommand] private Task ReviseLength() => DecideLength(CheckStatus.Revised);
    [RelayCommand] private Task RejectLength() => DecideLength(CheckStatus.Rejected);
    [RelayCommand] private void OpenRoom() { if (Selected != null) Ctx.Nav.Go("Ledger", new NavTarget("Ledger", Key: Selected.Room)); }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        yield return new ExportSheet
        {
            Name = $"{Kind} CHECK", Title = $"{Kind} CHECK - {Scope}",
            Columns = new() { new("SUBCONTRACTOR"), new("INVOICE", ColumnKind.Integer), new("ROOM"), new("STAGE"), new("ITEM"), new("QTY", ColumnKind.Number),
                new(IsHeight ? ">4.5 M CLAIMED" : "15 M CLAIMED", ColumnKind.Number), new(IsHeight ? "ACCEPTED HIGH" : "INVOICED", ColumnKind.Number), new("CHECK"), new("CHECKED BY"), new("NOTE", Width: 50) },
            Rows = Rows.Select(r => new object?[] { r.Sub, r.InvoiceNo, r.Room, r.Stage, r.Item, r.Qty, r.Claimed, r.Decided, r.CheckStatusText, r.CheckedBy, r.Note }).ToList(),
        };
    }
}
