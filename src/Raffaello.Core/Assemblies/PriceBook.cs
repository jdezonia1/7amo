using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Coding;
using Raffaello.Core.Import;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Assemblies;

/// <summary>A PO line seen as a price (from the Materials module).</summary>
public sealed record PoPrice(string PoNo, string Supplier, DateTime? Date, int LineNo, string ItemCode, string Description, string Unit, double Rate, double LengthPerPcs, double Qty);

/// <summary>
/// [assemblies] Component prices with their source and date. Order: manual price for the same specification, PO line (Materials
/// module, matched by the Coding fingerprints: cables by cores x size + CU/AL + fire rating, others by type words and sizes), supplier
/// price list, then the template default (done by the calculator). Units are converted (KM -> M, ROLL of 100 Y -> M, PCS of
/// conduit -> M with the stick length).
/// </summary>
public sealed class PriceBook : IPriceResolver
{
    private readonly List<AsmPrice> _manual;
    private readonly List<AsmPrice> _list;
    private readonly List<PoPrice> _po;
    private readonly AssemblySettings _settings;
    private readonly double _stickLen;

    public PriceBook(IEnumerable<AsmPrice> prices, IEnumerable<PoPrice> po, AssemblySettings settings, double stickLen = 3)
    {
        var all = prices.ToList();
        _manual = all.Where(p => p.Source == PriceSources.Manual).ToList();
        _list = all.Where(p => p.Source != PriceSources.Manual).ToList();
        _po = po.Where(p => p.Rate > 0).ToList();
        _settings = settings;
        _stickLen = stickLen > 0 ? stickLen : 3;
    }

    public int PoLines => _po.Count;
    public int ListPrices => _list.Count;
    public int ManualPrices => _manual.Count;

    /// <summary>Key of a specification: cable key for cables, sorted tokens otherwise.</summary>
    public static string KeyOf(string spec)
    {
        var k = Fingerprints.Key(spec);
        return k.Length > 0 ? k : Fingerprints.NormalizeDescription(spec);
    }

    public PriceHit? Find(string spec, string unit, string kind)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var key = KeyOf(spec);
        // 1. manual (same key, else a near-identical description)
        var manual = Best(_manual, spec, key, unit, 0.85);
        if (manual != null) return Hit(manual.Value.Price, manual.Value.Row, PriceSources.Manual, manual.Value.Score, manual.Value.Note);
        // 2. PO lines
        PriceHit? best = null; double bestScore = 0; DateTime bestDate = DateTime.MinValue;
        foreach (var p in _po)
        {
            var score = Fingerprints.Compare(spec, p.Description);
            if (score < _settings.PoMatchScore) continue;
            var conv = ConvertPrice(p.Rate, p.Unit, unit, p.Description, p.LengthPerPcs, _stickLen);
            if (conv is null) continue;
            var date = p.Date ?? DateTime.MinValue;
            if (score > bestScore + 1e-9 || Math.Abs(score - bestScore) < 1e-9 && date > bestDate)
            {
                best = new PriceHit(Math.Round(conv.Value.Price, 4), PriceSources.Po, $"{p.PoNo} line {p.LineNo}: {p.Description}", p.Date, p.Supplier, score,
                    conv.Value.Note.Length > 0 ? conv.Value.Note : (score < 1 ? $"match {score:P0}" : ""));
                bestScore = score; bestDate = date;
            }
        }
        if (best != null) return best;
        // 3. price list
        var listed = Best(_list, spec, key, unit, _settings.PriceListMatchScore);
        return listed != null ? Hit(listed.Value.Price, listed.Value.Row, PriceSources.PriceList, listed.Value.Score, listed.Value.Note) : null;
    }

    private static PriceHit Hit(double price, AsmPrice r, string source, double score, string note) =>
        new(Math.Round(price, 4), source, string.Join(" ", new[] { r.SourceRef, r.ItemCode, r.Description }.Where(s => s.Length > 0)), r.PriceDate, r.Supplier, score,
            string.Join("; ", new[] { note, score < 1 ? $"match {score:P0}" : "" }.Where(s => s.Length > 0)));

    private (AsmPrice Row, double Price, double Score, string Note)? Best(List<AsmPrice> rows, string spec, string key, string unit, double min)
    {
        (AsmPrice, double, double, string)? best = null;
        foreach (var r in rows)
        {
            var score = r.Key.Length > 0 && r.Key == key ? 1.0 : Fingerprints.Compare(spec, r.Description);
            if (score < min) continue;
            var conv = ConvertPrice(r.Price, r.Unit, unit, r.Description, 0, _stickLen);
            if (conv is null) continue;
            if (best is null || score > best.Value.Item3 + 1e-9 || Math.Abs(score - best.Value.Item3) < 1e-9 && (r.PriceDate ?? DateTime.MinValue) > (best.Value.Item1.PriceDate ?? DateTime.MinValue))
                best = (r, conv.Value.Price, score, conv.Value.Note);
        }
        return best;
    }

    /// <summary>Price per component unit from a price per source unit; null when the units cannot be compared.</summary>
    public static (double Price, string Note)? ConvertPrice(double price, string? fromUnit, string? toUnit, string description, double lengthPerPcs, double stickLen)
    {
        var f = Units.Normalize(fromUnit); var t = Units.Normalize(toUnit);
        if (f.Length == 0 || t.Length == 0 || f == t) return (price, "");
        if (f == Units.Km && t == Units.M) return (price / 1000, "PO price per KM / 1000");
        if (f == Units.M && t == Units.Km) return (price * 1000, "");
        if (f == Units.Roll && t == Units.M)
        {
            var len = RollLength(description);
            return len is { } l ? (price / l, $"roll of {l:0.##} m") : null;
        }
        if (f == Units.Pcs && t == Units.M)
        {
            var len = lengthPerPcs > 0 ? lengthPerPcs : stickLen;
            return (price / len, $"price per {len:0.##} m length");
        }
        if (Units.Family(f) == "COUNT" && Units.Family(t) == "COUNT") return (price, "");
        if (t is "PT" or "NO" or "END" && Units.Family(f) == "COUNT") return (price, "");
        return null;
    }

    /// <summary>Roll / coil length from the description: "(100Y)" = 91.44 m, "100M" / "100 m".</summary>
    public static double? RollLength(string description)
    {
        var d = description ?? "";
        var y = Regex.Match(d, @"(\d+(?:\.\d+)?)\s*(?:Y|YD|YDS|YARDS?)\b", RegexOptions.IgnoreCase);
        if (y.Success) return Math.Round(double.Parse(y.Groups[1].Value, CultureInfo.InvariantCulture) * 0.9144, 2);
        var m = Regex.Match(d, @"(\d+(?:\.\d+)?)\s*(?:M|MTR|MTRS|METERS?|METRES?)\b(?!\s*M)", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 10) return v;
        return null;
    }

    /// <summary>PO lines of the Materials module as prices.</summary>
    public static List<PoPrice> FromMaterials(MaterialsSnapshot snap)
    {
        var pos = snap.Pos.ToDictionary(p => p.Id);
        return snap.PoLines.Where(l => l.Rate > 0 && pos.ContainsKey(l.PoId)).Select(l =>
        {
            var po = pos[l.PoId];
            return new PoPrice(po.PoNo, po.Supplier, po.PoDate, l.LineNo, l.ItemCode, l.Description, l.Unit, l.Rate, l.LengthPerPcs, l.Qty);
        }).ToList();
    }
}

