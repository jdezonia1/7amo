using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.Core.Contracts;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using Raffaello.Core.Documents.Smart;

namespace Raffaello.App.ViewModels;

/// <summary>One extracted field on the review screen; editing it confirms it.</summary>
public sealed partial class ReviewField : ObservableObject
{
    public required FieldResult F { get; init; }
    public int Page => F.Page;
    public string Row { get; init; } = "";
    public string Name => F.Name;
    public string Alternatives => string.Join("  |  ", F.Candidates.Select(c => $"{c.Value} ({c.Engine} {c.Confidence:0.00})").Distinct());
    public string Note => F.Note;
    public double Confidence => F.Confidence;
    public string Status => F.Status;
    public bool NeedsReview => F.NeedsReview;

    public string Value
    {
        get => F.Value;
        set
        {
            if (value == F.Value) return;
            F.Value = value;
            F.Status = FieldStatus.Confirmed;
            F.Confidence = 1;
            F.Note = "edited on review";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(Confidence));
            OnPropertyChanged(nameof(NeedsReview));
            OnPropertyChanged(nameof(Stroke));
        }
    }

    /// <summary>Box colour by confidence: green sure, yellow check, dark red conflict / low.</summary>
    public Brush Stroke => F.Status == FieldStatus.Confirmed ? Brushes.SteelBlue
        : F.NeedsReview || F.Confidence < 0.75 ? new SolidColorBrush(Color.FromRgb(0x8B, 0, 0))
        : F.Confidence < 0.9 ? new SolidColorBrush(Color.FromRgb(0xE0, 0xC0, 0x00)) : new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57));

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _w;
    [ObservableProperty] private double _h;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class DiffRow : ObservableObject
{
    public required ContractItemDiff D { get; init; }
    public static string[] Choices { get; } = { "", "PDF", "EXCEL", "EDIT" };
    public string ItemNo => D.ItemNo;
    public string Field => D.Field;
    public string PdfValue => D.PdfValue;
    public string ExcelValue => D.ExcelValue;
    public string PdfInfo => D.PdfStatus.Length == 0 ? "" : $"{D.PdfStatus} {D.PdfConfidence:0.00}";
    public string Suggested => D.Suggested;
    public string Choice { get => D.Choice; set { D.Choice = value; OnPropertyChanged(); } }
    public string EditedValue { get => D.EditedValue; set { D.EditedValue = value; if (value.Length > 0) Choice = "EDIT"; OnPropertyChanged(); } }
}

public sealed record PageOption(int Number, string Caption);

/// <summary>
/// Review of a signed contract PDF before it becomes the contract record: the page with a box over every extracted field
/// (coloured by confidence), the fields (edit = confirm), the PDF vs Excel differences (choose PDF / Excel / edit), terms, clauses
/// and rules. F8 jumps through the fields that need a look only. SAVE writes the record; CONFIRM &amp; LEARN also stores the layout
/// template for this issuer.
/// </summary>
public sealed partial class ContractPdfImportViewModel : ObservableObject
{
    private readonly IPageRenderer _renderer;
    public ContractPdfReadResult Result { get; }
    public event Action<bool>? CloseRequested;
    /// <summary>Set when the window closes with SAVE (false) or CONFIRM &amp; LEARN (true).</summary>
    public bool Learn { get; private set; }

    public ContractPdfImportViewModel(ContractPdfReadResult result, IPageRenderer renderer)
    {
        Result = result;
        _renderer = renderer;
        foreach (var p in result.Document.Pages)
            Pages.Add(new PageOption(p.Number, $"p{p.Number}  {p.Kind.Type}  {p.Source}{(p.Source == TextSource.Ocr ? $" {p.Confidence:0.00}" : "")}"));
        foreach (var (name, f) in result.Body.Fields) Fields.Add(new ReviewField { F = f, Row = "HEADER" });
        foreach (var it in result.Schedule.Items)
            foreach (var f in it.Fields.Where(f => f.Status != FieldStatus.Missing || f.Name != ColumnRoles.Desc))
                Fields.Add(new ReviewField { F = f, Row = "item " + it.ItemNo });
        foreach (var d in result.Diffs) Diffs.Add(new DiffRow { D = d });
        Terms = TermsText(result);
        foreach (var c in result.Body.Clauses) Clauses.Add(c);
        foreach (var r in result.Rules) Rules.Add(r);
        ContractNo = result.ContractNo;
        UpdateSummary();
    }

