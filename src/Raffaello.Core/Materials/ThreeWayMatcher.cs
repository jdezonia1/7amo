using Raffaello.Core.Coding;
using Raffaello.Core.Data;

namespace Raffaello.Core.Materials;

public static class MatchStatus
{
    public const string Matched = "MATCHED";
    public const string QtyMismatch = "QTY MISMATCH";
    public const string NotOnPo = "NOT ON PO";
    public const string DnWithoutMir = "DN WITHOUT MIR";
    public const string OverPo = "OVER PO";

    /// <summary>Tag shown in the UI (OVER = red, CHECK = yellow, OK = green, DUE = soft yellow).</summary>
    public static string Tag(string s) => s switch { Matched => "OK", OverPo or NotOnPo => "OVER", QtyMismatch => "CHECK", DnWithoutMir => "DUE", _ => "" };
    public static int Severity(string s) => s switch { NotOnPo => 4, OverPo => 3, QtyMismatch => 2, DnWithoutMir => 1, _ => 0 };
}

/// <summary>One DN line after the PO <-> DN <-> MIR match.</summary>
public sealed class MatchRow
{
    public required MatDn Dn { get; init; }
    public required MatDnLine Line { get; init; }
    public MatPo? Po { get; set; }
    public MatPoLine? PoLine { get; set; }
    public double Score { get; set; }
    /// <summary>Quantity in the PO line unit.</summary>
    public double Qty { get; set; }
    public string Unit { get; set; } = "";
    public string ConversionNote { get; set; } = "";
    public double CumQty { get; set; }
    public double PoQty => PoLine?.Qty ?? 0;
    public double AllowedQty { get; set; }
    public string MirNos { get; set; } = "";
    /// <summary>OK / NOT IN MIR / "" (no batch evidence in the MIR).</summary>
    public string BatchCheck { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Notes { get; } = new();
    public string Tag => MatchStatus.Tag(Status);
    public string NoteText => string.Join("; ", Notes);
    public string PoLineText => PoLine is null ? "-" : $"{PoLine.LineNo:00} {PoLine.Description}";
}

/// <summary>Per PO line: ordered vs delivered (all DNs), tolerance and status.</summary>
public sealed record PoLineProgress(MatPo Po, MatPoLine Line, double Delivered, double Allowed, int DnCount)
{
    public double Remaining => Line.Qty - Delivered;
    public double Pct => Line.Qty <= 0 ? 0 : Delivered / Line.Qty;
    public bool IsOver => Delivered > Allowed + 1e-6;
}

public sealed class MatchResult
{
    public List<MatchRow> Rows { get; } = new();
    public List<PoLineProgress> PoLines { get; } = new();
    public int Count(string status) => Rows.Count(r => r.Status == status);
    public string Summary => string.Join(", ", new[] { MatchStatus.Matched, MatchStatus.QtyMismatch, MatchStatus.OverPo, MatchStatus.DnWithoutMir, MatchStatus.NotOnPo }
        .Select(s => $"{s} {Count(s)}"));
}

/// <summary>
/// 3-way match PO <-> DN <-> MIR. Lines are paired by attribute fingerprint (cables: cores x size, CU/AL, fire-rated, LV/MV; other
/// materials: type words + sizes) - never by text alone. DN quantities are converted to the PO unit (KM -> M, PCS -> M by pipe length),
/// accumulated over DNs in date order against PO qty x (1 + tolerance), and checked against the MIR (DN refs, DN quantities read from
/// the DN photos, test certificates, drum labels).
/// </summary>
public static class ThreeWayMatcher
{
    public const double MinScore = 0.75;

    public static MatchResult Match(MaterialsSnapshot s, MaterialsSettings settings, IEnumerable<long>? onlyDnIds = null)
    {
        var res = new MatchResult();
        var only = onlyDnIds?.ToHashSet();
        var mirsByDn = s.MirDns.GroupBy(m => m.DnNo.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => s.Mirs.FirstOrDefault(m => m.Id == x.MirId)).Where(m => m != null).Cast<MatMir>().ToList(), StringComparer.OrdinalIgnoreCase);
        var cum = new Dictionary<long, double>();
        var dnCount = new Dictionary<long, HashSet<long>>();

