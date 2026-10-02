using Raffaello.Core.Coding;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Materials;
using Raffaello.Core.Mos;

namespace Raffaello.Core.Insights;

/// <summary>One material: theoretical use from installed points vs delivered (DN) vs invoiced / paid (supplier invoices).</summary>
public sealed class ReconRow
{
    public string Material { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Scope { get; init; } = "";
    public string NormSource { get; init; } = "";
    public double InstalledPoints { get; init; }
    public double PlannedPoints { get; init; }
    public double Theoretical { get; init; }
    public double TheoreticalTotal { get; init; }
    public double Allowance { get; init; }
    public double Delivered { get; init; }
    public double Invoiced { get; init; }
    public double Paid { get; init; }
    public double DeliveredValue { get; init; }
    public int DnLines { get; init; }
    /// <summary>Delivered - theoretical use: material that should still be on site (or wasted).</summary>
    public double OnSite => Delivered - Theoretical;
    /// <summary>(Delivered - theoretical) / theoretical: wastage + stock; only wastage once the work using it is complete.</summary>
    public double ImpliedWastagePct => Theoretical > 1e-9 ? (Delivered - Theoretical) / Theoretical : 0;
    /// <summary>Delivered beyond what the whole project needs (PROJECT QTY x norm x (1 + allowance)).</summary>
    public double OverDelivered => TheoreticalTotal > 1e-9 ? Math.Max(0, Delivered - TheoreticalTotal * (1 + Allowance)) : 0;
    /// <summary>Delivered / installed points: an upper bound of the real consumption per point (learn the norm from it).</summary>
    public double ObservedPerPoint => InstalledPoints > 1e-9 ? Delivered / InstalledPoints : 0;
    public double NormPerPoint => InstalledPoints > 1e-9 ? Theoretical / InstalledPoints : PlannedPoints > 1e-9 ? TheoreticalTotal / PlannedPoints : 0;
    public double CompletionPct => PlannedPoints > 1e-9 ? InstalledPoints / PlannedPoints : 0;
    public string Status { get; init; } = "";
    public string Explanation { get; init; } = "";
    public List<Evidence> Evidence { get; init; } = new();
}

/// <summary>MOS line whose material is (theoretically) installed but still valued as on site: release candidate.</summary>
public sealed record MosReleaseCandidate(string BoqCode, string Description, string Material, double Delivered, double InstalledRecorded, double InstalledTheoretical, double ReleaseQty, double BoqRate, double MosPct)
{
    public double ReleaseValue => Math.Round(ReleaseQty * BoqRate * MosPct, 2);
}

public sealed class ReconResult
{
    public List<ReconRow> Rows { get; } = new();
    public List<MosReleaseCandidate> Mos { get; } = new();
    /// <summary>Delivered materials no norm covers (description, qty, unit).</summary>
    public List<(string Description, double Qty, string Unit)> Unmatched { get; } = new();
    public List<string> Notes { get; } = new();
    public bool UsingDefaultNorms { get; init; }
}

/// <summary>
/// Material reconciliation (roadmap 6): consumption norms x installed points (ledger, after SITE %) give the theoretical use; DN lines give
/// what was delivered; DN lines locked to supplier invoices give invoiced / paid. The gap is stock on site or wastage; deliveries above
/// what the whole project needs are over-deliveries; installed material still valued in the owner MOS is a release candidate.
/// </summary>
public static class MaterialReconciliation
{
    /// <summary>Starting norms (assumptions - edit them, or adopt the observed consumption once a material is fully installed).</summary>
    public static List<InsightNorm> DefaultNorms() => new()
    {
        new() { Material = "CONDUIT 20MM", Unit = "M", System = "POWER", Stage = "1ST FIX", PerPoint = 6, Source = "DEFAULT", Note = "wall outlet drop + share of the ceiling run" },
        new() { Material = "CONDUIT 20MM", Unit = "M", System = "LIGHT", Stage = "1ST FIX", PerPoint = 5, Source = "DEFAULT" },
        new() { Material = "CONDUIT 20MM", Unit = "M", System = "DATA", Stage = "1ST FIX", PerPoint = 6, Source = "DEFAULT" },
        new() { Material = "CABLE|3X2.5", Unit = "M", System = "POWER", Stage = "2ND FIX", PerPoint = 12, Source = "DEFAULT", Note = "radial / ring share per socket" },
        new() { Material = "CABLE|3X1.5", Unit = "M", System = "LIGHT", Stage = "2ND FIX", PerPoint = 8, Source = "DEFAULT" },
        new() { Material = "CAT6", Unit = "M", System = "DATA", Stage = "2ND FIX", PerPoint = 25, Source = "DEFAULT", Note = "home run to the rack" },
    };