    public string Title => $"CONTRACT PDF IMPORT - {Result.Document.FileName}";
    public ObservableCollection<PageOption> Pages { get; } = new();
    public ObservableCollection<ReviewField> Fields { get; } = new();
    public ObservableCollection<ReviewField> PageBoxes { get; } = new();
    public ObservableCollection<DiffRow> Diffs { get; } = new();
    public ObservableCollection<ContractClause> Clauses { get; } = new();
    public ObservableCollection<ContractRule> Rules { get; } = new();
    public string Terms { get; }
    public string[] Tabs { get; } = { "DIFFERENCES", "FIELDS", "TERMS & CLAUSES", "RULES" };

    [ObservableProperty] private string _tab = "DIFFERENCES";
    [ObservableProperty] private PageOption? _selectedPage;
    [ObservableProperty] private ImageSource? _pageImage;
    [ObservableProperty] private double _imageWidth = 1;
    [ObservableProperty] private double _imageHeight = 1;
    [ObservableProperty] private double _zoom = 0.35;
    [ObservableProperty] private ReviewField? _selectedField;
    [ObservableProperty] private DiffRow? _selectedDiff;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _contractNo = "";
    [ObservableProperty] private string _pageNote = "";

    public bool IsDiffs => Tab == "DIFFERENCES";
    public bool IsFields => Tab == "FIELDS";
    public bool IsTerms => Tab == "TERMS & CLAUSES";
    public bool IsRules => Tab == "RULES";
    partial void OnTabChanged(string value) { OnPropertyChanged(nameof(IsDiffs)); OnPropertyChanged(nameof(IsFields)); OnPropertyChanged(nameof(IsTerms)); OnPropertyChanged(nameof(IsRules)); }
    partial void OnContractNoChanged(string value) => Result.ContractNo = value.Trim();

    public void Start() => SelectedPage = Pages.FirstOrDefault(p => Result.Document.Pages.First(x => x.Number == p.Number).Kind.Type == DocTypes.RateSchedule) ?? Pages.FirstOrDefault();

    partial void OnSelectedPageChanged(PageOption? value) => _ = LoadPageAsync(value);