/// <summary>Result of reading a supplier price list.</summary>
public sealed class PriceListImport
{
    public string FileName { get; init; } = "";
    public List<AsmPrice> Prices { get; } = new();
    public List<string> Issues { get; } = new();
    public string Summary => $"{Prices.Count} prices from {Path.GetFileName(FileName)}" + (Issues.Count > 0 ? $", {Issues.Count} rows skipped" : "");
}

/// <summary>[assemblies] Reads a supplier price list (Excel / CSV): description + price columns found by header, optional unit, code, supplier, date.</summary>
public static class PriceListImporter
{
    private static readonly string[] DescH = { "DESCRIPTION", "ITEM DESCRIPTION", "MATERIAL", "ITEM", "DESC", "PRODUCT", "الوصف", "البيان" };
    private static readonly string[] PriceH = { "UNIT PRICE", "PRICE", "RATE", "UNIT RATE", "NET PRICE", "PRICE SAR", "سعر الوحدة", "السعر" };
    private static readonly string[] UnitH = { "UNIT", "UOM", "U/M", "الوحدة" };
    private static readonly string[] CodeH = { "CODE", "ITEM CODE", "MATERIAL CODE", "PART NO", "CAT NO", "REF" };
    private static readonly string[] SupplierH = { "SUPPLIER", "VENDOR", "BRAND", "MANUFACTURER" };
    private static readonly string[] DateH = { "DATE", "PRICE DATE", "VALID FROM", "QUOTATION DATE" };

    public static PriceListImport Read(string path, string supplier = "", DateTime? date = null, string? sheet = null)
    {
        var t = TableReader.Read(path, sheet);
        var res = new PriceListImport { FileName = path };
        if (!t.Has(DescH) || !t.Has(PriceH)) { res.Issues.Add("No DESCRIPTION / PRICE header found (first row with two filled cells is the header)."); return res; }
        foreach (var r in t.Rows)
        {
            var desc = r.Get(DescH);
            var price = r.GetNumber(PriceH);
            if (desc.Length == 0) continue;
            if (price is not > 0) { res.Issues.Add($"row {r.RowNumber}: no price for '{Trim(desc)}'"); continue; }
            res.Prices.Add(new AsmPrice
            {
                Key = PriceBook.KeyOf(desc), Description = desc, Unit = Units.Normalize(r.Get(UnitH)), Price = price.Value, Source = PriceSources.PriceList,
                SourceRef = Path.GetFileName(path), Supplier = r.Get(SupplierH) is { Length: > 0 } s ? s : supplier, ItemCode = r.Get(CodeH),
                PriceDate = r.GetDate(DateH) ?? date,
            });
        }
        return res;
    }

    private static string Trim(string s) => s.Length > 50 ? s[..50] + "..." : s;
}
