using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Ai;
using Raffaello.Core.Assemblies;
using Raffaello.Core.Export;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

// =====================================================================================================
//  [assemblies] BOQ ITEM BREAKDOWN (assembly / rate analysis)
// =====================================================================================================

/// <summary>Hand-over from another screen (Variations NEW ITEM): the description to break down and what to do with the result.</summary>
public static class AssembliesBridge
{
    public sealed record Request(string Description, string Unit, double Qty, Action<Breakdown> Apply, string ReturnTo, string Label);
    public static Request? Pending { get; set; }
}

/// <summary>A parameter of the current breakdown: template / global default and the per-item value.</summary>
public sealed partial class AsmParamRow : ObservableObject
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "";
    public double Default { get; init; }
    public string Scope { get; init; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Overridden))] private double _value;
    public bool Overridden => Math.Abs(Value - Default) > 1e-9;
}

public sealed partial class AssembliesViewModel : PageViewModel
{
    private readonly AssemblyService _svc;
    private List<AssemblySource> _all = new();
    private bool _loading;

    public AssembliesViewModel(PageContext ctx, AssemblyService svc) : base(ctx) => _svc = svc;

    public override string Key => "Assemblies";
    public override string Title => "BOQ ITEM BREAKDOWN";
    public override string Subtitle => "Item -> components, quantities, prices and the built-up rate (assembly / rate analysis)";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    // ------------------------------------------------------------------ tabs

    public string[] Tabs { get; } = { "BREAKDOWN", "BULK REQUIREMENTS", "TEMPLATES", "PRICES", "SETTINGS" };
    [ObservableProperty] private string _tab = "BREAKDOWN";
    public bool IsBreakdownTab => Tab == "BREAKDOWN";
    public bool IsBulkTab => Tab == "BULK REQUIREMENTS";
    public bool IsTemplatesTab => Tab == "TEMPLATES";
    public bool IsPricesTab => Tab == "PRICES";
    public bool IsSettingsTab => Tab == "SETTINGS";
    partial void OnTabChanged(string value)
    {
        foreach (var n in new[] { nameof(IsBreakdownTab), nameof(IsBulkTab), nameof(IsTemplatesTab), nameof(IsPricesTab), nameof(IsSettingsTab) }) OnPropertyChanged(n);
    }

    // ------------------------------------------------------------------ choices

    public string[] SourceKindChoices { get; } = { "ALL", SourceKinds.Contract, SourceKinds.Boq, SourceKinds.BoqLine };
    public static string[] ItemTypeChoices => ItemTypes.All;
    public static string[] ConduitChoices { get; } = { "", "PVC", "EMT", "RS", "FLEX", "NONE" };
    public static string[] MountChoices { get; } = { "", "WALL", "CEILING", "BOTH", "FLOOR", "STAND" };
    public static string[] HeightChoices { get; } = { "ANY", "LOW", "HIGH" };
    public static string[] SupplyChoices => SupplyScopes.All;
    public static string[] StageChoices { get; } = { "", StageNames.First, StageNames.Second, StageNames.Third };
    public static string[] KindChoices => ComponentKinds.All;
    public static string[] BasisChoices { get; } = { "", LabourBases.Subcontract, LabourBases.Hours };
    public static string[] LabourCategoryChoices { get; } = { "", "1ST FIX", "2ND FIX", "3RD FIX", "FLEX", "TERMINATION", "SELF" };
    public static string[] LabourModeChoices => AssemblySettings.LabourModes;

    // ------------------------------------------------------------------ breakdown tab

    public ObservableCollection<AssemblySource> Sources { get; } = new();
    public ObservableCollection<string> Groups { get; } = new();
    public ObservableCollection<BreakdownLine> Lines { get; } = new();
    public ObservableCollection<AsmParamRow> Params { get; } = new();
    public ObservableCollection<string> Flags { get; } = new();
    public ObservableCollection<string> TemplateCodes { get; } = new();

    [ObservableProperty] private string _sourceKind = "ALL";
    [ObservableProperty] private string _group = "ALL";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private AssemblySource? _selectedSource;
    [ObservableProperty] private ItemSpec? _spec;
    [ObservableProperty] private string _templateCode = "";
    [ObservableProperty] private Breakdown? _breakdown;
    [ObservableProperty] private BreakdownLine? _selectedLine;
    [ObservableProperty] private string _freeText = "";
    [ObservableProperty] private string _freeUnit = "PT";
    [ObservableProperty] private string _stagesText = "";
    [ObservableProperty] private string _rateCard = "";
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _manualPrice = "";
    [ObservableProperty] private bool _hasBridge;
    [ObservableProperty] private string _bridgeLabel = "";
    [ObservableProperty] private string _assistNote = "";
    [ObservableProperty] private string _currentDescription = "";

