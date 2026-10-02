using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Ai;
using Raffaello.Core.Export;
using Raffaello.Core.Queue;
using Raffaello.Core.Variations;

namespace Raffaello.App.ViewModels;

// =====================================================================================================
//  [phase4] VARIATIONS / EI register
// =====================================================================================================

public sealed record VariationListRow(Variation V, VariationTotals Totals, int AgeDays)
{
    public string Number => V.Number;
    public string Type => V.Type;
    public string Title => V.Title;
    public string Status => V.Status;
    public string Tag => V.Status switch
    {
        VariationStatus.Approved => "APPROVED",
        VariationStatus.Rejected => "REJECTED",
        VariationStatus.Withdrawn => "WITHDRAWN",
        VariationStatus.Draft => "DRAFT",
        _ => AgeDays > 60 ? "DUE" : "OPEN",
    };
    public string Caption => $"{V.Date:dd MMM yy}  |  {(VariationStatus.IsClosed(V.Status) ? V.Status : $"{AgeDays} d")}  |  NET {Totals.Net:N0}";
}

/// <summary>Editable variation line (the grid edits this, Save writes <see cref="VariationLine"/>s).</summary>
public sealed partial class VariationLineRow : ObservableObject
{
    public long Id { get; init; }
    public long RowVersion { get; init; }
    public string SourceKind { get; init; } = "";
    public long SourceId { get; init; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Amount), nameof(EffectiveRate), nameof(IsNew))] private string _kind = VariationLineKinds.Addition;
    [ObservableProperty] private string _itemCode = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Amount))] private double _qty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Amount), nameof(EffectiveRate))] private double _rate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Amount), nameof(EffectiveRate))] private double _material;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Amount), nameof(EffectiveRate))] private double _labour;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Amount), nameof(EffectiveRate))] private double _equipment;
    /// <summary>Percent (10 = 10 %).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Amount), nameof(EffectiveRate))] private double _overheadPercent;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Amount), nameof(EffectiveRate))] private double _profitPercent;
    [ObservableProperty] private string _notes = "";

    public event Action? Changed;
    partial void OnQtyChanged(double value) => Changed?.Invoke();
    partial void OnRateChanged(double value) => Changed?.Invoke();
    partial void OnKindChanged(string value) => Changed?.Invoke();
    partial void OnMaterialChanged(double value) => Changed?.Invoke();
    partial void OnLabourChanged(double value) => Changed?.Invoke();
    partial void OnEquipmentChanged(double value) => Changed?.Invoke();
    partial void OnOverheadPercentChanged(double value) => Changed?.Invoke();
    partial void OnProfitPercentChanged(double value) => Changed?.Invoke();

    public bool IsNew => Kind == VariationLineKinds.NewItem;
    public double EffectiveRate => VariationMath.RateOf(ToEntity());
    public double Amount => VariationMath.Amount(ToEntity());

    public VariationLine ToEntity() => new()
    {
        Id = Id, RowVersion = RowVersion, Kind = Kind, SourceKind = SourceKind, SourceId = SourceId, ItemCode = ItemCode.Trim(), Description = Description.Trim(), Unit = Unit.Trim(),
        Qty = Math.Abs(Qty), Rate = Rate, Material = Material, Labour = Labour, Equipment = Equipment, OverheadPct = OverheadPercent / 100.0, ProfitPct = ProfitPercent / 100.0, Notes = Notes,
    };

    public static VariationLineRow From(VariationLine l) => new()
    {
        Id = l.Id, RowVersion = l.RowVersion, SourceKind = l.SourceKind, SourceId = l.SourceId, Kind = l.Kind, ItemCode = l.ItemCode, Description = l.Description, Unit = l.Unit,
        Qty = l.Qty, Rate = l.Rate, Material = l.Material, Labour = l.Labour, Equipment = l.Equipment, OverheadPercent = Math.Round(l.OverheadPct * 100, 4), ProfitPercent = Math.Round(l.ProfitPct * 100, 4), Notes = l.Notes,
    };
}

