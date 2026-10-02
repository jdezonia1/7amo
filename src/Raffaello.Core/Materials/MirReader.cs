using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Raffaello.Core.Documents;

namespace Raffaello.Core.Materials;

public static class MirPageKind
{
    public const string Form = "MIR FORM";
    public const string Mar = "MAR";
    public const string Checklist = "CHECKLIST";
    public const string QtyList = "QTY LIST";
    public const string DnText = "DN";
    public const string DnPhoto = "DN PHOTO";
    public const string Cert = "TEST CERT";
    public const string Label = "DRUM LABEL";
    public const string Other = "OTHER";
}

public sealed class MirDocument
{
    public MatMir Header { get; init; } = new();
    public List<MatMirDn> Dns { get; } = new();
    public List<MatMirEvidence> Evidence { get; } = new();
    /// <summary>DNs read from DN pages / photos inside the MIR (can be saved as DNs).</summary>
    public List<ExtractionResult<DnDocument>> DnReads { get; } = new();
}

/// <summary>Which scan / photo pages to send to the cloud reader (each page is a paid request).</summary>
public sealed class MirReadOptions
{
    public bool ReadDnPhotos { get; init; } = true;
    public bool ReadCertificates { get; init; } = true;
    public bool ReadLabels { get; init; } = true;
    public IProgress<string>? Progress { get; init; }
}

