using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Contracts;
using Raffaello.Core.Documents.Ocr;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Documents.Smart;

public static class ColumnRoles
{
    public const string No = "NO", Desc = "DESC", Unit = "UNIT", Qty = "QTY", Rate = "RATE", Total = "TOTAL";
    public static readonly string[] All = { No, Desc, Unit, Qty, Rate, Total };
}

/// <summary>One rate-schedule row as read: every field with its readings, status and box.</summary>
public sealed class ScheduleItemRead
{
    public int Page { get; init; }
    public Box RowBox { get; init; }
    public string Section { get; set; } = "";
    public FieldResult No { get; set; } = new() { Name = ColumnRoles.No };
    public FieldResult Description { get; set; } = new() { Name = ColumnRoles.Desc };
    public FieldResult Unit { get; set; } = new() { Name = ColumnRoles.Unit };
    public FieldResult Qty { get; set; } = new() { Name = ColumnRoles.Qty };
    public FieldResult Rate { get; set; } = new() { Name = ColumnRoles.Rate };
    public FieldResult Total { get; set; } = new() { Name = ColumnRoles.Total };
    public List<string> Issues { get; } = new();
    public string ItemNo => No.Value;
    public double QtyValue => Qty.Number ?? 0;
    public double RateValue => Rate.Number ?? 0;
    public double TotalValue => Total.Number ?? 0;
    public bool ArithmeticOk => Qty.Number is double q && Rate.Number is double r && Total.Number is double t && DocValidators.AmountOk(q, r, t);
    public IEnumerable<FieldResult> Fields => new[] { No, Description, Unit, Qty, Rate, Total };
    public double Confidence => Fields.Where(f => f.Status != FieldStatus.Missing).Select(f => f.Confidence).DefaultIfEmpty(0).Min();
    public bool NeedsReview => Fields.Any(f => f.NeedsReview) || Issues.Count > 0;

    public ContractItem ToContractItem(string contractNo, int order)
    {
        var item = new ContractItem
        {
            ContractNo = contractNo, ItemNo = ItemNo, Order = order, Section = Section, Description = Description.Value,
            Unit = DocValidators.Unit(Unit.Value) ?? Unit.Value, Qty = QtyValue, Rate = RateValue,
        };
        ContractAttributeParser.Apply(item, ContractAttributeParser.Parse(item.Description, item.Unit));
        item.StagePct = ContractAttributeParser.DefaultStagePct(item);
        return item;
    }
}

/// <summary>Column layout found on one page (kept as a learnable template: role -> x-range as a fraction of the page width).</summary>
public sealed class PageColumns
{
    public int Page { get; init; }
    public string Method { get; init; } = "";
    public Dictionary<string, (double X0, double X1)> Roles { get; } = new();
    public int PageWidth { get; init; }
}

public sealed class ScheduleRead
{
    public List<ScheduleItemRead> Items { get; } = new();
    public List<(string Section, int Page)> Sections { get; } = new();
    public double? StatedTotal { get; set; }
    public int? StatedTotalPage { get; set; }
    public List<PageColumns> Columns { get; } = new();
    public List<ExtractionIssue> Issues { get; } = new();
    public int Rereads { get; set; }
    public double LinesTotal => Items.Sum(i => i.TotalValue);

    public List<ContractItem> ToContractItems(string contractNo)
    {
        var list = new List<ContractItem>();
        foreach (var i in Items.Where(i => i.ItemNo.Length > 0)) list.Add(i.ToContractItem(contractNo, list.Count + 1));
        ContractAttributeParser.InferHeightPairs(list);
        return list;
    }
}

/// <summary>
/// Reads rate schedules (contract price tables, Arabic or English) from smart pages: grid from the ruling lines (or whitespace /
/// learned template columns), column roles from the header words (رقم / التوصيف / الوحدة / الكمية / سعر الوحدة / الاجمالي,
/// Sr / Description / Unit / Qty / Rate / Amount) or from the cell contents, section rows, item rows and the grand total.
/// Every row is checked qty x rate = total; a failing or low-confidence number is re-read by the other engines / models and the
/// combination that satisfies the arithmetic wins (VALIDATED); when no reading fits, the field is a CONFLICT for the user.
/// </summary>
public static class ScheduleExtractor
{
    private static readonly Regex HeaderNo = new(@"^(رقم|م|SR\.?|NO\.?|ITEM|البند)$", RegexOptions.Compiled);

