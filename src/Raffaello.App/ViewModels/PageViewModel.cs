using CommunityToolkit.Mvvm.ComponentModel;
using Raffaello.App.Services;
using Raffaello.Core;
using Raffaello.Core.Chain;
using Raffaello.Core.Export;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

/// <summary>Shared plumbing for every module screen.</summary>
public interface INavigator
{
    void Go(string key, NavTarget? target = null);
    void OpenImport(string? kind = null);
    void OpenAsk(string? question = null);
}

public sealed class PageContext
{
    public required INavigator Nav { get; init; }
    public required DataService Data { get; init; }
    public required FilterState Filter { get; init; }
    public required ToastService Toasts { get; init; }
    public required DialogService Dialogs { get; init; }
    public required ExportService Exports { get; init; }
    public required SelectionService Selection { get; init; }
    public required ThemeService Theme { get; init; }
    public ProjectService Project => Data.Project;
}

public abstract partial class PageViewModel : ObservableObject
{
    protected PageContext Ctx { get; }
    private bool _dirty = true;

    [ObservableProperty] private bool _isActive;

    protected PageViewModel(PageContext ctx)
    {
        Ctx = ctx;
        ctx.Data.DataChanged += OnInputsChanged;
        ctx.Filter.Changed += () => { if (UsesFilter) OnInputsChanged(); };
        ctx.Theme.Changed += () => { if (HasCharts) OnInputsChanged(); };
    }

    public abstract string Key { get; }
    public abstract string Title { get; }
    public virtual string Subtitle => "";
    public virtual bool ShowFilterBar => true;
    protected virtual bool UsesFilter => true;
    protected virtual bool HasCharts => false;

    public ProjectService Project => Ctx.Project;
    public FilterSpec Spec => Ctx.Filter.Spec;
    public DateTime Today => Ctx.Project.Options.Today;

    /// <summary>[phase6] Building chosen in the header switcher (null = all).</summary>
    public string? BuildingFilter => Ctx.Filter.Spec.Building;
    /// <summary>Building used for imports / generation when one must be chosen (switcher, else BRANDED).</summary>
    public string WorkingBuilding => BuildingFilter ?? Raffaello.Core.Domain.Buildings.Branded;
    public bool InBuilding(string? building) => BuildingFilter is null || string.IsNullOrEmpty(building) || string.Equals(building, BuildingFilter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rows of the chain within the shared filter.</summary>
    public List<ChainRow> Scoped() => Spec.Apply(Ctx.Project.Chain).ToList();

    private void OnInputsChanged()
    {
        if (IsActive) SafeRefresh(); else _dirty = true;
    }

    public void Activate(NavTarget? target)
    {
        IsActive = true;
        Ctx.Selection.SelectedRecord = _selectedRecord;   // the assistant sees this page's selection again
        if (_dirty) SafeRefresh();
        if (target != null) NavigateTo(target);
    }

    public void Deactivate() => IsActive = false;

    private void SafeRefresh()
    {
        _dirty = false;
        try { Refresh(); }
        catch (Exception ex) { Ctx.Toasts.Show($"{Title} could not refresh", ex.Message, ToastKind.Error); }
    }

    public void ForceRefresh() => SafeRefresh();

    protected abstract void Refresh();

    /// <summary>Called when navigated to with a specific target (line, invoice, PO ...).</summary>
    protected virtual void NavigateTo(NavTarget target) { }

    /// <summary>Sheets for "Export current view".</summary>
    public virtual IEnumerable<ExportSheet> ExportCurrentView() => Array.Empty<ExportSheet>();

    protected void SelectLine(ChainRow? row) => Ctx.Selection.SelectedLine = row;

    private string _selectedRecord = "";

    /// <summary>
    /// Tells the assistant what is selected on this page (ledger line, invoice, PO / DN, variation, room, cable run, drawing takeoff);
    /// the text comes from <see cref="Raffaello.Core.Wiring.SelectedRecords"/>. Kept per page and restored when the page is shown again.
    /// </summary>
    protected void PublishSelection(string? record)
    {
        _selectedRecord = record ?? "";
        Ctx.Selection.SelectedRecord = _selectedRecord;
    }
}