    /// <summary>True when the norm's material describes the line: a cable fingerprint prefix ("CABLE|4X16") or words that must all appear.</summary>
    public static bool Matches(string material, string description)
    {
        if (string.IsNullOrWhiteSpace(material) || string.IsNullOrWhiteSpace(description)) return false;
        var m = material.Trim().ToUpperInvariant();
        if (m.StartsWith("CABLE|", StringComparison.Ordinal))
        {
            var spec = Fingerprints.Cable(description);
            return spec != null && spec.Key.StartsWith(m, StringComparison.Ordinal);
        }
        var need = Fingerprints.Tokens(m);
        if (need.Count == 0) return false;
        var have = Fingerprints.Tokens(description).ToHashSet(StringComparer.Ordinal);
        var norm = Fingerprints.NormalizeDescription(description);
        return need.All(t => have.Contains(t) || norm.Contains(t, StringComparison.Ordinal));
    }

    private static bool SameUnit(string a, string b)
    {
        static string U(string u) => (u ?? "").Trim().ToUpperInvariant() switch { "MTR" or "MT" or "METER" or "METRE" or "LM" or "M.L" or "RM" => "M", "NOS" or "NO" or "NO." or "PCS" or "PC" or "EA" => "NO", var x => x };
        return U(a) == U(b) || string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b);
    }