    public static async Task<ScheduleRead> ReadAsync(IEnumerable<SmartPage> pages, IFieldRereader? rereader = null, ReadTemplate? template = null, CancellationToken ct = default)
    {
        rereader ??= NullRereader.Instance;
        var res = new ScheduleRead();
        var section = "";
        foreach (var page in pages.OrderBy(p => p.Number))
        {
            ct.ThrowIfCancellationRequested();
            var grid = GridOf(page, template);
            if (grid is null) { res.Issues.Add(new(IssueLevel.Warn, "NO_TABLE", $"page {page.Number}: no table found", null, "")); continue; }
            var (roles, headerRows) = RolesFromHeader(grid);
            if (!Complete(roles) && template != null) roles = RolesFromTemplate(grid, template, page);
            if (!Complete(roles)) roles = Combine(roles, RolesFromContent(grid));
            if (!roles.Contains(ColumnRoles.Desc) || roles.Count(r => r is ColumnRoles.Qty or ColumnRoles.Rate or ColumnRoles.Total) < 2)
            {
                res.Issues.Add(new(IssueLevel.Warn, "NO_COLUMNS", $"page {page.Number}: could not tell the table columns apart", null, ""));
                continue;
            }
            var pw = page.Ocr?.Width ?? (int)page.Base.WidthPt;
            var cols = new PageColumns { Page = page.Number, Method = grid.Method, PageWidth = pw };
            for (var c = 0; c < roles.Length; c++) if (roles[c] is { } r) cols.Roles[r] = (grid.Columns[c].X0 / Math.Max(1, pw), grid.Columns[c].X1 / Math.Max(1, pw));
            res.Columns.Add(cols);
            int Col(string role) => Array.IndexOf(roles, role);

            for (var r = 0; r < grid.RowCount; r++)
            {
                if (headerRows.Contains(r)) continue;
                string Cell(string role) => Col(role) is var c and >= 0 ? grid[r, c].Text.Trim() : "";
                TableCell? CellObj(string role) => Col(role) is var c and >= 0 ? grid[r, c] : null;
                var rowText = string.Join(" ", grid.RowCells(r).Select(x => x.Text));
                if (rowText.Trim().Length == 0) continue;
                if (IsHeaderRow(grid, r)) continue;
                var noTxt = Cell(ColumnRoles.No);
                var desc = Cell(ColumnRoles.Desc);
                var nums = new[] { ColumnRoles.Qty, ColumnRoles.Rate, ColumnRoles.Total }.Select(Cell).ToArray();
                var anyNum = nums.Any(n => ArabicText.ParseNumber(n) != null);
                var no = ParseItemNo(noTxt);
                var normRow = ArabicText.Normalize(rowText);
                if (no is null && Regex.IsMatch(normRow, @"الاجمالي|\bTOTAL\b|GRAND") && ArabicText.ParseNumber(Cell(ColumnRoles.Total)) is double gt && gt > 0)
                {
                    res.StatedTotal = gt; res.StatedTotalPage = page.Number;
                    continue;
                }
                var qtyOrRate = ArabicText.ParseNumber(CleanNumber(nums[0])) != null || ArabicText.ParseNumber(CleanNumber(nums[1])) != null;
                if (no is null && (!anyNum || !qtyOrRate))
                {
                    if (desc.Length >= 3 && (ArabicText.HasArabic(desc) || desc.Count(char.IsLetter) >= 3)) { section = desc; res.Sections.Add((desc, page.Number)); }
                    continue;
                }
                if (no is null && desc.Length < 3) continue;   // stamp / signature noise
                var item = new ScheduleItemRead
                {
                    Page = page.Number, Section = section,
                    RowBox = new Box(grid.Bounds.X, grid.Rows[r].Y0, grid.Bounds.W, grid.Rows[r].Y1 - grid.Rows[r].Y0),
                };
                item.No = Field(ColumnRoles.No, no?.ToString(CultureInfo.InvariantCulture) ?? "", CellObj(ColumnRoles.No), page, page.Engine);
                if (no is null) item.Issues.Add("item number unreadable");
                item.Description = Field(ColumnRoles.Desc, desc, CellObj(ColumnRoles.Desc), page, page.Engine);
                var unitRaw = Cell(ColumnRoles.Unit);
                item.Unit = Field(ColumnRoles.Unit, DocValidators.Unit(unitRaw) ?? unitRaw, CellObj(ColumnRoles.Unit), page, page.Engine);
                if (unitRaw.Length > 0 && !DocValidators.IsUnit(unitRaw)) { item.Unit.Status = FieldStatus.Low; item.Unit.Note = $"'{unitRaw}' is not a known unit"; }
                item.Qty = Field(ColumnRoles.Qty, CleanNumber(nums[0]), CellObj(ColumnRoles.Qty), page, page.Engine);
                item.Rate = Field(ColumnRoles.Rate, CleanNumber(nums[1]), CellObj(ColumnRoles.Rate), page, page.Engine);
                item.Total = Field(ColumnRoles.Total, CleanNumber(nums[2]), CellObj(ColumnRoles.Total), page, page.Engine);
                await RepairNumbersAsync(item, page, rereader, CellObj(ColumnRoles.Qty), CellObj(ColumnRoles.Rate), CellObj(ColumnRoles.Total), ct).ConfigureAwait(false);
                res.Items.Add(item);
            }
        }
        if (rereader is EngineRereader er) res.Rereads = er.Calls;
        FixItemNumbers(res);
        Validate(res);
        return res;
    }

