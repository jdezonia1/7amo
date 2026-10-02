using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.Core.Documents;
using Raffaello.Core.Materials;

namespace Raffaello.App.ViewModels;

/// <summary>An editable header value of the document under review.</summary>
public sealed partial class ReviewField : ObservableObject
{
    public string Label { get; init; } = "";
    [ObservableProperty] private string _value = "";
    public Action<string>? Apply { get; init; }
    public string FieldName { get; init; } = "";
    [ObservableProperty] private string _issue = "";
}

/// <summary>Row of the review grid: wraps the extracted entity so edits go straight into it; failed checks are shown per row.</summary>
public abstract partial class ReviewRow : ObservableObject
{
    [ObservableProperty] private string _issue = "";
    [ObservableProperty] private string _tag = "";
    public abstract int Key { get; }
}

public sealed class PoReviewRow : ReviewRow
{
    public MatPoLine L { get; }
    public PoReviewRow(MatPoLine l) => L = l;
    public override int Key => L.LineNo;
    public int LineNo { get => L.LineNo; set => L.LineNo = value; }
    public string Description { get => L.Description; set => L.Description = value; }
    public string Unit { get => L.Unit; set => L.Unit = value; }
    public double Qty { get => L.Qty; set => L.Qty = value; }
    public double Rate { get => L.Rate; set => L.Rate = value; }
    public double Amount { get => L.Amount; set => L.Amount = value; }
    public double Check => Math.Round(L.Qty * L.Rate, 2);
    public void Touch() { OnPropertyChanged(nameof(Check)); }
}

public sealed class DnReviewRow : ReviewRow
{
    public MatDnLine L { get; }
    public DnReviewRow(MatDnLine l) => L = l;
    public override int Key => L.Order;
    public string ItemNo { get => L.ItemNo; set => L.ItemNo = value; }
    public string ItemCode { get => L.ItemCode; set => L.ItemCode = value; }
    public string Description { get => L.Description; set => L.Description = value; }
    public string Batch { get => L.Batch; set => L.Batch = value; }
    public double RawQty { get => L.RawQty; set => L.RawQty = value; }
    public string RawUnit { get => L.RawUnit; set => L.RawUnit = value; }
    public double Qty => L.Qty;
    public string Unit => L.Unit;
    public string Spec => Raffaello.Core.Coding.Fingerprints.Cable(L.Description)?.ToString() ?? "";
    public void Touch() { OnPropertyChanged(nameof(Qty)); OnPropertyChanged(nameof(Unit)); OnPropertyChanged(nameof(Spec)); }
}

public sealed class MirReviewRow : ReviewRow
{
    public MatMirEvidence E { get; }
    public MirReviewRow(MatMirEvidence e) => E = e;
    public override int Key => -1;
    public string Kind { get => E.Kind; set => E.Kind = value; }
    public int Page { get => E.Page; set => E.Page = value; }
    public string DrumNo { get => E.DrumNo; set => E.DrumNo = value; }
    public string Batch { get => E.Batch; set => E.Batch = value; }
    public string Description { get => E.Description; set => E.Description = value; }
    public double Qty { get => E.Qty; set => E.Qty = value; }
    public string Unit { get => E.Unit; set => E.Unit = value; }
    public string DnNo { get => E.DnNo; set => E.DnNo = value; }
    public string PoRef { get => E.PoRef; set => E.PoRef = value; }
}

public sealed record ReviewPage(int Number, string Source, string Kind, bool IsScan, PageImage? Image)
{
    public string Caption => $"P{Number}  {Source}{(Kind.Length > 0 ? "  " + Kind : "")}";
    public string Tag => Source is TextSource.None ? "CHECK" : Source == TextSource.Vision ? "DUE" : "OK";
}