        foreach (var dn in s.Dns.OrderBy(d => d.DnDate ?? DateTime.MaxValue).ThenBy(d => d.DnNo, StringComparer.Ordinal))
        {
            var po = s.FindPo(dn.PoNo);
            var poLines = po is null ? new List<MatPoLine>() : s.LinesOf(po).ToList();
            var mirs = mirsByDn.GetValueOrDefault(dn.DnNo.Trim()) ?? new List<MatMir>();
            var mirIds = mirs.Select(m => m.Id).ToHashSet();
            var evidence = s.MirEvidence.Where(e => mirIds.Contains(e.MirId)).ToList();
            var batchEvidence = evidence.Where(e => e.Batch.Length > 0 && e.Kind is "DNQTY" or "LABEL" or "CERT").ToList();
            var tol = po is null ? 0 : settings.ToleranceFor(po);

            foreach (var line in s.LinesOf(dn))
            {
                var row = new MatchRow { Dn = dn, Line = line, Po = po, MirNos = string.Join(", ", mirs.Select(m => m.MirNo)) };
                // ---- PO line
                MatPoLine? best = null; double bestScore = 0;
                if (line.PoLineId is long fixedId && line.MatchNote.StartsWith("MANUAL", StringComparison.Ordinal))
                {
                    best = poLines.FirstOrDefault(p => p.Id == fixedId); bestScore = best is null ? 0 : 1;
                }
                else
                {
                    foreach (var pl in poLines)
                    {
                        var sc = line.ItemCode.Length > 0 && pl.ItemCode.Length > 0 && line.ItemCode == pl.ItemCode ? 1.0 : Fingerprints.Compare(line.Description, pl.Description);
                        if (sc > bestScore + 1e-9) { bestScore = sc; best = pl; }
                    }
                }
                row.Score = bestScore;
                if (po is null)
                {
                    row.Status = MatchStatus.NotOnPo;
                    row.Notes.Add(dn.PoNo.Length == 0 ? "DN has no PO reference" : $"PO {dn.PoNo} is not imported");
                    row.Qty = line.Qty; row.Unit = line.Unit;
                }
                else if (best is null || bestScore < MinScore)
                {
                    row.Status = MatchStatus.NotOnPo;
                    var spec = Fingerprints.Cable(line.Description);
                    row.Notes.Add($"no line of {po.PoNo} matches {(spec != null ? spec.ToString() : line.Description)}");
                    row.Qty = line.Qty; row.Unit = line.Unit;
                }
                else
                {
                    row.PoLine = best;
                    if (bestScore < 1) row.Notes.Add($"matched at {bestScore:P0} (sheath / armour wording differs)");
                    var len = best.LengthPerPcs > 0 ? best.LengthPerPcs : settings.PipeLengthM;
                    var conv = Units.Convert(line.RawQty, line.RawUnit, best.Unit, len);
                    row.Qty = conv.Qty; row.Unit = conv.Ok ? Units.Normalize(best.Unit) : conv.Unit; row.ConversionNote = conv.Note;
                    if (!conv.Ok) { row.Status = MatchStatus.QtyMismatch; row.Notes.Add(conv.Note); }
                    var c = cum.GetValueOrDefault(best.Id) + (conv.Ok ? conv.Qty : 0);
                    cum[best.Id] = c;
                    (dnCount.TryGetValue(best.Id, out var set) ? set : dnCount[best.Id] = new HashSet<long>()).Add(dn.Id);
                    row.CumQty = c;
                    row.AllowedQty = best.Qty * (1 + tol);
                    if (c > row.AllowedQty + 1e-6)
                    {
                        row.Status = MatchStatus.OverPo;
                        row.Notes.Add($"delivered to date {Units.Fmt(c)} {row.Unit} > PO {Units.Fmt(best.Qty)} +{tol:P0} = {Units.Fmt(row.AllowedQty)}");
                    }
                }
                // ---- MIR
                if (mirs.Count == 0) { if (row.Status.Length == 0) row.Status = MatchStatus.DnWithoutMir; row.Notes.Add("no MIR lists this DN"); }
                else
                {
                    var dnq = evidence.Where(e => e.Kind == "DNQTY" && (e.DnNo.Length == 0 || e.DnNo == dn.DnNo) && line.Batch.Length > 0 && e.Batch == line.Batch).ToList();
                    if (dnq.Count > 0 && Math.Abs(dnq.Sum(e => e.Qty) - line.Qty) > Math.Max(0.5, line.Qty * 0.001))
                    {
                        if (MatchStatus.Severity(row.Status) < 2) row.Status = MatchStatus.QtyMismatch;
                        row.Notes.Add($"MIR copy of the DN shows {Units.Fmt(dnq.Sum(e => e.Qty))} {line.Unit} for batch {line.Batch}, DN {Units.Fmt(line.Qty)}");
                    }
                    var cert = evidence.Where(e => e.Kind == "CERT" && line.Batch.Length > 0 && (e.Batch == line.Batch || e.DrumNo == line.Batch) && e.Qty > 0).ToList();
                    if (cert.Count > 0 && Math.Abs(cert.Sum(e => e.Qty) - line.Qty) > Math.Max(1, line.Qty * 0.01))
                    {
                        if (MatchStatus.Severity(row.Status) < 2) row.Status = MatchStatus.QtyMismatch;
                        row.Notes.Add($"test certificate length {Units.Fmt(cert.Sum(e => e.Qty))} M vs DN {Units.Fmt(line.Qty)} {line.Unit}");
                    }
                    if (batchEvidence.Count > 0 && line.Batch.Length > 0)
                    {
                        row.BatchCheck = batchEvidence.Any(e => e.Batch == line.Batch || e.DrumNo == line.Batch) ? "OK" : "NOT IN MIR";
                        if (row.BatchCheck != "OK") row.Notes.Add($"batch {line.Batch} not on any certificate / label / DN copy in {row.MirNos}");
                    }
                }
                if (row.Status.Length == 0) row.Status = MatchStatus.Matched;
                if (only is null || only.Contains(dn.Id)) res.Rows.Add(row);
            }
        }
        foreach (var po in s.Pos)
        {
            var tol = settings.ToleranceFor(po);
            foreach (var pl in s.LinesOf(po))
                res.PoLines.Add(new PoLineProgress(po, pl, cum.GetValueOrDefault(pl.Id), pl.Qty * (1 + tol), dnCount.TryGetValue(pl.Id, out var set) ? set.Count : 0));
        }
        return res;
    }