    private static TableGrid? GridOf(SmartPage page, ReadTemplate? template)
    {
        if (page.Ocr != null)
        {
            var g = TableBuilder.FromRulings(page.Ocr).Where(x => x.ColCount >= 4).OrderByDescending(x => x.RowCount * x.ColCount).FirstOrDefault();
            if (g != null) return g;
        }
        var words = page.Words;
        if (words.Count == 0) return null;
        if (template?.ColumnRanges() is { Count: >= 4 } ranges)
        {
            var w = page.Ocr?.Width ?? page.Base.WidthPt;
            var cols = ranges.OrderBy(r => r.Value.X0).Select(r => (r.Value.X0 * w, r.Value.X1 * w)).ToList();
            return TableBuilder.FromColumns(words, cols);
        }
        var fake = new OcrPage { Words = words.ToList(), Width = (int)(page.Ocr?.Width ?? page.Base.WidthPt), Height = (int)(page.Ocr?.Height ?? page.Base.HeightPt) };
        return TableBuilder.FromWhitespace(fake);
    }

    private static readonly (string Role, Regex Rx)[] HeaderCues =
    {
        (ColumnRoles.Rate, new Regex(@"سعر|\bRATE\b|\bPRICE\b|U\.?\s*PRICE", RegexOptions.Compiled)),
        (ColumnRoles.Total, new Regex(@"الاجمالي|الاجمال|المجموع|\bTOTAL\b|\bAMOUNT\b", RegexOptions.Compiled)),
        (ColumnRoles.Qty, new Regex(@"الكميه|الكمية|\bQTY\b|\bQUANTITY\b", RegexOptions.Compiled)),
        (ColumnRoles.Unit, new Regex(@"^الوحده$|^الوحدة$|^UNIT$|^UOM$|^الوحده\s|\bUNIT\b", RegexOptions.Compiled)),
        (ColumnRoles.Desc, new Regex(@"التوصيف|البيان|الوصف|DESCRIPTION", RegexOptions.Compiled)),
        (ColumnRoles.No, new Regex(@"^(رقم|م|SR\.?|NO\.?|ITEM|البند|S/N)$", RegexOptions.Compiled)),
    };

