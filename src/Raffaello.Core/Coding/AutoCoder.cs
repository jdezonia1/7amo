using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Coding;

/// <summary>A line that needs a BOQ no. / cost code / budget resource.</summary>
public sealed record CodingRequest(string Supplier, string ItemCode, string Description, string Unit, long? PoId = null);

/// <summary>One suggested code with its score and where it came from.</summary>
public sealed record CodeCandidate(string BoqCode, string CostCode, string BudgetResourceCode, string ResourceCode, string Description, string Unit,
    string Source, double Score, string Why)
{
    public string Label => $"{BoqCode}  {Score:P0}  {Source}";
}

public sealed class CodingSuggestion
{
    public required CodingRequest Request { get; init; }
    public List<CodeCandidate> Top { get; init; } = new();
    /// <summary>AUTO / REVIEW / ASK (see <see cref="CodeStatus"/>).</summary>
    public string Status { get; init; } = CodeStatus.Ask;
    public CodeCandidate? Best => Top.FirstOrDefault();
    public string Provenance => Best is null ? "no candidate" : $"{Best.Source} {Best.Score:P0}: {Best.Why}";
}

/// <summary>Where candidate codes come from (one pool per run).</summary>
public sealed class CodingSources
{
    public List<MatCodeMemory> Memory { get; init; } = new();
    /// <summary>Owner BOQ / ERP budget list (E-Promise).</summary>
    public List<BoqItem> BudgetList { get; init; } = new();
    /// <summary>Lines already invoiced (subcontract and supplier invoices).</summary>
    public List<SubInvoiceLine> InvoicedLines { get; init; } = new();
    public List<InvoiceTemplateRow> TemplateRows { get; init; } = new();
    /// <summary>PO lines whose codes were confirmed earlier.</summary>
    public List<MatPoLine> CodedPoLines { get; init; } = new();
    public List<MatPoScope> PoScope { get; init; } = new();
    /// <summary>Owner BOQ rows imported in the BOQ screen (for MOS / variations).</summary>
    public List<Boq.BoqLine> OwnerBoq { get; init; } = new();

    public static CodingSources From(ProjectSnapshot p, MaterialsSnapshot m) => new()
    {
        Memory = m.CodeMemory, BudgetList = p.BoqItems, InvoicedLines = p.SubInvoiceLines.Where(l => l.Kind == "ITEM" && l.BoqCode.Length > 0).ToList(),
        TemplateRows = p.TemplateRows.Where(r => r.Kind == "ITEM" && r.BoqCode.Length > 0).ToList(),
        CodedPoLines = m.PoLines.Where(l => l.BoqCode.Length > 0 && l.CodeStatus is CodeStatus.Confirmed or CodeStatus.Manual or CodeStatus.Document).ToList(),
        PoScope = m.PoScope, OwnerBoq = m.BoqLines.Where(b => !b.IsHeading && b.BoqCode.Length > 0).ToList(),
    };
}

/// <summary>
/// Auto-coding of lines without BOQ no. / cost code / budget resource: (1) exact history (learned confirmations by supplier code,
/// normalised description or fingerprint; the PO's own scope-of-work sheet), (2) attribute fingerprint, (3) char n-gram TF-IDF
/// similarity against past invoiced lines and the E-Promise list. The unit must agree. Top-3 with score; high = auto-filled,
/// medium = filled and flagged, low = the user picks. Every confirmation is learned. Reused for owner MOS and BOQ items.
/// </summary>
public sealed class AutoCoder
{
    private readonly CodingSources _src;
    private readonly double _high, _medium;
    private readonly List<CodeCandidate> _pool = new();
    private readonly TextIndex _index = new();
    private readonly Dictionary<string, List<MatCodeMemory>> _memory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoqItem> _budgetByCode;
    /// <summary>Pool entries by cable cores x size, so the fingerprint stage never depends on the text ranking.</summary>
    private readonly Dictionary<string, List<int>> _cables = new(StringComparer.Ordinal);
    private readonly List<CableSpec?> _poolCable = new();