    partial void OnSelectedFieldChanged(ReviewField? oldValue, ReviewField? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue is null) return;
        newValue.IsSelected = true;
        if (SelectedPage?.Number != newValue.Page && newValue.Page > 0) SelectedPage = Pages.FirstOrDefault(p => p.Number == newValue.Page);
    }

    partial void OnSelectedDiffChanged(DiffRow? value)
    {
        if (value is null) return;
        var f = Fields.FirstOrDefault(x => x.Row == "item " + value.ItemNo && x.Name == (value.Field switch { "QTY" => ColumnRoles.Qty, "RATE" => ColumnRoles.Rate, "UNIT" => ColumnRoles.Unit, "DESCRIPTION" => ColumnRoles.Desc, _ => ColumnRoles.No }))
                ?? Fields.FirstOrDefault(x => x.Row == "item " + value.ItemNo);
        if (f != null) SelectedField = f;
    }

    private async Task LoadPageAsync(PageOption? p)
    {
        PageBoxes.Clear();
        if (p is null) { PageImage = null; return; }
        var page = Result.Document.Pages.First(x => x.Number == p.Number);
        byte[]? png = page.Ocr?.ImagePng;
        double w = page.Ocr?.Width ?? 0, h = page.Ocr?.Height ?? 0;
        if (png is null)
        {
            png = await _renderer.RenderPngAsync(Result.Document.Path, p.Number);
            w = page.Base.WidthPt; h = page.Base.HeightPt;   // text-layer boxes are in points
        }
        if (png is null) { PageImage = null; PageNote = "no page image"; return; }
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit();
        }
        bmp.Freeze();
        PageImage = bmp;
        ImageWidth = bmp.PixelWidth; ImageHeight = bmp.PixelHeight;
        var sx = w > 0 ? bmp.PixelWidth / w : 1; var sy = h > 0 ? bmp.PixelHeight / h : 1;
        foreach (var f in Fields.Where(f => f.Page == p.Number && f.F.Box is { W: > 0 }))
        {
            var b = f.F.Box!.Value;
            f.X = b.X * sx; f.Y = b.Y * sy; f.W = b.W * sx; f.H = b.H * sy;
            PageBoxes.Add(f);
        }
        PageNote = string.Join("; ", page.Notes);
    }

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(2, Zoom * 1.25);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.1, Zoom / 1.25);

    /// <summary>F8: the next field that needs a look (conflict, derived, low confidence, missing).</summary>
    [RelayCommand]
    private void NextLow()
    {
        var list = Fields.ToList();
        var start = SelectedField is null ? -1 : list.IndexOf(SelectedField);
        for (var k = 1; k <= list.Count; k++)
        {
            var f = list[(start + k) % list.Count];
            if (f.NeedsReview) { SelectedField = f; Tab = "FIELDS"; return; }
        }
        Summary = "No field left to review.";
    }

    [RelayCommand]
    private void AcceptSuggestions()
    {
        ContractPdfImport.AcceptSuggestions(Result);
        foreach (var d in Diffs) d.Choice = d.D.Choice;
        UpdateSummary();
    }

    /// <summary>Re-runs the checks after edits: items from the edited fields, differences against the Excel again (choices kept).</summary>
    [RelayCommand]
    private void Recheck()
    {
        var choices = Result.Diffs.ToDictionary(d => d.ItemNo + "|" + d.Field, d => (d.Choice, d.EditedValue));
        foreach (var it in Result.Schedule.Items) { it.Issues.Remove("qty x rate does not match the total"); if (!it.ArithmeticOk) it.Issues.Add("qty x rate does not match the total"); }
        Result.PdfItems.Clear();
        Result.PdfItems.AddRange(Result.Schedule.ToContractItems(Result.ContractNo));
        ContractPdfImport.Compare(Result);
        foreach (var d in Result.Diffs)
            if (choices.TryGetValue(d.ItemNo + "|" + d.Field, out var c)) { d.Choice = c.Choice; d.EditedValue = c.EditedValue; }
        Diffs.Clear();
        foreach (var d in Result.Diffs) Diffs.Add(new DiffRow { D = d });
        UpdateSummary();
    }

    private void UpdateSummary() =>
        Summary = $"{Result.Summary}  |  {Fields.Count(f => f.NeedsReview)} field(s) to review (F8)  |  {Result.Undecided} difference(s) undecided";

    [RelayCommand] private void Save() => Close(learn: false);
    [RelayCommand] private void SaveAndLearn() => Close(learn: true);
    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);

    private void Close(bool learn)
    {
        if (Result.ContractNo.Length == 0) { Summary = "Enter the contract number first."; return; }
        if (Result.Undecided > 0) { Summary = $"{Result.Undecided} difference(s) still need a choice (PDF / EXCEL / EDIT) - or ACCEPT SUGGESTIONS."; Tab = "DIFFERENCES"; return; }
        Learn = learn;
        CloseRequested?.Invoke(true);
    }

    private static string TermsText(ContractPdfReadResult r)
    {
        var t = r.Body.Terms;
        string D(DateTime? d) => d?.ToString("dd-MMM-yyyy") ?? "-";
        return string.Join("\n", new[]
        {
            $"Contract no:  {t.ContractNo}", $"Contract date:  {D(t.ContractDate)}    signed:  {D(t.SignedDate)}",
            $"Main contractor:  {t.FirstParty}  (CR {t.FirstPartyCr})", $"Subcontractor:  {t.Subcontractor}  (CR {t.SubcontractorCr})  {t.SubcontractorContact}",
            $"Scope:  {t.Scope}    labour only: {(t.LabourOnly ? "YES" : "NO")}", $"VAT:  {t.VatTreatment} {t.VatPct:P0}",
            $"Payment (outlets):  {t.PaymentTerms}", $"Payment (tray / pulling / panels):  {t.TrayPaymentTerms}",
            $"Retention:  {(t.RetentionPct is double rp ? rp.ToString("P0") : "not in the contract")}    advance:  {(t.AdvancePct is double ap ? ap.ToString("P0") : "-")}",
            $"Delay penalty:  {(t.DelayPenaltyPerWeek is double pw ? $"SAR {pw:N0} / week" : "-")}    cap:  {(t.DelayPenaltyCapPct is double pc ? pc.ToString("P0") : "-")}",
            $"Warranty:  {(t.WarrantyMonths is int wm ? wm + " months" : "-")}",
        });
    }
}