    partial void OnSourceKindChanged(string value) => FillSources();
    partial void OnGroupChanged(string value) => FillSources();
    partial void OnSearchChanged(string value) => FillSources();
    partial void OnSelectedSourceChanged(AssemblySource? value)
    {
        if (_loading || value is null) return;
        LoadSource(value);
    }

    protected override void Refresh()
    {
        _svc.Reload();
        _all = _svc.Sources(building: BuildingFilter);
        _loading = true;
        try
        {
            var keepGroup = Group;
            Groups.Clear(); Groups.Add("ALL");
            foreach (var g in _all.Select(s => s.Group).Where(g => g.Length > 0).Distinct().OrderBy(g => g)) Groups.Add(g);
            Group = Groups.Contains(keepGroup) ? keepGroup : "ALL";
            TemplateCodes.Clear(); TemplateCodes.Add("");
            foreach (var t in _svc.Library.Templates) TemplateCodes.Add(t.Code);
        }
        finally { _loading = false; }
        FillSources();
        FillTemplates();
        FillPrices();
        FillSettings();
        var cov = _svc.Coverage(_all);
        Summary = $"{_all.Count} items ({cov.Recognised} with a recognised type), {_svc.Library.Templates.Count} templates, PO lines priced {_svc.Prices.PoLines}, price list {_svc.Prices.ListPrices}, manual {_svc.Prices.ManualPrices}, contract labour items {_svc.Labour.Entries.Count}";
        if (Breakdown != null && Spec != null) Recalculate();
    }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key == "BRIDGE" && AssembliesBridge.Pending is { } req)
        {
            Tab = "BREAKDOWN";
            HasBridge = true;
            BridgeLabel = req.Label;
            FreeText = req.Description;
            FreeUnit = req.Unit.Length > 0 ? req.Unit : "PT";
            BreakdownText();
            return;
        }
        if (target.LineId is { } id && target.Key is { Length: > 0 } kind)
        {
            Tab = "BREAKDOWN";
            var src = _all.FirstOrDefault(s => s.Kind == kind && s.Id == id) ?? _svc.FindSource(kind, id);
            if (src != null) { _loading = true; SourceKind = "ALL"; Search = ""; _loading = false; FillSources(); SelectedSource = Sources.FirstOrDefault(s => s.Kind == src.Kind && s.Id == src.Id) ?? src; LoadSource(src); }
        }
    }

    private void FillSources()
    {
        if (_loading) return;
        var keep = SelectedSource;
        Sources.Clear();
        foreach (var s in _all.Where(s => SourceKind == "ALL" || s.Kind == SourceKind)
                     .Where(s => Group == "ALL" || s.Group == Group)
                     .Where(s => Search.Length == 0 || s.Description.Contains(Search, StringComparison.OrdinalIgnoreCase) || s.Code.Contains(Search, StringComparison.OrdinalIgnoreCase))
                     .Take(3000))
            Sources.Add(s);
        if (keep != null) { _loading = true; SelectedSource = Sources.FirstOrDefault(s => s.Kind == keep.Kind && s.Id == keep.Id); _loading = false; }
    }

    private void LoadSource(AssemblySource src)
    {
        Spec = _svc.SpecFor(src).Clone();
        StagesText = Spec.StagesText;
        TemplateCode = _svc.StoredSpec(src)?.TemplateCode ?? "";
        LoadParams(src);
        Recalculate();
    }

    private AssemblySource CurrentSource => SelectedSource ?? AssemblySource.FromText(FreeText, FreeUnit);

    private void LoadParams(AssemblySource src)
    {
        Params.Clear();
        var t = Spec is null ? null : _svc.TemplateFor(src, Spec, TemplateCode);
        if (t is null) return;
        var over = _svc.OverridesFor(src);
        foreach (var p in t.Params)
            Params.Add(new AsmParamRow { Name = p.Name, Label = p.Label, Unit = p.Unit, Default = p.Value, Scope = t.Code, Value = over.TryGetValue(p.Name, out var v) ? v : p.Value });
        foreach (var p in _svc.Library.Globals.Where(g => t.Params.All(x => x.Name != g.Name)))
            Params.Add(new AsmParamRow { Name = p.Name, Label = p.Label, Unit = p.Unit, Default = p.Value, Scope = "GLOBAL", Value = over.TryGetValue(p.Name, out var v) ? v : p.Value });
    }

    private Dictionary<string, double> Overrides() => Params.Where(p => p.Overridden).ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void BreakdownText()
    {
        if (FreeText.Trim().Length == 0) { Ctx.Toasts.Show("TYPE A DESCRIPTION", "or pick an item on the left", ToastKind.Warn); return; }
        _loading = true; SelectedSource = null; _loading = false;
        var src = AssemblySource.FromText(FreeText.Trim(), FreeUnit);
        Spec = ItemParser.Parse(src.Description, src.Unit, ItemSourceKind.Text);
        StagesText = Spec.StagesText;
        TemplateCode = "";
        LoadParams(src);
        Recalculate();
    }

    [RelayCommand]
    private void Reparse()
    {
        var src = CurrentSource;
        Spec = ItemParser.Parse(src.Description, src.Unit, src.ParseKind);
        StagesText = Spec.StagesText;
        LoadParams(src);
        Recalculate();
    }

    [RelayCommand]
    private void Recalculate()
    {
        if (Spec is null) return;
        try
        {
            var stages = (StagesText ?? "").Split(new[] { '+', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant()).Where(s => StageNames.All.Contains(s)).ToList();
            var stagesText = StagesText ?? ""; if (stagesText.Trim().Length > 0 && !stagesText.Contains("ALL", StringComparison.OrdinalIgnoreCase)) Spec!.Stages = stages;
            var b = _svc.Run(CurrentSource, Spec, Overrides(), TemplateCode.Length > 0 ? TemplateCode : null);
            Breakdown = b;
            CurrentDescription = $"{CurrentSource.KindLabel} {CurrentSource.Code}: {CurrentSource.Description}";
            Lines.Clear();
            foreach (var l in b.Lines) Lines.Add(l);
            Flags.Clear();
            foreach (var f in b.Flags.Concat(b.Warnings).Concat(b.Spec.Notes)) Flags.Add(f);
            StagesText = Spec.StagesText;
            var c = _svc.Settings.Currency;
            RateCard = $"MATERIAL {b.Material:N2}\nLABOUR {b.Labour:N2}  ({b.LabourMode})\n" + (b.Equipment > 0 ? $"EQUIPMENT {b.Equipment:N2}\n" : "") +
                       $"DIRECT {b.Direct:N2}\nOVERHEAD {b.OverheadPct:P1}  {b.Overhead:N2}\nPROFIT {b.ProfitPct:P1}  {b.Profit:N2}\nBUILT-UP RATE  {b.Rate:N2} {c} / {b.Unit}" +
                       (b.ReferenceRate is { } r ? $"\n{b.ReferenceLabel} RATE  {r:N2}\nMARGIN  {b.Margin:N2}  ({b.MarginPct:P1})" : "") +
                       (b.FreeIssueMaterial > 0 ? $"\nFREE ISSUE (not in rate)  {b.FreeIssueMaterial:N2}" : "");
            Verdict = b.Verdict;
            OnPropertyChanged(nameof(Spec));
        }
        catch (Exception ex) { Ctx.Toasts.Show("BREAKDOWN FAILED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task SaveSpec()
    {
        if (Spec is null || SelectedSource is not { } src) { Ctx.Toasts.Show("PICK AN ITEM", "typed descriptions are not stored - use APPLY or export", ToastKind.Warn); return; }
        try
        {
            var spec = Spec; var over = Overrides(); var code = TemplateCode;
            await Task.Run(() => _svc.SaveSpec(src, spec, over, code, true));
            Ctx.Toasts.Show("SPEC SAVED", $"{src.Code}: {spec.ItemType} - used for this item from now on", ToastKind.Good, 3);
        }
        catch (Exception ex) { Ctx.Toasts.Show("NOT SAVED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void ResetParams()
    {
        foreach (var p in Params) p.Value = p.Default;
        Recalculate();
    }

    [RelayCommand]
    private async Task SetManualPriceForLine()
    {
        if (SelectedLine is not { } l) { Ctx.Toasts.Show("PICK A COMPONENT LINE", kind: ToastKind.Warn); return; }
        if (!double.TryParse(ManualPrice, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price < 0) { Ctx.Toasts.Show("ENTER A PRICE", "e.g. 2.75", ToastKind.Warn); return; }
        try
        {
            await Task.Run(() => _svc.SetManualPrice(l.Spec, l.Unit, price, "set from the breakdown"));
            Ctx.Toasts.Show("PRICE SAVED", $"{l.Spec}: {price:N2} / {l.Unit} (MANUAL, today)", ToastKind.Good, 3);
            Recalculate();
            FillPrices();
        }
        catch (Exception ex) { Ctx.Toasts.Show("NOT SAVED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void ApplyToBridge()
    {
        if (AssembliesBridge.Pending is not { } req || Breakdown is not { } b) return;
        req.Apply(b);
        AssembliesBridge.Pending = null;
        HasBridge = false;
        Ctx.Toasts.Show("RATE BUILD-UP APPLIED", $"material {b.Material:N2}, labour {b.Labour:N2}, rate {b.Rate:N2}", ToastKind.Good);
        Ctx.Nav.Go(req.ReturnTo);
    }

    [RelayCommand]
    private void CancelBridge() { AssembliesBridge.Pending = null; HasBridge = false; }

    [RelayCommand]
    private void ExportBreakdown()
    {
        if (Breakdown is not { } b) return;
        Ctx.Exports.Export($"RATE_ANALYSIS_{Safe(CurrentSource.Code)}", new[] { AssemblyExporter.BreakdownSheet(CurrentSource, b) });
    }

    [RelayCommand]
    private void ExportPdf()
    {
        if (Breakdown is not { } b) return;
        var path = Ctx.Dialogs.SaveFile("Rate analysis (PDF)", $"RATE_ANALYSIS_{Safe(CurrentSource.Code)}_{DateTime.Now:yyyyMMdd}.pdf", "PDF|*.pdf");
        if (path is null) return;
        try
        {
            AssemblyExporter.RateAnalysisPdf(path, new[] { (CurrentSource, b) }, Info());
            Ctx.Toasts.Show("PDF SAVED", Path.GetFileName(path), ToastKind.Good);
            DialogService.OpenWithShell(path);
        }
        catch (IOException ex) { Ctx.Toasts.Show("PDF FAILED", ex.Message, ToastKind.Error); }
    }

    private RateSheetInfo Info() => new(Project.Settings.Project, Project.Settings.EffectiveUserName, DateTime.Today, _svc.Settings.Currency);
    private static string Safe(string s) => string.Concat((s.Length == 0 ? "ITEM" : s).Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_'));

    // ---- optional Claude assist

    [RelayCommand]
    private async Task AskClaude()
    {
        var key = AnthropicClient.ResolveKey(Project.Settings.AnthropicApiKey);
        if (key is null) { Ctx.Toasts.Show("CLAUDE IS NOT SET UP", "Add an API key in Settings to use the assist.", ToastKind.Warn); return; }
        if (Spec is null) return;
        var src = CurrentSource;
        AssistNote = "asking Claude ...";
        try
        {
            var client = new AnthropicClient(key) { Model = Project.Settings.AnthropicModel, Effort = "low", UseServerFallbacks = Project.Settings.UseServerFallbacks };
            var t = await new ClaudeAssemblyAssist(client).ProposeAsync(src.Description, src.Unit, Spec);
            if (t is null) { AssistNote = "no usable proposal"; return; }
            EditTemplate = t;
            EditTemplateIsNew = true;
            FillTemplateEditor();
            Tab = "TEMPLATES";
            AssistNote = $"proposal: {t.Components.Count} components - check and SAVE TEMPLATE to confirm";
            Ctx.Toasts.Show("CLAUDE PROPOSAL", "Check every line in the template editor, then SAVE TEMPLATE (it stays unconfirmed until Mohamed confirms).", ToastKind.Info, 8);
        }
        catch (Exception ex) { AssistNote = ""; Ctx.Toasts.Show("CLAUDE FAILED", ex.Message, ToastKind.Error); }
    }

    // ------------------------------------------------------------------ bulk tab

    public ObservableCollection<BulkRow> BulkRows { get; } = new();
    public ObservableCollection<RequirementLine> Requirements { get; } = new();
    [ObservableProperty] private string _bulkSummary = "";
    [ObservableProperty] private bool _bulkUseInstalled = true;
    [ObservableProperty] private bool _bulkBusy;
    private BulkResult? _bulk;

    [RelayCommand]
    private async Task RunBulk()
    {
        var items = Sources.Where(s => s.Qty > 0).ToList();
        if (items.Count == 0) { Ctx.Toasts.Show("NO ITEMS WITH A QUANTITY", "filter the item list (source / section / search) first", ToastKind.Warn); return; }
        BulkBusy = true;
        try
        {
            var installed = BulkUseInstalled;
            _bulk = await Task.Run(() => _svc.Bulk(items, installed));
            BulkRows.Clear(); foreach (var r in _bulk.Rows) BulkRows.Add(r);
            Requirements.Clear(); foreach (var m in _bulk.Materials) Requirements.Add(m);
            BulkSummary = _bulk.Summary;
        }
        catch (Exception ex) { Ctx.Toasts.Show("BULK RUN FAILED", ex.Message, ToastKind.Error); }
        finally { BulkBusy = false; }
    }

    [RelayCommand]
    private void ExportBulk()
    {
        if (_bulk is null) return;
        Ctx.Exports.Export("MATERIAL_REQUIREMENTS", new[] { AssemblyExporter.BulkItemsSheet(_bulk), AssemblyExporter.RequirementsSheet(_bulk) });
    }

    [RelayCommand]
    private void ExportBulkPdf()
    {
        if (_bulk is null || _bulk.Rows.Count == 0) return;
        var path = Ctx.Dialogs.SaveFile("Rate analysis sheets (PDF)", $"RATE_ANALYSIS_{DateTime.Now:yyyyMMdd}.pdf", "PDF|*.pdf");
        if (path is null) return;
        try
        {
            AssemblyExporter.RateAnalysisPdf(path, _bulk.Rows.Select(r => (r.Source, r.Breakdown)).ToList(), Info());
            Ctx.Toasts.Show("PDF SAVED", $"{_bulk.Rows.Count} rate analysis pages", ToastKind.Good);
            DialogService.OpenWithShell(path);
        }
        catch (IOException ex) { Ctx.Toasts.Show("PDF FAILED", ex.Message, ToastKind.Error); }
    }

    // ------------------------------------------------------------------ templates tab

    public ObservableCollection<AssemblyTemplate> Templates { get; } = new();
    public ObservableCollection<AsmParam> EditParams { get; } = new();
    public ObservableCollection<AsmComponent> EditComponents { get; } = new();
    public ObservableCollection<AsmParam> GlobalParams { get; } = new();
    public ObservableCollection<string> TemplateProblems { get; } = new();
    [ObservableProperty] private AssemblyTemplate? _selectedTemplate;
    [ObservableProperty] private AssemblyTemplate? _editTemplate;
    [ObservableProperty] private bool _editTemplateIsNew;
    [ObservableProperty] private AsmComponent? _selectedComponent;

    partial void OnSelectedTemplateChanged(AssemblyTemplate? value)
    {
        if (value is null) return;
        EditTemplate = value;
        EditTemplateIsNew = false;
        FillTemplateEditor();
    }

    private void FillTemplates()
    {
        var keep = SelectedTemplate?.Code;
        Templates.Clear();
        foreach (var t in _svc.Library.Templates) Templates.Add(t);
        GlobalParams.Clear();
        foreach (var g in _svc.Library.Globals) GlobalParams.Add(g);
        SelectedTemplate = Templates.FirstOrDefault(t => t.Code == keep) ?? Templates.FirstOrDefault();
    }

    private void FillTemplateEditor()
    {
        EditParams.Clear(); EditComponents.Clear(); TemplateProblems.Clear();
        if (EditTemplate is null) return;
        foreach (var p in EditTemplate.Params) EditParams.Add(p);
        foreach (var c in EditTemplate.Components.OrderBy(c => c.Order)) EditComponents.Add(c);
        foreach (var p in TemplateCheck.Problems(EditTemplate, _svc.Library.Globals)) TemplateProblems.Add(p);
    }

    private AssemblyTemplate EditedTemplate()
    {
        var t = EditTemplate!;
        var order = 0;
        foreach (var c in EditComponents) c.Order = ++order;
        return new AssemblyTemplate { Header = t.Header, Params = EditParams.ToList(), Components = EditComponents.ToList() };
    }

    [RelayCommand]
    private void CheckTemplate()
    {
        if (EditTemplate is null) return;
        TemplateProblems.Clear();
        var problems = TemplateCheck.Problems(EditedTemplate(), _svc.Library.Globals);
        foreach (var p in problems) TemplateProblems.Add(p);
        if (problems.Count == 0) TemplateProblems.Add("OK - every formula uses known names");
    }

    [RelayCommand]
    private async Task SaveTemplate()
    {
        if (EditTemplate is null) return;
        try
        {
            var t = EditedTemplate();
            if (t.Header.Origin == "SEED") t.Header.Origin = "USER";
            if (EditTemplateIsNew) await Task.Run(() => _svc.SaveAsNewTemplate(t));
            else { await Task.Run(() => _svc.Store.SaveTemplate(t)); _svc.Reload(); }
            EditTemplateIsNew = false;
            Ctx.Toasts.Show("TEMPLATE SAVED", t.Header.Code, ToastKind.Good, 3);
            Refresh();
        }
        catch (Exception ex) { Ctx.Toasts.Show("TEMPLATE NOT SAVED", ex.Message, ToastKind.Error, 8); }
    }

    [RelayCommand]
    private void DuplicateTemplate()
    {
        if (EditTemplate is null) return;
        var src = EditTemplate;
        EditTemplate = new AssemblyTemplate
        {
            Header = new AsmTemplate { Code = src.Code + "-COPY", Name = src.Header.Name + " (copy)", ItemType = src.ItemType, Unit = src.Header.Unit, Description = src.Header.Description, Origin = "USER", Active = true },
            Params = src.Params.Select(p => new AsmParam { Name = p.Name, Value = p.Value, Unit = p.Unit, Label = p.Label, Note = p.Note }).ToList(),
            Components = src.Components.Select(c => new AsmComponent
            {
                Order = c.Order, Key = c.Key, Name = c.Name, Spec = c.Spec, Unit = c.Unit, QtyFormula = c.QtyFormula, WastePct = c.WastePct, Stage = c.Stage, Kind = c.Kind,
                LabourBasis = c.LabourBasis, LabourCategory = c.LabourCategory, DefaultPrice = c.DefaultPrice, Notes = c.Notes,
            }).ToList(),
        };
        EditTemplateIsNew = true;
        FillTemplateEditor();
    }

    [RelayCommand]
    private async Task DeleteTemplate()
    {
        if (EditTemplate is not { } t || EditTemplateIsNew) return;
        if (!Ctx.Dialogs.Confirm("Delete template", $"Delete template {t.Code}? Items of type {t.ItemType} will use another template (or none).")) return;
        try { await Task.Run(() => _svc.Store.DeleteTemplate(t)); Refresh(); }
        catch (Exception ex) { Ctx.Toasts.Show("NOT DELETED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void AddComponent() => EditComponents.Add(new AsmComponent { Key = "c" + (EditComponents.Count + 1), Name = "New component", Unit = "PCS", QtyFormula = "1", Kind = ComponentKinds.Material });

    [RelayCommand]
    private void RemoveComponent() { if (SelectedComponent is { } c) EditComponents.Remove(c); }

    [RelayCommand]
    private void MoveComponentUp()
    {
        if (SelectedComponent is not { } c) return;
        var i = EditComponents.IndexOf(c);
        if (i > 0) EditComponents.Move(i, i - 1);
    }

    [RelayCommand]
    private void AddParam() => EditParams.Add(new AsmParam { Name = "new_param", Value = 0, Label = "New parameter" });

    [RelayCommand]
    private async Task SaveGlobals()
    {
        try { await Task.Run(() => _svc.Store.SaveGlobals(GlobalParams.ToList())); Ctx.Toasts.Show("GLOBAL PARAMETERS SAVED", kind: ToastKind.Good); Refresh(); }
        catch (Exception ex) { Ctx.Toasts.Show("NOT SAVED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task RestoreDefaults()
    {
        if (!Ctx.Dialogs.Confirm("Restore seeded templates", "Restore every SEEDED template to the built-in defaults? Templates you created or changed (origin USER / AI) are kept.")) return;
        var n = await Task.Run(() => _svc.Store.SeedDefaults(resetSeeded: true));
        Ctx.Toasts.Show("DEFAULTS RESTORED", $"{n} templates", ToastKind.Good);
        Refresh();
    }

    [RelayCommand]
    private void ExportTemplates() => Ctx.Exports.Export("ASSEMBLY_LIBRARY", AssemblyExporter.TemplateSheets(_svc.Library));

    // ------------------------------------------------------------------ prices tab

    public ObservableCollection<AsmPrice> PriceRows { get; } = new();
    [ObservableProperty] private AsmPrice? _selectedPrice;
    [ObservableProperty] private string _priceListSupplier = "";
    [ObservableProperty] private string _priceSummary = "";

    private void FillPrices()
    {
        PriceRows.Clear();
        foreach (var p in _svc.PriceRows.OrderBy(p => p.Source).ThenBy(p => p.Description)) PriceRows.Add(p);
        PriceSummary = $"{_svc.Prices.ManualPrices} manual, {_svc.Prices.ListPrices} price-list rows, {_svc.Prices.PoLines} PO lines from the Materials module (read live)";
    }

    [RelayCommand]
    private async Task ImportPriceList()
    {
        var path = Ctx.Dialogs.OpenFile("Supplier price list (Excel / CSV)", "Price list|*.xlsx;*.xlsm;*.csv|All files|*.*");
        if (path is null) return;
        try
        {
            var supplier = PriceListSupplier;
            var imp = await Task.Run(() => _svc.ReadPriceList(path, supplier, DateTime.Today));
            if (imp.Prices.Count == 0) { Ctx.Toasts.Show("NO PRICES READ", string.Join("; ", imp.Issues.Take(3)), ToastKind.Warn, 8); return; }
            if (!Ctx.Dialogs.Confirm("Import price list", $"{imp.Summary}.\nRows of an earlier import of the same file are replaced. Import?")) return;
            await Task.Run(() => _svc.CommitPriceList(imp));
            Ctx.Toasts.Show("PRICE LIST IMPORTED", imp.Summary, ToastKind.Good);
            Refresh();
        }
        catch (Exception ex) { Ctx.Toasts.Show("IMPORT FAILED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task SavePrice()
    {
        if (SelectedPrice is not { } p) return;
        try { await Task.Run(() => _svc.Store.SavePrice(p)); _svc.Reload(); FillPrices(); Ctx.Toasts.Show("PRICE SAVED", p.Description, ToastKind.Good, 2); }
        catch (Exception ex) { Ctx.Toasts.Show("NOT SAVED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task DeletePrice()
    {
        if (SelectedPrice is not { } p) return;
        try { await Task.Run(() => _svc.Store.DeletePrice(p)); _svc.Reload(); FillPrices(); }
        catch (Exception ex) { Ctx.Toasts.Show("NOT DELETED", ex.Message, ToastKind.Error); }
    }

    // ------------------------------------------------------------------ settings tab

    [ObservableProperty] private double _overheadPercent;
    [ObservableProperty] private double _profitPercent;
    [ObservableProperty] private string _labourMode = "AUTO";
    [ObservableProperty] private double _poMatchScore;

    private void FillSettings()
    {
        var s = _svc.Settings;
        OverheadPercent = Math.Round(s.OverheadPct * 100, 4);
        ProfitPercent = Math.Round(s.ProfitPct * 100, 4);
        LabourMode = s.LabourMode;
        PoMatchScore = s.PoMatchScore;
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        var s = _svc.Settings;
        s.OverheadPct = OverheadPercent / 100.0; s.ProfitPct = ProfitPercent / 100.0; s.LabourMode = LabourMode; s.PoMatchScore = Math.Clamp(PoMatchScore, 0.3, 1);
        try { await Task.Run(() => _svc.Store.SaveSettings(s)); Ctx.Toasts.Show("SETTINGS SAVED", kind: ToastKind.Good); Refresh(); }
        catch (Exception ex) { Ctx.Toasts.Show("NOT SAVED", ex.Message, ToastKind.Error); }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        if (IsBulkTab && _bulk != null) return new[] { AssemblyExporter.BulkItemsSheet(_bulk), AssemblyExporter.RequirementsSheet(_bulk) };
        if (IsTemplatesTab) return AssemblyExporter.TemplateSheets(_svc.Library);
        return Breakdown is { } b ? new[] { AssemblyExporter.BreakdownSheet(CurrentSource, b) } : Array.Empty<ExportSheet>();
    }
}
