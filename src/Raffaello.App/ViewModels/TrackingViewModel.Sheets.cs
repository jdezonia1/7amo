using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Queue;
using Raffaello.Core.Tracker;

namespace Raffaello.App.ViewModels;

/// <summary>TRACKING sheets: DASHBOARD, LEDGER, ROOMS, ROOM, PLANS, CONTROL, PROJECT QTY, SUMMARY, CAP, CABLES.</summary>
public sealed partial class TrackingViewModel
{
    // ------------------------------------------------------------------ DASHBOARD
    [ObservableProperty] private string _kpiProject = "";
    [ObservableProperty] private string _kpiClaimed = "";
    [ObservableProperty] private string _kpiRemaining = "";
    [ObservableProperty] private string _kpiPct = "";
    [ObservableProperty] private string _kpiLines = "";
    [ObservableProperty] private string _kpiAmount = "";
    public ObservableCollection<TrkStageRow> StageRows { get; } = new();
    public ObservableCollection<TrkStageRow> SystemRows { get; } = new();
    [ObservableProperty] private DataView? _subPerfView;
    [ObservableProperty] private DataView? _partView;

    private static TrkStageRow Agg(string name, IEnumerable<RoomBalance> bs)
    {
        var l = bs.ToList();
        var cap = l.Where(b => b.HasCap).ToList();
        var p = cap.Sum(b => b.ProjectQty);
        var c = l.Sum(b => b.Claimed);
        return new TrkStageRow(name, p, c, p - c, TrkStatus.Of(p, c, cap.Any(b => b.IsOver) && c > p), cap.Count(b => b.IsOver), l.Count(b => !b.HasCap && b.Claimed > 0),
            cap.Count(b => TrkStatus.Of(b) == TrkStatus.Complete), cap.Count(b => TrkStatus.Of(b) == TrkStatus.InProgress), cap.Count(b => TrkStatus.Of(b) == TrkStatus.NotStarted));
    }

    private void BuildDashboard()
    {
        var capped = _bal.Values.Where(b => b.HasCap).ToList();
        var project = capped.Sum(b => b.ProjectQty);
        var claimed = _bal.Values.Sum(b => b.Claimed);   // recon definition: every compared, non-rework claim
        KpiProject = project.ToString("#,0");
        KpiClaimed = claimed.ToString("#,0.#");
        KpiRemaining = (project - claimed).ToString("#,0.#");
        KpiPct = project <= 0 ? "-" : (claimed / project).ToString("P1");
        KpiLines = $"{capped.Count(b => TrkStatus.Of(b) == TrkStatus.Complete):N0} complete  |  {capped.Count(b => TrkStatus.Of(b) == TrkStatus.NotStarted):N0} not started  |  {capped.Count(b => b.IsOver):N0} over cap";
        KpiAmount = $"SAR {_ledgerAll.Sum(l => l.Amount):N0} (after WIR %)  |  {_ledgerAll.Count(l => l.RateInfo.IsMissing):N0} lines MISSING rate";

        var stages = _bal.Values.Select(b => b.Stage).Distinct().OrderBy(TrkStatus.StageRank).ToList();
        Fill(StageRows, stages.Select(st => Agg(st, _bal.Values.Where(b => b.Stage == st))).Append(Agg("ALL STAGES", _bal.Values)));
        Fill(SystemRows, _bal.Values.Select(b => b.Item).Distinct().OrderBy(x => x).Select(it => Agg(it, _bal.Values.Where(b => b.Item == it))));

        var t = new DataTable();
        Col(t, "SUBCONTRACTOR");
        foreach (var st in stages) Col(t, st, typeof(double));
        Col(t, "LINES", typeof(int)); Col(t, "AMOUNT (AFTER WIR)", typeof(double)); Col(t, "MISSING RATE", typeof(int));
        foreach (var g in _ledgerAll.Where(l => !l.Line.Rework).GroupBy(l => l.Sub).OrderBy(g => g.Key))
        {
            var row = new List<object> { g.Key };
            row.AddRange(stages.Select(st => (object)Math.Round(g.Where(l => l.Stage == st).Sum(l => l.Qty), 2)));
            row.Add(g.Count()); row.Add(Math.Round(g.Sum(l => l.Amount), 2)); row.Add(g.Count(l => l.RateInfo.IsMissing));
            t.Rows.Add(row.ToArray());
        }
        SubPerfView = t.DefaultView;

        var parts = _bal.Values.Select(b => PartOf(b.Room)).Distinct().OrderBy(x => x).ToList();
        var pt = new DataTable();
        Col(pt, "STAGE");
        foreach (var part in parts) { Col(pt, part + " QTY", typeof(double)); Col(pt, part + " %"); }
        foreach (var st in stages)
        {
            var row = new List<object> { st };
            foreach (var part in parts)
            {
                var bs = _bal.Values.Where(b => b.Stage == st && PartOf(b.Room) == part).ToList();
                var p = bs.Where(b => b.HasCap).Sum(b => b.ProjectQty);
                var c = bs.Sum(b => b.Claimed);
                row.Add(Math.Round(c, 2)); row.Add(p <= 0 ? "-" : (c / p).ToString("P0"));
            }
            pt.Rows.Add(row.ToArray());
        }
        PartView = pt.DefaultView;
    }