    /// <summary>Writes the match back to the DN lines (PO line link, converted qty, status) in one audited batch.</summary>
    public static int Apply(IMaterialsStore store, MatchResult result)
    {
        var changed = result.Rows.Where(r => r.Line.PoLineId != r.PoLine?.Id || r.Line.MatchStatus != r.Status || Math.Abs(r.Line.Qty - r.Qty) > 1e-9 || r.Line.Unit != r.Unit || r.Line.MatchNote != Note(r)).ToList();
        if (changed.Count == 0) return 0;
        store.Batch(w =>
        {
            foreach (var r in changed)
            {
                r.Line.PoLineId = r.PoLine?.Id; r.Line.MatchStatus = r.Status; r.Line.Qty = r.Qty; r.Line.Unit = r.Unit; r.Line.MatchNote = Note(r);
                if (r.ConversionNote.Length > 0) r.Line.ConversionNote = r.ConversionNote;
                w.Update(r.Line);
            }
        }, $"3-way match: {result.Summary}");
        return changed.Count;
    }

    private static string Note(MatchRow r) => r.Line.MatchNote.StartsWith("MANUAL", StringComparison.Ordinal) ? r.Line.MatchNote : r.NoteText;

    /// <summary>Pins a DN line to a PO line by hand (kept on later matches).</summary>
    public static void PinManual(IMaterialsStore store, MatDnLine line, MatPoLine? poLine)
    {
        line.PoLineId = poLine?.Id;
        line.MatchNote = poLine is null ? "" : $"MANUAL -> line {poLine.LineNo}";
        store.Update(line, poLine is null ? $"DN line {line.ItemNo}: manual PO link removed" : $"DN line {line.ItemNo} pinned to PO line {poLine.LineNo}");
    }
}
