using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Contracts;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;

namespace Raffaello.App.ViewModels;

public sealed class ContractRow
{
    public required Contract Contract { get; init; }
    public double Claimed { get; init; }
    public double Certified { get; init; }
    public double CertifiedPct => Contract.Value <= 0 ? 0 : Certified / Contract.Value;
    public double Retention => Certified * Contract.RetentionPct;
    public string Status => CertifiedPct > 1 ? "OVER" : "OK";
}

public sealed partial class BoqRow : ObservableObject
{
    public required BoqItem Item { get; init; }
    public double QsTotal { get; init; }
    public double Claimed { get; init; }
    [ObservableProperty] private double? _projectQty;
    [ObservableProperty] private double _rate;
    [ObservableProperty] private bool _isDirty;
    public string ItemCode => Item.ItemCode;
    public string Bill => Item.Bill;
    public string Description => Item.Description;
    public string System => Item.System;
    public string Stage => Item.Stage;
    public string Unit => Item.Unit;
    public double BoqQty => Item.BoqQty;
    public double Value => (ProjectQty ?? BoqQty) * Rate;
    public string Status => ProjectQty is null ? "DUE" : Claimed > ProjectQty + 0.0001 ? "OVER" : "OK";
    partial void OnProjectQtyChanged(double? value) { IsDirty = true; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Value)); }
    partial void OnRateChanged(double value) { IsDirty = true; OnPropertyChanged(nameof(Value)); }
}

public sealed class ContractItemRow
{
    public required ContractItem Item { get; init; }
    public int Links { get; init; }
    public string Status => Item.AttributesConfirmed ? "OK" : Item.ParseNotes.Length > 0 || Links == 0 ? "CHECK" : "DUE";
}

/// <summary>
/// Contracts: import the contract link workbook (items + BOQ links), the E-Promise budget list and the invoice template;
/// review and confirm item attributes; BOQ rates and PROJECT QTY.
/// </summary>
public sealed partial class ContractsViewModel : PageViewModel
{
    public const int BoqPageSize = 400;

    public ContractsViewModel(PageContext ctx, Services.SmartReading reading, Raffaello.Core.Documents.IPageRenderer renderer) : base(ctx) { _reading = reading; _renderer = renderer; }

    public override string Key => "Contracts";
    public override string Title => "CONTRACTS & BOQ";
    public override string Subtitle => "Contract items with their rate-driving attributes and BOQ links; E-Promise BOQ codes; rates and PROJECT QTY";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => true;   // [phase6] the building switcher drives it

    public ObservableCollection<ContractRow> Contracts { get; } = new();
    public ObservableCollection<BoqRow> Boq { get; } = new();
    public ObservableCollection<ContractItemRow> Items { get; } = new();
    public ObservableCollection<ContractItemBoq> ItemLinks { get; } = new();
    public ObservableCollection<string> ContractNos { get; } = new();
    public ObservableCollection<Attachment> ContractDocs { get; } = new();
    public string[] Tabs { get; } = { "CONTRACT ITEMS", "TERMS, RULES & OBLIGATIONS", "COMPARE CONTRACTS", "BOQ" };
    public static string[] FixStageOptions { get; } = { "", FixStages.First, FixStages.Second, FixStages.Third };
    public static string[] ConduitOptions { get; } = { "", Conduits.Pvc, Conduits.Emt, Conduits.Rs, Conduits.Flex, Conduits.None };
    public static string[] MountOptions { get; } = { "", Mounts.Wall, Mounts.Ceiling, Mounts.Both };
    public static string[] HeightOptions { get; } = { HeightBands.Any, HeightBands.Low, HeightBands.High };
    public string[] BuildingOptions { get; } = { "", Buildings.Branded, Buildings.Hotel };

    [ObservableProperty] private string _tab = "CONTRACT ITEMS";
    [ObservableProperty] private string _coverage = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _itemSearch = "";
    [ObservableProperty] private string _selectedContractNo = "";
    [ObservableProperty] private ContractItemRow? _selectedItem;
    [ObservableProperty] private string _itemsText = "";
    [ObservableProperty] private Attachment? _selectedDoc;

