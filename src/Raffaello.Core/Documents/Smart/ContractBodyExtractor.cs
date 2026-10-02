using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Documents.Ocr;

namespace Raffaello.Core.Documents.Smart;

/// <summary>Header terms and clauses of a subcontract as read, with a field result per term (review screen).</summary>
public sealed class ContractBodyRead
{
    public ContractTerms Terms { get; } = new();
    public List<ContractClause> Clauses { get; } = new();
    public Dictionary<string, FieldResult> Fields { get; } = new();
    public List<ExtractionIssue> Issues { get; } = new();
    /// <summary>Stage payments found: stage -> fraction (1ST FIX 0.9, HANDOVER 0.1 ...).</summary>
    public List<(string Group, string Stage, double Pct, string Source)> Payments { get; } = new();
}

/// <summary>
/// Reads the body of an Arabic (or English) subcontract: contract no, date, parties and CR numbers, scope and labour-only flag,
/// VAT treatment, stage payments (90 / 10, cable tray 70 / 20 / 10), retention, advance, delay penalty and cap, warranty,
/// termination, and the clause list (بند n + title + text) from the two-column clause table or from the running text.
/// Matching runs on a length-preserving fold of the text (alef / ya / ta-marbuta variants, digits), values are cut from the original.
/// </summary>
public static class ContractBodyExtractor
{
    private static readonly Regex ClauseHead = new(@"بند\s*[:\-]?\s*(\d{1,2})", RegexOptions.Compiled);

