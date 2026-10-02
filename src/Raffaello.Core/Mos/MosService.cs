using Raffaello.Core.Coding;
using Raffaello.Core.Data;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Mos;

/// <summary>One delivered DN line on the owner side: which owner BOQ code it values against, and why.</summary>
public sealed record DeliveredRow(MatDn Dn, MatDnLine Line, string BoqCode, string CodeSource, string MirNo, string MirStatus, bool Counts, string Why)
{
    public double Qty => Line.Qty;
    public string Unit => Line.Unit;
}

public sealed class MosBuild
{
    public required MosValuation Header { get; init; }
    public List<MosLine> Lines { get; } = new();
    public List<string> Warnings { get; } = new();
    public double CumAmount => Lines.Sum(l => l.CumAmount);
    public double PrevAmount => Lines.Sum(l => l.PrevAmount);
    public double CurrAmount => Lines.Sum(l => l.CurrAmount);
    public double Released => Lines.Sum(l => l.Released);
}

/// <summary>
/// Owner MOS (materials on site): delivered-materials ledger from DNs (with a MIR, unless the setting says otherwise) mapped to the
/// owner BOQ (DN line code, else the PO line's code, else the auto-coder), valued at qty x BOQ rate x MOS %, less what is installed
/// (MOS release). Cumulative valuations with revisions; approved revisions are locked and become "previous".
/// </summary>
public static class MosService
{
    public static List<DeliveredRow> Ledger(MaterialsSnapshot m, MaterialsSettings settings, AutoCoder? coder = null)
    {
        var rows = new List<DeliveredRow>();
        var poLines = m.PoLines.ToDictionary(l => l.Id);
        foreach (var dn in m.Dns.OrderBy(d => d.DnDate ?? DateTime.MaxValue))
        {
            var link = m.MirDns.FirstOrDefault(x => x.DnNo.Equals(dn.DnNo, StringComparison.OrdinalIgnoreCase));
            var mir = link is null ? null : m.Mirs.FirstOrDefault(x => x.Id == link.MirId);
            foreach (var l in m.LinesOf(dn))
            {
                string code = l.BoqCode, src = l.CodeSource;
                if (code.Length == 0 && l.PoLineId is long id && poLines.TryGetValue(id, out var pl) && pl.BoqCode.Length > 0)
                {
                    var po = m.Pos.FirstOrDefault(p => p.Id == pl.PoId);
                    var splits = po is null ? new List<(string, double, string)>() : SupplierInvoices.Splits(m, po, pl);
                    if (splits.Count > 1)
                    {
                        // a PO line spread over several owner BOQ codes: split the delivered qty the same way
                        foreach (var (c, share, why) in splits)
                            rows.Add(Row(dn, Clone(l, l.Qty * share), c, $"PO line {pl.LineNo} split {share:P0}: {why}", mir, settings));
                        continue;
                    }
                    code = pl.BoqCode; src = $"PO line {pl.LineNo} ({pl.CodeStatus})";
                }
                if (code.Length == 0 && coder != null)
                {
                    var s = coder.Suggest(new CodingRequest(dn.Supplier, l.ItemCode, l.Description, l.Unit));
                    if (s.Status != CodeStatus.Ask && s.Best != null) { code = s.Best.BoqCode; src = $"{s.Status} {s.Provenance}"; }
                }
                rows.Add(Row(dn, l, code, src, mir, settings));
            }
        }
        return rows;
    }

    private static MatDnLine Clone(MatDnLine l, double qty) => new()
    {
        Id = l.Id, DnId = l.DnId, ItemNo = l.ItemNo, ItemCode = l.ItemCode, Description = l.Description, Batch = l.Batch, RawQty = l.RawQty, RawUnit = l.RawUnit,
        Qty = qty, Unit = l.Unit, PoLineId = l.PoLineId, MatchStatus = l.MatchStatus,
    };