/// <summary>
/// Reads a MIR bundle: MIR no / rev / date, MAR ref, DN and PO refs, the required-quantity list, test certificates (drum no + qty)
/// and drum-label batches. Text pages are parsed locally; photos / scans are classified (OCR keywords, or position and size when
/// there is no OCR) and read with OCR or Claude vision when enabled.
/// </summary>
public static class MirReader
{
    private static readonly Regex MirNoRx = new(@"\b([A-Z0-9]+(?:-[A-Z0-9]+)*-MIR-[A-Z]{1,4}-\d{3,8}|MIR-[A-Z]{1,4}-\d{3,8})\b", RegexOptions.Compiled);
    private static readonly Regex DnRefRx = new(@"\b(?:DN|D\.N\.?|Delivery\s+Note)\s*(?:No\.?|#|:)?\s*:?\s*(\d{6,10})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PoRx = new(@"\b([A-Z]{2,6}-P\.?O\.?-[A-Z0-9]+(?:-[A-Z0-9]+)+)\b", RegexOptions.Compiled);
    private static readonly Regex QtyRow = new(@"^\s*(?<no>\d{1,3})\s+(?<size>\d{1,2}\s*[xX]\s*\d{1,4}(?:\.\d+)?)\s+(?<desc>\S.*?)\s{2,}(?<qty>[\d,]+(?:\.\d+)?)(?:\s|$)", RegexOptions.Compiled);

    public static async Task<ExtractionResult<MirDocument>> ReadAsync(string path, ReaderOptions? options = null, MirReadOptions? mir = null, CancellationToken ct = default)
    {
        options ??= ReaderOptions.Default;
        mir ??= new MirReadOptions();
        var text = await ReaderPipeline.ReadPdfAsync(path, options, ct).ConfigureAwait(false);
        var res = Parse(text);
        if (!options.CanUseVision)
        {
            var scans = text.Pages.Count(p => p.IsScan && p.Source != TextSource.Ocr);
            if (scans > 0) res.Info("VISION_OFF", $"{scans} photo / scan page(s) not read ({options.Vision?.Status ?? "cloud reading is off"}) - add DN refs and drum quantities by hand or enable cloud reading");
            return res;
        }
        foreach (var p in text.Pages.Where(p => p.IsScan && p.Source != TextSource.Ocr))
        {
            ct.ThrowIfCancellationRequested();
            var kind = p.Kind.TrimEnd('?');
            var want = kind switch { MirPageKind.DnPhoto => mir.ReadDnPhotos, MirPageKind.Cert => mir.ReadCertificates, MirPageKind.Label => mir.ReadLabels, _ => false };
            if (!want) continue;
            mir.Progress?.Report($"Reading page {p.Number} ({kind}) with Claude...");
            var (bytes, media) = ReaderPipeline.PageContent(path, p);
            try
            {
                if (kind == MirPageKind.DnPhoto)
                {
                    var dn = new ExtractionResult<DnDocument> { Value = new DnDocument(), FileName = $"{text.FileName} p{p.Number}" };
                    await DnReader.VisionAsync(dn, p.Number, bytes, media, options.Vision!, ct).ConfigureAwait(false);
                    MergeDn(res, dn, p.Number);
                }
                else
                {
                    var json = await options.Vision!.ExtractAsync(new VisionRequest
                    {
                        DocumentKind = kind == MirPageKind.Cert ? "cable test certificate" : "cable drum label photo", Page = p.Number, Content = bytes, MediaType = media, Schema = EvidenceSchema,
                        Instructions = kind == MirPageKind.Cert
                            ? "List every drum / batch on this test certificate: drum no, batch no, cable description (cores x size and type), tested length and unit, and the PO / order reference printed on it."
                            : "Read the drum label: batch no, drum no, cable description, length and unit. Return one item per label visible.",
                    }, ct).ConfigureAwait(false);
                    if (json != null) ApplyEvidence(res, p.Number, kind == MirPageKind.Cert ? "CERT" : "LABEL", json);
                }
                res.PageSources[p.Number] = TextSource.Vision;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                res.Warn("VISION_PAGE", $"page {p.Number}: cloud reading failed - {ex.Message}");
            }
        }
        Validate(res);
        return res;
    }

    public static ExtractionResult<MirDocument> Parse(DocText text)
    {
        var doc = new MirDocument();
        var res = new ExtractionResult<MirDocument> { Value = doc, FileName = text.FileName, Text = text };
        var h = doc.Header;
        h.SourceFile = text.FileName;
        h.ImportedAt = DateTime.Now;
        Classify(text);
        foreach (var p in text.Pages) res.PageSources[p.Number] = p.Source;
        h.PageKinds = string.Join("; ", text.Pages.GroupBy(p => p.Kind).Select(g => $"{g.Key} {Ranges(g.Select(p => p.Number))}"));

        var formLines = text.Pages.Where(p => p.Kind == MirPageKind.Form).SelectMany(p => p.Lines).ToList();
        if (formLines.Count == 0) formLines = text.AllLines.ToList();
        var formText = string.Join("\n", formLines);
        var mm = MirNoRx.Match(formText);
        if (mm.Success) h.MirNo = mm.Groups[1].Value;
        else res.Error("MIR_NO", "MIR number not found", field: nameof(MatMir.MirNo));
        h.Revision = TextScan.LabelValue(formLines, @"Revision\s*:") ?? "00";
        h.MirDate = TextScan.ParseDate(TextScan.LabelValue(formLines, @"\bDate\s*:"));
        var mar = Regex.Match(formText, @"Material\s+Approval\s+Request\s+Submittal\s+Ref\s*:?\s*(\S+)", RegexOptions.IgnoreCase);
        if (mar.Success) h.MarRef = mar.Groups[1].Value;
        var desc = Regex.Match(formText, @"(?m)^\s*1\s{3,}(\S.*?)(?:\s{2,}|$)");
        if (desc.Success) h.Description = desc.Groups[1].Value.Trim();
        var sup = Regex.Match(text.AllText, @"Supplier\s*:\s*(\S.*?)(?:\s{2,}|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (sup.Success) h.Supplier = sup.Groups[1].Value.Trim();

        var all = text.AllText;
        foreach (Match m in DnRefRx.Matches(all))
            if (!doc.Dns.Any(d => d.DnNo == m.Groups[1].Value)) doc.Dns.Add(new MatMirDn { DnNo = m.Groups[1].Value, Source = TextSource.TextLayer });
        var pos = PoRx.Matches(all).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (pos.Count == 1) foreach (var d in doc.Dns.Where(d => d.PoNo.Length == 0)) d.PoNo = pos[0];

        foreach (var p in text.Pages.Where(p => p.Kind == MirPageKind.QtyList))
            foreach (var l in p.Lines)
            {
                var q = QtyRow.Match(l);
                if (!q.Success) continue;
                doc.Evidence.Add(new MatMirEvidence
                {
                    Kind = "REQUIRED", Page = p.Number, Description = Regex.Replace($"{q.Groups["size"].Value} {q.Groups["desc"].Value}", @"\s+", " ").Trim(),
                    Qty = Units.ParseNumber(q.Groups["qty"].Value) ?? 0, Unit = Units.M, Source = p.Source,
                });
            }
        // DN pages with a text layer inside the bundle, and DN photos read by the offline OCR
        foreach (var p in text.Pages.Where(p => p.Kind == MirPageKind.DnText || p.Kind == MirPageKind.DnPhoto && p.Source == TextSource.Ocr))
        {
            var one = new DocText { FileName = $"{text.FileName} p{p.Number}" };
            one.Pages.Add(p);
            var dn = DnReader.Parse(one);
            if (dn.Value.Lines.Count > 0 || dn.Value.Header.DnNo.Length > 0) MergeDn(res, dn, p.Number);
        }
        // certificates and drum labels read by the offline OCR
        foreach (var p in text.Pages.Where(p => p.Source == TextSource.Ocr && p.Kind is MirPageKind.Cert or MirPageKind.Label))
        {
            var sp = new Documents.Smart.SmartPage { Number = p.Number, Text = p.Text, ReadingText = p.Text, Source = p.Source };
            var ev = p.Kind == MirPageKind.Cert ? Documents.Smart.EvidenceExtractor.TestCertificate(sp) : Documents.Smart.EvidenceExtractor.DrumLabel(sp);
            doc.Evidence.AddRange(ev);
        }
        Validate(res);
        return res;
    }

    private static void MergeDn(ExtractionResult<MirDocument> res, ExtractionResult<DnDocument> dn, int page)
    {
        var no = dn.Value.Header.DnNo;
        if (no.Length == 0 && dn.Value.Lines.Count == 0) return;
        var existing = res.Value.DnReads.FirstOrDefault(d => no.Length > 0 && d.Value.Header.DnNo == no);
        if (existing != null)
        {
            foreach (var l in dn.Value.Lines) { l.Order = existing.Value.Lines.Count + 1; existing.Value.Lines.Add(l); }
        }
        else res.Value.DnReads.Add(dn);
        if (no.Length > 0 && !res.Value.Dns.Any(d => d.DnNo == no))
            res.Value.Dns.Add(new MatMirDn { DnNo = no, PoNo = dn.Value.Header.PoNo, Source = dn.PageSources.GetValueOrDefault(page, TextSource.TextLayer) });
        foreach (var l in dn.Value.Lines)
            res.Value.Evidence.Add(new MatMirEvidence { Kind = "DNQTY", Page = page, DnNo = no, Batch = l.Batch, Description = l.Description, Qty = l.Qty, Unit = l.Unit, PoRef = dn.Value.Header.PoNo, Source = TextSource.Vision });
    }

    public static void Validate(ExtractionResult<MirDocument> res)
    {
        res.Issues.RemoveAll(i => i.Code is "NO_DN" or "CERT_PO" or "LABEL_BATCH");
        var d = res.Value;
        if (d.Dns.Count == 0) res.Warn("NO_DN", "No DN reference found - the DNs are usually phone photos inside the MIR: add the DN numbers before matching");
        var pos = d.Dns.Select(x => MaterialsSnapshot.PoKey(x.PoNo)).Where(x => x.Length > 0).ToHashSet();
        foreach (var e in d.Evidence.Where(e => e.Kind == "CERT" && e.PoRef.Length > 0 && pos.Count > 0 && !pos.Contains(MaterialsSnapshot.PoKey(e.PoRef))))
            res.Warn("CERT_PO", $"test certificate on page {e.Page} (drum {e.DrumNo}{e.Batch}) references {e.PoRef}, not a PO of this MIR's DNs");
        var dnBatches = d.Evidence.Where(e => e.Kind == "DNQTY" && e.Batch.Length > 0).Select(e => e.Batch).ToHashSet();
        if (dnBatches.Count > 0)
            foreach (var e in d.Evidence.Where(e => e.Kind == "LABEL" && e.Batch.Length > 0 && !dnBatches.Contains(e.Batch)))
                res.Warn("LABEL_BATCH", $"drum label on page {e.Page}: batch {e.Batch} is not on any DN of this MIR");
    }

    /// <summary>Page kinds from text keywords; scans without OCR by size / position (phone photo before the certificates = DN photo, A4 scans = certificates, later photos = drum labels).</summary>
    public static void Classify(DocText text)
    {
        foreach (var p in text.Pages)
        {
            var t = p.Text;
            if (!p.IsScan || p.Source == TextSource.Ocr)
            {
                p.Kind = Regex.IsMatch(t, @"\(MIR\)|Material\s+Inspection\s+Request|Doc\s+No\s*:\s*\S*-MIR-", RegexOptions.IgnoreCase) ? MirPageKind.Form
                    : Regex.IsMatch(t, @"\(MAR\)|Material\s+Approval\s+Request|Doc\s+No\s*:\s*\S*-MAT-", RegexOptions.IgnoreCase) ? MirPageKind.Mar
                    : Regex.IsMatch(t, @"Receiving\s+Checklist|ACCEPTANCE\s+CRITERIA", RegexOptions.IgnoreCase) ? MirPageKind.Checklist
                    : Regex.IsMatch(t, @"Delivery\s+Note", RegexOptions.IgnoreCase) ? (p.Source == TextSource.Ocr ? MirPageKind.DnPhoto : MirPageKind.DnText)
                    : Regex.IsMatch(t, @"TEST\s+(CERTIFICATE|REPORT)|ROUTINE\s+TEST|CERTIFICATE", RegexOptions.IgnoreCase) ? MirPageKind.Cert
                    : Regex.IsMatch(t, @"BATCH|DRUM|LENGTH\s*:", RegexOptions.IgnoreCase) && p.Source == TextSource.Ocr ? MirPageKind.Label
                    : QtyRow.Matches(t).Count >= 3 || Regex.IsMatch(t, @"QTY\s*/\s*M", RegexOptions.IgnoreCase) ? MirPageKind.QtyList
                    : MirPageKind.Other;
            }
        }
        // scans without OCR: guess by position and image shape
        var firstCert = text.Pages.FirstOrDefault(p => p.Kind is MirPageKind.QtyList or MirPageKind.Cert)?.Number ?? int.MaxValue;
        var seenA4 = false;
        foreach (var p in text.Pages.Where(p => p.Kind.Length == 0))
        {
            var im = p.DominantImage;
            var a4 = im != null && im.Height > im.Width && Math.Abs(im.Height / (double)Math.Max(1, im.Width) - 1.414) < 0.06 && im.Width >= 1200;
            if (a4) { p.Kind = MirPageKind.Cert + "?"; seenA4 = true; }
            else if (p.Number < firstCert && !seenA4) p.Kind = MirPageKind.DnPhoto + "?";
            else p.Kind = (seenA4 ? MirPageKind.Label : MirPageKind.Other) + "?";
        }
    }

    public static readonly JsonObject EvidenceSchema = new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("items"),
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("drum_no", "batch", "description", "length", "unit", "po_ref"),
                    ["properties"] = new JsonObject
                    {
                        ["drum_no"] = new JsonObject { ["type"] = "string" }, ["batch"] = new JsonObject { ["type"] = "string" }, ["description"] = new JsonObject { ["type"] = "string" },
                        ["length"] = new JsonObject { ["type"] = "number" }, ["unit"] = new JsonObject { ["type"] = "string" }, ["po_ref"] = new JsonObject { ["type"] = "string" },
                    },
                },
            },
        },
    };

    public static void ApplyEvidence(ExtractionResult<MirDocument> res, int page, string kind, JsonNode json)
    {
        foreach (var it in json["items"]?.AsArray() ?? new JsonArray())
        {
            if (it is null) continue;
            var raw = it["length"]?.GetValue<double>() ?? 0;
            var unit = Units.Normalize(it["unit"]?.GetValue<string>());
            var conv = unit == Units.Km ? Units.Convert(raw, unit, Units.M) : new Units.Conversion(raw, unit.Length == 0 ? Units.M : unit, true, "");
            res.Value.Evidence.Add(new MatMirEvidence
            {
                Kind = kind, Page = page, DrumNo = it["drum_no"]?.GetValue<string>() ?? "", Batch = it["batch"]?.GetValue<string>() ?? "",
                Description = it["description"]?.GetValue<string>() ?? "", Qty = conv.Qty, Unit = conv.Unit, PoRef = it["po_ref"]?.GetValue<string>() ?? "", Source = TextSource.Vision,
            });
        }
    }

    public static string Ranges(IEnumerable<int> pages)
    {
        var l = pages.OrderBy(x => x).ToList();
        var parts = new List<string>();
        for (var i = 0; i < l.Count;)
        {
            var j = i;
            while (j + 1 < l.Count && l[j + 1] == l[j] + 1) j++;
            parts.Add(i == j ? $"p{l[i]}" : $"p{l[i]}-{l[j]}");
            i = j + 1;
        }
        return string.Join(",", parts);
    }

    internal static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);
}