    internal static (string?[] Roles, HashSet<int> HeaderRows) RolesFromHeader(TableGrid g)
    {
        var best = new string?[g.ColCount];
        var header = new HashSet<int>();
        for (var r = 0; r < Math.Min(4, g.RowCount); r++)
        {
            var roles = new string?[g.ColCount];
            for (var c = 0; c < g.ColCount; c++)
            {
                var t = ArabicText.Normalize(g[r, c].Text);
                if (t.Length == 0 || t.Length > 30) continue;
                foreach (var (role, rx) in HeaderCues)
                    if (rx.IsMatch(t) && !roles.Contains(role)) { roles[c] = role; break; }
            }
            if (roles.Count(x => x != null) >= 3 && roles.Count(x => x != null) > best.Count(x => x != null)) { best = roles; header.Clear(); header.Add(r); }
        }
        return (best, header);
    }

    private static bool Complete(string?[] roles) =>
        roles.Contains(ColumnRoles.Desc) && roles.Contains(ColumnRoles.Qty) && roles.Contains(ColumnRoles.Rate) && roles.Contains(ColumnRoles.Total);

    /// <summary>Header roles where the header was readable, content roles for the rest (a merged header cell can hide three columns).</summary>
    private static string?[] Combine(string?[] header, string?[] content)
    {
        var res = (string?[])content.Clone();
        for (var c = 0; c < header.Length; c++)
        {
            if (header[c] is not { } h || h is ColumnRoles.Qty or ColumnRoles.Rate or ColumnRoles.Total) continue;
            if (res.Contains(h) && res[c] != h) continue;   // content placed it elsewhere with evidence
            if (res[c] is null) res[c] = h;
        }
        return res;
    }

    private static bool IsHeaderRow(TableGrid g, int r)
    {
        var hits = 0;
        for (var c = 0; c < g.ColCount; c++)
        {
            var t = ArabicText.Normalize(g[r, c].Text);
            if (t.Length is > 0 and <= 30 && HeaderCues.Any(h => h.Rx.IsMatch(t))) hits++;
        }
        return hits >= 3;
    }

    private static string?[] RolesFromTemplate(TableGrid g, ReadTemplate t, SmartPage page)
    {
        var w = page.Ocr?.Width ?? page.Base.WidthPt;
        var roles = new string?[g.ColCount];
        foreach (var (role, range) in t.ColumnRanges())
        {
            var x0 = range.X0 * w; var x1 = range.X1 * w;
            var best = -1; double ov = 0;
            for (var c = 0; c < g.ColCount; c++)
            {
                var o = Math.Max(0, Math.Min(x1, g.Columns[c].X1) - Math.Max(x0, g.Columns[c].X0));
                if (o > ov) { ov = o; best = c; }
            }
            if (best >= 0 && roles[best] is null) roles[best] = role;
        }
        return roles;
    }

    /// <summary>Roles from what the columns contain: longest text = description, units, small sequential integers at an edge = item no, and the three number columns ordered so that a x b = c holds most often.</summary>
    internal static string?[] RolesFromContent(TableGrid g)
    {
        var n = g.ColCount;
        var roles = new string?[n];
        var stats = Enumerable.Range(0, n).Select(c =>
        {
            var cells = g.ColumnCells(c).Select(x => x.Text.Trim()).Where(x => x.Length > 0).ToList();
            return new
            {
                Col = c,
                Count = cells.Count,
                AvgLen = cells.Count == 0 ? 0 : cells.Average(x => x.Length),
                Num = cells.Count == 0 ? 0 : cells.Count(x => ArabicText.ParseNumber(CleanNumber(x)) != null && x.Count(char.IsDigit) >= x.Replace(" ", "").Length / 2) / (double)cells.Count,
                Units = cells.Count == 0 ? 0 : cells.Count(DocValidators.IsUnit) / (double)cells.Count,
                SmallInt = cells.Count == 0 ? 0 : cells.Count(x => ParseItemNo(x) is int) / (double)cells.Count,
            };
        }).ToList();
        var desc = stats.OrderByDescending(s => s.AvgLen).First();
        roles[desc.Col] = ColumnRoles.Desc;
        var unit = stats.Where(s => roles[s.Col] is null && s.Units >= 0.4).OrderByDescending(s => s.Units).FirstOrDefault();
        if (unit != null) roles[unit.Col] = ColumnRoles.Unit;
        var numeric = stats.Where(s => roles[s.Col] is null && s.Num >= 0.5).OrderBy(s => s.Col).ToList();
        // item no: the edge numeric column of small integers
        var edge = numeric.Where(s => s.SmallInt >= 0.6 && (s.Col == 0 || s.Col == n - 1)).OrderByDescending(s => s.SmallInt).FirstOrDefault();
        if (edge != null) { roles[edge.Col] = ColumnRoles.No; numeric.Remove(edge); }
        if (numeric.Count >= 3)
        {
            var best = (Score: -1, Perm: Array.Empty<int>());
            foreach (var perm in Permutations(numeric.Select(s => s.Col).ToList(), 3))
            {
                var score = 0;
                for (var r = 0; r < g.RowCount; r++)
                {
                    var a = ArabicText.ParseNumber(CleanNumber(g[r, perm[0]].Text)); var b = ArabicText.ParseNumber(CleanNumber(g[r, perm[1]].Text)); var c = ArabicText.ParseNumber(CleanNumber(g[r, perm[2]].Text));
                    if (a is double x && b is double y && c is double z && DocValidators.AmountOk(x, y, z)) score++;
                }
                if (score > best.Score) best = (score, perm.ToArray());
            }
            // qty and rate are interchangeable in a x b = c: the column nearer the description is the quantity
            var qty = best.Perm[0]; var rate = best.Perm[1];
            if (Math.Abs(rate - desc.Col) < Math.Abs(qty - desc.Col)) (qty, rate) = (rate, qty);
            roles[qty] = ColumnRoles.Qty; roles[rate] = ColumnRoles.Rate; roles[best.Perm[2]] = ColumnRoles.Total;
        }
        return roles;
    }

