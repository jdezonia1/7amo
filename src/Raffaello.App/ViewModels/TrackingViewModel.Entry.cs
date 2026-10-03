using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Ledger;
using Raffaello.Core.Tracker;

namespace Raffaello.App.ViewModels;

/// <summary>TRACKING: RATES (rate per subcontractor x stage|item, set / clear / import) and ENTRY (smart statement entry).</summary>
public sealed partial class TrackingViewModel
{
    // ------------------------------------------------------------------ RATES
    public ObservableCollection<TrkRateRow> RateRows { get; } = new();
    [ObservableProperty] private TrkRateRow? _selectedRateRow;
    [ObservableProperty] private bool _ratesMissingOnly;
    [ObservableProperty] private string _rateSub = "";
    [ObservableProperty] private string _rateStage = "";
    [ObservableProperty] private string _rateItem = "";
    [ObservableProperty] private string _rateValue = "";
    [ObservableProperty] private string _rateStagePct = "";
    [ObservableProperty] private string _rateInvFrom = "";
    [ObservableProperty] private string _rateInvTo = "";
    [ObservableProperty] private string _rateNote = "";
    [ObservableProperty] private string _ratesInfo = "";
    [ObservableProperty] private string _mappingFiles = "";
    partial void OnRatesMissingOnlyChanged(bool value) { if (_built.Contains(TRates)) BuildRates(); }
    partial void OnSelectedRateRowChanged(TrkRateRow? value)
    {
        if (value is null) return;
        RateSub = value.Sub; RateStage = value.Stage; RateItem = value.Item;
        RateValue = value.Rate?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        RateStagePct = value.StagePct is > 0 and < 1 ? (value.StagePct * 100).ToString("0.##", CultureInfo.InvariantCulture) : "";
    }

    public const string MappingFolder = @"D:\RAFFLES MASTER FOLDER\RECON_TOOLS";

    private void BuildRates()
    {
        var rows = _ledgerAll.Where(l => !l.Line.Rework)
            .GroupBy(l => (l.Sub, l.Line.Building, l.Stage, l.Item, Rate: l.Rate?.ToString("0.####", CultureInfo.InvariantCulture), l.RateSource))
            .Select(g => new TrkRateRow(g.Key.Sub, g.Key.Building, g.Key.Stage, g.Key.Item, string.Join(",", g.Select(l => l.Invoice).Distinct().OrderBy(x => x)), g.Count(),
                g.Sum(l => l.Qty), g.Sum(l => l.QtyAfterWir), g.First().Rate, g.First().StagePct, g.First().RateStatus, g.Key.RateSource, g.First().RateReason, g.Sum(l => l.Amount), g.Sum(l => l.Payable)))
            .Where(r => !RatesMissingOnly || r.Rate is null)
            .OrderBy(r => r.Sub).ThenBy(r => TrkStatus.StageRank(r.Stage)).ThenBy(r => r.Item).ToList();
        Fill(RateRows, rows);
        RatesInfo = $"{rows.Count:N0} subcontractor x stage|item rates  |  MISSING {rows.Count(r => r.Rate is null):N0} (yellow)  |  CHECK {rows.Count(r => r.RateStatus == RateStatus.Check):N0}  |  AMOUNT SAR {rows.Sum(r => r.Amount):N2}";
        try
        {
            var found = Directory.Exists(MappingFolder) ? Directory.GetFiles(MappingFolder, "RATE_MAPPING_*.xlsx").Select(f => Path.GetFileName(f)).ToList() : new List<string>();
            MappingFiles = found.Count > 0 ? $"Found in RECON_TOOLS: {string.Join(", ", found)} - IMPORT MAPPING makes them the default rates of their building" : $"No RATE_MAPPING_*.xlsx in {MappingFolder} yet";
        }
        catch (Exception) { MappingFiles = ""; }
    }