/// <summary>
/// REVIEW screen for a PO / DN / MIR read: page image on the left, extracted header + lines on the right, failed checks highlighted,
/// inline edit, RE-CHECK runs the same validation again, ACCEPT hands the corrected document back to be saved.
/// </summary>
public sealed partial class MaterialsReviewViewModel : ObservableObject
{
    private readonly IPageRenderer _renderer;
    private readonly string _path;
    public string Kind { get; }
    public ExtractionResult<PoDocument>? Po { get; }
    public ExtractionResult<DnDocument>? Dn { get; }
    public ExtractionResult<MirDocument>? Mir { get; }

    public ObservableCollection<ReviewField> Fields { get; } = new();
    public ObservableCollection<ReviewRow> Rows { get; } = new();
    public ObservableCollection<ExtractionIssue> Issues { get; } = new();
    public ObservableCollection<ReviewPage> Pages { get; } = new();

    [ObservableProperty] private ReviewPage? _selectedPage;
    [ObservableProperty] private BitmapSource? _pageImage;
    [ObservableProperty] private string _pageText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private bool _hasImage;

    public bool IsPo => Po != null;
    public bool IsDn => Dn != null;
    public bool IsMir => Mir != null;
    public string Title => $"REVIEW {Kind}  |  {Path.GetFileName(_path)}";

    public event Action<bool>? CloseRequested;

    private MaterialsReviewViewModel(string kind, string path, IPageRenderer renderer, DocText? text, Dictionary<int, string> sources)
    {
        Kind = kind; _path = path; _renderer = renderer;
        if (text != null)
            foreach (var p in text.Pages) Pages.Add(new ReviewPage(p.Number, sources.GetValueOrDefault(p.Number, p.Source), p.Kind, p.IsScan, p.DominantImage));
    }