    // import fields
    [ObservableProperty] private string _importContractNo = "";
    [ObservableProperty] private string _importSub = "";
    [ObservableProperty] private string _importBuilding = "";

    public bool IsItems => Tab == "CONTRACT ITEMS";
    public bool IsBoq => Tab == "BOQ";

    partial void OnTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsItems)); OnPropertyChanged(nameof(IsBoq)); OnPropertyChanged(nameof(IsTerms)); OnPropertyChanged(nameof(IsCompare));
        if (IsTerms) FillTerms();
        if (IsCompare) FillCompare();
    }
    partial void OnSearchChanged(string value) => FillBoq();
    partial void OnItemSearchChanged(string value) => FillItems();
    partial void OnSelectedContractNoChanged(string value) { FillItems(); var c = Project.Snapshot.Contracts.FirstOrDefault(x => x.ContractNo == value); if (c != null) { ImportContractNo = c.ContractNo; ImportSub = c.Subcontractor; ImportBuilding = c.Building; } }
    partial void OnSelectedItemChanged(ContractItemRow? value)
    {
        ItemLinks.Clear();
        if (value is null) return;
        foreach (var l in Project.Snapshot.ItemBoqs.Where(l => l.ContractNo == value.Item.ContractNo && l.ItemNo == value.Item.ItemNo).OrderBy(l => l.Order)) ItemLinks.Add(l);
    }

    protected override void Refresh()
    {
        var p = Project;
        var s = p.Snapshot;
        var chainBySub = p.Chain.SelectMany(r => r.Subcontractors.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(sub => (sub, r))).GroupBy(x => x.sub).ToDictionary(g => g.Key, g => (Claimed: g.Sum(x => x.r.ClaimedValue), Certified: g.Sum(x => x.r.CertifiedValue)));
        Contracts.Clear();
        foreach (var c in s.Contracts.OrderBy(c => c.ContractNo))
        {
            var t = chainBySub.GetValueOrDefault(c.Subcontractor);
            Contracts.Add(new ContractRow { Contract = c, Claimed = t.Claimed, Certified = t.Certified });
        }
        var inBuilding = s.Contracts.Where(c => InBuilding(c.Building)).Select(c => c.ContractNo).ToHashSet();
        var nos = s.ContractItems.Select(i => i.ContractNo).Distinct().Where(n => BuildingFilter is null || inBuilding.Contains(n) || s.Contracts.All(c => c.ContractNo != n)).OrderBy(x => x).ToList();
        if (!ContractNos.SequenceEqual(nos)) { ContractNos.Clear(); foreach (var n in nos) ContractNos.Add(n); }
        if (!ContractNos.Contains(SelectedContractNo)) SelectedContractNo = ContractNos.FirstOrDefault() ?? "";
        else FillItems();
        FillBoq();
        if (IsTerms) FillTerms();
        if (IsCompare) FillCompare();
    }

    private void FillDocs()
    {
        ContractDocs.Clear();
        foreach (var a in Project.Workflow.AttachmentsOf(AttachmentKinds.Contract, SelectedContractNo)) ContractDocs.Add(a);
    }

    [RelayCommand]
    private async Task AttachContractDoc()
    {
        if (SelectedContractNo.Length == 0) { Ctx.Toasts.Show("PICK A CONTRACT FIRST", kind: ToastKind.Warn); return; }
        var files = Ctx.Dialogs.OpenFiles($"{SelectedContractNo}: contract documents (PDF / Excel)", "Documents|*.pdf;*.xlsx;*.xls;*.docx|All files|*.*");
        if (files is null) return;
        var no = SelectedContractNo;
        await Ctx.Data.WriteAsync(p => { foreach (var f in files) p.Workflow.AddAttachment(AttachmentKinds.Contract, no, AttachmentKinds.Contract, f); }, Ctx.Toasts, $"{files.Length} DOCUMENT(S) ATTACHED");
    }

    [RelayCommand]
    private async Task RemoveContractDoc()
    {
        if (SelectedDoc is not { } a) return;
        if (!Ctx.Dialogs.Confirm("Remove document", $"Remove {a.FileName} from {a.OwnerKey}? (The file on disk is not deleted.)")) return;
        await Ctx.Data.WriteAsync(p => p.Workflow.RemoveAttachment(a), Ctx.Toasts, "DOCUMENT REMOVED");
    }

    private void FillItems()
    {
        FillDocs();
        var keep = SelectedItem?.Item.ItemNo;
        var s = Project.Snapshot;
        var links = s.ItemBoqs.Where(l => l.ContractNo == SelectedContractNo).GroupBy(l => l.ItemNo).ToDictionary(g => g.Key, g => g.Count());
        Items.Clear();
        var all = s.ContractItems.Where(i => i.ContractNo == SelectedContractNo).OrderBy(i => i.Order).ToList();
        foreach (var i in all.Where(i => string.IsNullOrWhiteSpace(ItemSearch) || i.ItemNo.Equals(ItemSearch.Trim(), StringComparison.OrdinalIgnoreCase)
                                          || i.Description.Contains(ItemSearch, StringComparison.OrdinalIgnoreCase) || i.Systems.Contains(ItemSearch, StringComparison.OrdinalIgnoreCase)))
            Items.Add(new ContractItemRow { Item = i, Links = links.GetValueOrDefault(i.ItemNo) });
        ItemsText = all.Count == 0 ? "No contract items - import the contract link workbook." :
            $"{all.Count} ITEMS  |  {all.Count(i => i.AttributesConfirmed)} CONFIRMED  |  {all.Count(i => !links.ContainsKey(i.ItemNo))} WITHOUT BOQ CODE  |  VALUE SAR {all.Sum(i => i.Qty * i.Rate):N0}";
        SelectedItem = Items.FirstOrDefault(r => r.Item.ItemNo == keep) ?? Items.FirstOrDefault();
    }

    private void FillBoq()
    {
        var p = Project;
        var s = p.Snapshot;
        var chain = p.Chain.GroupBy(r => (r.Building, r.ItemCode)).ToDictionary(g => g.Key, g => (Qs: g.Sum(r => r.Qs), Claimed: g.Sum(r => r.Claimed)));
        var q = s.BoqItems.Where(b => string.IsNullOrWhiteSpace(Search) || b.ItemCode.Contains(Search, StringComparison.OrdinalIgnoreCase) || b.Description.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                      || b.CostCode.Contains(Search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.ItemCode).ToList();
        Boq.Clear();
        foreach (var b in q.Take(BoqPageSize))
        {
            var (building, code) = SplitCode(b.ItemCode);
            var t = chain.GetValueOrDefault((building, code));
            var row = new BoqRow { Item = b, QsTotal = t.Qs, Claimed = t.Claimed };
            row.ProjectQty = b.ProjectQty;
            row.Rate = b.Rate;
            row.IsDirty = false;
            Boq.Add(row);
        }
        Coverage = $"{s.BoqItems.Count:N0} BOQ CODES  |  SHOWING {Boq.Count:N0} OF {q.Count:N0}{(q.Count > BoqPageSize ? " - TYPE TO SEARCH" : "")}  |  WITH PROJECT QTY {s.BoqItems.Count(b => b.ProjectQty.HasValue):N0}";
    }

    private static (string Building, string Code) SplitCode(string itemCode) =>
        itemCode.StartsWith("H-") ? (Buildings.Hotel, itemCode[2..]) : itemCode.StartsWith("B-") ? (Buildings.Branded, itemCode[2..]) : (Buildings.Branded, itemCode);

    [RelayCommand]
    private async Task ConfirmItem()
    {
        if (SelectedItem is null) return;
        var item = SelectedItem.Item;
        await Ctx.Data.WriteAsync(p => p.Workflow.ConfirmItem(item), Ctx.Toasts, $"ITEM {item.ItemNo} CONFIRMED");
    }

    [RelayCommand]
    private async Task ImportLinkWorkbook()
    {
        if (string.IsNullOrWhiteSpace(ImportContractNo) || string.IsNullOrWhiteSpace(ImportSub)) { Ctx.Toasts.Show("CONTRACT NO AND SUBCONTRACTOR NEEDED", "The link workbook does not carry them.", ToastKind.Warn); return; }
        var file = Ctx.Dialogs.OpenFile("Contract link workbook (contract sheet + LINK TABLE)");
        if (file is null) return;
        var (no, sub, bld) = (ImportContractNo.Trim(), ImportSub.Trim().ToUpperInvariant(), string.IsNullOrWhiteSpace(ImportBuilding) ? null : ImportBuilding);
        try
        {
            var r = await Task.Run(() => Project.Workflow.PreviewContract(file, no, sub, bld));
            if (!Ctx.Dialogs.Confirm("Import contract", $"{no} - {sub}\n\n{r.Summary}\nValue SAR {r.Items.Sum(i => i.Qty * i.Rate):N0}\n\n{Issues(r.Issues)}\n\nReplace this contract's items and links? Confirmed attributes are kept.")) return;
            if (await Ctx.Data.WriteAsync(p => p.Workflow.CommitContract(r), Ctx.Toasts, "CONTRACT IMPORTED")) SelectedContractNo = no;
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ WORKBOOK", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task ImportEPromise()
    {
        var file = Ctx.Dialogs.OpenFile("Workbook with the 'E promise' budget sheet");
        if (file is null) return;
        try
        {
            var r = await Task.Run(() => Project.Workflow.PreviewEPromise(file));
            if (!Ctx.Dialogs.Confirm("Import E-Promise", $"{r.Summary}\n\nAdd / update these BOQ codes?")) return;
            var n = 0;
            if (await Ctx.Data.WriteAsync(p => n = p.Workflow.CommitEPromise(r), Ctx.Toasts)) Ctx.Toasts.Show("BOQ CODES IMPORTED", $"{n:N0} added / updated", ToastKind.Good);
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ WORKBOOK", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task ImportTemplate()
    {
        if (string.IsNullOrWhiteSpace(ImportContractNo)) { Ctx.Toasts.Show("CONTRACT NO NEEDED", kind: ToastKind.Warn); return; }
        var file = Ctx.Dialogs.OpenFile("Invoice workbook (INV sheet = template layout)");
        if (file is null) return;
        var no = ImportContractNo.Trim();
        try
        {
            var r = await Task.Run(() => Project.Workflow.PreviewTemplate(file, no));
            if (!Ctx.Dialogs.Confirm("Import invoice template", $"{no}\n\n{r.Summary}\n\n{Issues(r.Issues)}\n\nUse this as the invoice layout for {no}? Stage % comes from the template.")) return;
            await Ctx.Data.WriteAsync(p => p.Workflow.CommitTemplate(r, no), Ctx.Toasts, "INVOICE TEMPLATE IMPORTED");
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ WORKBOOK", ex.Message, ToastKind.Error); }
    }

    private static string Issues(IEnumerable<Raffaello.Core.Import.ImportIssue> issues) =>
        string.Join("\n", issues.GroupBy(i => System.Text.RegularExpressions.Regex.Replace(i.Message, @"\d+", "#")).Take(8).Select(g => $"- {g.First().Message}{(g.Count() > 1 ? $" (x{g.Count()})" : "")}"));

    [RelayCommand]
    private async Task Save()
    {
        var dirty = Boq.Where(b => b.IsDirty).ToList();
        if (dirty.Count == 0) { Ctx.Toasts.Show("NOTHING TO SAVE"); return; }
        foreach (var b in dirty)
        {
            b.Item.ProjectQty = b.ProjectQty;
            b.Item.Rate = b.Rate;
            var item = b.Item;
            if (!await Ctx.Data.WriteAsync(p => p.UpdateBoq(item), Ctx.Toasts)) return;
        }
        Ctx.Toasts.Show("BOQ SAVED", $"{dirty.Count} items", ToastKind.Good);
    }

    [RelayCommand]
    private async Task ApplyToLines()
    {
        // pushes each BOQ item's PROJECT QTY down to its lines pro-rata to QS, filling empty caps only
        var targets = Boq.Where(b => b.ProjectQty.HasValue && b.QsTotal > 0).ToList();
        var updates = new List<QtyLine>();
        foreach (var b in targets)
        {
            var (building, code) = SplitCode(b.ItemCode);
            foreach (var r in Project.Chain.Where(r => r.Building == building && r.ItemCode == code && !r.ProjectQty.HasValue))
            {
                r.Line.ProjectQty = Math.Floor(r.Qs * b.ProjectQty!.Value / b.QsTotal);
                updates.Add(r.Line);
            }
        }
        if (updates.Count == 0) { Ctx.Toasts.Show("NOTHING TO APPLY", "Every line already has a PROJECT QTY."); return; }
        if (!Ctx.Dialogs.Confirm("Apply PROJECT QTY", $"Fill PROJECT QTY on {updates.Count} lines that are empty, pro-rata to QS?")) { await Ctx.Data.ReloadAsync(); return; }
        await Ctx.Data.WriteAsync(p => p.FillProjectQty(updates), Ctx.Toasts, $"PROJECT QTY filled on {updates.Count} lines");
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        yield return new ExportSheet
        {
            Name = "BOQ", Title = "BOQ - RATES AND PROJECT QTY",
            Columns = new() { new("ITEM"), new("BILL"), new("DESCRIPTION", Width: 40), new("SYSTEM"), new("STAGE"), new("UNIT"), new("BOQ QTY", ColumnKind.Integer), new("QS (LINES)", ColumnKind.Integer), new("PROJECT QTY", ColumnKind.Integer), new("CLAIMED", ColumnKind.Integer), new("RATE", ColumnKind.Money), new("VALUE", ColumnKind.Money), new("STATUS") },
            Rows = Boq.Select(b => new object?[] { b.ItemCode, b.Bill, b.Description, b.System, b.Stage, b.Unit, b.BoqQty, b.QsTotal, b.ProjectQty, b.Claimed, b.Rate, b.Value, b.Status }).ToList(),
            TotalRow = new object?[] { "TOTAL", null, null, null, null, null, null, null, null, null, null, Boq.Sum(b => b.Value), null },
        };
        yield return new ExportSheet
        {
            Name = "CONTRACT ITEMS", Title = $"CONTRACT ITEMS - {SelectedContractNo}",
            Columns = new() { new("ITEM"), new("SECTION"), new("DESCRIPTION", Width: 60), new("UNIT"), new("QTY", ColumnKind.Number), new("RATE", ColumnKind.Money), new("STAGE"), new("CONDUIT"), new("MOUNT"),
                new("HEIGHT"), new("SYSTEMS"), new("CATEGORY"), new("SIZE"), new("STAGE %", ColumnKind.Percent), new("BOQ LINKS", ColumnKind.Integer), new("CONFIRMED"), new("PARSE NOTES", Width: 50) },
            Rows = Items.Select(r => new object?[] { r.Item.ItemNo, r.Item.Section, r.Item.Description, r.Item.Unit, r.Item.Qty, r.Item.Rate, r.Item.FixStage, r.Item.ConduitType, r.Item.Mount,
                r.Item.HeightBand, r.Item.Systems, r.Item.Category, r.Item.SizeKey, r.Item.StagePct, r.Links, r.Item.AttributesConfirmed ? "YES" : "", r.Item.ParseNotes }).ToList(),
        };
        yield return new ExportSheet
        {
            Name = "CONTRACTS", Title = "SUBCONTRACTS",
            Columns = new() { new("CONTRACT"), new("SUBCONTRACTOR"), new("SCOPE", Width: 40), new("VALUE", ColumnKind.Money), new("CLAIMED", ColumnKind.Money), new("CERTIFIED", ColumnKind.Money), new("CERTIFIED %", ColumnKind.Percent), new("RETENTION", ColumnKind.Money) },
            Rows = Contracts.Select(c => new object?[] { c.Contract.ContractNo, c.Contract.Subcontractor, c.Contract.Scope, c.Contract.Value, c.Claimed, c.Certified, c.CertifiedPct, c.Retention }).ToList(),
        };
    }

    // [assemblies] begin
    [RelayCommand]
    private void BreakdownItem()
    {
        if (SelectedItem is not { } r) { Ctx.Toasts.Show("PICK A CONTRACT ITEM", kind: ToastKind.Warn); return; }
        Ctx.Nav.Go("Assemblies", new Raffaello.Core.Queue.NavTarget("Assemblies", r.Item.Id, Raffaello.Core.Assemblies.SourceKinds.Contract));
    }
    // [assemblies] end
}
