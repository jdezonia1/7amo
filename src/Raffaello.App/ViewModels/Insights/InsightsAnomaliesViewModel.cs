using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Export;
using Raffaello.Core.Insights;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels.Insights;

public sealed partial class ThresholdRow : ObservableObject
{
    public string Key { get; init; } = "";
    public string Note { get; init; } = "";
    public double Default { get; init; }
    [ObservableProperty] private double _value;
    public long Id { get; set; }
    public long RowVersion { get; set; }
}

/// <summary>[insights] Claim anomalies: every warning with its explanation, evidence and suggested action; dismiss with a reason (recorded).</summary>
public sealed partial class InsightsAnomaliesViewModel : PageViewModel
{
    private readonly InsightsHub _hub;
    private List<Anomaly> _all = new();
    private string? _pendingSelect;
    private int _run;

    public InsightsAnomaliesViewModel(PageContext ctx, InsightsHub hub) : base(ctx) => _hub = hub;

    public override string Key => InsightsEngine.NavKey;
    public override string Title => "ANOMALIES";
    public override string Subtitle => "Unusual claims, explained with evidence - warnings only, nothing here blocks a claim or an invoice";
    public override bool ShowFilterBar => false;

    public string[] Severities { get; } = { "ALL", "HIGH", "MEDIUM", "LOW", "INFO" };
    public ObservableCollection<string> Kinds { get; } = new() { "ALL" };
    public ObservableCollection<string> Subcontractors { get; } = new() { "ALL" };
    public ObservableCollection<Anomaly> Rows { get; } = new();
    public ObservableCollection<ThresholdRow> Thresholds { get; } = new();

    [ObservableProperty] private string _severity = "ALL";
    [ObservableProperty] private string _kind = "ALL";
    [ObservableProperty] private string _sub = "ALL";
    [ObservableProperty] private bool _showDismissed;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Anomaly? _selected;
    [ObservableProperty] private string _dismissReason = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _highCount;
    [ObservableProperty] private int _mediumCount;
    [ObservableProperty] private int _lowCount;
    [ObservableProperty] private int _infoCount;
    [ObservableProperty] private int _dismissedCount;
    [ObservableProperty] private bool _showThresholds;

    partial void OnSeverityChanged(string value) => Apply();
    partial void OnKindChanged(string value) => Apply();
    partial void OnSubChanged(string value) => Apply();
    partial void OnShowDismissedChanged(bool value) => Apply();
    partial void OnSearchChanged(string value) => Apply();
    partial void OnSelectedChanged(Anomaly? value) => DismissReason = value?.Dismissal?.Reason ?? "";

    protected override void Refresh() => _ = LoadAsync(false);

    private async Task LoadAsync(bool hashNew)
    {
        var run = ++_run;
        IsBusy = true;
        StatusText = hashNew ? "Hashing documents and running every check..." : "Running the checks...";
        try
        {
            var p = Project;
            var building = BuildingFilter;
            var data = await Task.Run(() => _hub.LoadData());
            var list = await Task.Run(() => _hub.Compute(p, building, hashNew || data.FileHashes.Count == 0, out _, out _, data));
            if (run != _run) return;
            _all = list;
            LoadThresholds(data);
            Fill(Kinds, _all.Select(a => a.Kind));
            Fill(Subcontractors, _all.SelectMany(a => a.Subcontractor.Split(" / ")).Where(x => x.Trim().Length > 0).Select(x => x.Trim()));
            var live = _all.Where(a => !a.IsDismissed).ToList();
            HighCount = live.Count(a => a.Severity == InsightSeverity.High);
            MediumCount = live.Count(a => a.Severity == InsightSeverity.Medium);
            LowCount = live.Count(a => a.Severity == InsightSeverity.Low);
            InfoCount = live.Count(a => a.Severity == InsightSeverity.Info);
            DismissedCount = _all.Count(a => a.IsDismissed);
            StatusText = $"{_all.Count:N0} insights  |  {building ?? "ALL BUILDINGS"}  |  {DateTime.Now:HH:mm}";
            Apply();
        }
        catch (Exception ex) { StatusText = "Insights could not run: " + ex.Message; }
        finally { if (run == _run) IsBusy = false; }
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var keep = target.Count > 0 ? target[0] : "ALL";
        var list = values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (target.Skip(1).SequenceEqual(list)) return;
        target.Clear();
        target.Add(keep);
        foreach (var v in list) target.Add(v);
    }

    private void LoadThresholds(InsightsData data)
    {
        Thresholds.Clear();
        foreach (var (key, def) in InsightThresholds.Defaults)
        {
            var row = data.Thresholds.FirstOrDefault(t => t.Key == key);
            Thresholds.Add(new ThresholdRow { Key = key, Note = def.Note, Default = def.Value, Value = row?.Value ?? def.Value, Id = row?.Id ?? 0, RowVersion = row?.RowVersion ?? 0 });
        }
    }