public sealed partial class VariationsViewModel : PageViewModel
{
    private readonly AconexAutomationService _svc;
    private Variation? _current;
    private CancellationTokenSource? _suggestCts;

    public VariationsViewModel(PageContext ctx, AconexAutomationService svc) : base(ctx)
    {
        _svc = svc;
        Lines.CollectionChanged += (_, _) => UpdateTotals();
    }

    public override string Key => "Variations";
    public override string Title => "VARIATIONS / EI";
    public override string Subtitle => "Consultant instructions -> omission / addition / new item submissions, tracked to approval";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public ObservableCollection<VariationListRow> Items { get; } = new();
    public ObservableCollection<VariationLineRow> Lines { get; } = new();
    public ObservableCollection<VariationDoc> Docs { get; } = new();
    public ObservableCollection<VariationStatusChange> StatusLog { get; } = new();
    public ObservableCollection<Suggestion> Suggestions { get; } = new();
    public ObservableCollection<string> NextStatuses { get; } = new();
    public string[] StatusFilters { get; } = new[] { "ALL", "OPEN" }.Concat(VariationStatus.All).ToArray();
    public string[] Types => VariationTypes.All;
    public string[] Kinds => VariationLineKinds.All;

    [ObservableProperty] private string _statusFilter = "OPEN";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private VariationListRow? _selected;
    [ObservableProperty] private bool _hasCurrent;
    [ObservableProperty] private bool _isLocked;

    // editor
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _type = VariationTypes.Vo;
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private string _consultantRef = "";
    [ObservableProperty] private string _vTitle = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _building = "";
    [ObservableProperty] private string _aconexWorkflowNo = "";
    [ObservableProperty] private string _approvedAmount = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _statusNote = "";
    [ObservableProperty] private VariationLineRow? _selectedLine;
    [ObservableProperty] private VariationDoc? _selectedDoc;

    [ObservableProperty] private string _addTotal = "0.00";
    [ObservableProperty] private string _omitTotal = "0.00";
    [ObservableProperty] private string _netTotal = "0.00";
    [ObservableProperty] private string _lineWarnings = "";

    [ObservableProperty] private string _kpiOpen = "0";
    [ObservableProperty] private string _kpiOpenNet = "0";
    [ObservableProperty] private string _kpiApprovedNet = "0";
    [ObservableProperty] private string _kpiOld = "0";

    [ObservableProperty] private string _suggestNote = "";
    [ObservableProperty] private bool _suggesting;

    partial void OnStatusFilterChanged(string value) => LoadList();
    partial void OnSearchChanged(string value) => LoadList();
    private bool _listLoading;
    partial void OnSelectedChanged(VariationListRow? value) { if (value != null && !_listLoading) Load(value.V.Id); }

    private IVariationStore Store => _svc.Variations;

    protected override void Refresh()
    {
        LoadList();
        if (_current != null) Load(_current.Id);
    }