    private static DeliveredRow Row(MatDn dn, MatDnLine l, string code, string src, MatMir? mir, MaterialsSettings settings)
    {
        var counts = code.Length > 0 && (!settings.MosRequiresMir || (mir != null && mir.Status != "REJECTED"));
        var why = code.Length == 0 ? "no owner BOQ code" : !counts ? (mir is null ? "no MIR yet" : "MIR rejected") : "";
        return new DeliveredRow(dn, l, code, src, mir?.MirNo ?? "", mir?.Status ?? "", counts, why);
    }

    /// <summary>Owner BOQ rate by code: imported owner BOQ first, else the E-Promise list (flagged).</summary>
    public static (double Rate, string Description, string Unit, string Source) RateOf(ProjectSnapshot p, MaterialsSnapshot m, string code)
    {
        var b = m.BoqLines.FirstOrDefault(x => x.BoqCode.Equals(code, StringComparison.OrdinalIgnoreCase) && x.Rate > 0);
        if (b != null) return (b.Rate, b.Description, b.Unit, "OWNER BOQ");
        var e = p.BoqItems.FirstOrDefault(x => x.ItemCode.Equals(code, StringComparison.OrdinalIgnoreCase));
        return e is null ? (0, "", "", "") : (e.Rate, e.Description, e.Unit, e.Rate > 0 ? "E-PROMISE" : "");
    }

