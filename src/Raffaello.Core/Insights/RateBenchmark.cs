using Raffaello.Core.Coding;
using Raffaello.Core.Data;
using Raffaello.Core.Export;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Insights;

public static class RateSources
{
    public const string Subcontract = "SUBCONTRACT", Po = "PO", Owner = "OWNER BOQ";
}

public sealed class BenchmarkEntry
{
    public string Source { get; init; } = "";
    public string Party { get; init; } = "";
    public string Ref { get; init; } = "";
    public string Description { get; init; } = "";
    public string Unit { get; init; } = "";
    public double Rate { get; init; }
    /// <summary>Owner BOQ rate of the BOQ code(s) this line is linked to (contract link table / PO coding).</summary>
    public double? OwnerRate { get; init; }
    public string OwnerCodes { get; init; } = "";
    public double? Margin => OwnerRate is double o && Source != RateSources.Owner ? o - Rate : null;
    public double? MarginPct => OwnerRate is double o && o > 0 && Source != RateSources.Owner ? (o - Rate) / o : null;
    public double DeviationPct { get; set; }
    public bool IsOutlier { get; set; }
    public long Id { get; init; }
}

public sealed class BenchmarkGroup
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "";
    public List<BenchmarkEntry> Entries { get; init; } = new();
    private IEnumerable<double> Cost => Entries.Where(e => e.Source != RateSources.Owner && e.Rate > 0).Select(e => e.Rate);
    public int Parties => Entries.Select(e => e.Source + "|" + e.Party).Distinct().Count();
    public double Min => Cost.DefaultIfEmpty().Min();
    public double Max => Cost.DefaultIfEmpty().Max();
    public double Avg => Cost.DefaultIfEmpty().Average();
    public double Median => RobustStats.Median(Cost);
    public double Spread => Min > 0 ? Max / Min : 0;
    public double? OwnerRate => Entries.Where(e => e.Source == RateSources.Owner && e.Rate > 0).Select(e => (double?)e.Rate).Average()
                                ?? Entries.Where(e => e.OwnerRate.HasValue).Select(e => e.OwnerRate).Average();
    public double? Margin => OwnerRate is double o && Cost.Any() ? o - Avg : null;
    public double? MarginPct => OwnerRate is double o && o > 0 && Cost.Any() ? (o - Avg) / o : null;
    public int Outliers => Entries.Count(e => e.IsOutlier);
    public string Sources => string.Join(", ", Entries.GroupBy(e => e.Source).Select(g => $"{g.Key} {g.Count()}"));
}

/// <summary>
/// Rate benchmarking (roadmap 7): the same / similar item across subcontract schedules, supplier POs and the owner BOQ. Subcontract items
/// are grouped by their parsed attributes (category, stage, conduit, mount, height band, systems, size); supply lines by the coding
/// fingerprint (cable cores x size, conductor, fire rating / generic type words + sizes). Outliers are rates far from the group median;
/// margin = owner rate - cost rate wherever an owner rate is linked.
/// </summary>
public static class RateBenchmark
{
    public static string UnitKey(string u) => (u ?? "").Trim().ToUpperInvariant().TrimEnd('.') switch
    {
        "NO" or "NOS" or "NUMBER" or "EA" or "EACH" or "PCS" or "PC" or "عدد" or "SET" => "NO",
        "M" or "MTR" or "MT" or "LM" or "RM" or "M.L" or "METER" or "METRE" or "م.ط" or "مط" => "M",
        var x => x,
    };

    public static string ContractKey(Domain.ContractItem c)
    {
        if (string.IsNullOrWhiteSpace(c.Category)) return "SUB|" + Fingerprints.Key(c.Description) + "|" + UnitKey(c.Unit);
        var systems = string.Join(",", (c.Systems ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal));
        return $"SUB|{c.Category}|{c.FixStage}|{c.ConduitType}|{c.Mount}|{c.HeightBand}|{systems}|{c.SizeKey}|{Sizes(c.Description)}|{Words(c.Description)}|{UnitKey(c.Unit)}".ToUpperInvariant();
    }

    private static readonly HashSet<string> Boiler = new(StringComparer.Ordinal)
    {
        "INSTALLATION", "INSTALL", "INSTALLED", "CONNECTION", "CONNECT", "TESTING", "TEST", "COMMISSION", "COMMISSIONING", "HANDOVER", "HAND", "OVER", "LABOR", "LABOUR", "ONLY",
        "FIXING", "FIX", "PROVIDE", "PROVIDING", "MAKE", "INCLUDING", "REQUIRED", "WORK", "ITEM", "UNIT", "UNITS", "ETC", "BY", "AT", "OR", "FROM", "INTO", "ITS",
    };