    private void LoadList()
    {
        List<Variation> all; List<VariationLine> lines;
        try { all = Store.Variations(); lines = Store.AllLines(); }
        catch (Exception ex) { Ctx.Toasts.Show("VARIATIONS", ex.Message, ToastKind.Error); return; }
        var byVar = lines.GroupBy(l => l.VariationId).ToDictionary(g => g.Key, g => VariationMath.Totals(g));
        var rows = all.Select(v => new VariationListRow(v, byVar.GetValueOrDefault(v.Id) ?? new VariationTotals(0, 0, 0, 0), VariationMath.AgeDays(v, Today))).ToList();
        var open = rows.Where(r => !VariationStatus.IsClosed(r.Status)).ToList();
        KpiOpen = open.Count.ToString("N0");
        KpiOpenNet = open.Sum(r => r.Totals.Net).ToString("N0");
        KpiApprovedNet = rows.Where(r => r.Status == VariationStatus.Approved).Sum(r => r.V.ApprovedAmount ?? r.Totals.Net).ToString("N0");
        KpiOld = open.Count(r => r.AgeDays > 90).ToString("N0");
        IEnumerable<VariationListRow> view = rows;
        if (StatusFilter == "OPEN") view = view.Where(r => !VariationStatus.IsClosed(r.Status));
        else if (StatusFilter != "ALL") view = view.Where(r => r.Status == StatusFilter);
        if (!string.IsNullOrWhiteSpace(Search))
            view = view.Where(r => r.Number.Contains(Search, StringComparison.OrdinalIgnoreCase) || r.Title.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                   || r.V.ConsultantRef.Contains(Search, StringComparison.OrdinalIgnoreCase));
        var keep = _current?.Id;
        Items.Clear();
        foreach (var r in view.OrderByDescending(r => r.V.Date).ThenByDescending(r => r.Number)) Items.Add(r);
        _listLoading = true;
        try { Selected = keep is { } k ? Items.FirstOrDefault(i => i.V.Id == k) : null; }
        finally { _listLoading = false; }
    }

    private void Load(long id)
    {
        var v = Store.Get(id);
        if (v is null) { _current = null; HasCurrent = false; return; }
        _current = v;
        HasCurrent = true;
        IsLocked = VariationStatus.IsClosed(v.Status);
        Number = v.Number; Type = v.Type; Date = v.Date; ConsultantRef = v.ConsultantRef; VTitle = v.Title; Description = v.Description; Building = v.Building;
        AconexWorkflowNo = v.AconexWorkflowNo; Notes = v.Notes; Status = v.Status;
        ApprovedAmount = v.ApprovedAmount?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        foreach (var l in Lines) l.Changed -= UpdateTotals;
        Lines.Clear();
        foreach (var l in Store.Lines(id)) { var r = VariationLineRow.From(l); r.Changed += UpdateTotals; Lines.Add(r); }
        Docs.Clear();
        foreach (var d in Store.Docs(id)) Docs.Add(d);
        StatusLog.Clear();
        foreach (var s in Store.StatusLog(id).OrderByDescending(s => s.Id)) StatusLog.Add(s);
        NextStatuses.Clear();
        foreach (var s in VariationStatus.Next(v.Status)) NextStatuses.Add(s);
        Suggestions.Clear();
        SuggestNote = "";
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var entities = Lines.Select(l => l.ToEntity()).ToList();
        var t = VariationMath.Totals(entities);
        AddTotal = t.AddTotal.ToString("N2");
        OmitTotal = t.OmitTotal.ToString("N2");
        NetTotal = t.Net.ToString("N2");
        var warn = entities.Select((l, i) => (i, VariationMath.Validate(l))).Where(x => x.Item2.Count > 0).Select(x => $"line {x.i + 1}: {string.Join(", ", x.Item2)}").ToList();
        LineWarnings = string.Join("  |  ", warn.Take(4)) + (warn.Count > 4 ? $"  (+{warn.Count - 4} more)" : "");
    }

    // ------------------------------------------------------------------ commands: register

    [RelayCommand]
    private void New(string? type)
    {
        try
        {
            var v = Store.Create(new Variation { Type = type is { Length: > 0 } ? type : VariationTypes.Vo, Title = "New " + (type ?? "VO"), Date = DateTime.Today });
            _current = v;
            StatusFilter = "OPEN";
            LoadList();
            Selected = Items.FirstOrDefault(i => i.V.Id == v.Id);
            Load(v.Id);
            Ctx.Toasts.Show($"{v.Number} CREATED", "Type the title, attach the consultant documents, then SUGGEST items.", ToastKind.Good);
        }
        catch (Exception ex) { Ctx.Toasts.Show("COULD NOT CREATE", ex.Message, ToastKind.Error); }
    }

