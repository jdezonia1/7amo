using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Raffaello.App.Services;
using Raffaello.Core.Cables;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using Raffaello.Core.Export;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

// =====================================================================================================
//  [cables] CABLES: panel & cable register (from SLDs / schedules / drawings), claims per stage, duplicate FROM-TO flags
// =====================================================================================================

public sealed class CableRunRow
{
    public required RunProgress P { get; init; }
    public CableRun Run => P.Run;
    public string Ref => Run.Ref;
    public string Building => Run.Building;
    public string Level => Run.Level;
    public string From => Run.FromName;
    public string To => Run.ToName;
    public string Size => Run.SizeKey;
    public string Earth => Run.EarthSizeKey;
    public double? Design => Run.DesignLength;
    public double? Measured => Run.MeasuredLength;
    public double Pulled => P.PulledM;
    public double PullPct => P.PullPct;
    public double Terminated => P.TerminatedM;
    public double HandedOver => P.HandedOverM;
    public double Weighted => P.WeightedPct;
    public string Status => P.Status switch { "FLAGGED" => "CHECK", "DONE" => "OK", "IN PROGRESS" => "OPEN", _ => "OPEN" };
    public string Progress => P.Status;
    public string Register => Run.Status;
    public string Source => $"{Run.SourceKind} {Run.SourceDoc}{(Run.SourcePage > 0 ? " p" + Run.SourcePage : "")}".Trim();
    public string Subs => P.Subcontractors;
    public int Flags => P.OpenFlags;
    public string Confidence => Run.Confidence.ToString("0.00", CultureInfo.InvariantCulture);
}

public sealed class CableFlagRow
{
    public required CableFlag F { get; init; }
    public string Tag => F.Tag;
    public string Code => F.Code;
    public string Sub => F.Claim.Subcontractor;
    public int Inv => F.Claim.InvoiceNo;
    public string Stage => F.Claim.Stage;
    public string From => F.Claim.FromRaw;
    public string To => F.Claim.ToRaw;
    public string Size => F.Claim.SizeKey + (F.Claim.IsEarth ? " E" : "");
    public double Qty => F.Claim.Qty;
    public string Message => F.Message;
    public string Decision => F.IsBypassed ? $"{F.Decision!.By} {F.Decision.At:dd-MMM-yy}: {F.Decision.Reason}" : "";
}

public sealed class CableClaimRow
{
    public required CableClaim C { get; init; }
    public required string RunRef { get; init; }
    public string Sub => C.Subcontractor;
    public int Inv => C.InvoiceNo;
    public string Stage => C.Stage;
    public string Building => C.Building;
    public string From => C.FromRaw;
    public string To => C.ToRaw;
    public string Size => C.SizeKey + (C.IsEarth ? " E" : "");
    public double Qty => C.Qty;
    public double SitePct => C.SitePct;
    public double WirPct => C.WirPct;
    public double Final => C.QtyAfterWir;
    public string Match => C.MatchStatus;
    public string Source => C.Source + (C.StatementNo.Length > 0 ? " " + C.StatementNo : "");
    public string Ledger => C.LedgerSourceKey.Length > 0 ? "YES" : "";
}

public sealed partial class CablePanelNode : ObservableObject
{
    public required PanelNode Node { get; init; }
    public string Name => Node.Panel.Name;
    public string Type => Node.Panel.IsEquipment ? (Node.Panel.Type.Length > 0 ? Node.Panel.Type : "EQUIP") : Node.Panel.Type;
    public double Progress => Node.Progress;
    public string Caption => $"{Node.Feeders.Count} outgoing  |  {Node.Progress:P0}  |  {Node.Panel.Status}";
    public string Tag => Node.Panel.Status == CableStatus.Provisional ? "OPEN" : Node.Progress >= 0.999 ? "OK" : Node.Feeders.Any(f => f.OpenFlags > 0) ? "CHECK" : "DUE";
    public ObservableCollection<CablePanelNode> Children { get; } = new();
    [ObservableProperty] private bool _isExpanded = true;
}