    // ------------------------------------------------------------------ LEDGER
    public ObservableCollection<TrkLedgerRow> LedgerRows { get; } = new();
    public ObservableCollection<TrkTotalRow> LedgerTotals { get; } = new();
    public ObservableCollection<string> LedgerSubOptions { get; } = new();
    public ObservableCollection<string> LedgerInvoiceOptions { get; } = new();
    public string[] GroupByOptions { get; } = { "SUBCONTRACTOR", "INVOICE", "LOCATION", "STAGE" };
    [ObservableProperty] private string _ledgerSub = "ALL";
    [ObservableProperty] private string _ledgerInvoice = "ALL";
    [ObservableProperty] private string _ledgerSearch = "";
    [ObservableProperty] private string _ledgerGroupBy = "SUBCONTRACTOR";
    [ObservableProperty] private bool _ledgerMissingOnly;
    [ObservableProperty] private string _ledgerSummary = "";
    [ObservableProperty] private TrkLedgerRow? _selectedLedgerRow;

    partial void OnLedgerSubChanged(string value) { if (value != null) FilterLedger(); }
    partial void OnLedgerInvoiceChanged(string value) { if (value != null) FilterLedger(); }
    partial void OnLedgerSearchChanged(string value) => FilterLedger();
    partial void OnLedgerGroupByChanged(string value) { if (value != null) FilterLedger(); }
    partial void OnLedgerMissingOnlyChanged(bool value) => FilterLedger();
    partial void OnSelectedLedgerRowChanged(TrkLedgerRow? value) { if (value != null) PublishSelection(Raffaello.Core.Wiring.SelectedRecords.LedgerLine(value.Line)); }

    private void BuildLedger()
    {
        Sync(LedgerSubOptions, new[] { "ALL" }.Concat(Subcontractors));
        Sync(LedgerInvoiceOptions, new[] { "ALL" }.Concat(_claims.Select(c => c.InvoiceNo).Distinct().OrderBy(n => n).Select(n => n.ToString())));
        if (!LedgerSubOptions.Contains(LedgerSub)) LedgerSub = "ALL";
        if (!LedgerInvoiceOptions.Contains(LedgerInvoice)) LedgerInvoice = "ALL";
        FilterLedger();
    }