    public AutoCoder(CodingSources sources, double high = 0.85, double medium = 0.6)
    {
        _src = sources; _high = high; _medium = medium;
        _budgetByCode = sources.BudgetList.GroupBy(b => b.ItemCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var m in sources.Memory)
        {
            var k = m.KeyType + "|" + m.Key;
            if (!_memory.TryGetValue(k, out var l)) _memory[k] = l = new List<MatCodeMemory>();
            l.Add(m);
        }
        void Add(CodeCandidate c)
        {
            if (c.Description.Trim().Length == 0 || c.BoqCode.Length == 0) return;
            _pool.Add(c); _index.Add(c.Description);
            var spec = Fingerprints.Cable(c.Description);
            _poolCable.Add(spec);
            if (spec != null)
            {
                if (!_cables.TryGetValue(spec.CoreSize, out var l)) _cables[spec.CoreSize] = l = new List<int>();
                l.Add(_pool.Count - 1);
            }
        }
        foreach (var l in sources.InvoicedLines)
            Add(new(l.BoqCode, l.CostCode, l.BudgetResourceCode, "", l.BoqDescription.Length > 0 ? l.BoqDescription : l.Description, l.Unit, "INVOICED", 0, $"invoiced line {l.ItemNo}"));
        foreach (var l in sources.TemplateRows)
            Add(new(l.BoqCode, l.CostCode, l.BudgetResourceCode, "", l.Description, l.Unit, "INVOICED", 0, $"invoice template item {l.ItemNo}"));
        foreach (var l in sources.CodedPoLines)
            Add(new(l.BoqCode, l.CostCode, l.BudgetResourceCode, l.ResourceCode, l.Description, l.Unit, "PO HISTORY", 0, $"PO line {l.LineNo} ({l.CodeStatus})"));
        foreach (var b in sources.BudgetList)
            Add(new(b.ItemCode, b.CostCode, b.BudgetResourceCode, "", b.Description, b.Unit, "E-PROMISE", 0, $"budget list {b.ItemCode}"));
        foreach (var b in sources.OwnerBoq)
            Add(new(b.BoqCode, "", "", "", b.Description, b.Unit, "OWNER BOQ", 0, $"owner BOQ {b.BoqCode}"));
    }

    public int PoolSize => _pool.Count;

    /// <summary>Fills cost code / budget resource from the E-Promise row of the chosen BOQ code when the source lacks them.</summary>
    private CodeCandidate Complete(CodeCandidate c)
    {
        if ((c.CostCode.Length > 0 && c.BudgetResourceCode.Length > 0) || !_budgetByCode.TryGetValue(c.BoqCode, out var b)) return c;
        return c with { CostCode = c.CostCode.Length > 0 ? c.CostCode : b.CostCode, BudgetResourceCode = c.BudgetResourceCode.Length > 0 ? c.BudgetResourceCode : b.BudgetResourceCode };
    }

    public CodingSuggestion Suggest(CodingRequest r)
    {
        var cands = new List<CodeCandidate>();
        var fp = Fingerprints.Key(r.Description);
        var desc = Fingerprints.NormalizeDescription(r.Description);
        var cable = Fingerprints.Cable(r.Description);

        // (1) exact history: learned confirmations
        void FromMemory(string type, string key, double score, string what)
        {
            if (key.Length == 0 || !_memory.TryGetValue(type + "|" + key, out var hits)) return;
            foreach (var m in hits.Where(m => Units.Agree(m.Unit, r.Unit)).OrderByDescending(m => m.Uses))
                cands.Add(new(m.BoqCode, m.CostCode, m.BudgetResourceCode, m.ResourceCode, m.Description, m.Unit, "HISTORY", score, $"{what} confirmed {m.Uses}x by {m.ConfirmedBy}"));
        }
        if (r.ItemCode.Length > 0) FromMemory("SUPPLIERCODE", (r.Supplier + "|" + r.ItemCode).ToUpperInvariant(), 0.99, $"supplier code {r.ItemCode}");
        FromMemory("DESC", desc, 0.97, "same description");
        FromMemory("FP", fp, 0.92, "same attributes");

        // the PO's own scope-of-work sheet (document evidence)
        if (fp.Length > 0)
        {
            var scope = _src.PoScope.Where(s => (r.PoId is null || s.PoId == r.PoId) && s.Fingerprint == fp && Units.Agree(s.Unit, r.Unit)).ToList();
            var codes = scope.GroupBy(s => s.BoqCode).ToList();
            foreach (var g in codes)
            {
                var s0 = g.First();
                var share = scope.Sum(x => x.Qty) <= 0 ? 0 : g.Sum(x => x.Qty) / scope.Sum(x => x.Qty);
                cands.Add(new(g.Key, "", "", s0.ResourceCode, s0.ResourceName, s0.Unit, "PO SCOPE", codes.Count == 1 ? 0.96 : 0.88,
                    $"PO scope-of-work sheet: resource {s0.ResourceCode}, {g.Sum(x => x.Qty):N0} {s0.Unit}{(codes.Count > 1 ? $" ({share:P0} of {codes.Count} BOQ codes)" : "")}"));
            }
        }

        // (2) attribute fingerprint over the pool, (3) text similarity
        var hits = _index.Search(r.Description, 40);
        if (cable != null && _cables.TryGetValue(cable.CoreSize, out var same))
        {
            var simOf = hits.ToDictionary(h => h.Doc, h => h.Score);
            hits = same.Select(i => (i, simOf.GetValueOrDefault(i))).ToList();
        }
        foreach (var (doc, sim) in hits)
        {
            var c = _pool[doc];
            if (!Units.Agree(c.Unit, r.Unit)) continue;
            double score; string why;
            var other = _poolCable[doc];
            if (cable != null && other != null)
            {
                var a = Fingerprints.CompareCables(cable, other);
                if (a <= 0) continue;
                score = 0.75 + 0.15 * a + 0.05 * Math.Min(1, sim);
                why = $"same cable {cable.CoreSize} {cable.Conductor}{(cable.FireRated ? " FR" : "")} as '{Short(c.Description)}' ({c.Why})";
            }
            else if (cable != null || other != null) continue;
            else
            {
                var g = Fingerprints.CompareGeneric(r.Description, c.Description);
                score = Math.Min(0.84, 0.55 * sim + 0.35 * g);
                why = $"text {sim:P0} / attributes {g:P0} like '{Short(c.Description)}' ({c.Why})";
                if (score < 0.25) continue;
            }
            cands.Add(c with { Score = Math.Round(score, 3), Source = c.Source + (cable != null ? " FP" : " TEXT"), Why = why });
        }

        var top = cands.GroupBy(c => c.BoqCode, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score).Take(3).Select(Complete).ToList();
        var status = CodeStatus.Ask;
        if (top.Count > 0)
        {
            var best = top[0].Score;
            var ambiguous = top.Count > 1 && best - top[1].Score < 0.02 && best < 0.97;
            status = best >= _high && !ambiguous ? CodeStatus.Auto : best >= _medium ? CodeStatus.Review : CodeStatus.Ask;
        }
        return new CodingSuggestion { Request = r, Top = top, Status = status };
    }

