using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Domain;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Icon { get; init; } = "IconDashboard";
    public string Group { get; init; } = "";
    public string Shortcut { get; init; } = "";
    [ObservableProperty] private string _badge = "";
    [ObservableProperty] private bool _isActive;
    public Action<NavItem>? OnSelected { get; set; }
    partial void OnIsActiveChanged(bool value) { if (value) OnSelected?.Invoke(this); }
}

public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

/// <summary>Shell: sidebar navigation, shared filter, page host, Ask panel, command palette, toasts and shortcuts.</summary>
public sealed partial class MainViewModel : ObservableObject, INavigator
{
    private readonly Dictionary<string, PageViewModel> _pages;
    private bool _navigating;

    public MainViewModel(PageContext ctx, IEnumerable<PageViewModel> pages, AskViewModel ask, CommandPaletteViewModel palette, PresenceService presence, ImportViewModel import)
    {
        Ctx = ctx;
        Ask = ask;
        Palette = palette;
        Presence = presence;
        Import = import;
        _pages = pages.ToDictionary(p => p.Key);
        Groups = new[]
        {
            new NavGroup("TRACK", new[] { Item("Dashboard", "DASHBOARD", "IconDashboard", "Ctrl+1"), Item("Quantities", "QUANTITIES", "IconQuantities", "Ctrl+2"), Item("Statements", "STATEMENTS", "IconStatements", "Ctrl+3"), Item("Materials", "MATERIALS", "IconMaterials", "Ctrl+4") }),
            new NavGroup("DOCUMENTS", new[] { Item("Aconex", "ACONEX", "IconAconex", "Ctrl+5"), Item("Wir", "WIR / MIR", "IconWir", "Ctrl+6"), Item("Contracts", "CONTRACTS & BOQ", "IconContracts", "Ctrl+7") }),
            new NavGroup("OUTPUT", new[] { Item("Invoices", "INVOICES", "IconInvoices", "Ctrl+8"), Item("Reports", "REPORTS", "IconReports", "Ctrl+9") }),
        };
        SettingsItem = Item("Settings", "SETTINGS", "IconSettings", "");
        palette.SetNavigator(this);
        palette.Configure(this, PaletteActions);
        ctx.Data.DataChanged += UpdateBadges;
        ctx.Filter.Changed += () => OnPropertyChanged(nameof(FilterText));
    }

