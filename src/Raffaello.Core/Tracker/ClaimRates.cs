using System.Globalization;
using ClosedXML.Excel;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Mapping;

namespace Raffaello.Core.Tracker;

/// <summary>Rate status of a claim line: OK (contract / manual rate), CHECK (the item choice is ambiguous), MISSING (no rate - never 0 silently).</summary>
public static class RateStatus
{
    public const string Ok = "OK";
    public const string Check = "CHECK";
    public const string Missing = "MISSING";
}

/// <summary>The rate found for a claim line and where it came from.</summary>
public sealed record ClaimRate(double? Rate, double StagePct, string Status, string Source, string Reason, string ItemNo = "", string ContractNo = "")
{
    public bool IsMissing => Rate is null;
    public static ClaimRate Missing(string reason, string contractNo = "") => new(null, 1, RateStatus.Missing, "MISSING", reason, "", contractNo);
}

/// <summary>Amounts of one claim line: QTY x RATE, after SITE %, after WIR % (= the tracker INVOICE AMOUNT), and after the contract stage payment %.</summary>
public sealed record ClaimAmount(double Qty, double? Rate, double Amount, double AfterSite, double AfterWir, double Payable)
{
    public static ClaimAmount Of(ClaimLine c, ClaimRate r) => Of(c.Qty, c.SitePct, c.WirPct, r);

    public static ClaimAmount Of(double qty, double sitePct, double wirPct, ClaimRate r)
    {
        if (r.Rate is not double rate) return new(qty, null, 0, 0, 0, 0);
        var amount = qty * rate;
        var afterSite = amount * sitePct;
        var afterWir = afterSite * wirPct;
        return new(qty, rate, amount, afterSite, afterWir, afterWir * (r.StagePct > 0 ? r.StagePct : 1));
    }
}

/// <summary>
/// Manual / imported rate rules, stored as <see cref="MappingRule"/> rows (Kind RATE, audited like every write):
/// ContractNo = "SUB:&lt;SUBCONTRACTOR&gt;" (or "SUB:*" for every subcontractor), MatchKey = "STAGE|ITEM" or "STAGE|ITEM|INV 3" / "STAGE|ITEM|INV 1-4"
/// (an invoice period), Target = "rate" or "rate;stagePct" (invariant culture).
/// </summary>
public static class RateRules
{
    public const string Kind = "RATE";
    public const string AnySub = "*";

    /// <summary>"SUB:ROOTS", "SUB:*" (every subcontractor), optionally for one building: "SUB:ROOTS@BRANDED", "SUB:*@HOTEL".</summary>
    public static string Scope(string? subcontractor, string? building = null) =>
        "SUB:" + (string.IsNullOrWhiteSpace(subcontractor) ? AnySub : subcontractor.Trim().ToUpperInvariant())
        + (string.IsNullOrWhiteSpace(building) ? "" : "@" + building.Trim().ToUpperInvariant());

    public static string Key(string stage, string item, int? invoiceFrom = null, int? invoiceTo = null)
    {
        var k = $"{(stage ?? "").Trim().ToUpperInvariant()}|{(item ?? "").Trim().ToUpperInvariant()}";
        if (invoiceFrom is int a) k += invoiceTo is int b && b != a ? $"|INV {a}-{b}" : $"|INV {a}";
        return k;
    }

    public static string Target(double rate, double? stagePct) =>
        rate.ToString("0.####", CultureInfo.InvariantCulture) + (stagePct is double p ? ";" + p.ToString("0.####", CultureInfo.InvariantCulture) : "");

    public static (double Rate, double? StagePct)? ParseTarget(string target)
    {
        var p = (target ?? "").Split(';');
        if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return null;
        double? pct = p.Length > 1 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;
        return (r, pct);
    }

    /// <summary>Invoice period of a match key (null = every invoice).</summary>
    public static (int From, int To)? Period(string matchKey)
    {
        var parts = matchKey.Split('|');
        if (parts.Length < 3 || !parts[2].StartsWith("INV", StringComparison.OrdinalIgnoreCase)) return null;
        var range = parts[2][3..].Trim().Split('-', StringSplitOptions.TrimEntries);
        if (!int.TryParse(range[0], out var a)) return null;
        var b = range.Length > 1 && int.TryParse(range[1], out var bb) ? bb : a;
        return (Math.Min(a, b), Math.Max(a, b));
    }
}