    [RelayCommand]
    private async Task SetRate()
    {
        var rate = Num(RateValue);
        if (rate is null || rate <= 0 || string.IsNullOrWhiteSpace(RateStage) || string.IsNullOrWhiteSpace(RateItem)) { Ctx.Toasts.Show("STAGE, ITEM AND A RATE ARE NEEDED", kind: ToastKind.Warn); return; }
        var pct = Num(RateStagePct) is double p ? (p > 1 ? p / 100 : p) : (double?)null;
        int? from = int.TryParse(RateInvFrom, out var f) ? f : null;
        int? to = int.TryParse(RateInvTo, out var t) ? t : from;
        var who = string.IsNullOrWhiteSpace(RateSub) ? "EVERY SUBCONTRACTOR" : RateSub.Trim().ToUpperInvariant();
        if (!Ctx.Dialogs.Confirm("Set rate", $"{who}\n{RateStage} | {RateItem}{(from is null ? "" : $"  (invoices {from}-{to})")}\nRATE {rate:0.##}{(pct is null ? "" : $"  stage {pct:P0}")}\n\nThe change is kept in the audit log.")) return;
        var (sub, stage, item, note) = (RateSub, RateStage, RateItem, RateNote);
        await Ctx.Data.WriteAsync(pr => pr.Workflow.SetRate(sub, stage, item, rate.Value, pct, from, to, note), Ctx.Toasts, "RATE SAVED");
    }

    [RelayCommand]
    private async Task ClearRate()
    {
        if (string.IsNullOrWhiteSpace(RateStage) || string.IsNullOrWhiteSpace(RateItem)) return;
        int? from = int.TryParse(RateInvFrom, out var f) ? f : null;
        int? to = int.TryParse(RateInvTo, out var t) ? t : from;
        var (sub, stage, item) = (RateSub, RateStage, RateItem);
        var removed = false;
        await Ctx.Data.WriteAsync(pr => removed = pr.Workflow.ClearRate(sub, stage, item, from, to), Ctx.Toasts);
        Ctx.Toasts.Show(removed ? "MANUAL RATE REMOVED" : "NO MANUAL RATE FOR THIS KEY", removed ? "The contract rate applies again." : "", removed ? ToastKind.Good : ToastKind.Info);
    }

    [RelayCommand]
    private async Task ImportMapping()
    {
        var suggested = Path.Combine(MappingFolder, $"RATE_MAPPING_{WorkingBuilding}.xlsx");
        var file = File.Exists(suggested) && Ctx.Dialogs.Confirm("Import rate mapping", $"Use {suggested} (sheet MAPPING: stage | item -> contract rate, with your SR corrections)?\n\nNO = pick another file (RATE_MAPPING_* / REMAINING_VALUE_*).")
            ? suggested : Ctx.Dialogs.OpenFile("Rate MAPPING sheet (RATE_MAPPING_HOTEL / _BRANDED.xlsx or REMAINING_VALUE_*.xlsx, sheet MAPPING)");
        if (file is null) return;
        var building = RateMappingImporter.BuildingOf(file) ?? WorkingBuilding;
        List<RateMappingRow> rows; List<string> issues;
        try { (rows, issues) = await Task.Run(() => RateMappingImporter.Read(file)); }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ MAPPING", ex.Message, ToastKind.Error); return; }
        if (rows.Count == 0) { Ctx.Toasts.Show("NO RATES FOUND", string.Join("\n", issues.Take(5)), ToastKind.Warn, 10); return; }
        var conf = string.Join(", ", rows.GroupBy(r => r.Confidence.Length > 0 ? r.Confidence : "-").Select(g => $"{g.Key} {g.Count()}"));
        if (!Ctx.Dialogs.Confirm("Import rate mapping", $"{rows.Count} stage|item rates for {building} ({conf}).\n{issues.Count} rows skipped (they stay NO RATE).\n\nRows without a SUBCONTRACTOR become the {building} default for every subcontractor; a manual rate per subcontractor still wins.\n\nImport?")) return;
        var rules = RateMappingImporter.ToRules(rows, file, building);
        (int Added, int Replaced) res = default;
        await Ctx.Data.WriteAsync(pr => res = pr.Workflow.ImportRateRules(rules), Ctx.Toasts);
        Ctx.Toasts.Show("RATE MAPPING IMPORTED", $"{res.Added} added, {res.Replaced} replaced", ToastKind.Good, 6);
    }