    public PageContext Ctx { get; }
    public AskViewModel Ask { get; }
    public CommandPaletteViewModel Palette { get; }
    public PresenceService Presence { get; }
    public ImportViewModel Import { get; }
    public FilterState Filter => Ctx.Filter;
    public ObservableCollection<Toast> Toasts => Ctx.Toasts.Items;
    public IReadOnlyList<NavGroup> Groups { get; }
    public NavItem SettingsItem { get; }
    public string ProjectName => Ctx.Project.Settings.Project;
    public string[] Projects => new[] { Ctx.Project.Settings.Project, "DSQ - (NOT CONFIGURED)" };
    public string UserName => Ctx.Project.Settings.EffectiveUserName;
    public string UserInitials => string.Concat(UserName.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => char.ToUpperInvariant(s[0])));
    public string FilterText => Ctx.Filter.Description;

    [ObservableProperty] private PageViewModel? _current;
    [ObservableProperty] private bool _isWelcome;

    public event Action<string?>? ImportRequested;

    private NavItem Item(string key, string label, string icon, string shortcut) =>
        new() { Key = key, Label = label, Icon = icon, Shortcut = shortcut, OnSelected = i => { if (!_navigating) Go(i.Key); } };

    private IEnumerable<NavItem> AllItems => Groups.SelectMany(g => g.Items).Append(SettingsItem);

    public void Go(string key, NavTarget? target = null)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        _navigating = true;
        try
        {
            foreach (var i in AllItems) i.IsActive = i.Key == key;
            if (Current != page)
            {
                Current?.Deactivate();
                Current = page;
            }
            IsWelcome = key == "Welcome";
            Ctx.Selection.Screen = page.Title;
            if (key != "Welcome" && key != "Settings") Ctx.Project.Settings.LastModule = key;
            page.Activate(target);
        }
        finally { _navigating = false; }
    }

    public void OpenImport(string? kind = null) => ImportRequested?.Invoke(kind);
    public void OpenAsk(string? question = null) => Ask.Open(question);

    public void UpdateBadges()
    {
        var p = Ctx.Project;
        var s = p.Snapshot;
        string B(int n) => n <= 0 ? "" : n > 99 ? "99+" : n.ToString();
        foreach (var i in AllItems)
        {
            i.Badge = i.Key switch
            {
                "Dashboard" => B(p.Queue.Count(q => q.Severity >= Verdict.Check)),
                "Quantities" => B(p.Chain.Count(r => r.Verdict == Verdict.Over)),
                "Statements" => B(s.Invoices.Count(x => x.Status is InvoiceStatus.Received or InvoiceStatus.Redo)),
                "Materials" => B(p.Queue.Count(q => q.Category == "MATERIAL")),
                "Wir" => B(s.Wirs.Count(w => w.Kind == "WIR" && w.Status == WirStatus.Open && (p.Options.Today - w.SubmittedAt.Date).TotalDays > p.Options.WirDueDays)),
                "Aconex" => B(s.AconexDocs.Count(d => d.Queued)),
                "Invoices" => B(s.Invoices.Count(x => x.Status == InvoiceStatus.Received)),
                _ => "",
            };
        }
        OnPropertyChanged(nameof(UserName));
        OnPropertyChanged(nameof(UserInitials));
    }

    private IEnumerable<PaletteEntry> PaletteActions() => new[]
    {
        new PaletteEntry("ACTION", "Ask Raffaello", "Assistant with the selected line as context", "Ctrl+Shift+A", () => OpenAsk()),
        new PaletteEntry("ACTION", "Import...", "QSEXPORT / GIVEN / WIR / statement / PO / DN", "Ctrl+I", () => OpenImport()),
        new PaletteEntry("ACTION", "Export current view", "Excel, grey bold header", "Ctrl+E", ExportView),
        new PaletteEntry("ACTION", "Toggle light / dark", "", "Ctrl+Shift+L", ToggleTheme),
        new PaletteEntry("ACTION", "Reload data", "Read the shared data file again", "F5", () => _ = ReloadAsync()),
        new PaletteEntry("ACTION", "Weekly report", "Export the weekly progress workbook", "", () => Go("Reports")),
    };

    [RelayCommand] private void Navigate(string key) => Go(key);
    [RelayCommand] private void GoWelcome() => Go("Welcome");
    [RelayCommand] private void OpenPalette() => Palette.Open();
    [RelayCommand] private void ToggleAsk() { if (Ask.IsOpen) Ask.IsOpen = false; else OpenAsk(); }
    [RelayCommand] private void ImportFile() => OpenImport();
    [RelayCommand] private void ClearFilter() => Ctx.Filter.Clear();

    [RelayCommand]
    private void ExportView()
    {
        var sheets = Current?.ExportCurrentView().ToList() ?? new();
        if (sheets.Count == 0) { Ctx.Toasts.Show("NOTHING TO EXPORT", "This screen has no table to export."); return; }
        Ctx.Exports.Export($"RAFFAELLO_{Current!.Key.ToUpperInvariant()}", sheets);
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var s = Ctx.Project.Settings;
        s.Theme = Ctx.Theme.IsDark ? "Light" : "Dark";
        s.Save();
        Ctx.Theme.Apply(s.Theme, s.Accent);
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        await Ctx.Data.ReloadAsync();
        Ctx.Toasts.Show("DATA RELOADED", $"{Ctx.Project.Snapshot.Lines.Count:N0} lines from {Ctx.Project.DataLocation}", ToastKind.Good, 3);
    }

    [RelayCommand]
    private void GoIndex(string index)
    {
        var keys = new[] { "Dashboard", "Quantities", "Statements", "Materials", "Aconex", "Wir", "Contracts", "Invoices", "Reports" };
        if (int.TryParse(index, out var i) && i >= 1 && i <= keys.Length) Go(keys[i - 1]);
    }

    [RelayCommand]
    private void Escape()
    {
        if (Palette.IsOpen) Palette.IsOpen = false;
        else if (Ask.IsOpen) Ask.IsOpen = false;
    }

    /// <summary>Saves "since you were last here" state on exit.</summary>
    public void OnClosing()
    {
        var s = Ctx.Project.Settings;
        s.LastSeenAt = DateTime.Now;
        s.LastOverLineIds = Ctx.Project.Chain.Where(r => r.Verdict == Verdict.Over).Select(r => r.Id).ToList();
        s.LastLineId = Ctx.Selection.SelectedLine?.Id ?? s.LastLineId;
        s.Save();
    }
}