/// <summary>
/// Resolves a rate for every claim line, in this order: (1) manual rate for the subcontractor and the line's invoice period,
/// (2) manual rate for the subcontractor, (3) imported default rate for every subcontractor (MAPPING sheet),
/// (4) the subcontractor's contract item chosen by the mapping resolver (stage | system | height band).
/// When nothing fits the rate is MISSING with the reason (never 0).
/// </summary>
public sealed class RateBook
{
    private readonly List<Contract> _contracts;
    private readonly List<ContractItem> _items;
    private readonly List<ContractItemBoq> _links;
    private readonly List<BoqItem> _boq;
    private readonly List<MappingRule> _rules;
    private readonly ILookup<string, MappingRule> _rateRules;
    private readonly MappingOptions _options;
    private readonly IItemResolver _resolver = new DefaultItemResolver();
    private readonly Dictionary<string, ClaimRate> _cache = new();
    private readonly Dictionary<string, MappingContext?> _ctx = new();

    public RateBook(IEnumerable<Contract> contracts, IEnumerable<ContractItem> items, IEnumerable<ContractItemBoq> links, IEnumerable<BoqItem> boq,
        IEnumerable<MappingRule> rules, MappingOptions? options = null)
    {
        _contracts = contracts.ToList();
        _items = items.ToList();
        _links = links.ToList();
        _boq = boq.ToList();
        _rules = rules.ToList();
        _rateRules = _rules.Where(r => r.Kind == RateRules.Kind).ToLookup(r => r.ContractNo.ToUpperInvariant());
        _options = options ?? MappingOptions.Default;
    }

    public static RateBook From(ProjectSnapshot s, MappingOptions? options = null) => new(s.Contracts, s.ContractItems, s.ItemBoqs, s.BoqItems, s.MappingRules, options);