    private static IEnumerable<List<int>> Permutations(List<int> items, int k)
    {
        if (k == 0) { yield return new List<int>(); yield break; }
        foreach (var i in items)
            foreach (var rest in Permutations(items.Where(x => x != i).ToList(), k - 1))
            { rest.Insert(0, i); yield return rest; }
    }

    internal static int? ParseItemNo(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = ArabicText.FixDigitConfusions(ArabicText.NormalizeDigits(s.Trim())).Trim('.', ' ', '-');
        return Regex.IsMatch(t, @"^\d{1,4}$") && int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is > 0 and < 5000 ? v : null;
    }

    /// <summary>The numeric part of a cell ("SAR 1,920" / "1,920 *" / stamp noise around a number).</summary>
    internal static string CleanNumber(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = ArabicText.NormalizeDigits(s);
        var m = Regex.Matches(t, @"\d[\d,\s]*(?:\.\d+)?").Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToList();
        if (m.Count == 0) return ArabicText.FixDigitConfusions(t.Trim()) is var f && ArabicText.ParseNumber(f) != null && f.Any(char.IsDigit) ? f : "";
        var best = m.OrderByDescending(x => x.Count(char.IsDigit)).First();
        return Regex.Replace(best, @"\s+", "");
    }

    private static FieldResult Field(string name, string value, TableCell? cell, SmartPage page, string engine)
    {
        var conf = cell is null || cell.Words.Count == 0 ? (value.Length > 0 ? 0.5 : 0) : cell.Words.Average(w => w.Confidence);
        if (page.Source == TextSource.TextLayer) conf = Math.Max(conf, 0.97);
        var f = value.Length == 0 ? new FieldResult { Name = name, Status = FieldStatus.Missing, Page = page.Number, Box = cell?.Box }
            : FieldVote.Single(name, value, conf, engine, cell?.Box, page.Number);
        f.Box ??= cell?.Box;
        f.Page = page.Number;
        return f;
    }