    public static ContractBodyRead Read(IEnumerable<SmartPage> pages)
    {
        var res = new ContractBodyRead();
        var list = pages.OrderBy(p => p.Number).ToList();
        var text = string.Join("\n", list.Select(p => p.ReadingText.Length > 0 ? p.ReadingText : p.Text));
        var fixedText = FixCodes(text);
        var fold = ArabicText.Fold(fixedText);
        var t = res.Terms;

        // contract number (OCR may print letter O for zero: "O28-2O26")
        var cn = DocValidators.ContractNo.Match(fixedText);
        if (cn.Success) Set(res, "ContractNo", cn.Value, 0.95, PageOf(list, cn.Value));
        else res.Issues.Add(new(IssueLevel.Error, "CONTRACT_NO", "contract number not found", null, "ContractNo"));
        t.ContractNo = res.Fields.GetValueOrDefault("ContractNo")?.Value ?? "";

        // dates: the agreement date ("يوم الثلاثاء الموافق 12 مايو 2026" / "تاريخ اتفاقية العقد 12-May-2026") and the signature date
        var date = FirstDate(fold, fixedText, @"(الموافق|تاريخ\s*اتفاقيه\s*العقد|تاريخ\s*العقد|DATE)\s*:?\s*");
        if (date != null) { t.ContractDate = date; Set(res, "ContractDate", date.Value.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture), 0.85); }
        var signed = Regex.Matches(fixedText, @"\b(\d{1,2}-[A-Za-z]{3}-\d{2,4}|\d{1,2}\s*-\s*\d{1,2}\s*-\s*\d{4})\b").Select(m => DocValidators.Date(Regex.Replace(m.Value, @"\s", ""))).Where(d => d != null).ToList();
        if (signed.Count > 0) { t.SignedDate = signed.Max(); Set(res, "SignedDate", t.SignedDate!.Value.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture), 0.8); }

        // parties
        t.FirstParty = Cut(fold, fixedText, @"المقاول\s*الرييسي\s*(\(الطرف\s*الاول\))?\s*:?\s*", @"(?=\s*(س\s*\.?\s*ت|\(طرف|،|,|\n|$))", 120);
        t.Subcontractor = Cut(fold, fixedText, @"(مقاول\s*الباطن|اسم\s*مقاول\s*الباطن)\s*(\(الطرف\s*الثاني\))?\s*:?\s*", @"(?=\s*(سجل|س\s*\.?\s*ت|طرف|،|,|\n|$))", 120);
        if (t.FirstParty.Length > 0) Set(res, "FirstParty", t.FirstParty, 0.75);
        if (t.Subcontractor.Length > 0) Set(res, "Subcontractor", t.Subcontractor, 0.75);
        else res.Issues.Add(new(IssueLevel.Warn, "SUB", "subcontractor name not found", null, "Subcontractor"));
        var crs = Regex.Matches(fold, @"(س\s*\.?\s*ت|سجل\s*تجاري(\s*رقم)?|C\.?R\.?)\s*[:.]?\s*(\d{10})").Select(m => (m.Index, No: m.Groups[3].Value)).ToList();
        if (crs.Count > 0)
        {
            var firstIdx = fold.IndexOf("المقاول الرييسي", StringComparison.Ordinal);
            var subIdx = fold.IndexOf("مقاول الباطن", StringComparison.Ordinal);
            foreach (var cr in crs)
            {
                if (subIdx >= 0 && cr.Index > subIdx && (firstIdx < 0 || cr.Index - subIdx < Math.Abs(cr.Index - firstIdx) || cr.Index > firstIdx && subIdx > firstIdx)) { if (t.SubcontractorCr.Length == 0) t.SubcontractorCr = cr.No; }
                else if (t.FirstPartyCr.Length == 0) t.FirstPartyCr = cr.No;
            }
            if (t.FirstPartyCr.Length > 0) Set(res, "FirstPartyCr", t.FirstPartyCr, 0.85);
            if (t.SubcontractorCr.Length > 0) Set(res, "SubcontractorCr", t.SubcontractorCr, 0.85);
        }
        var phone = Regex.Match(fold, @"\b05\d{8}\b");
        if (phone.Success) t.SubcontractorContact = phone.Value;
        var project = Regex.Match(fixedText, @"The\s+Raff\S*\s+Hotel\s+and\s+Branded\s+Residences", RegexOptions.IgnoreCase);
        if (project.Success) t.Project = "The Raffles Hotel and Branded Residences";

        // scope, labour only
        var scope = Cut(fold, fixedText, @"(?=تنفيذ\s*ال?اعمال)", @"(?=\s*(\n|التوصيل|$))", 80);
        if (scope.Length > 0) { t.Scope = scope; Set(res, "Scope", scope, 0.7); }
        t.LabourOnly = Regex.IsMatch(fold, @"مصنعيات\s*فقط|LABOU?R\s+ONLY|مصنعيات\s+اعمال") || Regex.IsMatch(fold, @"\(مصنعيات");
        Set(res, "LabourOnly", t.LabourOnly ? "YES" : "NO", t.LabourOnly ? 0.85 : 0.5);

        // VAT
        var vat = Regex.Match(fold, @"(غير\s*شامله|لا\s*تشمل|EXCLUD\w*|شامله|INCLUD\w*)\s*(ال)?ضريبه(\s*القيمه)?\s*(ال)?مضافه\s*(\d{1,2})?\s*%?");
        if (!vat.Success) vat = Regex.Match(fold, @"(EXCLUD\w*|INCLUD\w*)\s+(OF\s+)?VAT\s*(\d{1,2})?");
        if (vat.Success)
        {
            var excl = Regex.IsMatch(vat.Groups[1].Value, @"غير|لا|EXCL");
            t.VatTreatment = excl ? "EXCLUDED" : "INCLUDED";
            var pctTxt = vat.Groups.Cast<Group>().Skip(1).Select(g => g.Value).LastOrDefault(v => Regex.IsMatch(v, @"^\d{1,2}$"));
            t.VatPct = pctTxt != null ? double.Parse(pctTxt, CultureInfo.InvariantCulture) / 100 : DocValidators.VatRate;
            Set(res, "Vat", $"{t.VatTreatment} {t.VatPct:P0}", 0.85);
        }

        // payments: "90% دفعه تصرف بمستخلص مع تنفيذ 1st Fix", cable tray "70% دفعه بعد اعمال التركيب"
        ReadPayments(res, fold, fixedText);
        var main = res.Payments.Where(p => p.Group == "MAIN").ToList();
        var tray = res.Payments.Where(p => p.Group == "TRAY").ToList();
        t.PaymentTerms = string.Join("; ", main.Select(p => $"{p.Stage} {p.Pct:P0}"));
        t.TrayPaymentTerms = string.Join("; ", tray.Select(p => $"{p.Stage} {p.Pct:P0}"));
        if (t.PaymentTerms.Length > 0) Set(res, "PaymentTerms", t.PaymentTerms, 0.75);
        if (t.TrayPaymentTerms.Length > 0) Set(res, "TrayPaymentTerms", t.TrayPaymentTerms, 0.7);

        // retention / advance
        var ret = Regex.Match(fold, @"(محتجزات|ضمان\s*حسن\s*التنفيذ|RETENTION)[^%\n]{0,60}?(\d{1,2}(\.\d+)?)\s*%");
        if (ret.Success) { t.RetentionPct = double.Parse(ret.Groups[2].Value, CultureInfo.InvariantCulture) / 100; Set(res, "RetentionPct", $"{t.RetentionPct:P0}", 0.8); }
        var adv = Regex.Match(fold, @"(دفعه\s*مقدمه|الدفعه\s*المقدمه|ADVANCE\s*PAYMENT)[^%\n]{0,60}?(\d{1,2}(\.\d+)?)\s*%");
        if (adv.Success) { t.AdvancePct = double.Parse(adv.Groups[2].Value, CultureInfo.InvariantCulture) / 100; Set(res, "AdvancePct", $"{t.AdvancePct:P0}", 0.8); }

        // delay penalty: "غرامه تاخير مقدارها 500 ريال ... عن كل اسبوع تاخير ... بحد اقصي 10%"
        var pen = Regex.Match(fold, @"غرام\S*\s*(تاخير)?[^\n]{0,40}?(\d[\d,]*)\s*ريال[^\n]{0,80}?(اسبوع|يوم)");
        if (!pen.Success) pen = Regex.Match(fold, @"(\d[\d,]*)\s*ريال[^\n]{0,40}?(\d[\d,]*)?[^\n]{0,40}?عن\s*كل\s*(اسبوع|يوم)");
        if (pen.Success)
        {
            var amount = pen.Groups.Cast<Group>().Skip(1).Select(g => g.Value).FirstOrDefault(v => Regex.IsMatch(v, @"^\d[\d,]*$"));
            if (amount != null && ArabicText.ParseNumber(amount) is double a)
            {
                t.DelayPenaltyPerWeek = pen.Value.Contains("يوم") && !pen.Value.Contains("اسبوع") ? a * 7 : a;
                Set(res, "DelayPenaltyPerWeek", $"{t.DelayPenaltyPerWeek:0} SAR / week", 0.8);
            }
        }
        var cap = Regex.Match(fold, @"(اقصي|اقصى|حد\s*اقصي|MAX\w*)\s*(\d{1,2})\s*%");
        if (!cap.Success) cap = Regex.Match(fold, @"(\d{1,2})\s*%\s*من\s*(اجمالي|قيمه)\s*(قيمه\s*)?(الاعمال|العقد)");
        if (cap.Success)
        {
            var v = cap.Groups.Cast<Group>().Skip(1).Select(g => g.Value).First(x => Regex.IsMatch(x, @"^\d{1,2}$"));
            t.DelayPenaltyCapPct = double.Parse(v, CultureInfo.InvariantCulture) / 100;
            Set(res, "DelayPenaltyCapPct", $"{t.DelayPenaltyCapPct:P0}", 0.8);
        }

        // warranty: "مده سنه" / "12 شهر" / "سنتين"
        var war = Regex.Match(fold, @"(الضمان|يضمن|WARRANTY)[^\n]{0,120}?(مده\s*)?(سنتين|سنه|عام|(\d{1,2})\s*(شهر|اشهر|سنه|سنوات|MONTHS?|YEARS?))");
        if (war.Success)
        {
            var g = war.Groups;
            t.WarrantyMonths = g[3].Value switch
            {
                "سنتين" => 24, "سنه" or "عام" => 12,
                _ => int.TryParse(g[4].Value, out var n) ? (Regex.IsMatch(g[5].Value, "سن|YEAR") ? n * 12 : n) : null,
            };
            if (t.WarrantyMonths != null) Set(res, "WarrantyMonths", $"{t.WarrantyMonths} months", 0.75);
        }

        ReadClauses(res, list);
        var term = res.Clauses.FirstOrDefault(c => Regex.IsMatch(ArabicText.Fold(c.Title), @"الغاء|انهاء|TERMINAT"));
        if (term != null) t.Termination = term.TextAr;
        if (res.Clauses.Count == 0) res.Issues.Add(new(IssueLevel.Warn, "CLAUSES", "no clauses (بند n) found", null, ""));
        return res;
    }

    /// <summary>Contract / PO codes with letter O for zero or stray spaces: "SUB-ELE-O28-2O26" -> "SUB-ELE-028-2026".</summary>
    public static string FixCodes(string text) =>
        Regex.Replace(text, @"\b[A-Za-z]{2,}(?:-[A-Za-z0-9]{1,})+\b", m => string.Join("-", m.Value.Split('-').Select(p => p.Any(char.IsDigit) ? ArabicText.FixDigitConfusions(p) : p)));

    private static int PageOf(List<SmartPage> pages, string value) => pages.FirstOrDefault(p => FixCodes(p.ReadingText + p.Text).Contains(value, StringComparison.OrdinalIgnoreCase))?.Number ?? 0;

    private static void Set(ContractBodyRead res, string field, string value, double conf, int page = 0) =>
        res.Fields[field] = FieldVote.Single(field, value, conf, "reader", null, page);

    /// <summary>The text after a label (matched on the fold), up to the end pattern, cut from the original.</summary>
    private static string Cut(string fold, string original, string label, string end, int max)
    {
        var m = Regex.Match(fold, label + @"(?<v>[^\n]{2," + max + "}?)" + end);
        if (!m.Success) return "";
        var g = m.Groups["v"];
        return original.Substring(g.Index, g.Length).Trim(' ', ':', '-', '،', ',');
    }

    private static DateTime? FirstDate(string fold, string original, string label)
    {
        var m = Regex.Match(fold, label + @"(?<v>[^\n]{4,30})");
        while (m.Success)
        {
            var v = original.Substring(m.Groups["v"].Index, m.Groups["v"].Length);
            var d = DocValidators.Date(Regex.Match(v, @"\d{1,2}\s*[-/ ]\s*[ء-يA-Za-z]+\s*[-/ ]\s*\d{2,4}|\d{1,2}[/.-]\d{1,2}[/.-]\d{4}|\d{4}[/.-]\d{1,2}[/.-]\d{1,2}").Value.Replace(" - ", "-"));
            if (d != null) return d;
            m = m.NextMatch();
        }
        return null;
    }

    private static void ReadPayments(ContractBodyRead res, string fold, string original)
    {
        var lines = fold.Split('\n');
        var group = "MAIN";
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"الكابل\s*تراي|CABLE\s*TRAY|الكابلات\s*واللوحات|اللوحات")) group = "TRAY";
            foreach (Match m in Regex.Matches(line, @"(\d{1,3})\s*%|%\s*(\d{1,3})"))
            {
                var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!double.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct) || pct is <= 0 or > 100) continue;
                var ctx = line;
                if (Regex.IsMatch(ctx, @"ضريب|VAT|غرام|اقصي|اقصى")) continue;
                var stage = Regex.IsMatch(ctx, @"1ST\s*FIX|الاولي|الأولى") ? "1ST FIX"
                    : Regex.IsMatch(ctx, @"2ND\s*FIX|الثانيه") ? "2ND FIX"
                    : Regex.IsMatch(ctx, @"3RD\s*FIX|الثالثه") ? "3RD FIX"
                    : Regex.IsMatch(ctx, @"التسليم\s*الابتدايي|HANDOVER|الاستلام\s*الابتدايي") ? "HANDOVER"
                    : Regex.IsMatch(ctx, @"الاختبار|TEST|التوصيل") ? "TEST & TERMINATION"
                    : Regex.IsMatch(ctx, @"التركيب|سحب\s*الكابلات|INSTALL") ? "INSTALLATION"
                    : "";
                if (stage.Length == 0) continue;
                var g = group == "TRAY" || stage is "TEST & TERMINATION" or "INSTALLATION" ? "TRAY" : "MAIN";
                if (res.Payments.Any(p => p.Group == g && p.Stage == stage)) continue;
                res.Payments.Add((g, stage, pct / 100, line.Trim()));
            }
        }
    }

    /// <summary>Clauses from the clause table (narrow column "بند n + title", wide column = text); else from the running text.</summary>
    private static void ReadClauses(ContractBodyRead res, List<SmartPage> pages)
    {
        ContractClause? current = null;
        foreach (var p in pages)
        {
            var grids = p.Ocr is null ? new List<TableGrid>() : TableBuilder.FromRulings(p.Ocr);
            var usedGrid = false;
            foreach (var g in grids.Where(g => g.ColCount >= 2))
            {
                var titleCol = Enumerable.Range(0, g.ColCount).OrderByDescending(c => g.ColumnCells(c).Count(x => ClauseHead.IsMatch(ArabicText.Fold(x.Text)))).First();
                if (g.ColumnCells(titleCol).Count(x => ClauseHead.IsMatch(ArabicText.Fold(x.Text))) < 1) continue;
                var bodyCol = Enumerable.Range(0, g.ColCount).Where(c => c != titleCol).OrderByDescending(c => g.Columns[c].X1 - g.Columns[c].X0).First();
                usedGrid = true;
                for (var r = 0; r < g.RowCount; r++)
                {
                    var title = g[r, titleCol].Text.Trim();
                    var body = string.Join("\n", LayoutBuilder.Lines(g[r, bodyCol].Words).Select(l => l.Text));
                    var m = ClauseHead.Match(ArabicText.Fold(title));
                    if (m.Success)
                    {
                        current = new ContractClause
                        {
                            ClauseNo = m.Groups[1].Value, Title = title.Remove(m.Index, m.Length).Trim(' ', ':', '-'), TextAr = body, Page = p.Number,
                            Confidence = g[r, bodyCol].Words.Count == 0 ? 0 : g[r, bodyCol].Words.Average(w => w.Confidence),
                        };
                        Upsert(res, current);
                    }
                    else if (current != null && body.Trim().Length > 0) current.TextAr += "\n" + body;
                }
            }
            if (usedGrid) continue;
            // running text: "بند 5 شروط الدفع" ... up to the next بند
            var text = p.ReadingText.Length > 0 ? p.ReadingText : p.Text;
            var fold = ArabicText.Fold(text);
            var heads = ClauseHead.Matches(fold).ToList();
            if (heads.Count == 0) { if (current != null && text.Trim().Length > 0 && res.Clauses.Count > 0) current.TextAr += "\n" + text.Trim(); continue; }
            for (var i = 0; i < heads.Count; i++)
            {
                var start = heads[i].Index + heads[i].Length;
                var end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
                var chunk = text[start..end].Trim();
                var nl = chunk.IndexOf('\n');
                current = new ContractClause
                {
                    ClauseNo = heads[i].Groups[1].Value, Title = (nl > 0 ? chunk[..nl] : chunk).Trim(' ', ':', '-'), TextAr = nl > 0 ? chunk[(nl + 1)..].Trim() : "", Page = p.Number, Confidence = p.Confidence,
                };
                Upsert(res, current);
            }
        }
        foreach (var c in res.Clauses) c.ContractNo = res.Terms.ContractNo;
    }

    private static void Upsert(ContractBodyRead res, ContractClause c)
    {
        var old = res.Clauses.FirstOrDefault(x => x.ClauseNo == c.ClauseNo);
        if (old is null) res.Clauses.Add(c);
        else if (c.TextAr.Length > old.TextAr.Length) { res.Clauses.Remove(old); res.Clauses.Add(c); }
    }
}