public sealed record AliasSuggestionRow(CablePanel Alias, CablePanel Target, double Score)
{
    public string Text => $"'{Alias.Name}'  =  '{Target.Name}' ?";
    public string ScoreText => Score.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>A run read from a drawing / schedule, editable before it is saved to the register.</summary>
public sealed partial class ReadRunRow : ObservableObject
{
    [ObservableProperty] private bool _include = true;
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _size = "";
    [ObservableProperty] private string _earth = "";
    [ObservableProperty] private double? _length;
    [ObservableProperty] private string _breaker = "";
    public double Confidence { get; init; }
    public int Page { get; init; }
    public string How { get; init; } = "";
    public required CableRun Source { get; init; }
    public string ConfidenceTag => Confidence >= 0.8 ? "OK" : Confidence >= 0.6 ? "DUE" : "CHECK";

    public CableRun ToRun()
    {
        var r = Source;
        r.FromName = PanelNames.Tidy(From); r.ToName = PanelNames.Tidy(To);
        if (!string.Equals(PanelNames.KeyOf(From, r.Building), r.FromKey, StringComparison.Ordinal)) r.FromKey = "";
        if (!string.Equals(PanelNames.KeyOf(To, r.Building), r.ToKey, StringComparison.Ordinal)) r.ToKey = "";
        r.SizeKey = Size.Trim(); r.EarthSizeKey = Earth.Trim(); r.DesignLength = Length; r.Breaker = Breaker.Trim();
        return r;
    }
}

public sealed partial class CablesViewModel : PageViewModel
{
    private readonly ICableStore _store;
    private readonly IServiceProvider _services;
    private CableSnapshot _snap = new();
    private List<CableFlag> _flags = new();
    private List<RunProgress> _progress = new();
    private CableReadResult? _read;

    public CablesViewModel(PageContext ctx, ICableStore store, IServiceProvider services) : base(ctx)
    {
        _store = store;
        _services = services;
    }

    public override string Key => "Cables";
    public override string Title => "CABLES";
    public override string Subtitle => "Panels and cable runs from SLDs / schedules / drawings - claims per stage (70 / 20 / 10) - duplicate FROM-TO flags";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => true;

    private CableService Svc => new(_store);

