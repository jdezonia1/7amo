using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Raffaello.Core.Coding;
using Raffaello.Core.Documents;
using Raffaello.Core.Import;

namespace Raffaello.Core.Materials;

/// <summary>A purchase order as read from a document, before saving.</summary>
public sealed class PoDocument
{
    public MatPo Header { get; init; } = new();
    public List<MatPoLine> Lines { get; } = new();
    public List<MatPoScope> Scope { get; } = new();
    public double LinesTotal => Lines.Sum(l => l.Amount);
}

/// <summary>
/// Reads a supplier PO from PDF (text layer, OCR'd scans, or Claude vision for pages without text) or Excel: supplier, PO no / date,
/// terms (advance, retention, tolerance header vs clause, penalty, payment, delivery), lines, the ePROMIS scope-of-work sheet,
/// and checks qty x rate = amount, sum of lines = stated total, VAT, grand total, line-number gaps and SOW quantities.
/// </summary>
public static class PoReader
{
    private static readonly Regex LineRx = new(
        @"^\s*(?<no>\d{1,4})\s{2,}(?<desc>\S.*?)\s{2,}(?<unit>[A-Za-z][A-Za-z.]{0,7})\s+(?<qty>[\d,]+(?:\.\d+)?)\s+(?:SAR\s*)?(?<rate>[\d,]+(?:\.\d+)?)\s+(?:SAR\s*)?(?<amt>[\d,]+(?:\.\d+)?)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex SowRx = new(
        @"^\s*(?<sr>\d{1,4})\s+(?<code>\d{8,14})\s+(?<name>\S.*?)\s{2,}(?<boq>B\d{1,2}-[0-9A-Z-]+)\s+(?<unit>[A-Za-z]{1,6})\s+(?<req>[\d,]+(?:\.\d+)?)\s+(?<app>[\d,]+(?:\.\d+)?)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex SowCont = new(@"^\s+\S.*?\s{2,}(?<suffix>[A-Z]{1,3}-\d{1,3})\s*$", RegexOptions.Compiled);
    private static readonly Regex PoNoRx = new(@"\b([A-Z]{2,6}-P\.?O\.?-[A-Z0-9]+(?:-[A-Z0-9]+)+)\b", RegexOptions.Compiled);

    public static async Task<ExtractionResult<PoDocument>> ReadAsync(string path, ReaderOptions? options = null, CancellationToken ct = default)
    {
        options ??= ReaderOptions.Default;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xlsx" or ".xlsm" or ".csv") return ReadExcel(path);
        var text = await ReaderPipeline.ReadPdfAsync(path, options, ct).ConfigureAwait(false);
        var res = Parse(text);
        if (res.Value.Lines.Count == 0 && options.CanUseVision)
        {
            await VisionLinesAsync(path, text, res, options.Vision!, ct).ConfigureAwait(false);
            Validate(res);
        }
        return res;
    }

    public static ExtractionResult<PoDocument> Parse(DocText text)
    {
        var doc = new PoDocument();
        var res = new ExtractionResult<PoDocument> { Value = doc, FileName = text.FileName, Text = text };
        foreach (var p in text.Pages) res.PageSources[p.Number] = p.Source;
        var lines = text.AllLines.ToList();
        var all = text.AllText;
        var h = doc.Header;
        h.SourceFile = text.FileName;
        h.ImportedAt = DateTime.Now;

        h.PoNo = TextScan.LabelValue(lines, @"P\.?\s?O\.?\s*Ref\s*:") ?? PoNoRx.Match(all).Groups[1].Value;
        if (h.PoNo.Length == 0) res.Error("PO_NO", "PO number not found", field: nameof(MatPo.PoNo));
        var to = TextScan.LabelValue(lines, @"\bTo\s*:");
        if (to != null) h.Supplier = Regex.Replace(to, @"\s*\(C\.?R.*$", "", RegexOptions.IgnoreCase).Trim();
        if (h.Supplier.Length == 0) res.Warn("SUPPLIER", "Supplier name not found - enter it before saving", field: nameof(MatPo.Supplier));
        var date = TextScan.LabelValue(lines, @"\bDate\s*:");
        h.PoDate = TextScan.ParseDate(date?.Split(' ')[0]);
        if (h.PoDate is null) res.Warn("PO_DATE", "PO date not found", field: nameof(MatPo.PoDate));
        h.Scope = TextScan.LabelValue(lines, @"\bSOW") ?? "";
        h.ScopeRef = TextScan.LabelValue(lines, @"PR\s+Scope\s+of\s+Work\s*:") ?? "";
        h.AdvancePct = TextScan.Percent(all, @"\bAdvance") ?? 0;
        h.RetentionPct = TextScan.Percent(all, @"\bRetention") ?? 0;
        var tolHeader = Regex.Match(all, @"(?m)^\s*Tolerance\s*:?\s*(\d+(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase);
        if (tolHeader.Success) h.ToleranceHeaderPct = double.Parse(tolHeader.Groups[1].Value, CultureInfo.InvariantCulture) / 100;
        var tolClause = Regex.Match(all, @"tolerance\s+of\s*(?:plus\s+or\s+minus|\+\s*/\s*-|±|\+-)?\s*(\d+(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase);
        if (tolClause.Success) h.ToleranceClausePct = double.Parse(tolClause.Groups[1].Value, CultureInfo.InvariantCulture) / 100;
        h.Remeasurable = Regex.IsMatch(all, @"Re-?\s*Measurable\s*[☒■✔✓xX]", RegexOptions.IgnoreCase);
        const string heads = @"Payment\s+Terms|Penalty|Delivery|Warranty|Packing|General\s+Conditions|Scope\s+of\s+Work";
        h.PaymentTerms = TextScan.Section(lines, @"Payment\s+Terms", heads);
        h.PenaltyText = TextScan.Section(lines, "Penalty", heads);
        h.DeliveryTerms = TextScan.Section(lines, "Delivery", heads);
        var pw = Regex.Match(h.PenaltyText, @"(\d+(?:\.\d+)?)\s*%\s*(?:per|/)\s*week", RegexOptions.IgnoreCase);
        if (pw.Success) h.PenaltyPctPerWeek = double.Parse(pw.Groups[1].Value, CultureInfo.InvariantCulture) / 100;
        var pm = Regex.Match(h.PenaltyText, @"max(?:imum)?\.?\s*(\d+(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase);
        if (pm.Success) h.PenaltyMaxPct = double.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture) / 100;

        foreach (var l in lines)
        {
            if (Regex.IsMatch(l, @"Grand\s+Total", RegexOptions.IgnoreCase)) h.StatedGrandTotal = TextScan.LastAmount(l) ?? h.StatedGrandTotal;
            else if (Regex.IsMatch(l, @"^\s*VAT\b", RegexOptions.IgnoreCase)) h.StatedVat = TextScan.LastAmount(l) ?? h.StatedVat;
            else if (Regex.IsMatch(l, @"^\s*Total\s+(Price|Amount)", RegexOptions.IgnoreCase)) h.StatedTotal = TextScan.LastAmount(l) ?? h.StatedTotal;
        }

        // ---- lines and scope-of-work rows
        var seen = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var s = SowRx.Match(l);
            if (s.Success)
            {
                var boq = s.Groups["boq"].Value;
                if (boq.EndsWith('-') && i + 1 < lines.Count && SowCont.Match(lines[i + 1]) is { Success: true } c) boq += c.Groups["suffix"].Value;
                doc.Scope.Add(new MatPoScope
                {
                    SrNo = int.Parse(s.Groups["sr"].Value, CultureInfo.InvariantCulture), ResourceCode = s.Groups["code"].Value, ResourceName = Regex.Replace(s.Groups["name"].Value, @"\s+", " ").Trim(),
                    BoqCode = boq.ToUpperInvariant(), Unit = Units.Normalize(s.Groups["unit"].Value), Qty = Units.ParseNumber(s.Groups["app"].Value) ?? 0,
                    Fingerprint = Fingerprints.Key(s.Groups["name"].Value),
                });
                continue;
            }
            var m = LineRx.Match(l);
            if (!m.Success) continue;
            var no = int.Parse(m.Groups["no"].Value, CultureInfo.InvariantCulture);
            if (!seen.Add(no)) { res.Warn("LINE_DUP", $"line {no} appears twice - second copy ignored", no); continue; }
            var desc = Regex.Replace(m.Groups["desc"].Value, @"\s+", " ").Trim();
            doc.Lines.Add(new MatPoLine
            {
                LineNo = no, Description = desc, Unit = Units.Normalize(m.Groups["unit"].Value),
                Qty = Units.ParseNumber(m.Groups["qty"].Value) ?? 0, Rate = Units.ParseNumber(m.Groups["rate"].Value) ?? 0, Amount = Units.ParseNumber(m.Groups["amt"].Value) ?? 0,
                Fingerprint = Fingerprints.Key(desc),
            });
        }
        foreach (var p in text.Pages.Where(p => p.IsScan && p.Source != TextSource.Ocr))
            res.Warn("SCAN_PAGE", $"page {p.Number} has no text layer (scan) - not read; use OCR or cloud reading if it carries PO lines or terms");
        Validate(res);
        return res;
    }

    /// <summary>All arithmetic and consistency checks; safe to call again after edits in the review screen.</summary>
    public static void Validate(ExtractionResult<PoDocument> res)
    {
        res.Issues.RemoveAll(i => i.Code is "LINE_AMOUNT" or "LINE_EMPTY" or "LINE_GAP" or "TOTAL" or "VAT" or "GRAND_TOTAL" or "NO_LINES" or "TOLERANCE_CONFLICT" or "SOW_QTY" or "UNIT");
        var doc = res.Value; var h = doc.Header;
        if (doc.Lines.Count == 0) { res.Error("NO_LINES", "No PO lines found"); return; }
        foreach (var l in doc.Lines)
        {
            if (l.Qty <= 0 || l.Description.Length == 0) res.Error("LINE_EMPTY", "quantity or description missing", l.LineNo);
            if (!Checks.AmountOk(l.Qty, l.Rate, l.Amount))
                res.Error("LINE_AMOUNT", $"qty x rate = {l.Qty * l.Rate:N2} but amount is {l.Amount:N2} (diff {l.Qty * l.Rate - l.Amount:N2})", l.LineNo);
            if (Units.Family(l.Unit) is not ("LENGTH" or "COUNT" or "ROLL"))
                res.Info("UNIT", $"unit '{l.Unit}' is not a standard length / count unit", l.LineNo);
        }
        var nos = doc.Lines.Select(l => l.LineNo).OrderBy(x => x).ToList();
        var missing = Enumerable.Range(nos[0], nos[^1] - nos[0] + 1).Except(nos).ToList();
        if (missing.Count > 0) res.Error("LINE_GAP", $"line number(s) {string.Join(", ", missing)} missing - a page may not have been read");
        var sum = Math.Round(doc.LinesTotal, 2);
        if (h.StatedTotal > 0 && !Checks.TotalOk(sum, h.StatedTotal))
            res.Error("TOTAL", $"sum of lines {sum:N2} differs from the stated total {h.StatedTotal:N2} by {sum - h.StatedTotal:N2}");
        else if (h.StatedTotal <= 0) res.Warn("TOTAL", $"stated total not found - lines sum to {sum:N2}");
        if (h.StatedVat > 0 && h.StatedTotal > 0 && !Checks.TotalOk(h.StatedTotal * 0.15, h.StatedVat))
            res.Warn("VAT", $"VAT {h.StatedVat:N2} is not 15 % of {h.StatedTotal:N2} ({h.StatedTotal * 0.15:N2})");
        if (h.StatedGrandTotal > 0 && h.StatedTotal > 0 && !Checks.TotalOk(h.StatedTotal + h.StatedVat, h.StatedGrandTotal))
            res.Error("GRAND_TOTAL", $"grand total {h.StatedGrandTotal:N2} is not total + VAT ({h.StatedTotal + h.StatedVat:N2})");
        if (h.ToleranceConflict)
            res.Warn("TOLERANCE_CONFLICT", $"header says tolerance {h.ToleranceHeaderPct:P0} but the conditions allow ±{h.ToleranceClausePct:P0} per item - OVER PO uses ±{h.EffectiveTolerancePct:P0} (change in the PO terms)");
        if (doc.Scope.Count > 0)
        {
            foreach (var l in doc.Lines)
            {
                var fp = l.Fingerprint;
                if (fp.Length == 0) continue;
                var rows = doc.Scope.Where(s => s.Fingerprint == fp).ToList();
                if (rows.Count == 0) continue;
                var q = rows.Sum(r => r.Qty);
                var conv = Units.Normalize(l.Unit) == Units.Normalize(rows[0].Unit);
                if (conv && Math.Abs(q - l.Qty) > 0.001)
                    res.Warn("SOW_QTY", $"scope-of-work sheet approves {q:N0} {rows[0].Unit} over {rows.Count} BOQ code(s), PO line has {l.Qty:N0}", l.LineNo);
            }
        }
    }

    // ------------------------------------------------------------------ Excel

    /// <summary>Excel PO: a table with No / Description / Unit / Qty / Rate / Amount; header values from labelled cells above it.</summary>
    public static ExtractionResult<PoDocument> ReadExcel(string path)
    {
        var t = TableReader.Read(path);
        var doc = new PoDocument();
        var res = new ExtractionResult<PoDocument> { Value = doc, FileName = Path.GetFileName(path) };
        res.PageSources[1] = TextSource.Excel;
        var h = doc.Header;
        h.SourceFile = Path.GetFileName(path);
        h.ImportedAt = DateTime.Now;
        var n = 0;
        foreach (var r in t.Rows)
        {
            var desc = r.Get("DESCRIPTION", "ITEM DESCRIPTION", "MATERIAL", "DESC");
            var qty = r.GetNumber("QTY", "QUANTITY", "ORDERED QTY");
            if (desc.Length == 0 || qty is null)
            {
                var joined = string.Join(" ", r.Cells.Values);
                if (Regex.IsMatch(joined, @"Grand\s+Total", RegexOptions.IgnoreCase)) h.StatedGrandTotal = TextScan.LastAmount(joined) ?? 0;
                else if (Regex.IsMatch(joined, @"\bVAT\b", RegexOptions.IgnoreCase)) h.StatedVat = TextScan.LastAmount(joined) ?? 0;
                else if (Regex.IsMatch(joined, @"\bTotal\b", RegexOptions.IgnoreCase)) h.StatedTotal = TextScan.LastAmount(joined) ?? 0;
                continue;
            }
            n++;
            var no = (int)(r.GetNumber("NO", "ITEM", "S/N", "SR", "LINE") ?? n);
            var rate = r.GetNumber("RATE", "UNIT PRICE", "UNIT PRICE (SAR)", "PRICE") ?? 0;
            var amt = r.GetNumber("AMOUNT", "TOTAL", "TOTAL PRICE", "TOTAL PRICE (SAR)", "VALUE") ?? Math.Round(qty.Value * rate, 2);
            doc.Lines.Add(new MatPoLine
            {
                LineNo = no, ItemCode = r.Get("CODE", "ITEM CODE", "MATERIAL CODE"), Description = desc, Unit = Units.Normalize(r.Get("UNIT", "UOM")),
                Qty = qty.Value, Rate = rate, Amount = amt, Fingerprint = Fingerprints.Key(desc),
                BoqCode = r.Get("BOQ", "BOQ CODE", "BOQ NO", "BOQ ITEM NO"), CostCode = r.Get("COST CODE"), BudgetResourceCode = r.Get("BUDGET RESOURCE CODE", "RESOURCE CODE"),
            });
        }
        foreach (var l in doc.Lines.Where(l => l.BoqCode.Length > 0)) { l.CodeStatus = CodeStatus.Document; l.CodeSource = "PO workbook"; l.CodeScore = 1; }
        Validate(res);
        if (doc.Header.PoNo.Length == 0) res.Warn("PO_NO", "PO number not in the workbook - enter it before saving", field: nameof(MatPo.PoNo));
        return res;
    }

    // ------------------------------------------------------------------ vision fallback

    public static readonly JsonObject LineSchema = new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("po_no", "supplier", "date", "lines"),
        ["properties"] = new JsonObject
        {
            ["po_no"] = new JsonObject { ["type"] = "string" },
            ["supplier"] = new JsonObject { ["type"] = "string" },
            ["date"] = new JsonObject { ["type"] = "string" },
            ["lines"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("no", "description", "unit", "qty", "rate", "amount"),
                    ["properties"] = new JsonObject
                    {
                        ["no"] = new JsonObject { ["type"] = "integer" }, ["description"] = new JsonObject { ["type"] = "string" }, ["unit"] = new JsonObject { ["type"] = "string" },
                        ["qty"] = new JsonObject { ["type"] = "number" }, ["rate"] = new JsonObject { ["type"] = "number" }, ["amount"] = new JsonObject { ["type"] = "number" },
                    },
                },
            },
        },
    };

