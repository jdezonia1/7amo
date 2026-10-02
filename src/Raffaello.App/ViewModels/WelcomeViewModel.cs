using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Analytics;
using Raffaello.Core.Domain;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

public sealed record SinceItem(string Label, int Count, string Module, string Status);
public sealed record RecentItem(string Kind, string Name, string When, string Detail);

/// <summary>First view on launch: greeting, what changed since last visit, three big actions, today's queue, data file status.</summary>
public sealed partial class WelcomeViewModel : PageViewModel
{
    private readonly PresenceService _presence;

    public WelcomeViewModel(PageContext ctx, PresenceService presence) : base(ctx) { _presence = presence; }

    public override string Key => "Welcome";
    public override string Title => "WELCOME";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public PresenceService Presence => _presence;

    [ObservableProperty] private string _greeting = "";
    [ObservableProperty] private string _dateLine = "";
    [ObservableProperty] private string _continueTitle = "";
    [ObservableProperty] private string _continueDetail = "";
    [ObservableProperty] private string _dataPath = "";
    [ObservableProperty] private string _dataSize = "";
    [ObservableProperty] private string _dataModified = "";
    [ObservableProperty] private string _dataRows = "";
    [ObservableProperty] private string _progressLine = "";
    [ObservableProperty] private string _certifiedLine = "";
    [ObservableProperty] private string _sinceWhen = "";

    public ObservableCollection<SinceItem> Since { get; } = new();
    public ObservableCollection<QueueItem> TopQueue { get; } = new();
    public ObservableCollection<RecentItem> Recent { get; } = new();

    public static DateTime RiyadhNow()
    {
        foreach (var id in new[] { "Asia/Riyadh", "Arab Standard Time" })
        {
            try { return TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(id)); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return DateTime.UtcNow.AddHours(3);
    }

    protected override void Refresh()
    {
        var p = Project;
        var s = p.Snapshot;
        var now = RiyadhNow();
        var part = now.Hour < 12 ? "Good morning" : now.Hour < 17 ? "Good afternoon" : "Good evening";
        var name = p.Settings.EffectiveUserName;
        var first = name.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        if (first.Length > 0) first = char.ToUpperInvariant(first[0]) + first[1..];
        Greeting = $"{part}, {first}";
        DateLine = now.ToString("dddd dd MMMM yyyy  |  HH:mm").ToUpperInvariant() + "  RIYADH";

        var since = p.Settings.LastSeenAt ?? DateTime.Now.AddDays(-7);
        SinceWhen = $"SINCE YOU WERE LAST HERE  |  {since:dd MMM HH:mm}".ToUpperInvariant();
        var overNow = p.Chain.Where(r => r.Verdict == Verdict.Over).Select(r => r.Id).ToHashSet();
        var newOver = p.Settings.LastOverLineIds.Count == 0 ? overNow.Count(id => p.ChainById[id].Line.UpdatedAt > since) : overNow.Count(id => !p.Settings.LastOverLineIds.Contains(id));
        var audit = p.Db.RecentAudit(500, since);
        Since.Clear();
        Since.Add(new("NEW WIRS", s.Wirs.Count(w => w.Kind == "WIR" && w.SubmittedAt > since.Date), "Wir", "OPEN"));
        Since.Add(new("NEW DNS", s.DeliveryNotes.Count(d => d.DnDate > since.Date), "Materials", "OK"));
        Since.Add(new("INVOICES RECEIVED", s.Invoices.Count(i => i.InvDate > since.Date), "Statements", "DUE"));
        Since.Add(new("NEW OVER LINES", newOver, "Quantities", "OVER"));
        Since.Add(new("CHANGES BY OTHERS", audit.Count(a => !string.Equals(a.User, p.Db.User, StringComparison.OrdinalIgnoreCase)), "Dashboard", "CHECK"));

        TopQueue.Clear();
        foreach (var q in p.Queue.Take(5)) TopQueue.Add(q);

        var lastModule = string.IsNullOrEmpty(p.Settings.LastModule) || p.Settings.LastModule == "Welcome" ? "Dashboard" : p.Settings.LastModule;
        ContinueTitle = $"CONTINUE  |  {lastModule.ToUpperInvariant()}";
        ContinueDetail = p.Settings.LastLineId is long id && p.ChainById.TryGetValue(id, out var row)
            ? $"Back to {row.Building} {row.Level} {row.Room} {row.System} {row.Stage} ({row.Status})"
            : "Pick up where you left off";

        Recent.Clear();
        foreach (var i in s.Imports.OrderByDescending(i => i.ImportedAt).Take(5))
            Recent.Add(new(i.Kind, i.FileName, i.ImportedAt.ToString("dd MMM HH:mm"), $"{i.Rows:N0} rows{(i.Errors > 0 ? $", {i.Errors} errors" : "")}"));
        foreach (var f in p.Settings.RecentFiles.Take(4))
            Recent.Add(new("FILE", Path.GetFileName(f), "", Path.GetDirectoryName(f) ?? ""));

        DataPath = p.Db.Path;
        try
        {
            var fi = new FileInfo(p.Db.Path);
            DataSize = fi.Exists ? $"{fi.Length / 1024.0 / 1024.0:N1} MB  |  WAL mode" : "not created yet";
            DataModified = fi.Exists ? $"last write {fi.LastWriteTime:dd MMM HH:mm}" : "";
        }
        catch { DataSize = "unreachable"; }
        DataRows = $"{s.Lines.Count:N0} lines  |  {s.Wirs.Count:N0} WIR/MIR  |  {s.Invoices.Count} statements  |  {s.DeliveryNotes.Count} DNs";

        var curve = ProjectAnalytics.SCurve(s, p.Chain, p.ProjectStart, p.PlannedFinish, Today);
        var nowPt = curve.LastOrDefault(c => c.Actual.HasValue);
        ProgressLine = nowPt is null ? "No progress yet" : $"ACTUAL {nowPt.Actual:P0}  vs  PLANNED {nowPt.Planned:P0}";
        CertifiedLine = $"SAR {p.Chain.Sum(r => r.CertifiedValue):N0} CERTIFIED TO DATE";
    }

    [RelayCommand]
    private void Continue()
    {
        var p = Project;
        var module = string.IsNullOrEmpty(p.Settings.LastModule) || p.Settings.LastModule == "Welcome" ? "Dashboard" : p.Settings.LastModule;
        Ctx.Nav.Go(module, p.Settings.LastLineId is long id ? new NavTarget(module, id) : null);
    }

    [RelayCommand] private void Import() => Ctx.Nav.OpenImport();
    [RelayCommand] private void Ask() => Ctx.Nav.OpenAsk();
    [RelayCommand] private void OpenQueue(QueueItem? item) { if (item != null) Ctx.Nav.Go(item.Target.Module, item.Target); }
    [RelayCommand] private void OpenSince(SinceItem? item) { if (item != null) Ctx.Nav.Go(item.Module, item.Module == "Quantities" ? new NavTarget("Quantities", Key: "OVER") : null); }
    [RelayCommand] private void OpenDashboard() => Ctx.Nav.Go("Dashboard");
    [RelayCommand] private void Refresh2() => _ = Presence.BeatAsync();
}