    /// <summary>The words that say what the item is (boilerplate such as "installation, testing and handover ... in accordance with" removed).</summary>
    public static string Words(string description)
    {
        var d = (description ?? "").ToUpperInvariant();
        foreach (var cut in new[] { "IN ACCORDANCE", "SUBJECT TO", "RATE SHALL", "AS PER " }) { var i = d.IndexOf(cut, StringComparison.Ordinal); if (i > 0) d = d[..i]; }
        return string.Join(" ", Fingerprints.Tokens(d).Where(t => t.Length > 2 && !Boiler.Contains(t) && !char.IsDigit(t[0])).Distinct().OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>Size-like words of a description (20A, 150MM2, 1C, 42WAY ...) - items of one category differ by them.</summary>
    public static string Sizes(string description) =>
        string.Join(" ", Fingerprints.Tokens(description).Where(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+(\.\d+)?(MM2|MM|A|W|KV|KW|KVA|IN|C|WAY|WAYS|M|P|POLE)?$"))
            .Distinct().OrderBy(t => t, StringComparer.Ordinal));

    public static string SupplyKey(string description, string unit) => "SUP|" + Fingerprints.Key(description) + "|" + UnitKey(unit);

    public static List<BenchmarkGroup> Build(ProjectSnapshot s, MaterialsSnapshot? m, double outlierPct = 0.25)
    {
        // owner rates by BOQ code (owner BOQ workbook rows, then the E-Promise list when it carries rates)
        var owner = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (m != null)
            foreach (var b in m.BoqLines.Where(b => !b.IsHeading && b.Rate > 0))
                foreach (var code in new[] { b.BoqCode, b.ItemNo }.Where(x => !string.IsNullOrWhiteSpace(x)))
                    owner.TryAdd(code.Trim(), b.Rate);
        foreach (var b in s.BoqItems.Where(b => b.Rate > 0 && b.ItemCode.Length > 0)) owner.TryAdd(b.ItemCode.Trim(), b.Rate);
        double? OwnerOf(IEnumerable<string> codes)
        {
            var r = codes.Select(c => owner.TryGetValue(c.Trim(), out var v) ? (double?)v : null).Where(v => v.HasValue).ToList();
            return r.Count == 0 ? null : r.Average();
        }

        var subOf = s.Contracts.GroupBy(c => c.ContractNo.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First().Subcontractor);
        var links = s.ItemBoqs.GroupBy(l => (C: l.ContractNo.Trim().ToUpperInvariant(), N: l.ItemNo.Trim().ToUpperInvariant())).ToDictionary(g => g.Key, g => g.Select(l => l.BoqCode).ToList());
        var entries = new List<(string Key, BenchmarkEntry E)>();
        foreach (var c in s.ContractItems.Where(c => c.Rate > 0))
        {
            var codes = links.GetValueOrDefault((c.ContractNo.Trim().ToUpperInvariant(), c.ItemNo.Trim().ToUpperInvariant())) ?? new List<string>();
            var party = subOf.TryGetValue(c.ContractNo.Trim().ToUpperInvariant(), out var sub) && sub.Length > 0 ? $"{sub} ({c.ContractNo})" : c.ContractNo;
            entries.Add((ContractKey(c), new BenchmarkEntry
            {
                Id = c.Id, Source = RateSources.Subcontract, Party = party, Ref = "item " + c.ItemNo, Description = Short(c.Description), Unit = c.Unit, Rate = c.Rate,
                OwnerRate = OwnerOf(codes), OwnerCodes = string.Join(", ", codes.Take(4)) + (codes.Count > 4 ? $" (+{codes.Count - 4})" : ""),
            }));
        }
        if (m != null)
        {
            var pos = m.Pos.ToDictionary(p => p.Id);
            foreach (var l in m.PoLines.Where(l => l.Rate > 0))
            {
                var po = pos.GetValueOrDefault(l.PoId);
                entries.Add((SupplyKey(l.Description, l.Unit), new BenchmarkEntry
                {
                    Id = l.Id, Source = RateSources.Po, Party = $"{po?.Supplier} ({po?.PoNo})", Ref = $"line {l.LineNo}", Description = Short(l.Description), Unit = l.Unit, Rate = l.Rate,
                    OwnerRate = l.BoqCode.Length > 0 ? OwnerOf(new[] { l.BoqCode }) : null, OwnerCodes = l.BoqCode,
                }));
            }
            foreach (var b in m.BoqLines.Where(b => !b.IsHeading && b.Rate > 0))
                entries.Add((SupplyKey(b.Description, b.Unit), new BenchmarkEntry { Id = b.Id, Source = RateSources.Owner, Party = "OWNER BOQ", Ref = b.BoqCode.Length > 0 ? b.BoqCode : b.ItemNo, Description = Short(b.Description), Unit = b.Unit, Rate = b.Rate }));
        }
        var legacyPo = s.PurchaseOrders.ToDictionary(p => p.Id);
        foreach (var l in s.PoLines.Where(l => l.Rate > 0))
            entries.Add((SupplyKey(l.Description, l.Unit), new BenchmarkEntry { Id = -l.Id, Source = RateSources.Po, Party = $"{legacyPo.GetValueOrDefault(l.PoId)?.Supplier} ({legacyPo.GetValueOrDefault(l.PoId)?.PoNo})", Ref = $"line {l.LineNo}", Description = Short(l.Description), Unit = l.Unit, Rate = l.Rate }));
        foreach (var b in s.BoqItems.Where(b => b.Rate > 0))
            entries.Add((SupplyKey(b.Description, b.Unit), new BenchmarkEntry { Id = -b.Id, Source = RateSources.Owner, Party = "OWNER BOQ", Ref = b.ItemCode, Description = Short(b.Description), Unit = b.Unit, Rate = b.Rate }));

        var groups = new List<BenchmarkGroup>();
        foreach (var g in entries.Where(e => e.Key.Split('|').Length > 2 && e.Key.Split('|')[1].Length > 0).GroupBy(e => e.Key))
        {
            var list = g.Select(x => x.E).ToList();
            var grp = new BenchmarkGroup
            {
                Key = g.Key, Unit = list[0].Unit, Entries = list,
                Label = list.OrderBy(e => e.Description.Length).First().Description,
            };
            var cost = list.Where(e => e.Source != RateSources.Owner).ToList();
            var med = grp.Median;
            var rates = cost.Select(e => e.Rate).ToList();
            foreach (var e in cost)
            {
                e.DeviationPct = med > 0 ? (e.Rate - med) / med : 0;
                var z = rates.Count >= 4 ? Math.Abs(RobustStats.Z(e.Rate, rates)) : double.NaN;
                e.IsOutlier = Math.Abs(e.DeviationPct) >= outlierPct && (double.IsNaN(z) || z >= 3);
            }
            groups.Add(grp);
        }
        return groups.OrderByDescending(x => x.Parties).ThenByDescending(x => x.Spread).ThenBy(x => x.Label, StringComparer.Ordinal).ToList();
    }

    private static string Short(string d)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(d ?? "", @"\s+", " ").Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^(supply,?\s*)?(install,?\s*)?(and\s+)?(hand over\s+)?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @",?\s*in accordance with good engineering practice.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return t.Length > 140 ? t[..140] + "..." : t;
    }

    /// <summary>Groups that compare more than one party (or carry an owner rate) - what the screen and export show.</summary>
    public static IEnumerable<BenchmarkGroup> Comparable(IEnumerable<BenchmarkGroup> groups) => groups.Where(g => g.Parties >= 2 || g.OwnerRate.HasValue);

    public static List<ExportSheet> Sheets(IReadOnlyList<BenchmarkGroup> groups, string scope)
    {
        var cmp = Comparable(groups).ToList();
        return new List<ExportSheet>
        {
            new()
            {
                Name = "RATE GROUPS", Title = "RATE BENCHMARK - SAME ITEM ACROSS CONTRACTS, POS AND OWNER BOQ", Subtitle = scope,
                Columns = new() { new("ITEM", ColumnKind.Text, 60), new("UNIT"), new("SOURCES", ColumnKind.Text, 26), new("PARTIES", ColumnKind.Integer), new("MIN", ColumnKind.Money), new("MAX", ColumnKind.Money),
                    new("AVG", ColumnKind.Money), new("MEDIAN", ColumnKind.Money), new("MAX / MIN", ColumnKind.Number), new("OUTLIERS", ColumnKind.Integer), new("OWNER RATE", ColumnKind.Money), new("MARGIN", ColumnKind.Money), new("MARGIN %", ColumnKind.Percent) },
                Rows = cmp.Select(g => new object?[] { g.Label, g.Unit, g.Sources, g.Parties, g.Min, g.Max, g.Avg, g.Median, g.Spread, g.Outliers, g.OwnerRate, g.Margin, g.MarginPct }).ToList(),
            },
            new()
            {
                Name = "RATE DETAIL", Title = "RATE BENCHMARK - EVERY RATE", Subtitle = scope + "  |  margin = owner rate - subcontract / supply rate",
                Columns = new() { new("ITEM GROUP", ColumnKind.Text, 50), new("SOURCE"), new("PARTY", ColumnKind.Text, 30), new("REF"), new("DESCRIPTION", ColumnKind.Text, 60), new("UNIT"), new("RATE", ColumnKind.Money),
                    new("VS MEDIAN", ColumnKind.Percent), new("OUTLIER"), new("OWNER CODES", ColumnKind.Text, 30), new("OWNER RATE", ColumnKind.Money), new("MARGIN", ColumnKind.Money), new("MARGIN %", ColumnKind.Percent) },
                Rows = cmp.SelectMany(g => g.Entries.OrderBy(e => e.Source).ThenBy(e => e.Rate).Select(e => new object?[]
                    { g.Label, e.Source, e.Party, e.Ref, e.Description, e.Unit, e.Rate, e.Source == RateSources.Owner ? null : e.DeviationPct, e.IsOutlier ? "OUTLIER" : "", e.OwnerCodes, e.OwnerRate, e.Margin, e.MarginPct })).ToList(),
            },
        };
    }
}