    private static async Task VisionLinesAsync(string path, DocText text, ExtractionResult<PoDocument> res, IVisionReader vision, CancellationToken ct)
    {
        foreach (var p in text.Pages)
        {
            var (bytes, media) = ReaderPipeline.PageContent(path, p);
            var json = await vision.ExtractAsync(new VisionRequest
            {
                DocumentKind = "supplier purchase order", Page = p.Number, Content = bytes, MediaType = media, Schema = LineSchema,
                Instructions = "Extract the PO number, supplier (the 'To' party), PO date and every priced line (no, description, unit, qty, unit price, total price). Return an empty lines array when the page has no priced lines.",
            }, ct).ConfigureAwait(false);
            if (json is null) continue;
            res.PageSources[p.Number] = TextSource.Vision;
            var h = res.Value.Header;
            if (h.PoNo.Length == 0) h.PoNo = json["po_no"]?.GetValue<string>() ?? "";
            if (h.Supplier.Length == 0) h.Supplier = json["supplier"]?.GetValue<string>() ?? "";
            h.PoDate ??= TextScan.ParseDate(json["date"]?.GetValue<string>());
            foreach (var l in json["lines"]?.AsArray() ?? new JsonArray())
            {
                if (l is null) continue;
                var desc = l["description"]?.GetValue<string>() ?? "";
                res.Value.Lines.Add(new MatPoLine
                {
                    LineNo = l["no"]?.GetValue<int>() ?? res.Value.Lines.Count + 1, Description = desc, Unit = Units.Normalize(l["unit"]?.GetValue<string>()),
                    Qty = l["qty"]?.GetValue<double>() ?? 0, Rate = l["rate"]?.GetValue<double>() ?? 0, Amount = l["amount"]?.GetValue<double>() ?? 0,
                    Fingerprint = Fingerprints.Key(desc),
                });
            }
        }
    }
}