    public string[] Tabs { get; } = { "REGISTER", "PANELS", "CLAIMS & FLAGS", "UNKNOWN RUNS", "READ SLD / SCHEDULE" };
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsRegister), nameof(IsPanels), nameof(IsFlags), nameof(IsUnknown), nameof(IsRead))] private string _tab = "REGISTER";
    public bool IsRegister => Tab == Tabs[0];
    public bool IsPanels => Tab == Tabs[1];
    public bool IsFlags => Tab == Tabs[2];
    public bool IsUnknown => Tab == Tabs[3];
    public bool IsRead => Tab == Tabs[4];

    // ---- KPIs
    [ObservableProperty] private string _kpiRuns = "0";
    [ObservableProperty] private string _kpiRunsDetail = "";
    [ObservableProperty] private string _kpiPanels = "0";
    [ObservableProperty] private string _kpiClaims = "0";
    [ObservableProperty] private string _kpiDuplicates = "0";
    [ObservableProperty] private string _kpiFlags = "0";

    // ---- register
    public ObservableCollection<CableRunRow> Runs { get; } = new();
    public ObservableCollection<string> Levels { get; } = new();
    public ObservableCollection<string> Sizes { get; } = new();
    public ObservableCollection<string> Subs { get; } = new();
    public string[] StatusFilters { get; } = { "ALL", "NOT STARTED", "IN PROGRESS", "DONE", "FLAGGED", CableStatus.Provisional, CableStatus.Proposed, CableStatus.Confirmed, CableStatus.Rejected };
    [ObservableProperty] private string _levelFilter = "ALL";
    [ObservableProperty] private string _sizeFilter = "ALL";
    [ObservableProperty] private string _subFilter = "ALL";
    [ObservableProperty] private string _statusFilter = "ALL";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private CableRunRow? _selectedRun;
    public ObservableCollection<CableClaimRow> RunClaims { get; } = new();
    [ObservableProperty] private string _editDesign = "";
    [ObservableProperty] private string _editMeasured = "";
    [ObservableProperty] private string _editEarth = "";
    [ObservableProperty] private string _runText = "";

    partial void OnLevelFilterChanged(string value) => FillRuns();
    partial void OnSizeFilterChanged(string value) => FillRuns();
    partial void OnSubFilterChanged(string value) => FillRuns();
    partial void OnStatusFilterChanged(string value) => FillRuns();
    partial void OnSearchChanged(string value) => FillRuns();
    partial void OnSelectedRunChanged(CableRunRow? value) => ShowRun(value);

    // ---- panels
    public ObservableCollection<CablePanelNode> Tree { get; } = new();
    public ObservableCollection<AliasSuggestionRow> AliasSuggestions { get; } = new();
    public ObservableCollection<CablePanel> AllPanels { get; } = new();
    [ObservableProperty] private CablePanelNode? _selectedNode;
    [ObservableProperty] private CablePanel? _mergeTarget;

    // ---- flags
    public ObservableCollection<CableFlagRow> FlagRows { get; } = new();
    public string[] FlagCodes { get; } = new[] { "ALL" }.Concat(CableFlagCodes.All).ToArray();
    [ObservableProperty] private string _flagCode = "ALL";
    [ObservableProperty] private bool _showBypassed;
    [ObservableProperty] private CableFlagRow? _selectedFlag;
    [ObservableProperty] private string _bypassReason = "";
    partial void OnFlagCodeChanged(string value) => FillFlags();
    partial void OnShowBypassedChanged(bool value) => FillFlags();

    // ---- unknown runs
    public ObservableCollection<CableClaimRow> UnknownClaims { get; } = new();
    public ObservableCollection<CableRun> Candidates { get; } = new();
    [ObservableProperty] private CableClaimRow? _selectedUnknown;
    [ObservableProperty] private CableRun? _selectedCandidate;
    [ObservableProperty] private string _unknownDesign = "";
    partial void OnSelectedUnknownChanged(CableClaimRow? value) => FillCandidates(value);

    // ---- read
    public ObservableCollection<ReadRunRow> ReadRuns { get; } = new();
    [ObservableProperty] private string _readText = "Open a cable schedule (Excel / CSV), an SLD PDF, a DWG / DXF drawing or a scanned SLD.";
    [ObservableProperty] private string _readIssues = "";
    [ObservableProperty] private string _mappingText = "";
    [ObservableProperty] private bool _hasRead;
    [ObservableProperty] private bool _isSchedule;
    [ObservableProperty] private bool _busy;

    protected override void Refresh()
    {
        _snap = _store.Load();
        _flags = CableFlagEngine.Evaluate(_snap);
        _progress = CableReports.Progress(_snap, _flags);
        var open = _flags.Where(f => !f.IsBypassed).ToList();
        KpiRuns = _snap.Runs.Count.ToString("N0");
        KpiRunsDetail = $"{_snap.Runs.Count(r => r.Status is CableStatus.Proposed or CableStatus.Confirmed)} design / {_snap.Runs.Count(r => r.Status == CableStatus.Provisional)} from statements";
        KpiPanels = _snap.Panels.Count.ToString("N0");
        KpiClaims = $"{_snap.Claims.Count:N0}  |  {_snap.Claims.Sum(c => c.Qty):N0} m";
        KpiDuplicates = open.Count(f => f.Code == CableFlagCodes.Duplicate).ToString("N0");
        KpiFlags = open.Count.ToString("N0");
        Reset(Levels, new[] { "ALL" }.Concat(_snap.Runs.Select(r => r.Level).Where(l => l.Length > 0).Distinct().OrderBy(l => l)));
        Reset(Sizes, new[] { "ALL" }.Concat(_snap.Runs.Select(r => r.SizeKey).Where(l => l.Length > 0).Distinct().OrderBy(l => CableSize.Parse(l).Mm2)));
        Reset(Subs, new[] { "ALL" }.Concat(_snap.Claims.Select(c => c.Subcontractor).Distinct().OrderBy(s => s)));
        FillRuns();
        FillTree();
        FillFlags();
        FillUnknown();
    }

    private static void Reset<T>(ObservableCollection<T> c, IEnumerable<T> items) { c.Clear(); foreach (var i in items) c.Add(i); }

    private void FillRuns()
    {
        var subRuns = SubFilter == "ALL" ? null : _snap.Claims.Where(c => c.Subcontractor == SubFilter).Select(c => c.RunId).ToHashSet();
        var q = Search.Trim();
        var rows = _progress.Where(p => InBuilding(p.Run.Building)
                                        && (LevelFilter == "ALL" || p.Run.Level == LevelFilter)
                                        && (SizeFilter == "ALL" || p.Run.SizeKey == SizeFilter)
                                        && (subRuns is null || subRuns.Contains(p.Run.Id))
                                        && (StatusFilter == "ALL" || p.Status == StatusFilter || p.Run.Status == StatusFilter)
                                        && (q.Length == 0 || $"{p.Run.Ref} {p.Run.FromName} {p.Run.ToName} {p.Run.FromKey} {p.Run.ToKey}".Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Run.Building).ThenBy(p => p.Run.FromName).ThenBy(p => p.Run.ToName).ThenByDescending(p => p.Run.SizeMm2)
            .Select(p => new CableRunRow { P = p });
        var keep = SelectedRun?.Run.Id;
        Reset(Runs, rows);
        if (keep != null) SelectedRun = Runs.FirstOrDefault(r => r.Run.Id == keep);
    }

    private void ShowRun(CableRunRow? r)
    {
        RunClaims.Clear();
        if (r is null) { RunText = ""; return; }
        foreach (var c in _snap.Claims.Where(c => c.RunId == r.Run.Id).OrderBy(c => c.InvoiceNo).ThenBy(c => c.Stage))
            RunClaims.Add(new CableClaimRow { C = c, RunRef = r.Ref });
        EditDesign = r.Run.DesignLength?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        EditMeasured = r.Run.MeasuredLength?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        EditEarth = r.Run.EarthSizeKey;
        var len = r.P.Length;
        RunText = $"{r.Run.Ref}  {r.From} -> {r.To}  {r.Size}{(r.Earth.Length > 0 ? " + E " + r.Earth : "")}\n" +
                  $"Design {(r.Design?.ToString("0.#") ?? "-")} m, measured {(r.Measured?.ToString("0.#") ?? "-")} m. Claimed after SITE %: pulling {r.Pulled:0.#} m ({r.PullPct:P0}), " +
                  $"termination & test {r.Terminated:0.#} m, handover {r.HandedOver:0.#} m, earth pulled {r.P.EarthPulledM:0.#} m.\n" +
                  $"Weighted progress {r.Weighted:P0} (70 / 20 / 10). Register: {r.Register}. Source: {r.Source}. {r.Run.Notes}" +
                  (len is null ? "\nNo design / measured length yet: over-length and cumulative checks start once a length is known." : "");
    }

    private void FillTree()
    {
        Tree.Clear();
        var roots = CableReports.Tree(new CableSnapshot { Panels = _snap.Panels.Where(p => InBuilding(p.Building)).ToList(), Runs = _snap.Runs, Claims = _snap.Claims, Aliases = _snap.Aliases },
            _progress.Where(p => InBuilding(p.Run.Building)).ToList());
        CablePanelNode Map(PanelNode n)
        {
            var vm = new CablePanelNode { Node = n, IsExpanded = n.Depth < 2 };
            foreach (var c in n.Children) vm.Children.Add(Map(c));
            return vm;
        }
        foreach (var r in roots) Tree.Add(Map(r));
        Reset(AliasSuggestions, CableService.AliasSuggestions(_snap).Where(s => InBuilding(s.A.Building)).Select(s => new AliasSuggestionRow(s.A, s.B, s.Score)));
        Reset(AllPanels, _snap.Panels.Where(p => InBuilding(p.Building)).OrderBy(p => p.Name));
    }

    private void FillFlags()
    {
        var rows = _flags.Where(f => (FlagCode == "ALL" || f.Code == FlagCode) && (ShowBypassed || !f.IsBypassed) && InBuilding(f.Claim.Building))
            .Select(f => new CableFlagRow { F = f });
        Reset(FlagRows, rows);
    }

    private void FillUnknown()
    {
        var runs = _snap.Runs.ToDictionary(r => r.Id);
        Reset(UnknownClaims, _snap.Claims.Where(c => InBuilding(c.Building) && (c.RunId is not long id || !runs.TryGetValue(id, out var r) || r.Status is CableStatus.Provisional or CableStatus.Rejected))
            .OrderBy(c => c.Building).ThenBy(c => c.FromRaw).ThenBy(c => c.ToRaw)
            .Select(c => new CableClaimRow { C = c, RunRef = c.RunId is long i && runs.TryGetValue(i, out var rr) ? rr.Ref : "" }));
    }

    private void FillCandidates(CableClaimRow? row)
    {
        Candidates.Clear();
        if (row is null) return;
        var c = row.C;
        foreach (var r in _snap.Runs.Where(r => r.Status is CableStatus.Proposed or CableStatus.Confirmed && r.Id != c.RunId)
                     .Select(r => (r, s: Math.Max(PanelNames.Similarity(r.FromName, c.FromRaw), PanelNames.Similarity(r.ToName, c.FromRaw)) + Math.Max(PanelNames.Similarity(r.ToName, c.ToRaw), PanelNames.Similarity(r.FromName, c.ToRaw)) + (r.SizeKey == c.SizeKey ? 0.5 : 0)))
                     .Where(x => x.s >= 0.8).OrderByDescending(x => x.s).Take(12))
            Candidates.Add(r.r);
        UnknownDesign = "";
    }

    // =================================================================== commands

    private async Task Write(Func<CableService, string> work, string title)
    {
        Busy = true;
        try
        {
            var msg = await Task.Run(() => work(Svc));
            await Ctx.Data.ReloadAsync();
            ForceRefresh();
            Ctx.Toasts.Show(title, msg, ToastKind.Good);
        }
        catch (Raffaello.Core.Data.ConcurrencyException ex) { Ctx.Toasts.Show("CHANGED BY SOMEONE ELSE", ex.Message, ToastKind.Warn); ForceRefresh(); }
        catch (Exception ex) { Ctx.Toasts.Show("CABLES", ex.Message, ToastKind.Error, 8); }
        finally { Busy = false; }
    }

    private static double? Num(string s) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;

    [RelayCommand]
    private Task SaveRun()
    {
        if (SelectedRun is null) return Task.CompletedTask;
        var r = SelectedRun.Run;
        r.DesignLength = Num(EditDesign);
        r.MeasuredLength = Num(EditMeasured);
        r.EarthSizeKey = CableSize.Parse(EditEarth).IsValid ? CableSize.Parse(EditEarth).Key : EditEarth.Trim().ToUpperInvariant();
        if (r.Status == CableStatus.Provisional && r.DesignLength is > 0) { r.Status = CableStatus.Proposed; r.SourceKind = CableSources.Manual; r.Notes = "design length entered by " + Project.CurrentUser; }
        return Write(s => { s.UpdateRun(r, $"Cable run {r.Ref} {r.Title}: design {r.DesignLength:0.##} m, measured {r.MeasuredLength:0.##} m, earth {r.EarthSizeKey}"); return r.Title; }, "RUN SAVED");
    }

    [RelayCommand]
    private Task SetRunStatus(string status)
    {
        if (SelectedRun is null) return Task.CompletedTask;
        var r = SelectedRun.Run;
        return Write(s => { s.SetRunStatus(r, status); return $"{r.Title}: {status}"; }, "RUN " + status);
    }

    [RelayCommand]
    private Task ConfirmAlias(AliasSuggestionRow? row)
    {
        if (row is null) return Task.CompletedTask;
        return Write(s => $"{s.ConfirmAlias(row.Alias.Key, row.Target.Key)} rows re-keyed: '{row.Alias.Name}' is now '{row.Target.Name}'", "SAME PANEL - MERGED");
    }

    [RelayCommand]
    private Task RejectAlias(AliasSuggestionRow? row)
    {
        if (row is null) return Task.CompletedTask;
        return Write(s => { s.RejectAlias(row.Alias.Key, row.Target.Key, row.Alias.Name); return $"'{row.Alias.Name}' and '{row.Target.Name}' stay separate"; }, "NOT THE SAME PANEL");
    }

    [RelayCommand]
    private Task MergePanel()
    {
        var a = SelectedNode?.Node.Panel;
        var b = MergeTarget;
        if (a is null || b is null || a.Key == b.Key) { Ctx.Toasts.Show("PICK TWO PANELS", "Select a panel in the tree and the panel it is the same as.", ToastKind.Warn); return Task.CompletedTask; }
        if (!Ctx.Dialogs.Confirm("Merge panels", $"'{a.Name}' is the same panel as '{b.Name}'?\n\nEvery run and claim of '{a.Name}' moves to '{b.Name}' and the spelling is learned.")) return Task.CompletedTask;
        return Write(s => $"{s.ConfirmAlias(a.Key, b.Key, a.Name, "merged by hand")} rows re-keyed", "PANELS MERGED");
    }

    [RelayCommand]
    private Task Bypass()
    {
        var f = SelectedFlag?.F;
        if (f is null) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(BypassReason)) { Ctx.Toasts.Show("REASON NEEDED", "A cable flag is let through with a reason (audited).", ToastKind.Warn); return Task.CompletedTask; }
        var reason = BypassReason;
        BypassReason = "";
        return Write(s => { s.Bypass(f, reason); return f.ClaimText; }, "FLAG BYPASSED");
    }

    [RelayCommand]
    private Task BypassAllShown()
    {
        var list = FlagRows.Where(r => !r.F.IsBypassed).Select(r => r.F).ToList();
        if (list.Count == 0) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(BypassReason)) { Ctx.Toasts.Show("REASON NEEDED", "Type the reason first.", ToastKind.Warn); return Task.CompletedTask; }
        if (!Ctx.Dialogs.Confirm("Bypass cable flags", $"Let {list.Count} flags through with the reason:\n\n{BypassReason}")) return Task.CompletedTask;
        var reason = BypassReason;
        BypassReason = "";
        return Write(s => { s.BypassAll(list, reason); return $"{list.Count} flags"; }, "FLAGS BYPASSED");
    }

    [RelayCommand]
    private Task LinkToRun()
    {
        var c = SelectedUnknown?.C;
        var r = SelectedCandidate;
        if (c is null || r is null) return Task.CompletedTask;
        return Write(s => { s.AssignRun(c, r); return $"{c.FromRaw} -> {c.ToRaw} {c.SizeKey} on {r.Ref} {r.Title}"; }, "CLAIM LINKED");
    }

    /// <summary>Gives the claim's (provisional) run a design length typed by the user - it becomes a register run.</summary>
    [RelayCommand]
    private Task ConfirmProvisional()
    {
        var c = SelectedUnknown?.C;
        if (c is null) return Task.CompletedTask;
        var run = _snap.Run(c.RunId);
        var len = Num(UnknownDesign);
        if (run is null || len is null) { Ctx.Toasts.Show("DESIGN LENGTH NEEDED", "Type the design length (m) from the SLD / schedule.", ToastKind.Warn); return Task.CompletedTask; }
        run.DesignLength = len; run.Status = CableStatus.Proposed; run.SourceKind = CableSources.Manual; run.Notes = $"confirmed by {Project.CurrentUser} from the unknown-run queue";
        return Write(s => { s.UpdateRun(run, $"Cable run {run.Ref} {run.Title} confirmed with design length {len:0.##} m"); return run.Title; }, "RUN CONFIRMED");
    }

    [RelayCommand]
    private Task Rematch() => Write(s => $"{s.Rematch()} claims linked to register runs", "RE-MATCHED");

    // ---- read

    [RelayCommand]
    private async Task Read(string kind)
    {
        var filter = kind switch
        {
            "SCHEDULE" => "Cable schedule|*.xlsx;*.xlsm;*.csv",
            "PDF" => "SLD PDF|*.pdf",
            "CAD" => "AutoCAD drawing|*.dwg;*.dxf",
            "SCAN" => "Scanned SLD|*.pdf",
            _ => "All|*.*",
        };
        var path = Ctx.Dialogs.OpenFile("Read cables from", filter);
        if (path is null) return;
        Busy = true;
        try
        {
            var o = new CableReadOptions { Building = WorkingBuilding, Profiles = _snap.Profiles };
            if (kind == "SCAN")
            {
                var raster = _services.GetService<IPageRasterizer>();
                var ocr = _services.GetService<ILayoutOcrEngine>() ?? _services.GetServices<IOcrEngine>().OfType<ILayoutOcrEngine>().FirstOrDefault();
                if (raster is null || ocr is null || !ocr.IsAvailable)
                {
                    Ctx.Toasts.Show("OCR NOT AVAILABLE", "Scanned SLDs need the document reader's layout OCR engine and page rasterizer (not installed in this build). Vector PDFs and DWG / DXF work without it.", ToastKind.Warn, 10);
                    return;
                }
                _read = await CableReaders.ReadScanAsync(path, raster, ocr, o);
            }
            else _read = await Task.Run(() => CableReaders.Read(path, o));
            ShowRead();
        }
        catch (Exception ex) { Ctx.Toasts.Show("COULD NOT READ", ex.Message, ToastKind.Error, 8); }
        finally { Busy = false; }
    }

    private void ShowRead()
    {
        ReadRuns.Clear();
        if (_read is null) { HasRead = false; return; }
        foreach (var r in _read.Runs.OrderBy(r => r.SourcePage).ThenBy(r => r.FromName).ThenBy(r => r.ToName))
            ReadRuns.Add(new ReadRunRow
            {
                Source = r, From = r.FromName, To = r.ToName, Size = r.SizeKey, Earth = r.EarthSizeKey, Length = r.DesignLength, Breaker = r.Breaker,
                Confidence = r.Confidence, Page = r.SourcePage, How = r.Notes, Include = r.Confidence >= 0.6 && r.SizeKey.Length > 0,
            });
        ReadText = _read.Summary;
        ReadIssues = string.Join("\n", _read.Issues.Take(40));
        IsSchedule = _read.Kind == CableSources.Schedule;
        MappingText = CableScheduleImporter.MappingText(_read.Mapping);
        HasRead = true;
    }

    [RelayCommand]
    private async Task ReReadWithMapping()
    {
        if (_read is null) return;
        var map = MappingText.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].Trim().ToUpperInvariant(), x => x[1].Trim());
        var path = _read.FileName;
        var sig = _read.HeaderSignature;
        try
        {
            _read = await Task.Run(() => CableReaders.Read(path, new CableReadOptions { Building = WorkingBuilding, Mapping = map }));
            ShowRead();
            await Task.Run(() =>
            {
                var existing = _store.All<CableImportProfile>().FirstOrDefault(p => p.Signature == sig);
                if (existing is null) _store.Insert(new CableImportProfile { Signature = sig, Name = Path.GetFileName(path), Mapping = CableScheduleImporter.MappingText(map), LastUsed = DateTime.Now }, $"Cable schedule column mapping remembered ({Path.GetFileName(path)})");
                else { existing.Mapping = CableScheduleImporter.MappingText(map); existing.LastUsed = DateTime.Now; _store.Update(existing, $"Cable schedule column mapping updated ({Path.GetFileName(path)})"); }
            });
            Ctx.Toasts.Show("MAPPING REMEMBERED", "The same header layout is read this way next time.", ToastKind.Good);
        }
        catch (Exception ex) { Ctx.Toasts.Show("COULD NOT READ", ex.Message, ToastKind.Error, 8); }
    }

    [RelayCommand]
    private async Task SaveRead()
    {
        if (_read is null) return;
        var runs = ReadRuns.Where(r => r.Include && r.From.Trim().Length > 0 && r.To.Trim().Length > 0).Select(r => r.ToRun()).ToList();
        var keys = runs.SelectMany(r => new[] { PanelNames.KeyOf(r.FromName, r.Building), PanelNames.KeyOf(r.ToName, r.Building) }).ToHashSet();
        var panels = _read.Panels.Where(p => keys.Contains(p.Key)).ToList();
        var name = $"{_read.Kind} {Path.GetFileName(_read.FileName)}";
        await Write(s =>
        {
            var m = s.SaveRegister(runs, panels, name);
            var linked = s.Rematch();
            return $"{m.RunsAdded} runs added, {m.RunsUpdated} updated, {m.ProvisionalUpgraded} statement runs confirmed by the design, {m.PanelsAdded} panels; {linked} claims re-linked"
                   + (m.Conflicts.Count > 0 ? $"; {m.Conflicts.Count} length conflicts kept the register value" : "");
        }, "REGISTER UPDATED");
        _read = null;
        ReadRuns.Clear();
        HasRead = false;
        ReadText = "Saved. Open the next file.";
    }

    [RelayCommand]
    private void Export() => Ctx.Exports.Export("CABLE_REGISTER", CableReports.Export(_snap, _flags));

    public override IEnumerable<ExportSheet> ExportCurrentView() => CableReports.Export(_snap, _flags);

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is null) return;
        if (target.Key.StartsWith("FLAGS|")) { Tab = Tabs[2]; FlagCode = target.Key[6..]; }
        else if (target.Key == "UNKNOWN") Tab = Tabs[3];
        else if (target.Key == "ALIASES") Tab = Tabs[1];
    }
}