    // ------------------------------------------------------------------ ENTRY (smart statement entry)
    public ObservableCollection<TrkLocation> LocationResults { get; } = new();
    public ObservableCollection<string> RecentLocations { get; } = new();
    public ObservableCollection<TrkEntryRow> EntryRows { get; } = new();
    public ObservableCollection<TrkStatementRow> StatementRows { get; } = new();
    public ObservableCollection<string> Drafts { get; } = new();
    public ObservableCollection<string> EntryStageOptions { get; } = new();
    public string[] PostOptionList => PostOptions.All;
    [ObservableProperty] private string _entrySub = "";
    [ObservableProperty] private string _entryInvoice = "1";
    [ObservableProperty] private string _entryStatementNo = "";
    [ObservableProperty] private DateTime? _entryDate = DateTime.Today;
    [ObservableProperty] private string _entrySitePct = "100";
    [ObservableProperty] private string _entryWirPct = "100";
    [ObservableProperty] private string _locationSearch = "";
    [ObservableProperty] private TrkLocation? _selectedLocation;
    [ObservableProperty] private string _entryLocation = "";
    [ObservableProperty] private string _entryLocationInfo = "Pick a location on the left (type part of its number, type or floor).";
    [ObservableProperty] private string _entryStageFilter = "ALL";
    [ObservableProperty] private string _addStage = "";
    [ObservableProperty] private string _addItem = "";
    [ObservableProperty] private string _statementSummary = "";
    [ObservableProperty] private string _selectedDraft = "";
    [ObservableProperty] private bool _isPosted;
    [ObservableProperty] private TrkStatementRow? _selectedStatementRow;
    private StatementDraft _draft = new();
    private List<TrkLocation> _locations = new();
    private List<PlannedLine> _plan = new();

    public static string DraftFolder => Path.Combine(Raffaello.Core.Settings.AppSettings.SettingsFolder, "statement_drafts");
    public string BuildingText => _draft.Header.Building;

    partial void OnLocationSearchChanged(string value) => SearchLocations();
    partial void OnSelectedLocationChanged(TrkLocation? value) { if (value != null) PickLocation(value.Code); }
    partial void OnEntryStageFilterChanged(string value) { if (value != null && EntryLocation.Length > 0 && _built.Contains(TEntry)) LoadEntryRows(); }
    partial void OnEntrySubChanged(string value) { _draft.Header.Subcontractor = (value ?? "").Trim().ToUpperInvariant(); Replan(); if (EntryLocation.Length > 0 && _built.Contains(TEntry)) LoadEntryRows(); }
    partial void OnEntryInvoiceChanged(string value) { _draft.Header.InvoiceNo = int.TryParse(value, out var n) ? n : 0; Replan(); }
    partial void OnEntryStatementNoChanged(string value) => _draft.Header.StatementNo = (value ?? "").Trim();
    partial void OnEntryDateChanged(DateTime? value) => _draft.Header.Date = value ?? DateTime.Today;
    partial void OnEntrySitePctChanged(string value) { _draft.Header.SitePct = (Num(value) ?? 100) / 100.0; Replan(); }
    partial void OnEntryWirPctChanged(string value) { _draft.Header.WirPct = (Num(value) ?? 100) / 100.0; Replan(); }