    public MaterialsReviewViewModel(string path, ExtractionResult<PoDocument> po, IPageRenderer renderer) : this("PURCHASE ORDER", path, renderer, po.Text, po.PageSources)
    {
        Po = po;
        var h = po.Value.Header;
        F("PO NO", h.PoNo, v => h.PoNo = v.Trim(), nameof(MatPo.PoNo));
        F("SUPPLIER", h.Supplier, v => h.Supplier = v.Trim(), nameof(MatPo.Supplier));
        F("PO DATE", h.PoDate?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) ?? "", v => h.PoDate = TextScan.ParseDate(v) ?? h.PoDate, nameof(MatPo.PoDate));
        F("SCOPE", h.Scope, v => h.Scope = v);
        F("ADVANCE %", Pct(h.AdvancePct), v => h.AdvancePct = P(v) ?? h.AdvancePct);
        F("RETENTION %", Pct(h.RetentionPct), v => h.RetentionPct = P(v) ?? h.RetentionPct);
        F("TOLERANCE HEADER %", h.ToleranceHeaderPct is double th ? Pct(th) : "", v => h.ToleranceHeaderPct = P(v));
        F("TOLERANCE CLAUSE %", h.ToleranceClausePct is double tc ? Pct(tc) : "", v => h.ToleranceClausePct = P(v));
        F("PENALTY", h.PenaltyText, v => h.PenaltyText = v);
        F("PAYMENT TERMS", h.PaymentTerms, v => h.PaymentTerms = v);
        F("STATED TOTAL", h.StatedTotal.ToString("0.00", CultureInfo.InvariantCulture), v => h.StatedTotal = Units.ParseNumber(v) ?? h.StatedTotal);
        F("VAT", h.StatedVat.ToString("0.00", CultureInfo.InvariantCulture), v => h.StatedVat = Units.ParseNumber(v) ?? h.StatedVat);
        F("GRAND TOTAL", h.StatedGrandTotal.ToString("0.00", CultureInfo.InvariantCulture), v => h.StatedGrandTotal = Units.ParseNumber(v) ?? h.StatedGrandTotal);
        foreach (var l in po.Value.Lines) Rows.Add(new PoReviewRow(l));
        AfterCheck();
    }

    public MaterialsReviewViewModel(string path, ExtractionResult<DnDocument> dn, IPageRenderer renderer) : this("DELIVERY NOTE", path, renderer, dn.Text, dn.PageSources)
    {
        Dn = dn;
        var h = dn.Value.Header;
        F("DN NO", h.DnNo, v => h.DnNo = v.Trim(), nameof(MatDn.DnNo));
        F("SUPPLIER", h.Supplier, v => h.Supplier = v.Trim());
        F("DN DATE", h.DnDate?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) ?? "", v => h.DnDate = TextScan.ParseDate(v) ?? h.DnDate);
        F("PO REF", h.PoNo, v => h.PoNo = v.Trim(), nameof(MatDn.PoNo));
        F("ORDER NO", h.OrderNo, v => h.OrderNo = v.Trim());
        foreach (var l in dn.Value.Lines) Rows.Add(new DnReviewRow(l));
        AfterCheck();
    }

    public MaterialsReviewViewModel(string path, ExtractionResult<MirDocument> mir, IPageRenderer renderer) : this("MIR", path, renderer, mir.Text, mir.PageSources)
    {
        Mir = mir;
        var h = mir.Value.Header;
        F("MIR NO", h.MirNo, v => h.MirNo = v.Trim(), nameof(MatMir.MirNo));
        F("REVISION", h.Revision, v => h.Revision = v.Trim());
        F("MIR DATE", h.MirDate?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) ?? "", v => h.MirDate = TextScan.ParseDate(v) ?? h.MirDate);
        F("MAR REF", h.MarRef, v => h.MarRef = v.Trim());
        F("DN NUMBERS (comma separated)", string.Join(", ", mir.Value.Dns.Select(d => d.DnNo)), v =>
        {
            var want = v.Split(',', ';', ' ').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            mir.Value.Dns.RemoveAll(d => !want.Contains(d.DnNo));
            foreach (var n in want.Where(n => !mir.Value.Dns.Any(d => d.DnNo == n))) mir.Value.Dns.Add(new MatMirDn { DnNo = n, Source = TextSource.Manual });
        });
        F("PO NUMBER FOR NEW DN REFS", mir.Value.Dns.Select(d => d.PoNo).FirstOrDefault(p => p.Length > 0) ?? "", v => { foreach (var d in mir.Value.Dns.Where(d => d.PoNo.Length == 0)) d.PoNo = v.Trim(); });
        foreach (var e in mir.Value.Evidence) Rows.Add(new MirReviewRow(e));
        AfterCheck();
    }

    private void F(string label, string value, Action<string> apply, string field = "") =>
        Fields.Add(new ReviewField { Label = label, Value = value, Apply = apply, FieldName = field });

    private static string Pct(double v) => (v * 100).ToString("0.##", CultureInfo.InvariantCulture);
    private static double? P(string v) => Units.ParseNumber(v.Replace("%", "")) is double d ? d / 100 : null;

    partial void OnSelectedPageChanged(ReviewPage? value) => _ = LoadPageAsync(value);

    private async Task LoadPageAsync(ReviewPage? p)
    {
        PageImage = null; HasImage = false;
        PageText = "";
        if (p is null) return;
        var text = (Po?.Text ?? Dn?.Text ?? Mir?.Text)?.Pages.FirstOrDefault(x => x.Number == p.Number);
        PageText = text?.Text ?? "";
        byte[]? bytes = p.Image?.Bytes;
        if (bytes is null || bytes.Length < 64) bytes = await _renderer.RenderPngAsync(_path, p.Number);
        if (bytes is null || bytes.Length < 64) return;
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze();
            PageImage = bmp; HasImage = true;
        }
        catch (Exception) { HasImage = false; }
    }

    public void Start()
    {
        SelectedPage = Pages.FirstOrDefault(p => !p.IsScan) ?? Pages.FirstOrDefault();
    }

    [RelayCommand]
    private void Recheck()
    {
        foreach (var f in Fields) f.Apply?.Invoke(f.Value);
        if (Po != null) { foreach (var l in Po.Value.Lines) { l.Unit = Units.Normalize(l.Unit); l.Fingerprint = Raffaello.Core.Coding.Fingerprints.Key(l.Description); } PoReader.Validate(Po); }
        if (Dn != null)
        {
            foreach (var l in Dn.Value.Lines)
            {
                l.RawUnit = Units.Normalize(l.RawUnit);
                var conv = l.RawUnit == Units.Km ? Units.Convert(l.RawQty, l.RawUnit, Units.M) : new Units.Conversion(l.RawQty, l.RawUnit, true, "");
                l.Qty = conv.Qty; l.Unit = conv.Unit; l.ConversionNote = conv.Note; l.Fingerprint = Raffaello.Core.Coding.Fingerprints.Key(l.Description);
            }
            DnReader.Validate(Dn);
        }
        if (Mir != null) MirReader.Validate(Mir);
        AfterCheck();
    }

    [RelayCommand]
    private void AddRow()
    {
        if (Po != null) { var l = new MatPoLine { LineNo = Po.Value.Lines.Select(x => x.LineNo).DefaultIfEmpty(0).Max() + 1, Unit = Units.M }; Po.Value.Lines.Add(l); Rows.Add(new PoReviewRow(l)); }
        else if (Dn != null) { var l = new MatDnLine { Order = Dn.Value.Lines.Count + 1, RawUnit = Units.M, Unit = Units.M }; Dn.Value.Lines.Add(l); Rows.Add(new DnReviewRow(l)); }
        else if (Mir != null) { var e = new MatMirEvidence { Kind = "CERT", Unit = Units.M, Source = TextSource.Manual }; Mir.Value.Evidence.Add(e); Rows.Add(new MirReviewRow(e)); }
    }

    [RelayCommand]
    private void RemoveRow()
    {
        var row = SelectedRow;
        if (row is null) return;
        switch (row)
        {
            case PoReviewRow p: Po?.Value.Lines.Remove(p.L); break;
            case DnReviewRow d: Dn?.Value.Lines.Remove(d.L); break;
            case MirReviewRow m: Mir?.Value.Evidence.Remove(m.E); break;
        }
        Rows.Remove(row);
    }

    private void AfterCheck()
    {
        var issues = Po?.Issues ?? Dn?.Issues ?? Mir?.Issues ?? new List<ExtractionIssue>();
        Issues.Clear();
        foreach (var i in issues.OrderByDescending(i => i.Level)) Issues.Add(i);
        foreach (var r in Rows)
        {
            var mine = issues.Where(i => i.Line == r.Key && r.Key >= 0).ToList();
            r.Issue = string.Join("; ", mine.Select(i => i.Message));
            r.Tag = mine.Any(i => i.Level == IssueLevel.Error) ? "ERROR" : mine.Any(i => i.Level == IssueLevel.Warn) ? "CHECK" : "OK";
            if (r is PoReviewRow p) p.Touch();
            if (r is DnReviewRow d) d.Touch();
        }
        foreach (var f in Fields) f.Issue = issues.FirstOrDefault(i => i.Field.Length > 0 && i.Field == f.FieldName)?.Message ?? "";
        var errors = issues.Count(i => i.Level == IssueLevel.Error);
        Summary = Po != null ? $"{Po.Value.Lines.Count} lines  |  sum SAR {Po.Value.LinesTotal:N2}  |  stated SAR {Po.Value.Header.StatedTotal:N2}  |  {errors} error(s), {issues.Count(i => i.Level == IssueLevel.Warn)} check(s)"
            : Dn != null ? $"{Dn.Value.Lines.Count} lines  |  {Dn.Value.Lines.Sum(l => l.Qty):N2} {Dn.Value.Lines.FirstOrDefault()?.Unit}  |  {errors} error(s), {issues.Count(i => i.Level == IssueLevel.Warn)} check(s)"
            : $"{Mir!.Value.Dns.Count} DN ref(s), {Mir.Value.Evidence.Count} evidence rows, {Mir.Value.DnReads.Count} DN(s) read inside  |  {errors} error(s)";
        HasErrors = errors > 0;
    }

    [ObservableProperty] private bool _hasErrors;
    [ObservableProperty] private ReviewRow? _selectedRow;

    [RelayCommand]
    private void Accept()
    {
        Recheck();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);
    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(4, Zoom * 1.25);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.25, Zoom / 1.25);
}