    private static string Short(string s) => s.Length <= 48 ? s : s[..48] + "...";

    /// <summary>Applies a suggestion to a PO line (AUTO and REVIEW fill the codes; ASK leaves them empty).</summary>
    public static void Apply(MatPoLine l, CodingSuggestion s)
    {
        l.CodeStatus = s.Status;
        l.CodeScore = s.Best?.Score ?? 0;
        l.CodeSource = s.Provenance;
        if (s.Status == CodeStatus.Ask || s.Best is null) return;
        l.BoqCode = s.Best.BoqCode; l.CostCode = s.Best.CostCode; l.BudgetResourceCode = s.Best.BudgetResourceCode;
        if (s.Best.ResourceCode.Length > 0) l.ResourceCode = s.Best.ResourceCode;
    }

    /// <summary>Remembers a confirmed code under every key of the line (supplier code, description, fingerprint).</summary>
    public static List<MatCodeMemory> Learn(IMaterialsStore store, MaterialsSnapshot snap, CodingRequest r, CodeCandidate chosen, string user)
    {
        var keys = new List<(string Type, string Key)>();
        if (r.ItemCode.Length > 0) keys.Add(("SUPPLIERCODE", (r.Supplier + "|" + r.ItemCode).ToUpperInvariant()));
        var d = Fingerprints.NormalizeDescription(r.Description);
        if (d.Length > 0) keys.Add(("DESC", d));
        var fp = Fingerprints.Key(r.Description);
        if (fp.Length > 0) keys.Add(("FP", fp));
        var rows = new List<MatCodeMemory>();
        store.Batch(w =>
        {
            foreach (var (type, key) in keys)
            {
                var m = snap.CodeMemory.FirstOrDefault(x => x.KeyType == type && x.Key == key && x.BoqCode.Equals(chosen.BoqCode, StringComparison.OrdinalIgnoreCase));
                if (m != null) { m.Uses++; m.ConfirmedBy = user; m.CostCode = chosen.CostCode; m.BudgetResourceCode = chosen.BudgetResourceCode; w.Update(m); }
                else
                {
                    m = new MatCodeMemory
                    {
                        KeyType = type, Key = key, Supplier = r.Supplier, Description = r.Description, Unit = Units.Normalize(r.Unit), BoqCode = chosen.BoqCode,
                        CostCode = chosen.CostCode, BudgetResourceCode = chosen.BudgetResourceCode, ResourceCode = chosen.ResourceCode, Uses = 1, ConfirmedBy = user,
                    };
                    w.Insert(m);
                    snap.CodeMemory.Add(m);
                }
                rows.Add(m);
            }
        }, $"Coding learned: '{r.Description}' -> {chosen.BoqCode}");
        return rows;
    }
}