    private static string Norm(string s) => new string((s ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The subcontractor's contract: same name (or one name starts with the other, e.g. MARUF / MARUF BIN SHAHEN), building first.</summary>
    public Contract? ContractFor(string subcontractor, string building)
    {
        var n = Norm(subcontractor);
        if (n.Length == 0) return null;
        var cands = _contracts.Where(c => Norm(c.Subcontractor) == n).ToList();
        if (cands.Count == 0)
            cands = _contracts.Where(c => Norm(c.Subcontractor) is { Length: >= 3 } cn && (n.StartsWith(cn) || cn.StartsWith(n))).ToList();
        return cands.OrderByDescending(c => string.Equals(c.Building, building, StringComparison.OrdinalIgnoreCase) ? 2 : c.Building.Length == 0 ? 1 : 0)
            .ThenByDescending(c => c.Status == "ACTIVE").ThenByDescending(c => c.SignedAt).FirstOrDefault();
    }

    private MappingRule? ManualRule(string scope, string stage, string item, int invoiceNo, bool periodOnly)
    {
        var key = RateRules.Key(stage, item);
        foreach (var r in _rateRules[scope.ToUpperInvariant()].OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Id))
        {
            var parts = r.MatchKey.Split('|');
            if (parts.Length < 2 || !string.Equals($"{parts[0]}|{parts[1]}", key, StringComparison.OrdinalIgnoreCase)) continue;
            var period = RateRules.Period(r.MatchKey);
            if (periodOnly ? period is { } p && invoiceNo >= p.From && invoiceNo <= p.To : period is null) return r;
        }
        return null;
    }

    private static ClaimRate FromRule(MappingRule r, string source)
    {
        var t = RateRules.ParseTarget(r.Target);
        if (t is null) return ClaimRate.Missing($"rate rule {r.MatchKey} has no number ('{r.Target}')");
        return new(t.Value.Rate, t.Value.StagePct ?? 1, RateStatus.Ok, source, $"{source}: {r.MatchKey} = {t.Value.Rate:0.##}{(r.Note.Length > 0 ? " (" + r.Note + ")" : "")}");
    }

    /// <summary>Rate for one claim line (cached per subcontractor x building x invoice x stage x item x band).</summary>
    public ClaimRate Resolve(ClaimLine c, string band = HeightBands.Low) => Resolve(c.Subcontractor, c.Building, c.InvoiceNo, c.Stage, c.Item, band);

    public ClaimRate Resolve(string subcontractor, string building, int invoiceNo, string stage, string item, string band = HeightBands.Low)
    {
        var cacheKey = $"{subcontractor}|{building}|{invoiceNo}|{stage}|{item}|{band}".ToUpperInvariant();
        if (_cache.TryGetValue(cacheKey, out var hit)) return hit;
        var res = ResolveCore(subcontractor ?? "", building ?? "", invoiceNo, stage ?? "", item ?? "", band);
        _cache[cacheKey] = res;
        return res;
    }

    private ClaimRate ResolveCore(string sub, string building, int invoiceNo, string stage, string item, string band)
    {
        // most specific first: this subcontractor in this building, this subcontractor, every subcontractor in this building, everyone;
        // within each, an invoice-period rate before the plain one
        foreach (var (scope, label) in new[] { (RateRules.Scope(sub, building), "MANUAL"), (RateRules.Scope(sub), "MANUAL"), (RateRules.Scope(null, building), "DEFAULT"), (RateRules.Scope(null), "DEFAULT") })
        {
            if (ManualRule(scope, stage, item, invoiceNo, periodOnly: true) is { } p) return FromRule(p, $"{label} INV {invoiceNo}");
            if (ManualRule(scope, stage, item, invoiceNo, periodOnly: false) is { } m) return FromRule(m, label);
        }

        var contract = ContractFor(sub, building);
        if (contract is null) return ClaimRate.Missing($"no contract for {sub} ({building}) - import it on CONTRACTS & BOQ or set a rate");
        if (!_ctx.TryGetValue(contract.ContractNo, out var ctx))
        {
            ctx = _items.Any(i => i.ContractNo == contract.ContractNo) ? new MappingContext(contract.ContractNo, _items, _links, _boq, _rules) { Options = _options } : null;
            _ctx[contract.ContractNo] = ctx;
        }
        if (ctx is null) return ClaimRate.Missing($"contract {contract.ContractNo} has no rate schedule imported - import its items or set a rate", contract.ContractNo);
        var (system, _) = SystemNames.Normalize(item);
        var m2 = _resolver.Resolve(stage, system, band, ctx);
        if (m2.Item is null) return ClaimRate.Missing($"contract {contract.ContractNo}: {m2.Explanation}", contract.ContractNo);
        if (m2.Item.Rate <= 0) return ClaimRate.Missing($"contract {contract.ContractNo} item {m2.Item.ItemNo} has no rate", contract.ContractNo);
        var status = Confidence.NeedsConfirmation(m2.Confidence) ? RateStatus.Check : RateStatus.Ok;
        return new(m2.Item.Rate, m2.Item.StagePct > 0 ? m2.Item.StagePct : 1, status, $"CONTRACT {contract.ContractNo} #{m2.Item.ItemNo}", m2.Explanation, m2.Item.ItemNo, contract.ContractNo);
    }

    /// <summary>
    /// Rate and amounts of a line. The accepted &gt; 4.5 m part is priced at the high-band item when the contract has one (the rest at the normal item).
    /// </summary>
    public (ClaimRate Rate, ClaimAmount Amount) Price(ClaimLine c)
    {
        var low = Resolve(c);
        var high = HeightCheck.AcceptedHigh(c);
        if (Math.Abs(high) < LedgerRules.Eps || low.IsMissing || Math.Abs(c.Qty) < LedgerRules.Eps) return (low, ClaimAmount.Of(c, low));
        var hr = Resolve(c, HeightBands.High);
        if (hr.IsMissing) return (low, ClaimAmount.Of(c, low));
        var blended = ((c.Qty - high) * low.Rate!.Value + high * hr.Rate!.Value) / c.Qty;
        var r = low with { Rate = blended, Reason = low.Reason + $"; {high:0.##} above 4.5 m at {hr.Rate:0.##}" };
        return (r, ClaimAmount.Of(c, r));
    }
}

/// <summary>One rate row of an imported MAPPING sheet (stage | item -> contract rate, with a confidence).</summary>
public sealed record RateMappingRow(string Subcontractor, string Stage, string Item, double Rate, double? StagePct, string Confidence, string ItemNo, int Row);

/// <summary>
/// Reads a "stage|item -> contract rate" MAPPING sheet (e.g. RECON_TOOLS\REMAINING_VALUE_HOTEL.xlsx). Columns are found by header text:
/// STAGE, ITEM, RATE (required; a KEY column "STAGE|ITEM" may replace STAGE + ITEM); SUBCONTRACTOR, STAGE % / PAY %, CONFIDENCE, ITEM NO (optional).
/// </summary>
public static class RateMappingImporter
{
    public const string Sheet = "MAPPING";