    public static ReconResult Build(ProjectSnapshot s, MaterialsSnapshot? m, IReadOnlyList<InsightNorm> norms, string? building = null)
    {
        bool In(string? b) => building is null || string.IsNullOrEmpty(b) || string.Equals(b, building, StringComparison.OrdinalIgnoreCase);
        var useDefaults = norms.Count == 0;
        var list = useDefaults ? DefaultNorms() : norms.Where(n => n.PerPoint > 0 && n.Material.Trim().Length > 0).ToList();
        var res = new ReconResult { UsingDefaultNorms = useDefaults };
        if (useDefaults) res.Notes.Add("No consumption norms entered yet - DEFAULT assumptions are used. Edit the norms table (metres per point) for real numbers.");

        var rooms = s.Rooms.GroupBy(r => r.Code.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        bool RoomOk(InsightNorm n, string room)
        {
            if (string.IsNullOrWhiteSpace(n.RoomType)) return true;
            if (!rooms.TryGetValue(room.Trim().ToUpperInvariant(), out var r)) return false;
            return string.Equals(r.RoomType, n.RoomType.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(r.AreaType, n.RoomType.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        bool KeyOk(InsightNorm n, string stage, string item) =>
            (string.IsNullOrWhiteSpace(n.System) || string.Equals(item.Trim(), n.System.Trim(), StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(n.Stage) || string.Equals(stage.Trim(), n.Stage.Trim(), StringComparison.OrdinalIgnoreCase));

        var claims = LedgerRules.Effective(s.Claims.Where(c => In(c.Building))).Where(c => !c.Rework).ToList();
        var plan = s.RoomQtys.Where(q => In(q.Building)).ToList();

        // deliveries (phase-3 DN lines, PO unit) + legacy DN lines (through their PO line)
        var deliveries = new List<(long Id, string Desc, double Qty, string Unit, double Rate, string Ref, string BoqCode, long? LockInvoice)>();
        if (m != null)
        {
            var poLines = m.PoLines.ToDictionary(l => l.Id);
            var dns = m.Dns.ToDictionary(d => d.Id);
            var locks = m.Locks.GroupBy(k => k.DnLineId).ToDictionary(g => g.Key, g => g.First());
            foreach (var l in m.DnLines)
            {
                var pl = l.PoLineId is long pid ? poLines.GetValueOrDefault(pid) : null;
                var desc = l.Description.Length > 0 ? l.Description : pl?.Description ?? "";
                var dn = dns.GetValueOrDefault(l.DnId);
                deliveries.Add((l.Id, desc + " " + (pl?.Description ?? ""), l.Qty, l.Unit.Length > 0 ? l.Unit : pl?.Unit ?? "", pl?.Rate ?? 0, $"DN {dn?.DnNo} {l.ItemNo}", l.BoqCode, locks.GetValueOrDefault(l.Id)?.SubInvoiceId));
            }
        }
        var legacyPo = s.PoLines.ToDictionary(l => l.Id);
        var legacyDn = s.DeliveryNotes.ToDictionary(d => d.Id);
        foreach (var l in s.DnLines)
            if (legacyPo.TryGetValue(l.PoLineId, out var pl))
                deliveries.Add((-l.Id, pl.Description, l.Qty, pl.Unit, pl.Rate, $"DN {legacyDn.GetValueOrDefault(l.DnId)?.DnNo}", "", null));

        var approved = s.SubInvoices.Where(i => i.Status == SubInvoiceStatus.Approved).Select(i => i.Id).ToHashSet();
        var used = new HashSet<long>();
        foreach (var g in list.GroupBy(n => n.Material.Trim().ToUpperInvariant()))
        {
            var ns = g.ToList();
            var unit = ns[0].Unit;
            double installed = 0, planned = 0, theo = 0, theoTotal = 0;
            foreach (var n in ns)
            {
                var inst = claims.Where(c => KeyOk(n, c.Stage, c.Item) && RoomOk(n, c.Room)).Sum(c => c.Qty * c.SitePct);
                var pl = plan.Where(q => KeyOk(n, q.Stage, q.Item) && RoomOk(n, q.Room)).Sum(q => q.Qty);
                installed += inst; planned += pl;
                theo += inst * n.PerPoint; theoTotal += pl * n.PerPoint;
            }
            var mine = deliveries.Where(d => Matches(g.Key, d.Desc)).ToList();
            var unitOk = mine.Where(d => SameUnit(d.Unit, unit)).ToList();
            foreach (var d in mine) used.Add(d.Id);
            var delivered = unitOk.Sum(d => d.Qty);
            var invoiced = unitOk.Where(d => d.LockInvoice.HasValue).Sum(d => d.Qty);
            var paid = unitOk.Where(d => d.LockInvoice is long id && approved.Contains(id)).Sum(d => d.Qty);
            var allowance = ns.Max(n => n.WastageAllowance);
            var row0 = (Delivered: delivered, Theo: theo, Total: theoTotal);
            string status, why;
            if (delivered <= 1e-9 && theo <= 1e-9) { status = "NO DATA"; why = "Nothing delivered and nothing installed yet for this material."; }
            else if (delivered <= 1e-9) { status = "NO DELIVERY"; why = $"Installed points need {theo:N0} {unit} but no DN line matches '{g.Key}'. Import the DNs or adjust the material name."; }
            else if (theoTotal > 1e-9 && delivered > theoTotal * (1 + allowance)) { status = "OVER-DELIVERED"; why = $"Delivered {delivered:N0} {unit} is more than the whole project needs ({theoTotal:N0} + {allowance:P0} allowance)."; }
            else if (delivered < theo * 0.98) { status = "INSTALLED > DELIVERED"; why = $"Installed points need {theo:N0} {unit} but only {delivered:N0} were delivered: the norm is too high, DNs are missing, or material came from another PO."; }
            else { status = "OK"; why = $"{delivered - theo:N0} {unit} should be on site or wasted ({(theo > 0 ? (delivered - theo) / theo : 0):P0} of the theoretical use)."; }
            if (invoiced > delivered + 1e-6) { status = "INVOICED > DELIVERED"; why = $"Invoiced {invoiced:N0} {unit} is more than delivered {delivered:N0}."; }
            if (mine.Count > unitOk.Count) why += $" {mine.Count - unitOk.Count} DN lines in another unit were left out.";
            res.Rows.Add(new ReconRow
            {
                Material = g.Key, Unit = unit, NormSource = string.Join("/", ns.Select(n => n.Source).Distinct()),
                Scope = string.Join("; ", ns.Select(n => $"{(n.System.Length > 0 ? n.System : "ALL")} {(n.Stage.Length > 0 ? n.Stage : "ALL STAGES")}{(n.RoomType.Length > 0 ? " " + n.RoomType : "")} {n.PerPoint:0.##}/pt")),
                InstalledPoints = installed, PlannedPoints = planned, Theoretical = theo, TheoreticalTotal = theoTotal, Allowance = allowance,
                Delivered = row0.Delivered, Invoiced = invoiced, Paid = paid, DeliveredValue = unitOk.Sum(d => d.Qty * d.Rate), DnLines = unitOk.Count,
                Status = status, Explanation = why,
                Evidence = unitOk.Take(40).Select(d => new Evidence(EvidenceKinds.Dn, Math.Abs(d.Id), $"{d.Ref}: {d.Qty:N0} {d.Unit}")).ToList(),
            });
        }
        foreach (var d in deliveries.Where(d => !used.Contains(d.Id)).GroupBy(d => (Fingerprints.Cable(d.Desc)?.ToString() ?? d.Desc.Trim(), d.Unit)))
            res.Unmatched.Add((d.Key.Item1, d.Sum(x => x.Qty), d.Key.Unit));

        // MOS release candidates: owner MOS still values material that the installed points have used
        if (m != null && m.MosLines.Count > 0)
        {
            var last = m.MosValuations.Where(v => v.Status != MosStatus.Rejected).OrderByDescending(v => v.No).ThenByDescending(v => v.Revision).FirstOrDefault();
            if (last != null)
            {
                var installedByMat = res.Rows.ToDictionary(r => r.Material, r => r.Theoretical);
                var deliveredByMat = res.Rows.ToDictionary(r => r.Material, r => r.Delivered);
                var dnByCode = deliveries.Where(d => d.BoqCode.Length > 0).GroupBy(d => d.BoqCode.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var ml in m.MosLines.Where(l => l.ValuationId == last.Id && l.OnSiteQty > 1e-9))
                {
                    var code = ml.BoqCode.Trim().ToUpperInvariant();
                    var mat = list.Select(n => n.Material.Trim().ToUpperInvariant()).Distinct()
                        .FirstOrDefault(x => (dnByCode.GetValueOrDefault(code) ?? new()).Any(d => Matches(x, d.Desc)) || Matches(x, ml.BoqDescription));
                    if (mat is null || !installedByMat.TryGetValue(mat, out var theoInst) || deliveredByMat[mat] <= 1e-9) continue;
                    // share of the material's theoretical use that belongs to this BOQ code (by delivered quantity)
                    var share = Math.Clamp(ml.DeliveredQty / deliveredByMat[mat], 0, 1);
                    var instCode = Math.Min(ml.DeliveredQty, theoInst * share);
                    var release = instCode - ml.InstalledQty;
                    if (release <= 1e-6) continue;
                    res.Mos.Add(new MosReleaseCandidate(ml.BoqCode, ml.BoqDescription, mat, ml.DeliveredQty, ml.InstalledQty, instCode, release, ml.BoqRate, ml.MosPct > 0 ? ml.MosPct : last.MosPct));
                }
            }
        }
        if (res.Unmatched.Count > 0) res.Notes.Add($"{res.Unmatched.Count} delivered materials have no consumption norm (see UNMATCHED).");
        return res;
    }

    /// <summary>Reconciliation rows that deserve a warning, as insights (never blocking).</summary>
    public static IEnumerable<Anomaly> ToAnomalies(ReconResult r)
    {
        foreach (var row in r.Rows.Where(x => x.Status is "OVER-DELIVERED" or "INSTALLED > DELIVERED" or "INVOICED > DELIVERED"))
            yield return new Anomaly
            {
                Fingerprint = $"MAT|{row.Status}|{row.Material}", Kind = AnomalyKinds.Material,
                Severity = row.Status == "INVOICED > DELIVERED" ? InsightSeverity.High : row.Status == "OVER-DELIVERED" ? InsightSeverity.Medium : InsightSeverity.Low,
                Title = $"{row.Material}: {row.Status.ToLowerInvariant()}", Explanation = row.Explanation,
                SuggestedAction = row.Status switch
                {
                    "OVER-DELIVERED" => "Stop further call-offs on the PO line and agree a return / credit with the supplier.",
                    "INVOICED > DELIVERED" => "Hold the supplier invoice until the missing DNs are produced.",
                    _ => "Check the norm (metres per point) and whether all DNs are imported.",
                },
                Score = Math.Abs(row.Delivered - row.Theoretical), Evidence = row.Evidence,
            };
    }
}