    private static async Task RepairNumbersAsync(ScheduleItemRead item, SmartPage page, IFieldRereader rereader, TableCell? qc, TableCell? rc, TableCell? tc, CancellationToken ct)
    {
        var fields = new[] { item.Qty, item.Rate, item.Total };
        var cells = new[] { qc, rc, tc };
        var confident = fields.All(f => f.Status != FieldStatus.Missing && f.Confidence >= 0.9);
        if (item.ArithmeticOk && confident) return;
        // second opinions on every number cell of the row
        var cands = new List<FieldCandidate>[3];
        for (var i = 0; i < 3; i++)
        {
            cands[i] = new List<FieldCandidate>(fields[i].Candidates);
            if (cells[i] is { } c && page.Source == TextSource.Ocr)
            {
                var box = c.Words.Count > 0 ? Box.Union(c.Words.Select(w => w.Box)) : c.Box;
                foreach (var alt in await rereader.RereadAsync(page, box, numeric: true, ct).ConfigureAwait(false))
                {
                    var cleaned = CleanNumber(alt.Value);
                    if (cleaned.Length > 0) cands[i].Add(alt with { Value = cleaned });
                }
            }
        }
        // the combination that satisfies qty x rate = total, preferring agreement between engines
        var combos = from q in cands[0].Select(c => (c, v: ArabicText.ParseNumber(c.Value))).Where(x => x.v != null)
                     from r in cands[1].Select(c => (c, v: ArabicText.ParseNumber(c.Value))).Where(x => x.v != null)
                     from t in cands[2].Select(c => (c, v: ArabicText.ParseNumber(c.Value))).Where(x => x.v != null)
                     where DocValidators.AmountOk(q.v!.Value, r.v!.Value, t.v!.Value)
                     select (q, r, t);
        var ok = combos.ToList();
        if (ok.Count > 0)
        {
            var (q, r, t) = ok.OrderByDescending(x => x.q.c.Confidence + x.r.c.Confidence + x.t.c.Confidence).First();
            Apply(item.Qty, cands[0], q.v!.Value); Apply(item.Rate, cands[1], r.v!.Value); Apply(item.Total, cands[2], t.v!.Value);
            return;
        }
        // one number unreadable: derive it from the other two - only when both were read the same way by two models - and flag it
        var vals = cands.Select(cs => cs.Select(c => ArabicText.ParseNumber(c.Value)).FirstOrDefault(v => v != null)).ToArray();
        bool Agreed(int i) => cands[i].Count >= 2 && cands[i].Select(c => ArabicText.ParseNumber(c.Value)).Where(v => v != null).Distinct().Count() == 1
                              && cands[i].Count(c => ArabicText.ParseNumber(c.Value) != null) >= 2;
        string Read(int i) => string.Join(" / ", cands[i].Select(c => c.Value).Distinct());
        if (Agreed(0) && Agreed(1) && vals[0] is double q4 && vals[1] is double r4)
        {
            var read = Read(2);
            Apply(item.Qty, cands[0], q4); Apply(item.Rate, cands[1], r4);
            Derive(item.Total, Math.Round(q4 * r4, 2), read.Length == 0 ? "qty x rate (total cell unreadable)" : $"qty x rate - the total cell read '{read}' (stamp / smudge?)");
            item.Total.Candidates.AddRange(cands[2]);
            return;
        }
        if (Agreed(1) && Agreed(2) && vals[1] is double r2 && vals[2] is double t2 && r2 > 0)
        {
            Apply(item.Rate, cands[1], r2); Apply(item.Total, cands[2], t2);
            Derive(item.Qty, Math.Round(t2 / r2, 3), $"total / rate - the qty cell read '{Read(0)}'");
            item.Qty.Candidates.AddRange(cands[0]);
            return;
        }
        if (Agreed(0) && Agreed(2) && vals[0] is double q3 && vals[2] is double t3 && q3 > 0)
        {
            Apply(item.Qty, cands[0], q3); Apply(item.Total, cands[2], t3);
            Derive(item.Rate, Math.Round(t3 / q3, 3), $"total / qty - the rate cell read '{Read(1)}'");
            item.Rate.Candidates.AddRange(cands[1]);
            return;
        }
        // readings exist but disagree with the arithmetic: flag all three - never pick silently
        foreach (var (f, cs) in fields.Zip(cands))
        {
            var dec = FieldVote.Decide(f.Name, cs);
            f.Value = dec.Value.Length > 0 ? dec.Value : f.Value;
            f.Candidates.Clear(); f.Candidates.AddRange(cs);
            f.Status = FieldStatus.Conflict;
            f.Confidence = Math.Min(f.Confidence, 0.45);
            f.Note = $"qty x rate <> total ({item.Qty.Value} x {item.Rate.Value} vs {item.Total.Value})";
        }
        item.Issues.Add("qty x rate does not match the total");
    }