    private void Apply()
    {
        var q = _all.AsEnumerable();
        if (!ShowDismissed) q = q.Where(a => !a.IsDismissed);
        if (Severity != "ALL") q = q.Where(a => a.Tag == Severity);
        if (Kind != "ALL") q = q.Where(a => a.Kind == Kind);
        if (Sub != "ALL") q = q.Where(a => a.Subcontractor.Split(" / ").Any(x => x.Trim().Equals(Sub, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(a => a.Title.Contains(s, StringComparison.OrdinalIgnoreCase) || a.Room.Contains(s, StringComparison.OrdinalIgnoreCase) || a.Explanation.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        var keep = _pendingSelect ?? Selected?.Fingerprint;
        Rows.Clear();
        foreach (var a in q.Take(5000)) Rows.Add(a);
        Selected = Rows.FirstOrDefault(a => a.Fingerprint == keep) ?? Rows.FirstOrDefault();
        if (_pendingSelect != null && Selected?.Fingerprint == _pendingSelect) _pendingSelect = null;
    }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is not { Length: > 0 } key) return;
        if (key.StartsWith("KIND|", StringComparison.Ordinal)) { Severity = "ALL"; Kind = key[5..]; return; }
        _pendingSelect = key;
        Severity = "ALL"; Kind = "ALL"; Sub = "ALL"; Search = "";
        if (_all.FirstOrDefault(a => a.Fingerprint == key) is { IsDismissed: true }) ShowDismissed = true;
        Apply();
    }

    [RelayCommand] private Task Recheck() => LoadAsync(true);

    [RelayCommand]
    private async Task Dismiss()
    {
        if (Selected is not { } a) return;
        if (string.IsNullOrWhiteSpace(DismissReason)) { Ctx.Toasts.Show("REASON NEEDED", "Write why this warning does not apply - the reason is recorded.", ToastKind.Warn); return; }
        try
        {
            var reason = DismissReason.Trim();
            await Task.Run(() => _hub.Store.Dismiss(a.Fingerprint, a.Kind, a.Title, reason));
            Ctx.Toasts.Show("DISMISSED", a.Title, ToastKind.Good);
            await Ctx.Data.ReloadAsync();
        }
        catch (Exception ex) { Ctx.Toasts.Show("COULD NOT DISMISS", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task Restore()
    {
        if (Selected is not { IsDismissed: true } a) return;
        try
        {
            await Task.Run(() => _hub.Store.Restore(a.Fingerprint, "restored on the Anomalies page"));
            await Ctx.Data.ReloadAsync();
        }
        catch (Exception ex) { Ctx.Toasts.Show("COULD NOT RESTORE", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task SaveThresholds()
    {
        try
        {
            var data = await Task.Run(() => _hub.LoadData());
            var rows = Thresholds.Where(t => Math.Abs(t.Value - t.Default) > 1e-12 || t.Id > 0).Select(t =>
            {
                var existing = data.Thresholds.FirstOrDefault(x => x.Key == t.Key);
                var e = existing ?? new InsightThreshold { Key = t.Key };
                e.Value = t.Value; e.Note = t.Note;
                return e;
            }).ToList();
            await Task.Run(() => _hub.Store.ReplaceAll(rows, $"Insight thresholds saved ({rows.Count} changed from default)"));
            Ctx.Toasts.Show("THRESHOLDS SAVED", "The checks run again with the new values.", ToastKind.Good);
            await LoadAsync(false);
        }
        catch (Exception ex) { Ctx.Toasts.Show("COULD NOT SAVE", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void ResetThresholds() { foreach (var t in Thresholds) t.Value = t.Default; }

    [RelayCommand]
    private void OpenEvidence(Evidence? e)
    {
        if (e is null) return;
        switch (e.Kind)
        {
            case EvidenceKinds.Document when e.Path.Length > 0:
                if (System.IO.File.Exists(e.Path)) DialogService.OpenWithShell(e.Path); else Ctx.Toasts.Show("FILE NOT FOUND", e.Path, ToastKind.Warn);
                break;
            case EvidenceKinds.Ledger or EvidenceKinds.Room: Ctx.Nav.Go("Ledger", new NavTarget("Ledger", Key: e.NavKey)); break;
            case EvidenceKinds.Invoice or EvidenceKinds.InvoiceLine: Ctx.Nav.Go("Invoices", new NavTarget("Invoices", Key: e.NavKey)); break;
            case EvidenceKinds.Statement: Ctx.Nav.Go("Statements", new NavTarget("Statements", Key: e.NavKey)); break;
            case EvidenceKinds.Wir: Ctx.Nav.Go("Wir", new NavTarget("Wir", Key: e.NavKey)); break;
            case EvidenceKinds.ContractItem: Ctx.Nav.Go("Contracts", new NavTarget("Contracts", Key: e.NavKey)); break;
            case EvidenceKinds.Dn or EvidenceKinds.Po: Ctx.Nav.Go("Materials", new NavTarget("Materials", Key: e.NavKey)); break;
        }
    }

    [RelayCommand] private void Export() => Ctx.Exports.Export("RAFFAELLO_ANOMALIES", ExportCurrentView());

    public override IEnumerable<ExportSheet> ExportCurrentView() =>
        new[] { InsightsEngine.AnomalySheet(Rows.Count > 0 ? Rows : _all, $"{BuildingFilter ?? "ALL BUILDINGS"}  |  {Severity} / {Kind} / {Sub}  |  {DateTime.Today:dd MMM yyyy}") };
}