    private Variation? HeaderFromEditor()
    {
        if (_current is null) return null;
        var v = _current;
        v.Type = Type; v.Date = Date ?? v.Date; v.ConsultantRef = ConsultantRef.Trim(); v.Title = VTitle.Trim(); v.Description = Description; v.Building = Building.Trim();
        v.AconexWorkflowNo = AconexWorkflowNo.Trim().ToUpperInvariant(); v.Notes = Notes;
        v.ApprovedAmount = double.TryParse(ApprovedAmount, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : null;
        return v;
    }

    [RelayCommand]
    private void Save()
    {
        var v = HeaderFromEditor();
        if (v is null) return;
        try
        {
            Store.Save(v, Lines.Select(l => l.ToEntity()).ToList());
            Ctx.Toasts.Show($"{v.Number} SAVED", $"{Lines.Count} lines, net {NetTotal}", ToastKind.Good);
        }
        catch (Raffaello.Core.Data.ConcurrencyException ex) { Ctx.Toasts.Show("CHANGED BY SOMEONE ELSE", ex.Message, ToastKind.Warn); }
        catch (InvalidOperationException ex) { Ctx.Toasts.Show("NOT SAVED", ex.Message, ToastKind.Warn); }
        LoadList();
        Load(v.Id);
    }

    [RelayCommand]
    private void Move(string? to)
    {
        if (_current is null || string.IsNullOrEmpty(to)) return;
        try
        {
            if (!IsLocked) Store.Save(HeaderFromEditor()!, Lines.Select(l => l.ToEntity()).ToList());
            var fresh = Store.Get(_current.Id)!;
            var v = Store.SetStatus(fresh, to, StatusNote.Trim());
            StatusNote = "";
            Ctx.Toasts.Show($"{v.Number}: {to}", kind: ToastKind.Good);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Raffaello.Core.Data.ConcurrencyException) { Ctx.Toasts.Show("STATUS NOT CHANGED", ex.Message, ToastKind.Warn); }
        LoadList();
        Load(_current.Id);
    }

    [RelayCommand]
    private void Delete()
    {
        if (_current is null) return;
        if (!Ctx.Dialogs.Confirm("Delete variation", $"Delete {_current.Number} and its lines? (Only drafts can be deleted.)")) return;
        try { Store.Delete(Store.Get(_current.Id)!); _current = null; HasCurrent = false; Lines.Clear(); Docs.Clear(); }
        catch (InvalidOperationException ex) { Ctx.Toasts.Show("NOT DELETED", ex.Message, ToastKind.Warn); }
        LoadList();
    }

    [RelayCommand]
    private void OpenWorkflow()
    {
        if (string.IsNullOrWhiteSpace(AconexWorkflowNo)) { Ctx.Toasts.Show("NO WORKFLOW NUMBER", "Type the Aconex workflow number of the submission."); return; }
        Ctx.Nav.Go("Aconex", new NavTarget("Aconex", Key: AconexWorkflowNo.Trim().ToUpperInvariant()));
    }

    // ------------------------------------------------------------------ documents

    [RelayCommand]
    private async Task AddDoc()
    {
        if (_current is null) return;
        var f = Ctx.Dialogs.OpenFile("Consultant document (EI / SI / VO / drawing)", "Documents|*.pdf;*.txt;*.png;*.jpg;*.jpeg;*.docx;*.xlsx|All files|*.*");
        if (f is null) return;
        var id = _current.Id;
        var folder = Path.Combine(Project.Workflow.Folders().VariationDocs, DocumentRegister.Safe(_current.Number));   // [phase6] shared documents folder
        try
        {
            var doc = await Task.Run(() => VariationDocuments.Prepare(id, f, folder));
            Store.AddDoc(doc);
            Ctx.Toasts.Show("DOCUMENT ADDED", doc.TextStatus == "TEXT" ? $"{doc.Pages} pages of text read" : $"{doc.TextStatus} - no text for suggestions (scan?)",
                doc.TextStatus == "TEXT" ? ToastKind.Good : ToastKind.Warn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Ctx.Toasts.Show("COULD NOT ADD", ex.Message, ToastKind.Error); }
        Load(id);
    }

    [RelayCommand] private void OpenDoc(VariationDoc? d) { d ??= SelectedDoc; if (d != null && File.Exists(d.Path)) DialogService.OpenWithShell(d.Path); }

    [RelayCommand]
    private void RemoveDoc(VariationDoc? d)
    {
        d ??= SelectedDoc;
        if (d is null || _current is null || IsLocked) return;
        Store.RemoveDoc(d);
        Load(_current.Id);
    }

    // ------------------------------------------------------------------ suggestions

    [RelayCommand]
    private async Task Suggest()
    {
        if (_current is null) return;
        var s = Project.Snapshot;
        var candidates = SuggestCandidate.From(s.BoqItems, s.ContractItems).ToList();
        if (candidates.Count == 0) { Ctx.Toasts.Show("NO BOQ / CONTRACT ITEMS", "Import the E-Promise BOQ or a contract first (CONTRACTS & BOQ).", ToastKind.Warn); return; }
        var title = VTitle;
        var body = Description + "\n" + string.Join("\n", Docs.Select(d => d.ExtractedText));
        Suggesting = true;
        try
        {
            var offline = await Task.Run(() => new BoqSuggester(candidates).Suggest(title, body, 25));
            var note = $"{offline.Count} suggestions from {candidates.Count:N0} items (keywords + attributes)";
            var st = Project.Settings;
            if (st.VariationsUseClaude && AnthropicClient.ResolveKey(st.AnthropicApiKey) is { } key && offline.Count > 1)
            {
                _suggestCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                try
                {
                    var client = new AnthropicClient(key) { Model = st.AnthropicModel, Effort = "low", MaxTokens = 2000, UseServerFallbacks = st.UseServerFallbacks };
                    offline = await new ClaudeRanker(client).RerankAsync(title, body, offline, _suggestCts.Token);
                    note += ", re-ranked by Claude";
                }
                catch (Exception ex) when (ex is AnthropicException or System.Net.Http.HttpRequestException or OperationCanceledException) { note += $" (Claude ranking skipped: {ex.Message})"; }
            }
            Suggestions.Clear();
            foreach (var x in offline) Suggestions.Add(x);
            SuggestNote = note;
        }
        finally { Suggesting = false; }
    }

    private void AddFromSuggestion(Suggestion? s, string kind)
    {
        if (s is null || _current is null || IsLocked) return;
        var r = new VariationLineRow
        {
            SourceKind = s.Source, SourceId = s.Candidate.Id, Kind = kind, ItemCode = s.Code, Description = s.Description, Unit = s.Unit, Rate = s.Rate, Qty = 1,
        };
        r.Changed += UpdateTotals;
        Lines.Add(r);
        SelectedLine = r;
    }

    [RelayCommand] private void AddAsAddition(Suggestion? s) => AddFromSuggestion(s, VariationLineKinds.Addition);
    [RelayCommand] private void AddAsOmission(Suggestion? s) => AddFromSuggestion(s, VariationLineKinds.Omission);

    [RelayCommand]
    private void AddNewItem()
    {
        if (_current is null || IsLocked) return;
        var r = new VariationLineRow { Kind = VariationLineKinds.NewItem, Description = "New item - describe", Unit = "no", Qty = 1, OverheadPercent = 10, ProfitPercent = 10 };
        r.Changed += UpdateTotals;
        Lines.Add(r);
        SelectedLine = r;
    }

    [RelayCommand]
    private void RemoveLine(VariationLineRow? r)
    {
        r ??= SelectedLine;
        if (r is null || IsLocked) return;
        r.Changed -= UpdateTotals;
        Lines.Remove(r);
    }

    // ------------------------------------------------------------------ exports

    private VariationHeaderInfo Info() => new(Project.Settings.Project, "MOBCO", Project.Settings.EffectiveUserName);

    [RelayCommand]
    private void ExportSubmission(string? format)
    {
        if (_current is null) return;
        var pdf = string.Equals(format, "PDF", StringComparison.OrdinalIgnoreCase);
        var v = HeaderFromEditor()!;
        var path = Ctx.Dialogs.SaveFile($"{v.Number} submission", $"{DocumentRegister.Safe(v.Number)}_{DocumentRegister.Safe(v.Title)}".TrimEnd('_') + (pdf ? ".pdf" : ".xlsx"),
            pdf ? "PDF|*.pdf" : "Excel workbook|*.xlsx");
        if (path is null) return;
        var lines = Lines.Select(l => l.ToEntity()).ToList();
        try
        {
            if (pdf) VariationExporter.SubmissionPdf(path, v, lines, Info());
            else VariationExporter.SubmissionExcel(path, v, lines, Docs.ToList(), Info());
            Ctx.Toasts.Show("SUBMISSION SAVED", Path.GetFileName(path), ToastKind.Good);
            DialogService.OpenWithShell(path);
        }
        catch (IOException ex) { Ctx.Toasts.Show("EXPORT FAILED", ex.Message + " (is the file open?)", ToastKind.Error); }
    }

    private List<ExportSheet> RegisterSheets()
    {
        var vars = Store.Variations();
        var lines = Store.AllLines();
        var docs = vars.SelectMany(v => Store.Docs(v.Id)).ToList();
        var rows = VariationExporter.RegisterRows(vars, lines, docs, Today);
        var byVar = lines.GroupBy(l => l.VariationId).ToDictionary(g => g.Key, g => VariationMath.Totals(g).Net);
        return new List<ExportSheet>
        {
            VariationExporter.RegisterSheet(rows, Today),
            VariationExporter.AgeingSheet(VariationMath.Ageing(vars, v => byVar.GetValueOrDefault(v.Id), Today)),
        };
    }

    [RelayCommand] private void ExportRegister() => Ctx.Exports.Export("VARIATIONS_REGISTER", RegisterSheets());

    [RelayCommand]
    private void ExportRegisterPdf()
    {
        var path = Ctx.Dialogs.SaveFile("Variations register (PDF)", $"VARIATIONS_REGISTER_{DateTime.Now:yyyyMMdd}.pdf", "PDF|*.pdf");
        if (path is null) return;
        try
        {
            PdfTables.Export(path, $"{Project.Settings.Project}  |  {DateTime.Now:dd-MMM-yyyy}", RegisterSheets(), a3: true);
            DialogService.OpenWithShell(path);
        }
        catch (IOException ex) { Ctx.Toasts.Show("EXPORT FAILED", ex.Message, ToastKind.Error); }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView() => RegisterSheets();

    // [assemblies] begin - NEW ITEM rate build-up from the BOQ item breakdown page
    [RelayCommand]
    private void BreakdownLine()
    {
        if (SelectedLine is not { } l || l.Description.Trim().Length == 0) { Ctx.Toasts.Show("PICK A LINE WITH A DESCRIPTION", "the breakdown reads the description", ToastKind.Warn); return; }
        AssembliesBridge.Pending = new AssembliesBridge.Request(l.Description, l.Unit, l.Qty, b =>
        {
            l.Kind = VariationLineKinds.NewItem;
            l.Material = b.Material; l.Labour = b.Labour; l.Equipment = b.Equipment;
            l.OverheadPercent = Math.Round(b.OverheadPct * 100, 4); l.ProfitPercent = Math.Round(b.ProfitPct * 100, 4);
            l.Notes = $"Rate build-up: template {b.TemplateCode}, {b.Spec.Summary}" + (b.UnknownPrices > 0 ? $"; {b.UnknownPrices} unknown prices" : "");
        }, "Variations", $"{Number}: NEW ITEM '{(l.Description.Length > 40 ? l.Description[..40] + "..." : l.Description)}'");
        Ctx.Nav.Go("Assemblies", new NavTarget("Assemblies", Key: "BRIDGE"));
    }
    // [assemblies] end
}