    private void FilterLedger()
    {
        if (!_built.Contains(TLedger)) return;
        var q = _ledgerAll.AsEnumerable();
        if (LedgerSub is { Length: > 0 } s && s != "ALL") q = q.Where(l => l.Sub == s);
        if (LedgerInvoice is { Length: > 0 } i && i != "ALL" && int.TryParse(i, out var n)) q = q.Where(l => l.Invoice == n);
        if (LedgerMissingOnly) q = q.Where(l => l.RateInfo.IsMissing);
        if (!string.IsNullOrWhiteSpace(LedgerSearch))
        {
            var words = LedgerSearch.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(l => words.All(w => l.Location.Contains(w, StringComparison.OrdinalIgnoreCase) || l.Stage.Contains(w, StringComparison.OrdinalIgnoreCase)
                                            || l.Item.Contains(w, StringComparison.OrdinalIgnoreCase) || l.Floor.Contains(w, StringComparison.OrdinalIgnoreCase) || l.Notes.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        var rows = q.OrderBy(l => l.Sub).ThenBy(l => l.Invoice).ThenBy(l => TrkStatus.StageRank(l.Stage)).ThenBy(l => l.Location).ThenBy(l => l.Item).ToList();
        Fill(LedgerRows, rows);
        Fill(LedgerTotals, Totals(rows, LedgerGroupBy));
        LedgerSummary = $"{rows.Count:N0} LINES  |  QTY {rows.Sum(r => r.Qty):N1}  |  AFTER WIR {rows.Sum(r => r.QtyAfterWir):N1}  |  AMOUNT SAR {rows.Sum(r => r.Amount):N2}  |  PAYABLE SAR {rows.Sum(r => r.Payable):N2}  |  MISSING RATE {rows.Count(r => r.RateInfo.IsMissing):N0}";
    }

    private static List<TrkTotalRow> Totals(IEnumerable<TrkLedgerRow> rows, string by)
    {
        Func<TrkLedgerRow, string> key = by switch
        {
            "INVOICE" => l => $"{l.Sub}  INV {l.Invoice}",
            "LOCATION" => l => l.Location,
            "STAGE" => l => l.Stage,
            _ => l => l.Sub,
        };
        var list = rows.ToList();
        return list.GroupBy(key).OrderBy(g => g.Key)
            .Select(g => new TrkTotalRow(g.Key, g.Count(), g.Sum(l => l.Qty), g.Sum(l => l.QtyAfterWir), g.Sum(l => l.Amounts.Amount), g.Sum(l => l.Amount), g.Sum(l => l.Payable), g.Count(l => l.RateInfo.IsMissing)))
            .Append(new TrkTotalRow("TOTAL", list.Count, list.Sum(l => l.Qty), list.Sum(l => l.QtyAfterWir), list.Sum(l => l.Amounts.Amount), list.Sum(l => l.Amount), list.Sum(l => l.Payable), list.Count(l => l.RateInfo.IsMissing)))
            .ToList();
    }

    [RelayCommand]
    private void RateForLine()
    {
        if (SelectedLedgerRow is not { } l) return;
        RateSub = l.Sub; RateStage = l.Stage; RateItem = l.Item; RateValue = l.Rate?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        RateStagePct = l.StagePct is > 0 and < 1 ? (l.StagePct * 100).ToString("0.##", CultureInfo.InvariantCulture) : "";
        Tab = TRates;
    }

    // ------------------------------------------------------------------ ROOMS
    public ObservableCollection<TrkRoomRow> RoomRows { get; } = new();
    private List<TrkRoomRow> _roomsAll = new();
    [ObservableProperty] private string _roomsSearch = "";
    [ObservableProperty] private string _roomsState = "ALL";
    [ObservableProperty] private TrkRoomRow? _selectedRoomRow;
    public string[] StateOptions { get; } = { "ALL", TrkStatus.OverCap, TrkStatus.InProgress, TrkStatus.NotStarted, TrkStatus.Complete, TrkStatus.NoCap };
    partial void OnRoomsSearchChanged(string value) => FilterRooms();
    partial void OnRoomsStateChanged(string value) { if (value != null) FilterRooms(); }

    private void BuildRooms()
    {
        var amountByRoom = _ledgerAll.GroupBy(l => l.Location, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount), StringComparer.OrdinalIgnoreCase);
        var subsByRoom = _claims.GroupBy(c => c.Room, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => string.Join("  ", g.Select(c => c.Subcontractor).Distinct().OrderBy(x => x)), StringComparer.OrdinalIgnoreCase);
        var floorByRoom = _claims.GroupBy(c => c.Room, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Floor, StringComparer.OrdinalIgnoreCase);
        _roomsAll = _bal.Values.GroupBy(b => b.Room, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            _rooms.TryGetValue(g.Key, out var r);
            var cap = g.Where(b => b.HasCap).ToList();
            var p = cap.Sum(b => b.ProjectQty);
            var c = g.Sum(b => b.Claimed);
            var overKeys = cap.Count(b => b.IsOver);
            return new TrkRoomRow
            {
                Location = g.Key, Part = PartOf(g.Key), Floor = r?.Floor.ToString() ?? "", Level = r?.Level ?? floorByRoom.GetValueOrDefault(g.Key) ?? "", Unit = r?.Unit ?? "",
                UnitType = r?.RoomType ?? "NOT IN ROOMS", Plan = r?.Plan ?? "", ProjectQty = p, AllSubs = c, Remaining = p - c,
                State = overKeys > 0 ? TrkStatus.OverCap : TrkStatus.Of(p, c, false), Subs = subsByRoom.GetValueOrDefault(g.Key) ?? "",
                StagesActive = string.Join(" ", g.Where(b => b.Claimed > 0).Select(b => b.Stage).Distinct().OrderBy(TrkStatus.StageRank)),
                OverCap = overKeys, NoCap = g.Count(b => !b.HasCap && b.Claimed > 0), Amount = amountByRoom.GetValueOrDefault(g.Key),
            };
        }).OrderBy(r => r.Part).ThenBy(r => r.Level).ThenBy(r => r.Location, StringComparer.OrdinalIgnoreCase).ToList();
        FilterRooms();
    }

    private void FilterRooms()
    {
        if (!_built.Contains(TRooms)) return;
        var q = _roomsAll.AsEnumerable();
        if (RoomsState is { Length: > 0 } st && st != "ALL") q = q.Where(r => r.State == st);
        var txt = (RoomsSearch ?? "").Trim();
        if (txt.Length > 0) q = q.Where(r => r.Location.Contains(txt, StringComparison.OrdinalIgnoreCase) || r.UnitType.Contains(txt, StringComparison.OrdinalIgnoreCase) || r.Level.Contains(txt, StringComparison.OrdinalIgnoreCase));
        Fill(RoomRows, q);
    }

    [RelayCommand]
    private void OpenRoom(object? row)
    {
        var code = row switch { TrkRoomRow r => r.Location, TrkLedgerRow l => l.Location, TrkControlRow c => c.Location, TrkPlanRow p => p.FirstRoom, string s => s, _ => SelectedRoomRow?.Location };
        if (string.IsNullOrEmpty(code)) return;
        RoomLocation = code;
        Tab = TRoom;
    }

    // ------------------------------------------------------------------ ROOM
    [ObservableProperty] private string _roomLocation = "";
    [ObservableProperty] private string _roomHeader = "";
    [ObservableProperty] private string _roomState = "";
    public ObservableCollection<TrkStageRow> RoomStages { get; } = new();
    public ObservableCollection<TrkKeyRow> RoomKeys { get; } = new();
    [ObservableProperty] private DataView? _roomSubsView;
    partial void OnRoomLocationChanged(string value) { if (_built.Contains(TRoom) && IsRoom) BuildRoom(); else _built.Remove(TRoom); }

    private void BuildRoom()
    {
        _built.Add(TRoom);
        if (string.IsNullOrWhiteSpace(RoomLocation)) { var first = _bal.Values.Select(b => b.Room).OrderBy(x => x).FirstOrDefault(); if (first != null) { RoomLocation = first; return; } }
        var code = (RoomLocation ?? "").Trim();
        var bs = _bal.Values.Where(b => b.Room.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
        _rooms.TryGetValue(code, out var r);
        var cap = bs.Where(b => b.HasCap).ToList();
        var p = cap.Sum(b => b.ProjectQty);
        var c = bs.Sum(b => b.Claimed);
        RoomState = cap.Any(b => b.IsOver) ? TrkStatus.OverCap : TrkStatus.Of(p, c, false);
        RoomHeader = r is null ? $"{code}  |  not in the room list" :
            $"{PartOf(code)}  |  FLOOR {r.Floor}  |  {r.Level}  |  UNIT {r.Unit}  |  {r.RoomType}  |  PLAN {r.Plan}  |  OVER-CAP LINES {cap.Count(b => b.IsOver)}  |  NO-CAP LINES {bs.Count(b => !b.HasCap && b.Claimed > 0)}";
        var stages = TrkStatus.StageOrder.Concat(bs.Select(b => b.Stage)).Distinct().ToList();
        Fill(RoomStages, stages.Select(st =>
        {
            var l = bs.Where(b => b.Stage == st).ToList();
            var lp = l.Where(b => b.HasCap).Sum(b => b.ProjectQty);
            var lc = l.Sum(b => b.Claimed);
            return new TrkStageRow(st, lp, lc, lp - lc, l.Any(b => b.HasCap && b.IsOver) ? TrkStatus.OverCap : TrkStatus.Of(lp, lc, false), l.Count(b => b.HasCap && b.IsOver), l.Count(b => !b.HasCap && b.Claimed > 0));
        }));
        var lines = _ledgerAll.Where(l => l.Location.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
        var n = 0;
        Fill(RoomKeys, bs.OrderBy(b => TrkStatus.StageRank(b.Stage)).ThenBy(b => b.Item).Select(b => new TrkKeyRow(++n, b.Stage, b.Item, b.ProjectQty, b.Claimed, b.Remaining, TrkStatus.Of(b),
            b.BySubcontractor.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "",
            string.Join("  ", b.BySubcontractor.Select(kv => $"{kv.Key} {kv.Value:0.##}")),
            lines.Where(l => l.Stage == b.Stage && l.Item == b.Item).Sum(l => l.Amount))));
        var t = new DataTable();
        Col(t, "SUBCONTRACTOR");
        var used = stages.Where(st => bs.Any(b => b.Stage == st)).ToList();
        foreach (var st in used) Col(t, st, typeof(double));
        Col(t, "AMOUNT", typeof(double)); Col(t, "INVOICES");
        foreach (var g in lines.Where(l => !l.Line.Rework).GroupBy(l => l.Sub).OrderBy(g => g.Key))
        {
            var row = new List<object> { g.Key };
            row.AddRange(used.Select(st => (object)Math.Round(g.Where(l => l.Stage == st).Sum(l => l.Qty), 2)));
            row.Add(Math.Round(g.Sum(l => l.Amount), 2));
            row.Add($"{g.Count()} lines  (inv {string.Join(", ", g.Select(l => l.Invoice).Distinct().OrderBy(x => x))})");
            t.Rows.Add(row.ToArray());
        }
        RoomSubsView = t.DefaultView;
    }

    [RelayCommand] private void RoomOnPlan() { if (RoomLocation.Length > 0) Ctx.Nav.Go("Ledger", new NavTarget("Ledger", Key: RoomLocation.Trim())); }
    [RelayCommand] private void RoomEnter() { if (RoomLocation.Length > 0) { var code = RoomLocation.Trim(); Tab = TEntry; PickLocation(code); } }

    // ------------------------------------------------------------------ PLANS
    public ObservableCollection<TrkPlanRow> PlanRows { get; } = new();
    [ObservableProperty] private TrkPlanRow? _selectedPlanRow;

    private void BuildPlans()
    {
        var byRoom = _bal.Values.GroupBy(b => b.Room, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        string PlanOf(string room) => _rooms.TryGetValue(room, out var r) ? (r.Plan.Length > 0 ? r.Plan : r.Level.Length > 0 ? r.Level : "(no plan)") : "(not in rooms)";
        Fill(PlanRows, byRoom.Keys.GroupBy(PlanOf).Select(g =>
        {
            var rooms = g.ToList();
            var bs = rooms.SelectMany(x => byRoom[x]).ToList();
            var level = rooms.Select(x => _rooms.TryGetValue(x, out var r) ? r.Level : "").FirstOrDefault(x => x.Length > 0) ?? "";
            return new TrkPlanRow(g.Key, level, rooms.Count, bs.Where(b => b.HasCap).Sum(b => b.ProjectQty), bs.Sum(b => b.Claimed), bs.Where(b => b.HasCap).Sum(b => b.Remaining),
                rooms.Count(x => byRoom[x].Any(b => b.HasCap && b.IsOver)), rooms.Count(x => byRoom[x].All(b => b.Claimed <= LedgerRules.Eps)), rooms.OrderBy(x => x).First());
        }).OrderBy(r => r.Plan));
    }

    [RelayCommand] private void OpenPlan(TrkPlanRow? row) { if ((row ?? SelectedPlanRow) is { } r) Ctx.Nav.Go("Ledger", new NavTarget("Ledger", Key: r.FirstRoom)); }
    [RelayCommand] private void OpenPlanView() => Ctx.Nav.Go("Plan");

    // ------------------------------------------------------------------ CONTROL
    public ObservableCollection<TrkCheckRow> ControlChecks { get; } = new();
    public ObservableCollection<TrkControlRow> ControlRows { get; } = new();
    [ObservableProperty] private TrkCheckRow? _selectedCheck;
    partial void OnSelectedCheckChanged(TrkCheckRow? value) { if (value != null) Fill(ControlRows, ControlLines(value.Code == "ROOMS" ? "OVER" : value.Code)); }

    private IEnumerable<TrkControlRow> ControlLines(string code)
    {
        TrkControlRow FromBal(RoomBalance b, string note) => new(b.Room, b.Stage, b.Item, b.ProjectQty, b.Claimed, b.Remaining, string.Join(" ", b.BySubcontractor.Keys), "", note, TrkStatus.Stripe(TrkStatus.Of(b)));
        TrkControlRow FromLine(TrkLedgerRow l, string note, string status) => new(l.Location, l.Stage, l.Item, l.ProjectQty, l.Qty, l.Remaining, l.Sub, l.Invoice.ToString(), note, status);
        var known = _rooms.Keys.Concat(_caps.Select(q => q.Room)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return code switch
        {
            "OVER" => _bal.Values.Where(b => b.HasCap && b.IsOver).OrderBy(b => b.Room).ThenBy(b => TrkStatus.StageRank(b.Stage)).Select(b => FromBal(b, $"over by {b.Claimed - b.ProjectQty:0.##}")),
            "NOCAP" => _bal.Values.Where(b => !b.HasCap && b.Claimed > LedgerRules.Eps).OrderBy(b => b.Room).Select(b => FromBal(b, "claims against a blank / zero PROJECT QTY")),
            "UNKNOWN" => _ledgerAll.Where(l => !known.Contains(l.Location)).Select(l => FromLine(l, "location not in ROOMS / PROJECT QTY", "OVER")),
            "LENGTH" => _ledgerAll.Where(l => LengthCheck.IsPending(l.Line)).Select(l => FromLine(l, $"15 m check: claimed {l.Line.LengthClaimedQty:0.##} vs plan {l.Qty:0.##}", "DUE")),
            "HEIGHT" => _ledgerAll.Where(l => HeightCheck.IsPending(l.Line)).Select(l => FromLine(l, $">4.5 m check: {l.Line.QtyAbove45:0.##}", "DUE")),
            "NOTCOMPARED" => _ledgerAll.Where(l => LedgerRules.IsNotCompared(l.Line)).Select(l => FromLine(l, "NOT COMPARED (cable pulling = site statement, cable tray = final Revit model)", "OPEN")),
            "RATE" => _ledgerAll.Where(l => l.RateInfo.IsMissing).Select(l => FromLine(l, l.RateInfo.Reason, "CHECK")),
            "NOSUB" => _ledgerAll.Where(l => l.Sub.Length == 0).Select(l => FromLine(l, "no subcontractor", "CHECK")),
            "NOSITE" => _ledgerAll.Where(l => l.SitePct <= 0).Select(l => FromLine(l, "SITE % is 0 / empty", "CHECK")),
            "STAGE" => _ledgerAll.Where(l => TrkStatus.StageRank(l.Stage) == 99).Select(l => FromLine(l, "stage not in the tracker list", "CHECK")),
            "INVDIFF" => InvoiceStatusRows().Where(r => r.Status == DrawingStatus.Differs).Select(r => new TrkControlRow("", "", "", r.Invoiced ?? 0, r.DrawnQty, r.Diff ?? 0, r.Subcontractor, r.InvoiceNo.ToString(), r.Note, "CHECK")),
            _ => Array.Empty<TrkControlRow>(),
        };
    }

    private void BuildControl()
    {
        var keep = SelectedCheck?.Code;
        var roomsOver = _bal.Values.Where(b => b.HasCap && b.IsOver).Select(b => b.Room).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var checks = new List<TrkCheckRow>
        {
            new("OVER", "OVER CAP  (subcontractors qty above the PROJECT QTY)", ControlLines("OVER").Count()),
            new("NOCAP", "CLAIMS AGAINST A BLANK / ZERO PROJECT QTY", ControlLines("NOCAP").Count()),
            new("UNKNOWN", "LEDGER LINES ON AN UNKNOWN LOCATION", ControlLines("UNKNOWN").Count()),
            new("LENGTH", "15 m ROUTE-LENGTH CHECKS PENDING", ControlLines("LENGTH").Count()),
            new("HEIGHT", "> 4.5 m HEIGHT CHECKS PENDING", ControlLines("HEIGHT").Count()),
            new("NOTCOMPARED", "NOT COMPARED  (cable pulling / cable tray)", ControlLines("NOTCOMPARED").Count()),
            new("RATE", "RATE MISSING  (no contract rate / no manual rate)", ControlLines("RATE").Count()),
            new("STAGE", "LEDGER LINES ON AN OFF-LIST STAGE", ControlLines("STAGE").Count()),
            new("NOSUB", "LEDGER LINES WITH NO SUBCONTRACTOR", ControlLines("NOSUB").Count()),
            new("NOSITE", "LEDGER LINES WITH NO SITE %", ControlLines("NOSITE").Count()),
            new("ROOMS", "ROOMS CURRENTLY OVER CAP", roomsOver),
            new("INVDIFF", "INVOICES THAT DIFFER FROM THE DRAWINGS (> 2 points and 2 %)", ControlLines("INVDIFF").Count()),
        };
        Fill(ControlChecks, checks);
        SelectedCheck = null;
        SelectedCheck = ControlChecks.FirstOrDefault(c => c.Code == keep) ?? ControlChecks.First();
    }

    // ------------------------------------------------------------------ PROJECT QTY
    [ObservableProperty] private DataView? _projectQtyView;
    [ObservableProperty] private string _projectQtySearch = "";
    [ObservableProperty] private string _projectQtyInfo = "";
    partial void OnProjectQtySearchChanged(string value) => ApplyFilter(ProjectQtyView, "c4", value);

    private void BuildProjectQty()
    {
        var keys = _caps.Where(q => q.Qty != 0).Select(q => (q.Stage, q.Item)).Distinct().OrderBy(k => TrkStatus.StageRank(k.Stage)).ThenBy(k => k.Item).ToList();
        var t = new DataTable();
        foreach (var h in new[] { "PART", "FLOOR", "LEVEL", "UNIT / AREA No.", "LOCATION", "UNIT TYPE", "QTY SOURCE" }) Col(t, h);
        Col(t, "TOTAL No.", typeof(double));
        var idx = new Dictionary<(string, string), int>();
        foreach (var k in keys) { idx[k] = t.Columns.Count; Col(t, $"{k.Stage}|{k.Item}", typeof(double)); }
        foreach (var g in _caps.GroupBy(q => q.Room, StringComparer.OrdinalIgnoreCase).OrderBy(g => PartOf(g.Key)).ThenBy(g => _rooms.TryGetValue(g.Key, out var r) ? r.Floor : 0).ThenBy(g => g.Key))
        {
            _rooms.TryGetValue(g.Key, out var r);
            var row = t.NewRow();
            row[0] = PartOf(g.Key); row[1] = r?.Floor.ToString() ?? ""; row[2] = r?.Level ?? ""; row[3] = r?.Unit ?? ""; row[4] = g.Key; row[5] = r?.RoomType ?? "";
            row[6] = string.Join(", ", g.Select(q => q.Source).Where(x => x.Length > 0).Distinct());
            row[7] = g.Sum(q => q.Qty);
            foreach (var q in g.GroupBy(q => (q.Stage, q.Item))) if (idx.TryGetValue(q.Key, out var ci)) row[ci] = q.Sum(x => x.Qty);
            t.Rows.Add(row);
        }
        ProjectQtyView = t.DefaultView;
        ProjectQtyInfo = $"{t.Rows.Count:N0} locations x {keys.Count} stage|item keys  |  TOTAL {_caps.Sum(q => q.Qty):N0}";
        ApplyFilter(ProjectQtyView, "c4", ProjectQtySearch);
    }

    private static string Like(string? text) => (text ?? "").Trim().Replace("'", "''").Replace("[", "").Replace("]", "").Replace("*", "").Replace("%", "");

    private static void ApplyFilter(DataView? view, string column, string text)
    {
        if (view is null) return;
        var t = Like(text);
        view.RowFilter = t.Length == 0 ? "" : $"{column} LIKE '%{t}%'";
    }

    // ------------------------------------------------------------------ SUMMARY
    [ObservableProperty] private DataView? _summaryView;
    [ObservableProperty] private string _summarySearch = "";
    [ObservableProperty] private string _summaryState = "ALL";
    [ObservableProperty] private string _summarySub = "ALL";
    [ObservableProperty] private string _summaryInfo = "";
    public ObservableCollection<string> SummarySubOptions { get; } = new();
    private Dictionary<string, string> _summarySubCols = new();
    private string _summaryStatusCol = "";
    partial void OnSummarySearchChanged(string value) => FilterSummary();
    partial void OnSummaryStateChanged(string value) { if (value != null) FilterSummary(); }
    partial void OnSummarySubChanged(string value) { if (value != null) FilterSummary(); }

    private void BuildSummary()
    {
        var subs = Subcontractors.ToList();
        Sync(SummarySubOptions, new[] { "ALL" }.Concat(subs));
        var t = new DataTable();
        Col(t, "KEY"); Col(t, "LOCATION"); Col(t, "STAGE"); Col(t, "ITEM"); Col(t, "PROJECT QTY", typeof(double));
        _summarySubCols = new();
        foreach (var s in subs) _summarySubCols[s] = Col(t, s, typeof(double)).ColumnName;
        var cAll = Col(t, "ALL SUBCONTRACTORS", typeof(double)).ColumnName;
        var cRem = Col(t, "REMAINING", typeof(double)).ColumnName;
        var cPct = Col(t, "% USED").ColumnName;
        _summaryStatusCol = Col(t, "STATUS").ColumnName;
        var cAmt = Col(t, "AMOUNT (AFTER WIR)", typeof(double)).ColumnName;
        var amounts = _ledgerAll.GroupBy(l => l.Line.Key).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        t.BeginLoadData();
        foreach (var b in _bal.Values.OrderBy(b => b.Room, StringComparer.OrdinalIgnoreCase).ThenBy(b => TrkStatus.StageRank(b.Stage)).ThenBy(b => b.Item))
        {
            var row = t.NewRow();
            var key = LedgerKeys.Key(b.Room, b.Stage, b.Item);
            row[0] = key; row[1] = b.Room; row[2] = b.Stage; row[3] = b.Item; row[4] = b.ProjectQty;
            foreach (var (s, col) in _summarySubCols) row[col] = b.BySubcontractor.GetValueOrDefault(s);
            row[cAll] = b.Claimed; row[cRem] = b.Remaining; row[cPct] = b.HasCap ? b.UsedPct.ToString("P0") : "-"; row[_summaryStatusCol] = TrkStatus.Of(b);
            row[cAmt] = Math.Round(amounts.GetValueOrDefault(key), 2);
            t.Rows.Add(row);
        }
        t.EndLoadData();
        SummaryView = t.DefaultView;
        FilterSummary();
    }

    private void FilterSummary()
    {
        if (SummaryView?.Table is not { } table) return;
        var parts = new List<string>();
        var txt = Like(SummarySearch);
        if (txt.Length > 0) parts.Add($"c0 LIKE '%{txt}%'");
        if (SummaryState is { Length: > 0 } st && st != "ALL") parts.Add($"{_summaryStatusCol} = '{st}'");
        if (SummarySub is { Length: > 0 } sb && sb != "ALL" && _summarySubCols.TryGetValue(sb, out var col)) parts.Add($"{col} <> 0");
        SummaryView.RowFilter = string.Join(" AND ", parts);
        SummaryInfo = $"{SummaryView.Count:N0} of {table.Rows.Count:N0} location | stage | item lines";
    }

    // ------------------------------------------------------------------ CAP + CABLES
    public ObservableCollection<TrkCapRow> CapRows { get; } = new();
    private List<TrkCapRow> _capAll = new();
    [ObservableProperty] private string _capSearch = "";
    partial void OnCapSearchChanged(string value) => FilterCap();

    private void BuildCap()
    {
        _capAll = _caps.GroupBy(q => q.Key).Select(g => new TrkCapRow(g.Key, g.First().Room, g.First().Stage, g.First().Item, g.Sum(q => q.Qty), string.Join(", ", g.Select(q => q.Source).Distinct())))
            .OrderBy(r => r.Location, StringComparer.OrdinalIgnoreCase).ThenBy(r => TrkStatus.StageRank(r.Stage)).ThenBy(r => r.Item).ToList();
        FilterCap();
    }

    private void FilterCap()
    {
        if (!_built.Contains(TCap)) return;
        var t = (CapSearch ?? "").Trim();
        Fill(CapRows, t.Length == 0 ? _capAll : _capAll.Where(r => r.Key.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    public ObservableCollection<TrkLedgerRow> CableRows { get; } = new();
    [ObservableProperty] private string _cablesInfo = "";

    private void BuildCables()
    {
        var rows = _ledgerAll.Where(l => LedgerRules.IsNotCompared(l.Line) || StatementPlanner.IsNotComparedStage(l.Stage)).OrderBy(l => l.Stage).ThenBy(l => l.Sub).ThenBy(l => l.Invoice).ThenBy(l => l.Location).ToList();
        Fill(CableRows, rows);
        CablesInfo = $"{rows.Count:N0} lines  |  CABLE PULLING {rows.Where(r => r.Stage == "CABLE PULLING").Sum(r => r.Qty):N1} m  |  CABLE TRAY {rows.Where(r => r.Stage == "CABLE TRAY").Sum(r => r.Qty):N1} m  |  final qty {rows.Sum(r => r.QtyAfterWir):N1}  |  AMOUNT SAR {rows.Sum(r => r.Amount):N2}  |  not compared with a total";
    }

    [RelayCommand] private void OpenCablesPage() => Ctx.Nav.Go("Cables");
}