    /// <summary>Locations that have a total or claims (the tracker keys); plain room-list entries without data are left out.</summary>
    private IEnumerable<TrkLocation> AllLocations()
    {
        var codes = _bal.Values.Select(b => b.Room).Distinct(StringComparer.OrdinalIgnoreCase);
        return codes.Select(code =>
        {
            _rooms.TryGetValue(code, out var r);
            var floor = r?.Level is { Length: > 0 } lv ? lv : "";
            return new TrkLocation(code, $"{PartOf(code)}  {floor}  {r?.RoomType}".Trim(), floor, r?.Floor ?? 0);
        }).OrderBy(l => l.FloorRank).ThenBy(l => l.Code, StringComparer.OrdinalIgnoreCase);
    }

    private void BuildEntry()
    {
        _locations = AllLocations().ToList();
        Sync(EntryStageOptions, new[] { "ALL" }.Concat(StageOptions));
        if (BuildingFilter is not null) _draft.Header.Building = BuildingFilter;
        OnPropertyChanged(nameof(BuildingText));
        SearchLocations();
        RefreshDrafts();
        if (EntryLocation.Length > 0) LoadEntryRows();
        Replan();
    }

    private void SearchLocations()
    {
        var words = (LocationSearch ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Fill(LocationResults, _locations.Where(l => words.All(w => l.Code.Contains(w, StringComparison.OrdinalIgnoreCase) || l.Caption.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(250));
    }

    public void PickLocation(string code)
    {
        EntryLocation = code;
        // ALL buildings in the header: the statement takes the building of the location
        if (BuildingFilter is null && !IsPosted)
        {
            var b = _rooms.TryGetValue(code, out var room) && room.Building.Length > 0 ? room.Building
                : _caps.FirstOrDefault(q => q.Room.Equals(code, StringComparison.OrdinalIgnoreCase))?.Building ?? _draft.Header.Building;
            if (_draft.Lines.Count == 0 || _draft.Lines.All(l => l.Room.Equals(code, StringComparison.OrdinalIgnoreCase))) _draft.Header.Building = b;
            OnPropertyChanged(nameof(BuildingText));
        }
        RecentLocations.Remove(code);
        RecentLocations.Insert(0, code);
        while (RecentLocations.Count > 8) RecentLocations.RemoveAt(RecentLocations.Count - 1);
        if (_built.Contains(TEntry)) LoadEntryRows();
    }

    [RelayCommand] private void PickRecent(string? code) { if (code != null) PickLocation(code); }

    private void LoadEntryRows()
    {
        var code = EntryLocation;
        var bs = _bal.Values.Where(b => b.Room.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
        var lines = _claims.Where(c => c.Room.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
        var inDraft = _draft.Lines.Where(l => l.Room.Equals(code, StringComparison.OrdinalIgnoreCase)).GroupBy(l => (l.Stage, l.Item)).ToDictionary(g => g.Key, g => g.Sum(l => l.Claimed));
        var h = _draft.Header;
        Fill(EntryRows, bs.Where(b => EntryStageFilter is null or "ALL" || b.Stage == EntryStageFilter)
            .OrderBy(b => TrkStatus.StageRank(b.Stage)).ThenBy(b => b.Item)
            .Select(b =>
            {
                var keyLines = lines.Where(c => c.Stage == b.Stage && c.Item == b.Item).ToList();
                var lp = keyLines.Count(c => LengthCheck.IsPending(c));
                var hp = keyLines.Count(c => HeightCheck.IsPending(c));
                var by = string.Join("  ", keyLines.Where(c => !c.Rework).GroupBy(c => c.Subcontractor).Select(g => $"{g.Key} {g.Sum(c => c.Qty):0.##} (inv {string.Join(",", g.Select(c => c.InvoiceNo).Distinct().OrderBy(x => x))})"));
                var r = new TrkEntryRow
                {
                    Stage = b.Stage, Item = b.Item, Total = b.ProjectQty, Claimed = b.Claimed, Remaining = b.Remaining, ByWhom = by,
                    Pending = ((lp > 0 ? $"15 m x{lp} " : "") + (hp > 0 ? $">4.5 m x{hp}" : "")).Trim(),
                    NotCompared = StatementPlanner.IsNotComparedStage(b.Stage), Rate = h.Subcontractor.Length > 0 ? _rates.Resolve(h.Subcontractor, h.Building, h.InvoiceNo, b.Stage, b.Item).Rate : null,
                    RateTip = h.Subcontractor.Length > 0 ? _rates.Resolve(h.Subcontractor, h.Building, h.InvoiceNo, b.Stage, b.Item).Reason : "Pick the subcontractor to see his rate",
                };
                if (inDraft.TryGetValue((b.Stage, b.Item), out var q)) r.ClaimedNow = q.ToString();
                return r;
            }));
        _rooms.TryGetValue(code, out var room);
        var cap = bs.Where(b => b.HasCap).ToList();
        EntryLocationInfo = $"{code}  |  {room?.Level}  {room?.RoomType}  |  TOTAL {cap.Sum(b => b.ProjectQty):N0}  |  ALREADY CLAIMED {bs.Sum(b => b.Claimed):N1}  |  REMAINING {cap.Sum(b => Math.Max(0, b.Remaining)):N1}" +
                            (cap.Any(b => b.IsOver) ? $"  |  {cap.Count(b => b.IsOver)} KEYS OVER" : "");
    }

    [RelayCommand]
    private void AddKeyRow()
    {
        if (EntryLocation.Length == 0 || string.IsNullOrWhiteSpace(AddStage) || string.IsNullOrWhiteSpace(AddItem)) return;
        var st = AddStage.Trim().ToUpperInvariant(); var it = AddItem.Trim().ToUpperInvariant();
        if (EntryRows.Any(r => r.Stage == st && r.Item == it)) return;
        var b = Project.Workflow.Balance(EntryLocation, st, it);
        var h = _draft.Header;
        EntryRows.Add(new TrkEntryRow
        {
            Stage = st, Item = it, Total = b.ProjectQty, Claimed = b.Claimed, Remaining = b.Remaining, NotCompared = StatementPlanner.IsNotComparedStage(st),
            Rate = h.Subcontractor.Length > 0 ? _rates.Resolve(h.Subcontractor, h.Building, h.InvoiceNo, st, it).Rate : null,
        });
    }

    /// <summary>Adds the typed quantities of the location to the statement (replacing earlier lines of the same location x stage x item).</summary>
    [RelayCommand]
    private void AddToStatement()
    {
        if (IsPosted) { Ctx.Toasts.Show("STATEMENT ALREADY POSTED", "Start a NEW statement.", ToastKind.Warn); return; }
        if (EntryRows.Any(r => r.Invalid)) { Ctx.Toasts.Show("WHOLE NUMBERS ONLY", "Fix the red cells first.", ToastKind.Warn); return; }
        var typed = EntryRows.Where(r => r.Qty is > 0).ToList();
        if (typed.Count == 0) { Ctx.Toasts.Show("TYPE THE CLAIMED QUANTITIES FIRST", kind: ToastKind.Warn); return; }
        var floor = _rooms.TryGetValue(EntryLocation, out var room) ? room.Level : "";
        foreach (var r in typed)
        {
            var existing = _draft.Lines.FirstOrDefault(l => l.Room.Equals(EntryLocation, StringComparison.OrdinalIgnoreCase) && l.Stage == r.Stage && l.Item == r.Item);
            if (existing != null) existing.Claimed = r.Qty!.Value;
            else _draft.Lines.Add(new StatementDraftLine { Room = EntryLocation, Floor = floor, Stage = r.Stage, Item = r.Item, Claimed = r.Qty!.Value,
                Option = StatementPlanner.IsSecondFix(r.Stage) ? PostOptions.WithinRemaining : PostOptions.WithinRemaining });
        }
        RebuildStatementRows();
        Ctx.Toasts.Show($"{typed.Count} LINES ADDED", $"{EntryLocation}: claimed {typed.Sum(r => r.Qty):N0}", ToastKind.Good, 3);
    }

    private void RebuildStatementRows()
    {
        Fill(StatementRows, _draft.Lines.Select(l => new TrkStatementRow(l, Replan)));
        Replan();
    }

    private void Replan()
    {
        if (_draft.Lines.Count == 0 || StatementRows.Count == 0) { StatementSummary = "No lines yet - pick a location, type the claimed quantities, ADD TO STATEMENT."; return; }
        if (IsPosted) return;   // a posted statement keeps the result it was posted with
        _plan = Project.Workflow.PlanStatement(_draft);
        ApplyPlan(_plan);
    }

    private void ApplyPlan(List<PlannedLine> plan)
    {
        var cmp = StatementComparison.Build(_draft.Header, plan, _rates);
        for (var i = 0; i < plan.Count && i < StatementRows.Count; i++)
        {
            var p = plan[i];
            StatementRows[i].Apply(p, cmp.FirstOrDefault(c => c.Location == p.Line.Room && c.Stage == p.Line.Stage && c.Item == p.Line.Item));
        }
        var claimed = plan.Sum(p => p.Claimed);
        var cert = plan.Sum(p => p.Post);
        StatementSummary = $"{(IsPosted ? "POSTED" : "PREVIEW")}  |  {plan.Select(p => p.Line.Room).Distinct().Count()} LOCATIONS  |  CLAIMED {claimed:N0}  SAR {cmp.Sum(c => c.ClaimedAmount):N2}  |  " +
                           $"CERTIFIED {cert:N0}  SAR {cmp.Sum(c => c.CertifiedAmount):N2}  |  DIFFERENCE {claimed - cert:N0}  SAR {cmp.Sum(c => c.DifferenceAmount):N2}" +
                           (cmp.Any(c => c.Rate is null) ? $"  |  RATE MISSING ON {cmp.Count(c => c.Rate is null)}" : "");
    }

    [RelayCommand]
    private void RemoveStatementLine()
    {
        if (SelectedStatementRow is not { } r || IsPosted) return;
        _draft.Lines.Remove(r.Line);
        RebuildStatementRows();
    }

    [RelayCommand] private Task PostWithinRemaining() => Post(withinRemaining: true);
    [RelayCommand] private Task PostAsSet() => Post(withinRemaining: false);

    private async Task Post(bool withinRemaining)
    {
        if (IsPosted) { Ctx.Toasts.Show("ALREADY POSTED", "Start a NEW statement.", ToastKind.Warn); return; }
        if (_draft.Lines.Count == 0) return;
        if (_draft.Header.Subcontractor.Length == 0 || _draft.Header.InvoiceNo <= 0) { Ctx.Toasts.Show("SUBCONTRACTOR AND INVOICE No. NEEDED", kind: ToastKind.Warn); return; }
        if (withinRemaining)
            foreach (var r in StatementRows.Where(r => r.Option != PostOptions.Skip)) r.Option = PostOptions.WithinRemaining;
        if (_draft.Header.StatementNo.Length == 0) EntryStatementNo = $"ST-{DateTime.Now:yyyyMMdd-HHmm}";
        if (BuildingFilter is not null) _draft.Header.Building = BuildingFilter;
        var plan = Project.Workflow.PlanStatement(_draft);
        var msg = $"{_draft.Header.Subcontractor}  INV {_draft.Header.InvoiceNo}  STATEMENT {_draft.Header.StatementNo}  ({_draft.Header.Building})\n\n" +
                  $"Claimed {plan.Sum(p => p.Claimed):N0}  ->  posted {plan.Sum(p => p.Post):N0}\n" +
                  string.Join("\n", plan.GroupBy(p => p.Status).Select(g => $"  {g.Key}: {g.Count()} lines")) +
                  "\n\nEach line goes through the normal claim check (append-only ledger). Post?";
        if (!Ctx.Dialogs.Confirm("Post statement", msg)) return;
        (List<PlannedLine> Plan, int Posted, List<string> Problems) res = default;
        var draft = _draft;
        var ok = await Ctx.Data.WriteAsync(p => res = p.Workflow.PostStatement(draft), Ctx.Toasts);
        if (!ok || res.Plan is null) return;
        IsPosted = true;
        ApplyPlan(res.Plan);
        try { draft.Save(DraftFolder); } catch (Exception) { }
        RefreshDrafts();
        Ctx.Toasts.Show("STATEMENT POSTED", $"{res.Posted} ledger lines posted" + (res.Problems.Count > 0 ? $"; {res.Problems.Count} blocked:\n" + string.Join("\n", res.Problems.Take(3)) : "") + "\nEXPORT (Ctrl+E) gives the comparison for the subcontractor.",
            res.Problems.Count > 0 ? ToastKind.Warn : ToastKind.Good, 10);
    }

    [RelayCommand]
    private void NewStatement()
    {
        if (_draft.Lines.Count > 0 && !IsPosted && !Ctx.Dialogs.Confirm("New statement", "Start a new statement? Lines not saved are lost (SAVE DRAFT keeps them).")) return;
        _draft = new StatementDraft
        {
            Header = new StatementHeader
            {
                Subcontractor = EntrySub.Trim().ToUpperInvariant(), InvoiceNo = int.TryParse(EntryInvoice, out var n) ? n : 1, Building = WorkingBuilding, Date = DateTime.Today,
                SitePct = (Num(EntrySitePct) ?? 100) / 100.0, WirPct = (Num(EntryWirPct) ?? 100) / 100.0,
            },
        };
        IsPosted = false;
        EntryStatementNo = "";
        EntryDate = DateTime.Today;
        StatementRows.Clear();
        Replan();
        if (EntryLocation.Length > 0) LoadEntryRows();
    }

    [RelayCommand]
    private void SaveDraft()
    {
        if (_draft.Lines.Count == 0) { Ctx.Toasts.Show("NOTHING TO SAVE", kind: ToastKind.Info); return; }
        try
        {
            if (_draft.Header.StatementNo.Length == 0) EntryStatementNo = $"ST-{DateTime.Now:yyyyMMdd-HHmm}";
            var path = _draft.Save(DraftFolder);
            RefreshDrafts();
            SelectedDraft = Path.GetFileName(path);
            Ctx.Toasts.Show("DRAFT SAVED", path, ToastKind.Good, 4);
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT SAVE DRAFT", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void OpenDraft()
    {
        if (string.IsNullOrEmpty(SelectedDraft)) return;
        var d = StatementDraft.Load(Path.Combine(DraftFolder, SelectedDraft));
        if (d is null) { Ctx.Toasts.Show("CANNOT READ DRAFT", SelectedDraft, ToastKind.Error); return; }
        _draft = d;
        IsPosted = false;
        EntrySub = d.Header.Subcontractor; EntryInvoice = d.Header.InvoiceNo.ToString(); EntryStatementNo = d.Header.StatementNo; EntryDate = d.Header.Date;
        EntrySitePct = (d.Header.SitePct * 100).ToString("0.##", CultureInfo.InvariantCulture); EntryWirPct = (d.Header.WirPct * 100).ToString("0.##", CultureInfo.InvariantCulture);
        Fill(StatementRows, _draft.Lines.Select(l => new TrkStatementRow(l, Replan)));
        if (d.Posted)
        {
            // already posted: show it read-only; the remaining now already includes its own lines, so no live re-plan
            IsPosted = true;
            StatementSummary = "POSTED statement (read only). Start NEW for the next one.";
        }
        else Replan();
        if (d.Lines.FirstOrDefault() is { } first) PickLocation(first.Room);
    }

    private void RefreshDrafts()
    {
        try { Sync(Drafts, StatementDraft.List(DraftFolder).Select(f => Path.GetFileName(f))); } catch (Exception) { }
    }
}