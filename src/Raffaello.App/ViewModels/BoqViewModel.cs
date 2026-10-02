using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Ai;
using Raffaello.Core.Boq;
using Raffaello.Core.Export;
using Raffaello.Core.Materials;

namespace Raffaello.App.ViewModels;

public sealed record BoqSystemRow(string System, int Items, double Amount, int Unsure)
{
    public string Caption => $"{Items} items  |  SAR {Amount:N0}{(Unsure > 0 ? $"  |  {Unsure} to check" : "")}";
}

/// <summary>
/// BOQ: import the owner BOQ from Excel, categorise every row by system and category (EN / AR keyword rules, the heading it sits
/// under, learned corrections, optional Claude fallback), review and correct; corrections are learned for the next import.
/// </summary>
public sealed partial class BoqViewModel : PageViewModel
{
    private readonly MaterialsService _m;

    public BoqViewModel(PageContext ctx, MaterialsService materials) : base(ctx) => _m = materials;

    public override string Key => "Boq";
    public override string Title => "BOQ";
    public override string Subtitle => "Owner BOQ by system and category - corrections are learned";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public ObservableCollection<BoqLine> Rows { get; } = new();
    public ObservableCollection<BoqSystemRow> Systems { get; } = new();
    public ObservableCollection<string> Sources { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();
    public string[] SystemChoices => BoqCategorizer.Systems;
    public string[] CategoryChoices => BoqCategorizer.Categories;

    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _systemFilter = "ALL";
    [ObservableProperty] private bool _onlyUnsure;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private BoqLine? _selected;
    [ObservableProperty] private string _editSystem = "";
    [ObservableProperty] private string _editCategory = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isPreview;
    private BoqImportResult? _preview;
    private List<BoqLine> _all = new();

    public ObservableCollection<string> SystemFilters { get; } = new();

    partial void OnSourceChanged(string value) { if (!IsPreview) LoadStored(); }
    private bool _filling;
    partial void OnSystemFilterChanged(string value) => Fill();
    partial void OnOnlyUnsureChanged(bool value) => Fill();
    partial void OnSearchChanged(string value) => Fill();
    partial void OnSelectedChanged(BoqLine? value) { EditSystem = value?.System ?? ""; EditCategory = value?.Category ?? ""; }

    protected override void Refresh()
    {
        _m.Reload();
        var keep = Source;
        Sources.Clear();
        foreach (var s in _m.Snapshot.BoqLines.Select(b => b.Source).Distinct().OrderBy(s => s)) Sources.Add(s);
        if (!IsPreview) { Source = Sources.Contains(keep) ? keep : Sources.FirstOrDefault() ?? ""; LoadStored(); }
    }

    private void LoadStored()
    {
        _all = _m.Snapshot.BoqLines.Where(b => b.Source == Source).OrderBy(b => b.Sheet).ThenBy(b => b.RowNo).ToList();
        Issues.Clear();
        Fill();
    }

    private void Fill()
    {
        if (_filling) return;
        _filling = true;
        try { FillCore(); } finally { _filling = false; }
    }

    private void FillCore()
    {
        var items = _all.Where(r => !r.IsHeading).ToList();
        Systems.Clear();
        foreach (var g in items.GroupBy(r => r.System.Length == 0 ? "(NOT CATEGORISED)" : r.System).OrderByDescending(g => g.Sum(r => r.Amount)))
            Systems.Add(new BoqSystemRow(g.Key, g.Count(), g.Sum(r => r.Amount), g.Count(r => r.CatScore < 0.75 && !r.Confirmed)));
        var keepFilter = SystemFilter ?? "ALL";
        SystemFilters.Clear();
        SystemFilters.Add("ALL");
        foreach (var s in Systems) SystemFilters.Add(s.System);
        if (!SystemFilters.Contains(keepFilter)) keepFilter = "ALL";
        if (SystemFilter != keepFilter) SystemFilter = keepFilter;
        Rows.Clear();
        foreach (var r in _all.Where(r => SystemFilter == "ALL" || (r.System.Length == 0 ? "(NOT CATEGORISED)" : r.System) == SystemFilter || r.IsHeading && SystemFilter == "ALL")
                     .Where(r => !OnlyUnsure || (!r.IsHeading && r.CatScore < 0.75 && !r.Confirmed))
                     .Where(r => Search.Length == 0 || r.Description.Contains(Search, StringComparison.OrdinalIgnoreCase) || r.BoqCode.Contains(Search, StringComparison.OrdinalIgnoreCase)))
            Rows.Add(r);
        Summary = $"{(IsPreview ? "PREVIEW (not saved) - " : "")}{items.Count} items, SAR {items.Sum(r => r.Amount):N2}, categorised {items.Count(r => r.System.Length > 0)} / {items.Count}, confirmed {items.Count(r => r.Confirmed)}";
    }

    [RelayCommand]
    private async Task Import()
    {
        var path = Ctx.Dialogs.OpenFile("Owner BOQ workbook");
        if (path is null) return;
        try
        {
            _preview = await Task.Run(() => BoqImporter.Read(path, _m.Snapshot.BoqRules));
            IsPreview = true;
            _all = _preview.Rows;
            Issues.Clear();
            foreach (var i in _preview.Issues) Issues.Add(i);
            Fill();
            Ctx.Toasts.Show("BOQ READ", _preview.Summary + " - check, then SAVE", ToastKind.Info, 6);
        }
        catch (Exception ex) { Ctx.Toasts.Show("BOQ IMPORT FAILED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task SaveImport()
    {
        var p = _preview;
        if (p is null) return;
        try
        {
            await Task.Run(() => BoqImporter.Commit(_m.Store, p));
            IsPreview = false; _preview = null;
            _m.Reload();
            Source = p.FileName;
            Refresh();
            Ctx.Toasts.Show("BOQ SAVED", p.Summary, ToastKind.Good);
            Ctx.Data.RaiseChanged();
        }
        catch (Exception ex) { Ctx.Toasts.Show("BOQ NOT SAVED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void DiscardImport()
    {
        _preview = null; IsPreview = false;
        Refresh();
    }

    /// <summary>Applies the correction to the row (and every row with the same description) and learns it.</summary>
    [RelayCommand]
    private async Task ApplyAndLearn()
    {
        var row = Selected;
        if (row is null) return;
        var rule = BoqCategorizer.Learn(row, EditSystem, EditCategory, _m.Snapshot.BoqRules);
        var key = rule.Key;
        foreach (var same in _all.Where(r => r != row && !r.IsHeading && Raffaello.Core.Coding.Fingerprints.NormalizeDescription(r.Description) == key))
        { same.System = EditSystem; same.Category = EditCategory; same.CatSource = "LEARNED"; same.CatScore = 1; }
        try
        {
            await Task.Run(() =>
            {
                _m.Store.Batch(w =>
                {
                    if (rule.Id == 0) w.Insert(rule); else w.Update(rule);
                    if (!IsPreview) foreach (var r in _all.Where(r => r.Id > 0 && (r == row || r.CatSource == "LEARNED" && Raffaello.Core.Coding.Fingerprints.NormalizeDescription(r.Description) == key))) w.Update(r);
                }, $"BOQ categorisation learned: '{row.Description}' -> {EditSystem} / {EditCategory}");
            });
            _m.Reload();
            if (!IsPreview) LoadStored(); else Fill();
            Ctx.Toasts.Show("LEARNED", $"{EditSystem} / {EditCategory}", ToastKind.Good, 3);
        }
        catch (Exception ex) { Ctx.Toasts.Show("NOT SAVED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task AskClaude()
    {
        var key = AnthropicClient.ResolveKey(Project.Settings.AnthropicApiKey);
        if (!_m.Settings.BoqAiFallback || key is null) { Ctx.Toasts.Show("CLAUDE FALLBACK IS OFF", "Turn it on in Materials > SETTINGS and set an API key in Settings.", ToastKind.Warn); return; }
        var client = new AnthropicClient(key) { Model = Project.Settings.AnthropicModel, Effort = "low", UseServerFallbacks = Project.Settings.UseServerFallbacks };
        try
        {
            var n = await BoqCategorizer.AiFallbackAsync(_all, client);
            if (!IsPreview && n > 0) await Task.Run(() => _m.Store.Batch(w => { foreach (var r in _all.Where(r => r.Id > 0 && r.CatSource == "AI")) w.Update(r); }, $"BOQ: {n} rows categorised by Claude"));
            Fill();
            Ctx.Toasts.Show("CLAUDE CATEGORISED", $"{n} rows (marked AI - check them)", ToastKind.Good);
        }
        catch (Exception ex) { Ctx.Toasts.Show("CLAUDE FAILED", ex.Message, ToastKind.Error); }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        yield return new ExportSheet
        {
            Name = "BOQ", Title = $"OWNER BOQ - {Source}", Subtitle = Summary,
            Columns = new() { new("SHEET"), new("ROW", ColumnKind.Integer), new("ITEM"), new("BOQ CODE"), new("DESCRIPTION", Width: 60), new("UNIT"), new("QTY", ColumnKind.Number), new("RATE", ColumnKind.Money), new("AMOUNT", ColumnKind.Money), new("SYSTEM"), new("CATEGORY"), new("SOURCE"), new("SCORE", ColumnKind.Percent) },
            Rows = Rows.Select(r => new object?[] { r.Sheet, r.RowNo, r.ItemNo, r.BoqCode, r.Description, r.Unit, r.Qty, r.Rate, r.Amount, r.System, r.Category, r.CatSource, r.CatScore }).ToList(),
        };
    }
}