    private static void Apply(FieldResult f, List<FieldCandidate> cands, double value)
    {
        var agree = cands.Where(c => ArabicText.ParseNumber(c.Value) is double v && Math.Abs(v - value) < 1e-6).ToList();
        var engines = agree.Select(c => c.Engine).Distinct().Count();
        var original = ArabicText.ParseNumber(f.Value);
        f.Candidates.Clear(); f.Candidates.AddRange(cands);
        f.Value = value.ToString("0.###", CultureInfo.InvariantCulture);
        if (original is double o && Math.Abs(o - value) < 1e-6)
        {
            f.Status = engines >= 2 ? FieldStatus.Agreed : FieldStatus.Validated;
            f.Confidence = Math.Max(f.Confidence, engines >= 2 ? 0.97 : 0.92);
        }
        else
        {
            f.Status = FieldStatus.Validated;
            f.Confidence = Math.Max(0.85, agree.Select(c => c.Confidence).DefaultIfEmpty(0).Max());
            f.Note = $"first reading '{(original?.ToString(CultureInfo.InvariantCulture) ?? "-")}' corrected by the arithmetic check";
        }
    }

    private static void Derive(FieldResult f, double value, string how)
    {
        f.Value = value.ToString("0.###", CultureInfo.InvariantCulture);
        f.Status = FieldStatus.Derived;
        f.Confidence = 0.7;
        f.Note = "derived: " + how;
    }

    /// <summary>Unreadable item numbers between two good ones are filled from the sequence (flagged).</summary>
    private static void FixItemNumbers(ScheduleRead res)
    {
        for (var i = 0; i < res.Items.Count; i++)
        {
            var it = res.Items[i];
            if (it.No.Value.Length > 0) continue;
            var prev = i > 0 ? ParseItemNo(res.Items[i - 1].No.Value) : 0;
            var next = i + 1 < res.Items.Count ? ParseItemNo(res.Items[i + 1].No.Value) : null;
            if (prev is int p && (next is null || next == p + 2))
            {
                Derive(it.No, p + 1, "item numbering (cell unreadable)");
                it.Issues.Remove("item number unreadable");
            }
        }
    }

    public static void Validate(ScheduleRead res)
    {
        var nos = res.Items.Select(i => ParseItemNo(i.No.Value)).Where(x => x != null).Select(x => x!.Value).ToList();
        var (missing, dups) = DocValidators.Sequence(nos);
        if (missing.Count > 0) res.Issues.Add(new(IssueLevel.Warn, "ITEM_GAP", $"item numbers missing: {Ranges(missing)}", null, ""));
        if (dups.Count > 0) res.Issues.Add(new(IssueLevel.Warn, "ITEM_DUP", $"item numbers twice: {string.Join(", ", dups.Take(10))}", null, ""));
        var bad = res.Items.Count(i => !i.ArithmeticOk);
        if (bad > 0) res.Issues.Add(new(IssueLevel.Warn, "ARITH", $"{bad} row(s) where qty x rate <> total after re-reading", null, ""));
        if (res.StatedTotal is double st)
        {
            if (!DocValidators.TotalOk(res.Items.Select(i => i.TotalValue), st))
                res.Issues.Add(new(IssueLevel.Warn, "GRAND_TOTAL", $"rows sum to {res.LinesTotal:N2} but the schedule total says {st:N2}", null, ""));
        }
        var review = res.Items.Count(i => i.NeedsReview);
        if (review > 0) res.Issues.Add(new(IssueLevel.Info, "REVIEW", $"{review} of {res.Items.Count} rows need a look (low confidence, derived or conflicting fields)", null, ""));
    }

    private static string Ranges(List<int> xs)
    {
        var parts = new List<string>();
        for (var i = 0; i < xs.Count;)
        {
            var j = i;
            while (j + 1 < xs.Count && xs[j + 1] == xs[j] + 1) j++;
            parts.Add(i == j ? $"{xs[i]}" : $"{xs[i]}-{xs[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts.Take(20));
    }
}