    public static (List<RateMappingRow> Rows, List<string> Issues) Read(string path)
    {
        var rows = new List<RateMappingRow>();
        var issues = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(fs);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Trim().Equals(Sheet, StringComparison.OrdinalIgnoreCase));
        if (ws is null) { issues.Add($"no sheet '{Sheet}' in {Path.GetFileName(path)}"); return (rows, issues); }
        var used = ws.RangeUsed();
        if (used is null) { issues.Add("MAPPING sheet is empty"); return (rows, issues); }
        int hdr = 0;
        var cols = new Dictionary<string, int>();
        var first = used.FirstRow().RowNumber();
        for (var r = first; r <= Math.Min(used.LastRow().RowNumber(), first + 15) && hdr == 0; r++)
        {
            var map = new Dictionary<string, int>();
            foreach (var cell in ws.Row(r).CellsUsed())
            {
                var t = cell.GetString().Trim().ToUpperInvariant();
                if (t.Length > 0) map.TryAdd(t, cell.Address.ColumnNumber);
            }
            int? Find(params string[] names)
            {
                foreach (var n in names) if (map.TryGetValue(n, out var c)) return c;
                foreach (var n in names) { var hit = map.FirstOrDefault(kv => kv.Key.StartsWith(n + " ") || kv.Key.StartsWith(n + "(")); if (hit.Key != null) return hit.Value; }
                return null;
            }
            var rate = Find("RATE", "CONTRACT RATE", "UNIT RATE", "NEW RATE");
            var stage = Find("STAGE");
            var item = Find("ITEM", "SYSTEM");
            var key = Find("KEY");
            if (rate is null || (stage is null || item is null) && key is null) continue;
            hdr = r;
            cols["RATE"] = rate.Value;
            if (stage is int s) cols["STAGE"] = s;
            if (item is int i) cols["ITEM"] = i;
            if (key is int k) cols["KEY"] = k;
            if (Find("SUBCONTRACTOR") is int sc) cols["SUB"] = sc;
            if (Find("STAGE %", "PAY %", "STAGE PCT", "PAYMENT %") is int sp) cols["PCT"] = sp;
            if (Find("USED CONFIDENCE", "CONFIDENCE", "AUTO CONFIDENCE") is int cf) cols["CONF"] = cf;
            if (Find("USED SR", "ITEM NO", "CONTRACT ITEM", "SR") is int no) cols["NO"] = no;
        }
        if (hdr == 0) { issues.Add("MAPPING: no header row with STAGE, ITEM (or KEY) and RATE"); return (rows, issues); }
        for (var r = hdr + 1; r <= used.LastRow().RowNumber(); r++)
        {
            string Get(string c) => cols.TryGetValue(c, out var n) ? ws.Cell(r, n).GetString().Trim() : "";
            var stage = Get("STAGE").ToUpperInvariant();
            var item = Get("ITEM").ToUpperInvariant();
            if ((stage.Length == 0 || item.Length == 0) && Get("KEY").Split('|') is { Length: >= 2 } kp) { stage = kp[^2].Trim().ToUpperInvariant(); item = kp[^1].Trim().ToUpperInvariant(); }
            if (stage.Length == 0 && item.Length == 0) continue;
            var rateCell = ws.Cell(r, cols["RATE"]);
            double? rate = rateCell.TryGetValue<double>(out var rv) ? rv
                : double.TryParse(rateCell.GetString().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var rp) ? rp : null;
            if (rate is null || rate <= 0) { issues.Add($"row {r}: {stage}|{item} has no rate - skipped (stays MISSING)"); continue; }
            double? pct = null;
            if (cols.TryGetValue("PCT", out var pc) && ws.Cell(r, pc).TryGetValue<double>(out var pv) && pv > 0) pct = pv > 1 ? pv / 100 : pv;
            rows.Add(new(Get("SUB").ToUpperInvariant(), stage, item, rate.Value, pct, Get("CONF").ToUpperInvariant(), Get("NO"), r));
        }
        return (rows, issues);
    }

    /// <summary>The building a mapping file is for (HOTEL / BRANDED in its name), else null.</summary>
    public static string? BuildingOf(string file)
    {
        var n = Path.GetFileName(file).ToUpperInvariant();
        return n.Contains("HOTEL") ? Buildings.Hotel : n.Contains("BRANDED") ? Buildings.Branded : null;
    }

    /// <summary>Rules for the rows: one per subcontractor (blank = every subcontractor) x stage|item, for the building when given; the last row wins on duplicates.</summary>
    public static List<MappingRule> ToRules(IEnumerable<RateMappingRow> rows, string file, string? building = null) =>
        rows.GroupBy(r => (RateRules.Scope(r.Subcontractor, building), RateRules.Key(r.Stage, r.Item)))
            .Select(g => g.Last())
            .Select(r => new MappingRule
            {
                Kind = RateRules.Kind, ContractNo = RateRules.Scope(r.Subcontractor, building), MatchKey = RateRules.Key(r.Stage, r.Item),
                Target = RateRules.Target(r.Rate, r.StagePct), UseCount = 1,
                Note = $"MAPPING {Path.GetFileName(file)} row {r.Row}{(r.Confidence.Length > 0 ? " CONFIDENCE " + r.Confidence : "")}{(r.ItemNo.Length > 0 ? " item " + r.ItemNo : "")}",
            }).ToList();
}