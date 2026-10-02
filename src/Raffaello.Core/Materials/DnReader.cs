using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Raffaello.Core.Coding;
using Raffaello.Core.Documents;
using Raffaello.Core.Import;

namespace Raffaello.Core.Materials;

public sealed class DnDocument
{
    public MatDn Header { get; init; } = new();
    public List<MatDnLine> Lines { get; } = new();
    /// <summary>Sub-total rows printed under each item (item no -> qty in raw unit).</summary>
    public List<(string ItemNo, double Qty, string Unit)> SubTotals { get; } = new();
}

/// <summary>
/// Reads a supplier delivery note from the PDF text layer, a photo / scan (OCR or Claude vision) or Excel: DN no / date, PO ref,
/// order no, lines with item, material code, description, batch / drum no, qty and unit; KM is converted to M. Checks sub-totals,
/// missing batches and duplicate drums.
/// </summary>
public static class DnReader
{
    private static readonly Regex Rc = new(
        @"^\s*(?<item>\d{4,8})\s+(?<code>\d{6,12})\s+(?<desc>\S.*?)\s{2,}(?<sloc>\d{3,4})\s+(?<dnpos>\d{3})\s+(?<batch>[0-9A-Z]{6,16})\s+(?<qty>[\d,]+(?:\.\d+)?)\s+(?<unit>[A-Za-z]{1,5})\.?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex Generic = new(
        @"^\s*(?<item>\d{1,8})\s+(?:(?<code>[0-9][0-9A-Z-]{4,14})\s+)?(?<desc>[^\d\s].*?)\s{2,}(?:(?<batch>[0-9A-Z]{6,16})\s+)?(?<qty>[\d,]+(?:\.\d+)?)\s+(?<unit>KM|M|MT|MTR|MTRS|PCS|PC|NO|NOS|EA|ROLL|ROLLS|SET|LOT|KG|LM)\.?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SubTotal = new(@"Sub-?\s*Total\s+(?<qty>[\d,]+(?:\.\d+)?)\s+(?<unit>[A-Za-z]{1,5})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DnNoRx = new(@"Delivery\s+Note\s*(?:No\.?|Number|#|:)?\s*:?\s*(\d{5,12})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PoRx = new(@"\b([A-Z]{2,6}-P\.?O\.?-[A-Z0-9]+(?:-[A-Z0-9]+)+|P\.?O\.?\s*(?:No\.?|#|:)\s*[A-Z0-9-]{4,})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<ExtractionResult<DnDocument>> ReadAsync(string path, ReaderOptions? options = null, CancellationToken ct = default)
    {
        options ??= ReaderOptions.Default;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xlsx" or ".xlsm" or ".csv") return ReadExcel(path).FirstOrDefault() ?? Empty(path, "No DN rows in the workbook");
        if (ext is ".jpg" or ".jpeg" or ".png")
        {
            var img = new PageImage { Bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false), MediaType = ext == ".png" ? "image/png" : "image/jpeg", Coverage = 1 };
            var text = new DocText { FileName = Path.GetFileName(path) };
            text.Pages.Add(new DocPage { Number = 1, DominantImage = img, HasTextLayer = false });
            await ReaderPipeline.OcrScansAsync(text, options, ct).ConfigureAwait(false);
            var r = Parse(text);
            if (r.Value.Lines.Count == 0 && options.CanUseVision) { await VisionAsync(r, 1, img.Bytes, img.MediaType, options.Vision!, ct).ConfigureAwait(false); Validate(r); }
            return r;
        }
        var doc = await ReaderPipeline.ReadPdfAsync(path, options, ct).ConfigureAwait(false);
        var res = Parse(doc);
        if (res.Value.Lines.Count == 0 && options.CanUseVision)
        {
            foreach (var p in doc.Pages.Any(x => x.IsScan) ? doc.Pages.Where(x => x.IsScan).ToList() : doc.Pages)
            {
                var (bytes, media) = ReaderPipeline.PageContent(path, p);
                await VisionAsync(res, p.Number, bytes, media, options.Vision!, ct).ConfigureAwait(false);
            }
            Validate(res);
        }
        return res;
    }

    private static ExtractionResult<DnDocument> Empty(string path, string why)
    {
        var r = new ExtractionResult<DnDocument> { Value = new DnDocument(), FileName = Path.GetFileName(path) };
        r.Error("NO_LINES", why);
        return r;
    }

    public static ExtractionResult<DnDocument> Parse(DocText text)
    {
        var doc = new DnDocument();
        var res = new ExtractionResult<DnDocument> { Value = doc, FileName = text.FileName, Text = text };
        foreach (var p in text.Pages) res.PageSources[p.Number] = p.Source;
        var h = doc.Header;
        h.SourceFile = text.FileName;
        h.ImportedAt = DateTime.Now;
        h.Source = text.Pages.Any(p => p.Source == TextSource.TextLayer) ? TextSource.TextLayer : text.Pages.Any(p => p.Source == TextSource.Ocr) ? TextSource.Ocr : TextSource.None;
        // OCR of photos: Arabic-Indic digits, "RAF-P,O-E-045" (comma for dot)
        var lines = text.AllLines.Select(l => Regex.Replace(Documents.ArabicText.NormalizeDigits(l), @"\bP\s?[,.]\s?O\s?[,.]?-", "P.O-")).ToList();
        var all = string.Join("\n", lines);

        var dn = DnNoRx.Match(all);
        if (dn.Success) h.DnNo = dn.Groups[1].Value;
        // photo / scan layout: "Delivery Number / Date 81064344 / 15.08.2026"
        var dn2 = Regex.Match(all, @"Delivery\s+Number\s*/\s*Date\s*:?\s*(\d{6,10})\s*/?\s*(\d{1,2}[./-]\d{1,2}[./-]\d{4})?", RegexOptions.IgnoreCase);
        if (dn2.Success)
        {
            h.DnNo = dn2.Groups[1].Value;
            if (dn2.Groups[2].Success) h.DnDate = TextScan.ParseDate(dn2.Groups[2].Value.Replace('/', '.').Replace('-', '.'));
        }
        var poLine = lines.FirstOrDefault(l => PoRx.IsMatch(l));
        if (poLine != null)
        {
            var pm = PoRx.Match(poLine);
            h.PoNo = Regex.Replace(pm.Groups[1].Value, @"^P\.?O\.?\s*(?:No\.?|#|:)\s*", "", RegexOptions.IgnoreCase).Trim();
            var dates = TextScan.Dates(poLine).ToList();
            if (h.DnDate is null) h.DnDate = dates.Where(d => d.Index < pm.Index).Select(d => (DateTime?)d.Date).FirstOrDefault() ?? dates.Select(d => (DateTime?)d.Date).FirstOrDefault();
            var after = dates.Where(d => d.Index > pm.Index).ToList();
            if (after.Count > 0) h.PoDate = after[0].Date;
            var rest = poLine[(pm.Index + pm.Length)..];
            var nums = Regex.Matches(TextScan.DatePattern.Replace(rest, " "), @"(?<![\d.,])\d{6,10}(?![\d.,])").Select(m => m.Value).ToList();
            if (nums.Count > 0) h.OrderNo = nums[0];
            if (nums.Count > 1) h.CustomerNo = nums[^1];
            if (h.OrderNo.Length == 0)
            {
                var ord = Regex.Match(all, @"Order\s+Number\s*/\s*Date\s*:?\s*\n?\s*(\d{6,10})", RegexOptions.IgnoreCase);
                if (ord.Success) h.OrderNo = ord.Groups[1].Value;
            }
            if (h.CustomerNo.Length == 0)
            {
                var cus = Regex.Match(all, @"Customer\s+No\.?\s*:?\s*(\d{6,10})", RegexOptions.IgnoreCase);
                if (cus.Success) h.CustomerNo = cus.Groups[1].Value;
            }
        }
        h.DnDate ??= lines.SelectMany(TextScan.Dates).Select(d => (DateTime?)d.Date).FirstOrDefault();
        h.TruckNo = TextScan.LabelValue(lines, @"Truck\s+No\.?") ?? "";
        h.Supplier = GuessSupplier(all);

        MatDnLine? last = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var st = SubTotal.Match(l);
            if (st.Success)
            {
                if (last != null) doc.SubTotals.Add((last.ItemNo, Units.ParseNumber(st.Groups["qty"].Value) ?? 0, Units.Normalize(st.Groups["unit"].Value)));
                continue;
            }
            var m = Rc.Match(l);
            if (!m.Success) m = Generic.Match(l);
            if (m.Success)
            {
                var desc = m.Groups["desc"].Value.Trim();
                // wrapped description: next line is a short indented fragment ("D" of "_STD")
                if (i + 1 < lines.Count && Regex.IsMatch(lines[i + 1], @"^\s{6,}\S{1,24}\s*$") && !SubTotal.IsMatch(lines[i + 1]))
                {
                    var frag = lines[i + 1].Trim();
                    desc += frag.Length <= 3 ? frag : " " + frag;
                    i++;
                }
                var raw = Units.ParseNumber(m.Groups["qty"].Value) ?? 0;
                var unit = Units.Normalize(m.Groups["unit"].Value);
                var conv = unit == Units.Km ? Units.Convert(raw, unit, Units.M) : new Units.Conversion(raw, unit, true, "");
                last = new MatDnLine
                {
                    Order = doc.Lines.Count + 1, ItemNo = m.Groups["item"].Value, ItemCode = m.Groups["code"].Value, Description = desc, Batch = m.Groups["batch"].Value,
                    RawQty = raw, RawUnit = unit, Qty = conv.Qty, Unit = conv.Unit, ConversionNote = conv.Note, Fingerprint = Fingerprints.Key(desc),
                };
                doc.Lines.Add(last);
            }
            else if (Loose(l) is { } x)
            {
                var conv = Units.Convert(x.Qty, Units.Km, Units.M);
                last = new MatDnLine
                {
                    Order = doc.Lines.Count + 1, ItemNo = x.Item, ItemCode = x.Code, Description = x.Desc, Batch = x.Batch, RawQty = x.Qty, RawUnit = Units.Km,
                    Qty = conv.Qty, Unit = conv.Unit, ConversionNote = conv.Note, Fingerprint = Fingerprints.Key(x.Desc),
                };
                doc.Lines.Add(last);
                if (x.ItemRepaired) res.Warn("ITEM_READ", $"item number read as '{x.RawItem}' (pen marks / margin) - taken as {x.Item}, check it", last.Order);
            }
        }
        foreach (var p in text.Pages.Where(p => p.IsScan && p.Source != TextSource.Ocr))
            res.Warn("SCAN_PAGE", $"page {p.Number} is a photo / scan without text - use OCR or cloud reading");
        Validate(res);
        return res;
    }

    private sealed record LooseLine(string Item, string RawItem, bool ItemRepaired, string Code, string Desc, string Batch, double Qty);

    /// <summary>
    /// A DN line from a phone photo where OCR glued or split columns ("B043110 001", "0010013278392", "00132899601.046 KMc"): batch (001x + 6 digits),
    /// quantity in KM right after it, material code (100 + 5 digits), item number (six digits, 000nn0; pen ticks in the margin are cut off and flagged).
    /// </summary>
    private static LooseLine? Loose(string line)
    {
        var l = Documents.ArabicText.NormalizeDigits(line);
        if (!Regex.IsMatch(l, @"K\s*M", RegexOptions.IgnoreCase) || SubTotal.IsMatch(l)) return null;
        var compact = Regex.Replace(l, @"\s+", "");
        var b = Regex.Matches(compact, @"001[1-9]\d{6}").LastOrDefault();
        if (b is null) return null;
        var after = compact[(b.Index + b.Length)..];
        var q = Regex.Match(after, @"^[^\d]{0,2}(\d{1,3}\.\d{3})K?M?", RegexOptions.IgnoreCase);
        if (!q.Success) return null;
        var code = Regex.Match(compact[..b.Index], @"100\d{5}");
        var desc = Regex.Match(l, @"\d\s*[Xx]\s*\d+(?:\.\d+)?\s*mm\S*", RegexOptions.IgnoreCase);
        var rawItem = Regex.Match(l, @"^\s*(\d{1,7})").Groups[1].Value;
        var item = rawItem;
        var repaired = false;
        if (!Regex.IsMatch(item, @"^000\d{3}$"))
        {
            repaired = true;
            var d = item.Length >= 3 ? item[^3..] : item;
            item = d.Length == 0 ? "" : "000" + d.PadLeft(3, '0');
        }
        var descText = desc.Success ? Regex.Replace(desc.Value, @"(\d{4}|\d{4}\d{3})$", "") : "";
        return new LooseLine(item, rawItem, repaired, code.Success ? code.Value : "", descText, b.Value, Units.ParseNumber(q.Groups[1].Value) ?? 0);
    }

    public static void Validate(ExtractionResult<DnDocument> res)
    {
        res.Issues.RemoveAll(i => i.Code is "NO_LINES" or "QTY" or "SUBTOTAL" or "BATCH" or "BATCH_DUP" or "DN_NO" or "PO_REF");
        var doc = res.Value;
        if (doc.Header.DnNo.Length == 0) res.Error("DN_NO", "DN number not found", field: nameof(MatDn.DnNo));
        if (doc.Header.PoNo.Length == 0) res.Warn("PO_REF", "PO reference not found - pick the PO before matching", field: nameof(MatDn.PoNo));
        if (doc.Lines.Count == 0) { res.Error("NO_LINES", "No DN lines found"); return; }
        foreach (var l in doc.Lines)
        {
            if (l.RawQty <= 0) res.Error("QTY", "quantity missing or zero", l.Order);
            if (l.Batch.Length == 0) res.Warn("BATCH", "no batch / drum number - the MIR cross-check cannot use this line", l.Order);
        }
        foreach (var g in doc.Lines.Where(l => l.Batch.Length > 0).GroupBy(l => l.Batch).Where(g => g.Count() > 1))
            res.Warn("BATCH_DUP", $"batch {g.Key} appears on {g.Count()} lines", g.First().Order);
        foreach (var (item, qty, unit) in doc.SubTotals)
        {
            var lines = doc.Lines.Where(l => l.ItemNo == item).ToList();
            var sum = lines.Sum(l => l.RawQty);
            if (lines.Count > 0 && Math.Abs(sum - qty) > 0.0005 && Units.Normalize(lines[0].RawUnit) == unit)
                res.Error("SUBTOTAL", $"item {item}: lines sum to {sum:0.###} {unit} but the sub-total says {qty:0.###}", lines[0].Order);
        }
    }

    /// <summary>Supplier from the e-mail / web domain printed on the DN ("rcgc@riyadh-cables.com" -> "Riyadh Cables").</summary>
    public static string GuessSupplier(string text)
    {
        var m = Regex.Match(text, @"@(?:www\.)?([a-z0-9-]+)\.(?:com|sa|net|com\.sa)", RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        var words = m.Groups[1].Value.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    // ------------------------------------------------------------------ Excel (one workbook may hold several DNs)

    public static List<ExtractionResult<DnDocument>> ReadExcel(string path)
    {
        var t = TableReader.Read(path);
        var list = new List<ExtractionResult<DnDocument>>();
        foreach (var g in t.Rows.Where(r => r.Get("DESCRIPTION", "MATERIAL", "ITEM DESCRIPTION").Length > 0).GroupBy(r => r.Get("DN", "DN NO", "DELIVERY NOTE", "DELIVERY NOTE NO")))
        {
            var doc = new DnDocument();
            var res = new ExtractionResult<DnDocument> { Value = doc, FileName = Path.GetFileName(path) };
            res.PageSources[1] = TextSource.Excel;
            var first = g.First();
            doc.Header.DnNo = g.Key;
            doc.Header.PoNo = first.Get("PO", "PO NO", "PO REF", "P.O");
            doc.Header.DnDate = first.GetDate("DATE", "DN DATE", "DELIVERY DATE");
            doc.Header.Supplier = first.Get("SUPPLIER", "VENDOR");
            doc.Header.OrderNo = first.Get("ORDER", "ORDER NO", "SO", "SALES ORDER");
            doc.Header.Source = TextSource.Excel;
            doc.Header.SourceFile = Path.GetFileName(path);
            doc.Header.ImportedAt = DateTime.Now;
            foreach (var r in g)
            {
                var desc = r.Get("DESCRIPTION", "MATERIAL", "ITEM DESCRIPTION");
                var raw = r.GetNumber("QTY", "QUANTITY", "DELIVERED QTY") ?? 0;
                var unit = Units.Normalize(r.Get("UNIT", "UOM"));
                var conv = unit == Units.Km ? Units.Convert(raw, unit, Units.M) : new Units.Conversion(raw, unit, true, "");
                doc.Lines.Add(new MatDnLine
                {
                    Order = doc.Lines.Count + 1, ItemNo = r.Get("ITEM", "LINE", "NO"), ItemCode = r.Get("CODE", "ITEM CODE", "MATERIAL CODE"), Description = desc,
                    Batch = r.Get("BATCH", "DRUM", "DRUM NO", "BATCH NO", "LOT"), RawQty = raw, RawUnit = unit, Qty = conv.Qty, Unit = conv.Unit, ConversionNote = conv.Note,
                    Fingerprint = Fingerprints.Key(desc),
                });
            }
            Validate(res);
            list.Add(res);
        }
        return list;
    }

    // ------------------------------------------------------------------ vision

    public static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("dn_no", "po_no", "date", "supplier", "lines"),
        ["properties"] = new JsonObject
        {
            ["dn_no"] = new JsonObject { ["type"] = "string" }, ["po_no"] = new JsonObject { ["type"] = "string" },
            ["date"] = new JsonObject { ["type"] = "string" }, ["supplier"] = new JsonObject { ["type"] = "string" },
            ["lines"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("item", "code", "description", "batch", "qty", "unit"),
                    ["properties"] = new JsonObject
                    {
                        ["item"] = new JsonObject { ["type"] = "string" }, ["code"] = new JsonObject { ["type"] = "string" }, ["description"] = new JsonObject { ["type"] = "string" },
                        ["batch"] = new JsonObject { ["type"] = "string" }, ["qty"] = new JsonObject { ["type"] = "number" }, ["unit"] = new JsonObject { ["type"] = "string" },
                    },
                },
            },
        },
    };

    internal static async Task VisionAsync(ExtractionResult<DnDocument> res, int page, byte[] bytes, string media, IVisionReader vision, CancellationToken ct)
    {
        var json = await vision.ExtractAsync(new VisionRequest
        {
            DocumentKind = "supplier delivery note", Page = page, Content = bytes, MediaType = media, Schema = Schema,
            Instructions = "Extract the delivery note number, PO reference, DN date (as printed), supplier and every delivered line: item no, material code, description, batch / drum no, quantity and unit (KM, M, PCS ...). Skip sub-total rows.",
        }, ct).ConfigureAwait(false);
        if (json is null) return;
        ApplyVision(res, page, json);
    }

    /// <summary>Applies a vision answer to a DN result (also used by the MIR reader for DN photos inside a MIR).</summary>
    public static void ApplyVision(ExtractionResult<DnDocument> res, int page, JsonNode json)
    {
        res.PageSources[page] = TextSource.Vision;
        var h = res.Value.Header;
        if (h.DnNo.Length == 0) h.DnNo = json["dn_no"]?.GetValue<string>()?.Trim() ?? "";
        if (h.PoNo.Length == 0) h.PoNo = json["po_no"]?.GetValue<string>()?.Trim() ?? "";
        if (h.Supplier.Length == 0) h.Supplier = json["supplier"]?.GetValue<string>()?.Trim() ?? "";
        h.DnDate ??= TextScan.ParseDate(json["date"]?.GetValue<string>());
        h.Source = TextSource.Vision;
        foreach (var l in json["lines"]?.AsArray() ?? new JsonArray())
        {
            if (l is null) continue;
            var desc = l["description"]?.GetValue<string>() ?? "";
            var raw = l["qty"]?.GetValue<double>() ?? 0;
            var unit = Units.Normalize(l["unit"]?.GetValue<string>());
            var conv = unit == Units.Km ? Units.Convert(raw, unit, Units.M) : new Units.Conversion(raw, unit, true, "");
            res.Value.Lines.Add(new MatDnLine
            {
                Order = res.Value.Lines.Count + 1, ItemNo = l["item"]?.GetValue<string>() ?? "", ItemCode = l["code"]?.GetValue<string>() ?? "", Description = desc,
                Batch = l["batch"]?.GetValue<string>() ?? "", RawQty = raw, RawUnit = unit, Qty = conv.Qty, Unit = conv.Unit, ConversionNote = conv.Note, Fingerprint = Fingerprints.Key(desc),
            });
        }
    }

    internal static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);
}