    public static MosBuild Build(ProjectSnapshot p, MaterialsSnapshot m, MaterialsSettings settings, int no, int revision, DateTime periodTo, AutoCoder? coder = null)
    {
        var h = new MosValuation { No = no, Revision = revision, PeriodTo = periodTo, MosPct = settings.MosPct, CreatedAt = DateTime.Now };
        var b = new MosBuild { Header = h };
        var ledger = Ledger(m, settings, coder).Where(r => (r.Dn.DnDate ?? DateTime.MinValue).Date <= periodTo.Date).ToList();
        foreach (var r in ledger.Where(r => !r.Counts)) b.Warnings.Add($"DN {r.Dn.DnNo} line {r.Line.ItemNo} ({r.Line.Description}) not valued: {r.Why}");
        var prev = m.MosValuations.Where(v => v.Status == MosStatus.Approved && v.No < no).OrderByDescending(v => v.No).ThenByDescending(v => v.Revision).FirstOrDefault();
        var prevLines = prev is null ? new Dictionary<string, MosLine>(StringComparer.OrdinalIgnoreCase)
            : m.MosLines.Where(l => l.ValuationId == prev.Id).GroupBy(l => l.BoqCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var order = 0;
        var codes = ledger.Where(r => r.Counts).Select(r => r.BoqCode).Concat(prevLines.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.Ordinal);
        foreach (var code in codes)
        {
            var rows = ledger.Where(r => r.Counts && r.BoqCode.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
            var (rate, desc, unit, rsrc) = RateOf(p, m, code);
            if (rate <= 0) b.Warnings.Add($"{code}: no owner BOQ rate - import the owner BOQ (BOQ screen) or enter the rate");
            else if (rsrc == "E-PROMISE") b.Warnings.Add($"{code}: rate taken from the E-Promise list - check it against the owner BOQ");
            var installed = m.MosInstalled.Where(x => x.BoqCode.Equals(code, StringComparison.OrdinalIgnoreCase) && x.AsOf.Date <= periodTo.Date).Sum(x => x.Qty);
            var delivered = rows.Sum(r => r.Qty);
            if (installed > delivered + 1e-6) b.Warnings.Add($"{code}: installed {Units.Fmt(installed)} is more than delivered {Units.Fmt(delivered)} - MOS is zero");
            b.Lines.Add(new MosLine
            {
                RowOrder = order++, BoqCode = code, BoqDescription = desc.Length > 0 ? desc : rows.FirstOrDefault()?.Line.Description ?? "", Unit = unit.Length > 0 ? unit : rows.FirstOrDefault()?.Unit ?? "",
                BoqRate = rate, DeliveredQty = Math.Round(delivered, 4), InstalledQty = Math.Round(installed, 4), MosPct = settings.MosPct,
                PrevAmount = prevLines.TryGetValue(code, out var pl) ? pl.CumAmount : 0,
                Sources = string.Join(", ", rows.GroupBy(r => r.Dn.DnNo).Select(g => $"DN {g.Key}{(g.First().MirNo.Length > 0 ? " / " + g.First().MirNo : "")}")),
                CodeSource = string.Join(" | ", rows.Select(r => r.CodeSource).Distinct().Take(3)),
            });
        }
        return b;
    }

    public static int NextRevision(MaterialsSnapshot m, int no) => m.MosValuations.Where(v => v.No == no).Select(v => v.Revision + 1).DefaultIfEmpty(0).Max();
    public static int NextNo(MaterialsSnapshot m) => m.MosValuations.Where(v => v.Status == MosStatus.Approved).Select(v => v.No + 1).DefaultIfEmpty(1).Max();

    public static MosValuation SaveDraft(IMaterialsStore store, MaterialsSnapshot m, MosBuild b)
    {
        var h = b.Header;
        var existing = m.MosValuations.FirstOrDefault(v => v.No == h.No && v.Revision == h.Revision);
        if (existing is { Locked: true }) throw new InvalidOperationException($"{existing.Title} is approved and locked.");
        var old = existing is null ? new List<MosLine>() : m.MosLines.Where(l => l.ValuationId == existing.Id).ToList();
        store.Batch(w =>
        {
            if (existing != null)
            {
                existing.PeriodTo = h.PeriodTo; existing.MosPct = h.MosPct; existing.Notes = h.Notes;
                w.Update(existing);
                foreach (var l in old) w.Delete(l);
                h.Id = existing.Id; h.RowVersion = existing.RowVersion; h.Status = existing.Status;
            }
            else w.Insert(h);
            foreach (var l in b.Lines) { l.Id = 0; l.ValuationId = h.Id; }
            w.InsertMany(b.Lines);
        }, $"{h.Title} saved: SAR {b.CumAmount:N2} on site, {b.CurrAmount:N2} this period");
        return h;
    }

    public static void Submit(IMaterialsStore store, MosValuation v, string aconex) { Guard(v); v.Status = MosStatus.Submitted; v.AconexNo = aconex; store.Update(v, $"{v.Title} submitted (Aconex {aconex})"); }
    public static void Reject(IMaterialsStore store, MosValuation v, string reason) { Guard(v); v.Status = MosStatus.Rejected; v.RejectionReason = reason; store.Update(v, $"{v.Title} rejected: {reason}"); }
    public static void Approve(IMaterialsStore store, MosValuation v) { Guard(v); v.Status = MosStatus.Approved; v.Locked = true; v.ApprovedAt = DateTime.Now; store.Update(v, $"{v.Title} approved and locked"); }
    private static void Guard(MosValuation v) { if (v.Locked) throw new InvalidOperationException($"{v.Title} is approved and locked."); }

    public static MosBuild Stored(MaterialsSnapshot m, MosValuation v)
    {
        var b = new MosBuild { Header = v };
        b.Lines.AddRange(m.MosLines.Where(l => l.ValuationId == v.Id).OrderBy(l => l.RowOrder));
        return b;
    }

    /// <summary>Per BOQ code: cumulative amount before and after (for the revision diff).</summary>
    public static List<(string BoqCode, double Before, double After)> Diff(MosBuild before, MosBuild after)
    {
        var a = before.Lines.ToDictionary(l => l.BoqCode, l => l.CumAmount, StringComparer.OrdinalIgnoreCase);
        var c = after.Lines.ToDictionary(l => l.BoqCode, l => l.CumAmount, StringComparer.OrdinalIgnoreCase);
        return a.Keys.Union(c.Keys, StringComparer.OrdinalIgnoreCase).Select(k => (k, a.GetValueOrDefault(k), c.GetValueOrDefault(k)))
            .Where(x => Math.Abs(x.Item2 - x.Item3) > 0.005).OrderBy(x => x.k).ToList();
    }
}
