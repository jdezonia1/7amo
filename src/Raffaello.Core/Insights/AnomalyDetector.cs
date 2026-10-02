using System.Globalization;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Insights;

/// <summary>
/// Claim anomaly detection (roadmap 5). Each detector looks at the room ledger, the invoices, the WIR register and the documents and
/// returns warnings with evidence. Nothing here blocks a claim or an invoice: it tells Mohamed where to look first.
/// Typical values are medians and spreads are MADs, so a handful of bad claims cannot hide themselves by moving the average.
/// </summary>
public static class AnomalyDetector
{
    /// <summary>Subcontractor x invoice x room x stage x item: the ledger lines (corrections included) added together.</summary>
    internal sealed class Agg
    {
        public string Sub = "", Room = "", Stage = "", Item = "", Key = "", RoomType = "", AreaType = "", Building = "";
        public int Inv;
        public double Qty, LengthClaimed, LengthPlan, Above45;
        public bool Cumulative;
        public List<ClaimLine> Lines = new();
    }

    public static List<Anomaly> Detect(AnomalyInputs i)
    {
        var res = new List<Anomaly>();
        var s = i.Project;
        var claims = LedgerRules.Effective(s.Claims.Where(c => i.In(c.Building))).Where(c => !c.Rework).ToList();
        var rooms = s.Rooms.GroupBy(r => r.Code.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        var aggs = Aggregate(claims, rooms);
        var dating = new ClaimDating(s, i.Data.InvoicePeriods);

        void Run(Func<IEnumerable<Anomaly>> f) { try { res.AddRange(f()); } catch (Exception ex) { res.Add(Failed(ex)); } }
        Run(() => InvoiceJumps(aggs, i.Data));
        Run(() => KeyOutliers(aggs, s.RoomQtys.Where(q => i.In(q.Building)).ToList(), i.Data));
        Run(() => SharedKeys(s.RoomQtys.Where(q => i.In(q.Building)).ToList(), claims));
        Run(() => LengthClaims(aggs, s.RoomQtys.Where(q => i.In(q.Building)).ToList(), i.Data));
        Run(() => HeightClaims(aggs, rooms, i.Data));
        Run(() => WirChecks(claims, s.Wirs, dating));
        Run(() => DuplicateDocuments(i.Documents, i.Hashes, i.Data));
        Run(() => CopiedInvoices(aggs, i));
        Run(() => CumulativeChecks(aggs, i));
        Run(() => NumberPatterns(aggs, i.Data));
        Run(() => RateChecks(i));

        // one per fingerprint (the most severe), dismissals applied
        var dismissed = i.Data.ActiveDismissals();
        var list = res.GroupBy(a => a.Fingerprint).Select(g => g.OrderByDescending(a => a.Severity).ThenByDescending(a => a.Score).First()).ToList();
        foreach (var a in list) if (dismissed.TryGetValue(a.Fingerprint, out var d)) a.Dismissal = d;
        return list.OrderByDescending(a => a.Severity).ThenByDescending(a => a.Score).ThenBy(a => a.Title, StringComparer.Ordinal).ToList();
    }

    private static Anomaly Failed(Exception ex) => new()
    {
        Fingerprint = "ERROR|" + ex.GetType().Name + "|" + ex.Message, Kind = "ERROR", Severity = InsightSeverity.Info, Title = "One insight check could not run",
        Explanation = ex.Message, SuggestedAction = "Report it with error.log.",
    };

    internal static List<Agg> Aggregate(IEnumerable<ClaimLine> claims, IReadOnlyDictionary<string, Room> rooms)
    {
        var map = new Dictionary<string, Agg>(StringComparer.Ordinal);
        foreach (var c in claims)
        {
            var k = $"{ClaimDating.Norm(c.Subcontractor)}#{c.InvoiceNo}#{c.Key}";
            if (!map.TryGetValue(k, out var a))
            {
                rooms.TryGetValue(c.Room.Trim().ToUpperInvariant(), out var room);
                map[k] = a = new Agg
                {
                    Sub = c.Subcontractor.Trim(), Inv = c.InvoiceNo, Room = c.Room.Trim().ToUpperInvariant(), Stage = c.Stage.Trim().ToUpperInvariant(), Item = c.Item.Trim().ToUpperInvariant(), Key = c.Key,
                    RoomType = room?.RoomType is { Length: > 0 } rt ? rt.Trim().ToUpperInvariant() : (room?.AreaType ?? c.AreaType ?? "").Trim().ToUpperInvariant(),
                    AreaType = (room?.AreaType is { Length: > 0 } at ? at : c.AreaType ?? "").Trim().ToUpperInvariant(), Building = c.Building,
                };
            }
            a.Qty += c.Qty;
            if (c.LengthApplies && Math.Abs(c.LengthClaimedQty) > Math.Abs(c.Qty) + LedgerRules.Eps) { a.LengthClaimed += c.LengthClaimedQty; a.LengthPlan += c.Qty; }
            a.Above45 += c.QtyAbove45;
            a.Cumulative |= c.IsCumulative;
            a.Lines.Add(c);
        }
        return map.Values.ToList();
    }

    private static Evidence Ev(ClaimLine c) =>
        new(EvidenceKinds.Ledger, c.Id, $"#{c.Id} {c.Subcontractor} INV {c.InvoiceNo} {c.Room} {c.Stage} {c.Item} {F(c.Qty)}", NavKey: c.Room);

    private static string F(double v) => v.ToString(Math.Abs(v - Math.Round(v)) < 1e-9 ? "N0" : "N1", CultureInfo.InvariantCulture);
    private static string InvText(int n) => $"INV-{n:00}";

    // ------------------------------------------------------------------ 1. invoice totals jump

    internal static IEnumerable<Anomaly> InvoiceJumps(List<Agg> aggs, InsightsData d)
    {
        var jump = d.Threshold(InsightThresholds.JumpRatio);
        var z = d.Threshold(InsightThresholds.RobustZ);
        var minExcess = d.Threshold(InsightThresholds.MinExcess);
        foreach (var sub in aggs.Where(a => !a.Cumulative && a.Inv > 0).GroupBy(a => ClaimDating.Norm(a.Sub)))
            foreach (var st in sub.GroupBy(a => a.Stage))
            {
                var totals = st.GroupBy(a => a.Inv).OrderBy(g => g.Key).Select(g => (Inv: g.Key, Total: g.Sum(a => a.Qty), Aggs: g.ToList())).ToList();
                for (var k = 1; k < totals.Count; k++)
                {
                    var cur = totals[k];
                    var prior = totals.Take(k).Select(t => t.Total).Where(t => t > 0).ToList();
                    if (prior.Count == 0 || cur.Total <= 0) continue;
                    var med = RobustStats.Median(prior);
                    if (med <= 0) continue;
                    var ratio = cur.Total / med;
                    var robust = prior.Count >= 3 ? RobustStats.Z(cur.Total, prior) : double.NaN;
                    var flagged = ratio >= jump && cur.Total - med >= minExcess * 10 && (double.IsNaN(robust) || robust >= z);
                    if (!flagged) continue;
                    var name = st.First().Sub;
                    yield return new Anomaly
                    {
                        Fingerprint = $"JUMP|{sub.Key}|{cur.Inv}|{st.Key}", Kind = AnomalyKinds.InvoiceJump, Severity = ratio >= 2 * jump ? InsightSeverity.High : InsightSeverity.Medium,
                        Title = $"{name} {InvText(cur.Inv)} {st.Key}: {F(cur.Total)} points, {ratio:0.0}x his usual",
                        Explanation = $"{name} claimed {F(cur.Total)} {st.Key} points in {InvText(cur.Inv)}. His earlier invoices claimed {string.Join(", ", totals.Take(k).Select(t => $"{InvText(t.Inv)} {F(t.Total)}"))} " +
                                      $"(median {F(med)}). A jump of {ratio:0.0}x" + (double.IsNaN(robust) ? "" : $" (robust z {robust:0.0})") + " is unusual for one invoice period.",
                        SuggestedAction = "Check the site statement and the WIRs behind the biggest rooms of this invoice before certifying.",
                        Subcontractor = name, InvoiceNo = cur.Inv, Stage = st.Key, Score = cur.Total - med,
                        Evidence = cur.Aggs.OrderByDescending(a => a.Qty).Take(15).SelectMany(a => a.Lines.Take(1)).Select(Ev).ToList(),
                    };
                }
            }
    }

    // ------------------------------------------------------------------ 2. one room claimed far above rooms of the same type / re-claimed keys

    internal static IEnumerable<Anomaly> KeyOutliers(List<Agg> aggs, List<RoomQty> project, InsightsData d)
    {
        var z = d.Threshold(InsightThresholds.RobustZ);
        var minRatio = d.Threshold(InsightThresholds.MinRatio);
        var minExcess = d.Threshold(InsightThresholds.MinExcess);
        var caps = project.GroupBy(q => q.Key).ToDictionary(g => g.Key, g => g.Sum(q => q.Qty));
        var usable = aggs.Where(a => !a.Cumulative && a.Qty > 0 && a.RoomType.Length > 0).ToList();
        foreach (var grp in usable.GroupBy(a => (a.RoomType, a.Stage, a.Item)))
        {
            var sample = grp.Select(a => a.Qty).ToList();
            if (sample.Count < 5) continue;
            var med = RobustStats.Median(sample);
            foreach (var a in grp)
            {
                var rz = RobustStats.Z(a.Qty, sample);
                if (rz < z || a.Qty < med * minRatio || a.Qty - med < minExcess) continue;
                var cap = caps.GetValueOrDefault(a.Key);
                var overCap = cap > 0 && a.Qty > cap + LedgerRules.Eps;
                yield return new Anomaly
                {
                    Fingerprint = $"OUTLIER|{ClaimDating.Norm(a.Sub)}|{a.Inv}|{a.Key}", Kind = AnomalyKinds.KeyOutlier, Severity = overCap ? InsightSeverity.High : InsightSeverity.Medium,
                    Title = $"{a.Room} {a.Stage} {a.Item}: {F(a.Qty)} claimed vs typical {F(med)} for {grp.Key.RoomType}",
                    Explanation = $"{a.Sub} {InvText(a.Inv)} claims {F(a.Qty)} {a.Item} points at {a.Stage} in {a.Room}. Across {sample.Count} claims for {grp.Key.RoomType} rooms the typical " +
                                  $"claim is {F(med)} (robust z {rz:0.0})." + (cap > 0 ? $" PROJECT QTY for the room is {F(cap)}" + (overCap ? " - this one claim is already above it." : ".") : " The room has no PROJECT QTY."),
                    SuggestedAction = "Compare with the marked-up drawing for this room; a typing slip (e.g. 82 for 28) or a whole-floor quantity booked to one room is common.",
                    Subcontractor = a.Sub, InvoiceNo = a.Inv, Room = a.Room, Stage = a.Stage, Item = a.Item, Building = a.Building, Score = a.Qty - med,
                    Evidence = a.Lines.Select(Ev).ToList(),
                };
            }
        }
        // the same subcontractor claims a key again after his own claims already reached PROJECT QTY
        foreach (var g in aggs.Where(a => !a.Cumulative && a.Inv > 0).GroupBy(a => (Sub: ClaimDating.Norm(a.Sub), a.Key)))
        {
            var cap = caps.GetValueOrDefault(g.Key.Key);
            if (cap <= 0) continue;
            double before = 0;
            foreach (var a in g.OrderBy(x => x.Inv))
            {
                if (before >= cap - LedgerRules.Eps && a.Qty > LedgerRules.Eps)
                {
                    yield return new Anomaly
                    {
                        Fingerprint = $"REPEAT|{g.Key.Sub}|{a.Inv}|{a.Key}", Kind = AnomalyKinds.KeyRepeat, Severity = InsightSeverity.High,
                        Title = $"{a.Sub} {InvText(a.Inv)} claims {a.Room} {a.Stage} {a.Item} again ({F(a.Qty)}) after reaching PROJECT QTY {F(cap)}",
                        Explanation = $"His earlier invoices already claimed {F(before)} of PROJECT QTY {F(cap)} for {a.Room} {a.Stage} {a.Item}; {InvText(a.Inv)} adds {F(a.Qty)} more.",
                        SuggestedAction = "Usually a room booked twice under different invoice numbers - reject the repeat unless it is a correction with a reason.",
                        Subcontractor = a.Sub, InvoiceNo = a.Inv, Room = a.Room, Stage = a.Stage, Item = a.Item, Building = a.Building, Score = a.Qty * 2,
                        Evidence = g.SelectMany(x => x.Lines).Select(Ev).ToList(),
                    };
                }
                before += a.Qty;
            }
        }
    }

    // ------------------------------------------------------------------ 3. the same room x stage x item claimed by two or more subcontractors

    internal static IEnumerable<Anomaly> SharedKeys(List<RoomQty> project, List<ClaimLine> claims)
    {
        var balances = LedgerRules.Balances(project, claims);
        var byKey = claims.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var b in balances.Values)
        {
            var subs = b.BySubcontractor.Where(kv => kv.Value > LedgerRules.Eps).OrderByDescending(kv => kv.Value).ToList();
            if (subs.Count < 2) continue;
            var key = LedgerKeys.Key(b.Room, b.Stage, b.Item);
            var sev = b.HasCap && b.IsOver ? InsightSeverity.High : !b.HasCap ? InsightSeverity.Medium : InsightSeverity.Low;
            yield return new Anomaly
            {
                Fingerprint = $"SHARED|{key}", Kind = AnomalyKinds.MultiSub, Severity = sev,
                Title = $"{b.Room} {b.Stage} {b.Item} claimed by {subs.Count} subcontractors" + (b.HasCap && b.IsOver ? $" - {F(b.Claimed)} of {F(b.ProjectQty)}" : ""),
                Explanation = $"{string.Join(", ", subs.Select(kv => $"{kv.Key} {F(kv.Value)}"))}. " +
                              (b.HasCap ? $"Together {F(b.Claimed)} against PROJECT QTY {F(b.ProjectQty)}" + (b.IsOver ? $" - {F(b.Claimed - b.ProjectQty)} over the cap." : " (within the cap - fine if the work was split).") : "The room has no PROJECT QTY for this key."),
                SuggestedAction = b.IsOver ? "Find out who did the work (site team / WIR); the other claim must be reversed." : "Confirm the split of the room with the site team.",
                Room = b.Room, Stage = b.Stage, Item = b.Item, Subcontractor = string.Join(" / ", subs.Select(kv => kv.Key)),
                Score = b.HasCap ? Math.Max(0, b.Claimed - b.ProjectQty) * 10 + b.Claimed : b.Claimed,
                Evidence = (byKey.GetValueOrDefault(key) ?? new()).Select(Ev).ToList(),
            };
        }
    }

    // ------------------------------------------------------------------ 4. 15 m length claims far above the typical for the room type

    internal static IEnumerable<Anomaly> LengthClaims(List<Agg> aggs, List<RoomQty> project, InsightsData d)
    {
        var high = d.Threshold(InsightThresholds.LengthRatioHigh);
        var z = d.Threshold(InsightThresholds.RobustZ);
        var caps = project.GroupBy(q => q.Key).ToDictionary(g => g.Key, g => g.Sum(q => q.Qty));
        var rows = aggs.Where(a => a.LengthClaimed > a.LengthPlan + LedgerRules.Eps).Select(a =>
        {
            // DATA RACK extras carry plan qty 0: compare with the room's plan qty for the key
            var plan = a.LengthPlan > LedgerRules.Eps ? a.LengthPlan : Math.Max(a.Qty, caps.GetValueOrDefault(a.Key));
            var extra = a.LengthClaimed - a.LengthPlan;
            var ratio = plan > LedgerRules.Eps ? (plan + extra) / plan : double.PositiveInfinity;
            return (A: a, Plan: plan, Extra: extra, Ratio: ratio);
        }).ToList();
        foreach (var grp in rows.GroupBy(r => (r.A.RoomType, r.A.Item)))
        {
            var sample = grp.Where(r => !double.IsInfinity(r.Ratio)).Select(r => r.Ratio).ToList();
            var med = sample.Count > 0 ? RobustStats.Median(sample) : 1;
            foreach (var r in grp)
            {
                var rz = sample.Count >= 5 && !double.IsInfinity(r.Ratio) ? RobustStats.Z(r.Ratio, sample) : double.NaN;
                InsightSeverity sev;
                if (r.Ratio >= high) sev = InsightSeverity.High;
                else if (!double.IsNaN(rz) && rz >= z && r.Ratio > 1.2) sev = InsightSeverity.Medium;
                else continue;
                var a = r.A;
                var round = Math.Abs(a.LengthClaimed) >= 50 && Math.Abs(a.LengthClaimed % 50) < 1e-9;
                yield return new Anomaly
                {
                    Fingerprint = $"LENGTH|{ClaimDating.Norm(a.Sub)}|{a.Inv}|{a.Key}", Kind = AnomalyKinds.Length, Severity = sev,
                    Title = $"{a.Room} {a.Item} 15 m claim: {F(r.Plan + r.Extra)} for {F(r.Plan)} plan points ({(double.IsInfinity(r.Ratio) ? "no plan qty" : r.Ratio.ToString("0.0") + "x")})",
                    Explanation = $"{a.Sub} {InvText(a.Inv)} claims {F(r.Extra)} extra {a.Item} points in {a.Room} for routes longer than 15 m - {(double.IsInfinity(r.Ratio) ? "the room has no plan quantity" : $"{r.Ratio:0.0}x the plan quantity")}. " +
                                  $"Typical for {(a.RoomType.Length > 0 ? a.RoomType : "this room type")}: {med:0.00}x." + (r.Ratio >= high ? $" Above {high:0.0}x means an average route of {15 * r.Ratio:0} m or more per point." : "") +
                                  (round ? " The claimed quantity is a round number." : ""),
                    SuggestedAction = "Ask for the marked route lengths and decide it on the Checks page (15 m LENGTH): revised = max(1, L / 15) per point.",
                    Subcontractor = a.Sub, InvoiceNo = a.Inv, Room = a.Room, Stage = a.Stage, Item = a.Item, Building = a.Building, Score = r.Extra,
                    Evidence = a.Lines.Select(Ev).ToList(),
                };
            }
        }
    }

    // ------------------------------------------------------------------ 5. >4.5 m claims far above the typical for the room type / subcontractor

    internal static IEnumerable<Anomaly> HeightClaims(List<Agg> aggs, IReadOnlyDictionary<string, Room> rooms, InsightsData d)
    {
        var z = d.Threshold(InsightThresholds.RobustZ);
        var aptShare = d.Threshold(InsightThresholds.HighShareApartment);
        var withHigh = aggs.Where(a => a.Above45 > LedgerRules.Eps && Math.Abs(a.Qty) > LedgerRules.Eps).ToList();
        var shares = aggs.Where(a => a.Qty > 0).GroupBy(a => (a.RoomType, ClaimDating.Norm(a.Sub))).ToDictionary(g => g.Key, g => g.Select(a => Math.Clamp(a.Above45 / a.Qty, 0, 1)).ToList());
        foreach (var a in withHigh)
        {
            var share = Math.Clamp(a.Above45 / a.Qty, 0, 1);
            rooms.TryGetValue(a.Room, out var room);
            if (room?.HighAreaNote is { Length: > 0 }) continue;   // confirmed high area
            var sample = shares.GetValueOrDefault((a.RoomType, ClaimDating.Norm(a.Sub))) ?? new List<double>();
            var rz = sample.Count >= 5 ? RobustStats.Z(share, sample) : double.NaN;
            var apartment = a.AreaType == "APARTMENT";
            InsightSeverity sev;
            if (apartment && share > aptShare) sev = InsightSeverity.Medium;
            else if (!double.IsNaN(rz) && rz >= z) sev = InsightSeverity.Medium;
            else sev = InsightSeverity.Low;
            yield return new Anomaly
            {
                Fingerprint = $"HEIGHT|{ClaimDating.Norm(a.Sub)}|{a.Inv}|{a.Key}", Kind = AnomalyKinds.Height, Severity = sev,
                Title = $"{a.Room} {a.Stage} {a.Item}: {F(a.Above45)} of {F(a.Qty)} claimed above 4.5 m",
                Explanation = $"{a.Sub} {InvText(a.Inv)} claims {share:P0} of the points in {a.Room} above 4.5 m (higher contract rate)." +
                              (apartment ? " The room is an apartment - ceilings there are below 4.5 m." : "") +
                              (double.IsNaN(rz) ? "" : $" Typical share for {a.RoomType} rooms by {a.Sub}: {RobustStats.Median(sample):P0}."),
                SuggestedAction = "Decide it on the Checks page (HEIGHT) with a site photo; mark the room as a high area once confirmed.",
                Subcontractor = a.Sub, InvoiceNo = a.Inv, Room = a.Room, Stage = a.Stage, Item = a.Item, Building = a.Building, Score = a.Above45,
                Evidence = a.Lines.Where(l => l.QtyAbove45 > 0).Select(Ev).ToList(),
            };
        }
    }

    // ------------------------------------------------------------------ 6. claims without WIR, before the WIR approval, WIR not approved

    internal static IEnumerable<Anomaly> WirChecks(List<ClaimLine> claims, List<Wir> wirs, ClaimDating dating)
    {
        var reg = wirs.Where(w => w.Kind != "MIR").GroupBy(w => ClaimDating.WirKey(w.WirNo)).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.OrderByDescending(w => w.Id).First());
        var positive = claims.Where(c => c.Qty > LedgerRules.Eps && c.Source != "REVERSAL").ToList();
        foreach (var g in positive.Where(c => string.IsNullOrWhiteSpace(c.WirNo)).GroupBy(c => (Sub: ClaimDating.Norm(c.Subcontractor), c.InvoiceNo)))
        {
            var list = g.ToList();
            var sub = list[0].Subcontractor;
            var share = (double)list.Count / positive.Count(c => ClaimDating.Norm(c.Subcontractor) == g.Key.Sub && c.InvoiceNo == g.Key.InvoiceNo);
            yield return new Anomaly
            {
                Fingerprint = $"NOWIR|{g.Key.Sub}|{g.Key.InvoiceNo}", Kind = AnomalyKinds.NoWir, Severity = InsightSeverity.Medium,
                Title = $"{sub} {InvText(g.Key.InvoiceNo)}: {list.Count:N0} ledger lines without a WIR no. ({share:P0})",
                Explanation = $"{list.Count:N0} claim lines ({F(list.Sum(c => c.Qty))} points) of {sub} {InvText(g.Key.InvoiceNo)} carry no WIR number, so nothing shows the consultant accepted the work. " +
                              "Payment per stage is due only after consultant acceptance (contract).",
                SuggestedAction = "Enter the WIR numbers on the ledger lines (or attach the WIRs); hold lines that have no accepted WIR.",
                Subcontractor = sub, InvoiceNo = g.Key.InvoiceNo, Score = list.Sum(c => c.Qty) / 10,
                Evidence = list.OrderByDescending(c => c.Qty).Take(40).Select(Ev).ToList(),
            };
        }
        var withWir = positive.Where(c => !string.IsNullOrWhiteSpace(c.WirNo)).ToList();
        var unknown = new List<(ClaimLine C, string W)>();
        var before = new List<(ClaimLine C, Wir W, DateTime At)>();
        var notApproved = new List<(ClaimLine C, Wir W)>();
        foreach (var c in withWir)
            foreach (var w in ClaimDating.WirNumbers(c.WirNo))
            {
                if (!reg.TryGetValue(ClaimDating.WirKey(w), out var wir)) { unknown.Add((c, w)); continue; }
                if (wir.Status != WirStatus.Approved) { notApproved.Add((c, wir)); continue; }
                if (dating.Of(c) is { } at && wir.ApprovedAt is { } ap && at.Date < ap.Date) before.Add((c, wir, at.Date));
            }
        foreach (var g in notApproved.GroupBy(x => (x.W.Id, Sub: ClaimDating.Norm(x.C.Subcontractor), x.C.InvoiceNo)))
        {
            var w = g.First().W; var c0 = g.First().C;
            yield return new Anomaly
            {
                Fingerprint = $"WIRNA|{g.Key.Sub}|{g.Key.InvoiceNo}|{ClaimDating.WirKey(w.WirNo)}", Kind = AnomalyKinds.WirNotApproved, Severity = InsightSeverity.High,
                Title = $"{c0.Subcontractor} {InvText(c0.InvoiceNo)} claims under {w.WirNo}, which is {w.Status}",
                Explanation = $"{g.Count()} ledger lines ({F(g.Sum(x => x.C.Qty))} points) reference {w.WirNo} ({w.Level} {w.System} {w.Stage}), submitted {w.SubmittedAt:dd MMM yyyy}, status {w.Status}.",
                SuggestedAction = w.Status == WirStatus.Rejected ? "A rejected WIR cannot support payment - remove the lines from the invoice." : "Hold these lines until the WIR is approved.",
                Subcontractor = c0.Subcontractor, InvoiceNo = c0.InvoiceNo, Score = g.Sum(x => x.C.Qty),
                Evidence = new[] { new Evidence(EvidenceKinds.Wir, w.Id, $"{w.WirNo} {w.Status}", NavKey: w.WirNo) }.Concat(g.Select(x => Ev(x.C))).ToList(),
            };
        }
        foreach (var g in before.GroupBy(x => (x.W.Id, Sub: ClaimDating.Norm(x.C.Subcontractor), x.C.InvoiceNo)))
        {
            var w = g.First().W; var c0 = g.First().C; var at = g.Min(x => x.At);
            yield return new Anomaly
            {
                Fingerprint = $"BEFOREWIR|{g.Key.Sub}|{g.Key.InvoiceNo}|{ClaimDating.WirKey(w.WirNo)}", Kind = AnomalyKinds.BeforeWir, Severity = InsightSeverity.High,
                Title = $"{c0.Subcontractor} {InvText(c0.InvoiceNo)} claimed {(w.ApprovedAt!.Value.Date - at).TotalDays:0} days before {w.WirNo} was approved",
                Explanation = $"The claim is dated {at:dd MMM yyyy} but {w.WirNo} was approved on {w.ApprovedAt:dd MMM yyyy}. {g.Count()} lines, {F(g.Sum(x => x.C.Qty))} points.",
                SuggestedAction = "The work was claimed before the consultant accepted it - make sure the certified invoice was not paid ahead of the WIR.",
                Subcontractor = c0.Subcontractor, InvoiceNo = c0.InvoiceNo, Score = g.Sum(x => x.C.Qty),
                Evidence = new[] { new Evidence(EvidenceKinds.Wir, w.Id, $"{w.WirNo} approved {w.ApprovedAt:dd-MMM-yy}", NavKey: w.WirNo) }.Concat(g.Select(x => Ev(x.C))).ToList(),
            };
        }
        if (reg.Count > 0)
            foreach (var g in unknown.GroupBy(x => (Sub: ClaimDating.Norm(x.C.Subcontractor), x.C.InvoiceNo)))
            {
                var c0 = g.First().C;
                yield return new Anomaly
                {
                    Fingerprint = $"WIRUNK|{g.Key.Sub}|{g.Key.InvoiceNo}", Kind = AnomalyKinds.WirUnknown, Severity = InsightSeverity.Low,
                    Title = $"{c0.Subcontractor} {InvText(c0.InvoiceNo)}: {g.Select(x => x.W).Distinct(StringComparer.OrdinalIgnoreCase).Count()} WIR numbers not in the WIR register",
                    Explanation = $"WIRs {string.Join(", ", g.Select(x => x.W).Distinct(StringComparer.OrdinalIgnoreCase).Take(10))} are written on the ledger but not in the WIR / MIR register, so their approval date cannot be checked.",
                    SuggestedAction = "Import the WIR register (WIR / MIR page) or correct the numbers.",
                    Subcontractor = c0.Subcontractor, InvoiceNo = c0.InvoiceNo, Score = g.Count(),
                    Evidence = g.Select(x => Ev(x.C)).Take(40).ToList(),
                };
            }
    }

    // ------------------------------------------------------------------ 7. duplicate attachments / reused photos

    internal static IEnumerable<Anomaly> DuplicateDocuments(List<DocumentUse> uses, List<InsightFileHash> hashes, InsightsData d)
    {
        if (uses.Count == 0 || hashes.Count == 0) yield break;
        var maxDist = (int)d.Threshold(InsightThresholds.PhashDistance);
        var byPath = hashes.GroupBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rows = uses.Where(u => byPath.ContainsKey(u.Path)).Select(u => (U: u, H: byPath[u.Path])).ToList();
        Evidence EvDoc((DocumentUse U, InsightFileHash H) x) => new(EvidenceKinds.Document, x.U.EvidenceId, $"{Path.GetFileName(x.U.Path)} ({x.U.UsedBy})", x.U.Path);
        // exact copies used by different owners
        foreach (var g in rows.Where(r => r.H.Sha256.Length > 0).GroupBy(r => r.H.Sha256))
        {
            var owners = g.Select(r => r.U.UsedBy).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (owners.Count < 2) continue;
            var subs = g.Select(r => r.U.Subcontractor).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            yield return new Anomaly
            {
                Fingerprint = $"DUPFILE|{g.Key}", Kind = AnomalyKinds.DuplicateFile, Severity = subs.Count > 1 ? InsightSeverity.High : InsightSeverity.Medium,
                Title = $"Same file attached to {owners.Count} records: {Path.GetFileName(g.First().U.Path)}",
                Explanation = $"Byte-for-byte identical (SHA-256 {g.Key[..12]}...) and used by {string.Join(", ", owners)}." + (subs.Count > 1 ? $" Different subcontractors: {string.Join(", ", subs)}." : ""),
                SuggestedAction = "A document / photo proves one piece of work only - ask for the right evidence for the other record(s).",
                Subcontractor = string.Join(" / ", subs), Score = owners.Count,
                Evidence = g.Select(EvDoc).ToList(),
            };
        }
        // perceptually identical photos (resized / recompressed) that are not byte copies
        var imgs = rows.Where(r => r.H.PHash.Length == 16).GroupBy(r => r.H.Sha256).Select(g => g.ToList()).Take(3000).ToList();
        var seen = new HashSet<string>();
        for (var a = 0; a < imgs.Count; a++)
            for (var b = a + 1; b < imgs.Count; b++)
            {
                var x = imgs[a][0]; var y = imgs[b][0];
                var dist = DocumentHashes.Distance(x.H.PHash, y.H.PHash);
                if (dist > maxDist) continue;
                var ownersA = imgs[a].Select(r => r.U.UsedBy).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (imgs[b].All(r => ownersA.Contains(r.U.UsedBy)) && ownersA.Count == 1) continue;   // same record
                var fp = string.CompareOrdinal(x.H.Sha256, y.H.Sha256) < 0 ? $"{x.H.Sha256}|{y.H.Sha256}" : $"{y.H.Sha256}|{x.H.Sha256}";
                if (!seen.Add(fp)) continue;
                var all = imgs[a].Concat(imgs[b]).ToList();
                var subs = all.Select(r => r.U.Subcontractor).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                yield return new Anomaly
                {
                    Fingerprint = $"PHOTO|{fp}", Kind = AnomalyKinds.DuplicatePhoto, Severity = subs.Count > 1 ? InsightSeverity.High : InsightSeverity.Medium,
                    Title = $"Same photo reused: {Path.GetFileName(x.U.Path)} ~ {Path.GetFileName(y.U.Path)}",
                    Explanation = $"The two images differ in {dist} of 64 perceptual-hash bits (same picture resized / recompressed). Used by {string.Join(", ", all.Select(r => r.U.UsedBy).Distinct(StringComparer.OrdinalIgnoreCase))}.",
                    SuggestedAction = "Ask for a dated photo of each location.",
                    Subcontractor = string.Join(" / ", subs), Score = 64 - dist,
                    Evidence = all.Select(EvDoc).ToList(),
                };
            }
    }

    // ------------------------------------------------------------------ 8. copied invoices

    internal static IEnumerable<Anomaly> CopiedInvoices(List<Agg> aggs, AnomalyInputs i)
    {
        var s = i.Project;
        var threshold = i.Data.Threshold(InsightThresholds.CopySimilarity);
        foreach (var c in InvoiceCopyDetector.Detect(s.Invoices, s.InvoiceLines, Math.Min(0.98, threshold)))
            yield return new Anomaly
            {
                Fingerprint = $"COPY|STATEMENT|{c.Invoice.Id}", Kind = AnomalyKinds.CopiedInvoice, Severity = InsightSeverity.High,
                Title = $"{c.Invoice.Subcontractor} {c.Invoice.InvoiceNo} is a copy of {c.CopyOf.InvoiceNo}", Explanation = c.Message + ". A genuine cumulative statement moves at least some lines.",
                SuggestedAction = "Reject it and ask for a corrected statement.", Subcontractor = c.Invoice.Subcontractor, Score = c.MatchingLines,
                Evidence = new() { new(EvidenceKinds.Statement, c.Invoice.Id, $"{c.Invoice.InvoiceNo}", NavKey: c.Invoice.Subcontractor + "|" + c.Invoice.InvoiceNo), new(EvidenceKinds.Statement, c.CopyOf.Id, $"{c.CopyOf.InvoiceNo}", NavKey: c.CopyOf.Subcontractor + "|" + c.CopyOf.InvoiceNo) },
            };
        // ledger: invoice n repeats an earlier invoice of the same subcontractor (room x stage x item x qty)
        foreach (var sub in aggs.Where(a => a.Inv > 0).GroupBy(a => ClaimDating.Norm(a.Sub)))
        {
            var invs = sub.GroupBy(a => a.Inv).OrderBy(g => g.Key).Select(g => (Inv: g.Key, Set: g.Select(a => $"{a.Key}|{Math.Round(a.Qty, 2)}").ToList(), Aggs: g.ToList())).ToList();
            for (var k = 1; k < invs.Count; k++)
            {
                if (invs[k].Set.Count < 5) continue;
                for (var j = k - 1; j >= 0; j--)
                {
                    var prev = invs[j];
                    var match = Matches(invs[k].Set, prev.Set);
                    var sim = match / (double)Math.Max(invs[k].Set.Count, prev.Set.Count);
                    if (sim < threshold) continue;
                    var name = invs[k].Aggs[0].Sub;
                    yield return new Anomaly
                    {
                        Fingerprint = $"COPY|LEDGER|{sub.Key}|{invs[k].Inv}|{prev.Inv}", Kind = AnomalyKinds.CopiedInvoice, Severity = InsightSeverity.High,
                        Title = $"{name} {InvText(invs[k].Inv)} repeats {InvText(prev.Inv)} ({sim:P0} of lines identical)",
                        Explanation = $"{match} of {invs[k].Set.Count} room x stage x item quantities in {InvText(invs[k].Inv)} are the same as in {InvText(prev.Inv)} ({prev.Set.Count} lines). " +
                                      "Each invoice should claim new work; a repeated block is usually the previous invoice sent again.",
                        SuggestedAction = $"Reject {InvText(invs[k].Inv)} (or the repeated lines) and ask for the real progress of the period.",
                        Subcontractor = name, InvoiceNo = invs[k].Inv, Score = match * 10,
                        Evidence = invs[k].Aggs.Take(30).SelectMany(a => a.Lines.Take(1)).Select(Ev).ToList(),
                    };
                    break;
                }
            }
        }
        // invoice revisions in the app: current quantities identical to an earlier invoice number
        var subInv = s.SubInvoices.Where(x => InvoiceKinds.IsSubcontractor(x) && x.Status != SubInvoiceStatus.Rejected).ToList();
        var lines = s.SubInvoiceLines.Where(l => l.Kind == "ITEM").GroupBy(l => l.SubInvoiceId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var g in subInv.GroupBy(x => (x.ContractNo, Sub: ClaimDating.Norm(x.Subcontractor))))
        {
            var latest = g.GroupBy(x => x.InvoiceNo).Select(x => x.OrderByDescending(r => r.Revision).First()).OrderBy(x => x.InvoiceNo).ToList();
            Dictionary<string, double> Curr(SubInvoice h) => (lines.GetValueOrDefault(h.Id) ?? new()).Where(l => Math.Abs(l.CurrQty) > 1e-9)
                .GroupBy(l => $"{l.ItemNo}|{l.BoqCode}").ToDictionary(x => x.Key, x => Math.Round(x.Sum(l => l.CurrQty), 2));
            for (var k = 1; k < latest.Count; k++)
            {
                var cur = Curr(latest[k]);
                if (cur.Count < 3) continue;
                var prev = Curr(latest[k - 1]);
                var same = cur.Count(kv => prev.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) < 0.005);
                var sim = same / (double)Math.Max(cur.Count, prev.Count);
                if (sim < threshold) continue;
                yield return new Anomaly
                {
                    Fingerprint = $"COPY|SUBINV|{latest[k].Id}", Kind = AnomalyKinds.CopiedInvoice, Severity = InsightSeverity.High,
                    Title = $"{latest[k].Title}: current quantities repeat INV-{latest[k - 1].InvoiceNo:00} ({sim:P0})",
                    Explanation = $"{same} of {cur.Count} item rows carry the same current quantity as INV-{latest[k - 1].InvoiceNo:00}.",
                    SuggestedAction = "Rebuild the invoice from the ledger for this period.", Subcontractor = latest[k].Subcontractor, InvoiceNo = latest[k].InvoiceNo, Score = same * 10,
                    Evidence = new() { InvEv(latest[k]), InvEv(latest[k - 1]) },
                };
            }
        }
    }

    private static Evidence InvEv(SubInvoice h) => new(EvidenceKinds.Invoice, h.Id, h.Title, NavKey: $"{h.ContractNo}|{h.Subcontractor}|{h.InvoiceNo}");

    private static int Matches(List<string> a, List<string> b)
    {
        var bag = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var n = 0;
        foreach (var x in a) if (bag.TryGetValue(x, out var c) && c > 0) { bag[x] = c - 1; n++; }
        return n;
    }

    // ------------------------------------------------------------------ 9. cumulative invoices inconsistent with previous ones

    internal static IEnumerable<Anomaly> CumulativeChecks(List<Agg> aggs, AnomalyInputs i)
    {
        var s = i.Project;
        // app invoices: previous on invoice n must be the cumulative of invoice n-1, cum = prev + current, cum never goes down
        var lines = s.SubInvoiceLines.Where(l => l.Kind == "ITEM").GroupBy(l => l.SubInvoiceId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var g in s.SubInvoices.Where(x => InvoiceKinds.IsSubcontractor(x) && x.Status != SubInvoiceStatus.Rejected).GroupBy(x => (x.ContractNo, Sub: ClaimDating.Norm(x.Subcontractor))))
        {
            var latest = g.GroupBy(x => x.InvoiceNo).Select(x => x.OrderByDescending(r => r.Status == SubInvoiceStatus.Approved).ThenByDescending(r => r.Revision).First()).OrderBy(x => x.InvoiceNo).ToList();
            for (var k = 0; k < latest.Count; k++)
            {
                var h = latest[k];
                var mine = lines.GetValueOrDefault(h.Id) ?? new();
                var badSum = mine.Where(l => Math.Abs(l.PrevQty + l.CurrQty - l.CumQty) > 0.01).ToList();
                if (badSum.Count > 0)
                    yield return new Anomaly
                    {
                        Fingerprint = $"CUM|SUM|{h.Id}", Kind = AnomalyKinds.Cumulative, Severity = InsightSeverity.High,
                        Title = $"{h.Title}: cumulative is not previous + current on {badSum.Count} rows",
                        Explanation = string.Join("; ", badSum.Take(6).Select(l => $"item {l.ItemNo} {l.BoqCode}: {F(l.PrevQty)} + {F(l.CurrQty)} <> {F(l.CumQty)}")),
                        SuggestedAction = "Rebuild the invoice in the app (the Excel was edited by hand?).", Subcontractor = h.Subcontractor, InvoiceNo = h.InvoiceNo, Score = badSum.Count * 100,
                        Evidence = new() { InvEv(h) },
                    };
                if (k == 0) continue;
                var prev = latest[k - 1];
                if (prev.InvoiceNo != h.InvoiceNo - 1) continue;
                var prevCum = (lines.GetValueOrDefault(prev.Id) ?? new()).GroupBy(l => $"{l.ItemNo}|{l.BoqCode}").ToDictionary(x => x.Key, x => x.Sum(l => l.CumQty));
                var curPrev = mine.GroupBy(l => $"{l.ItemNo}|{l.BoqCode}").ToDictionary(x => x.Key, x => (Prev: x.Sum(l => l.PrevQty), Cum: x.Sum(l => l.CumQty)));
                var mism = curPrev.Where(kv => Math.Abs(kv.Value.Prev - prevCum.GetValueOrDefault(kv.Key)) > 0.01).ToList();
                var down = curPrev.Where(kv => kv.Value.Cum < prevCum.GetValueOrDefault(kv.Key) - 0.01).ToList();
                if (mism.Count > 0)
                    yield return new Anomaly
                    {
                        Fingerprint = $"CUM|PREV|{h.Id}", Kind = AnomalyKinds.Cumulative, Severity = InsightSeverity.High,
                        Title = $"{h.Title}: previous quantities do not match INV-{prev.InvoiceNo:00} cumulative ({mism.Count} rows)",
                        Explanation = string.Join("; ", mism.Take(6).Select(kv => $"{kv.Key}: previous {F(kv.Value.Prev)} vs INV-{prev.InvoiceNo:00} cumulative {F(prevCum.GetValueOrDefault(kv.Key))}")),
                        SuggestedAction = $"Previous must be the last approved cumulative ({prev.Title}). Rebuild from the ledger.", Subcontractor = h.Subcontractor, InvoiceNo = h.InvoiceNo, Score = mism.Count * 50,
                        Evidence = new() { InvEv(h), InvEv(prev) },
                    };
                if (down.Count > 0)
                    yield return new Anomaly
                    {
                        Fingerprint = $"CUM|DOWN|{h.Id}", Kind = AnomalyKinds.Cumulative, Severity = InsightSeverity.Medium,
                        Title = $"{h.Title}: cumulative went down on {down.Count} rows vs INV-{prev.InvoiceNo:00}",
                        Explanation = string.Join("; ", down.Take(6).Select(kv => $"{kv.Key}: {F(kv.Value.Cum)} < {F(prevCum.GetValueOrDefault(kv.Key))}")),
                        SuggestedAction = "A correction is fine but it must be explained in the invoice notes.", Subcontractor = h.Subcontractor, InvoiceNo = h.InvoiceNo, Score = down.Count * 20,
                        Evidence = new() { InvEv(h), InvEv(prev) },
                    };
            }
        }
        // legacy statements: cumulative claimed amount or line quantity goes down
        foreach (var g in s.Invoices.Where(x => x.Status != InvoiceStatus.Rejected).GroupBy(x => ClaimDating.Norm(x.Subcontractor)))
        {
            var ord = g.OrderBy(x => x.InvDate).ThenBy(x => x.InvoiceNo, StringComparer.OrdinalIgnoreCase).ToList();
            for (var k = 1; k < ord.Count; k++)
            {
                var a = ord[k - 1]; var b = ord[k];
                var la = s.InvoiceLines.Where(l => l.InvoiceId == a.Id).GroupBy(l => l.LineId).ToDictionary(x => x.Key, x => x.Sum(l => l.CumQty));
                var lb = s.InvoiceLines.Where(l => l.InvoiceId == b.Id).GroupBy(l => l.LineId).ToDictionary(x => x.Key, x => x.Sum(l => l.CumQty));
                var down = la.Where(kv => lb.GetValueOrDefault(kv.Key) < kv.Value - 0.01).ToList();
                if (down.Count == 0 && b.ClaimedAmount >= a.ClaimedAmount - 0.01) continue;
                yield return new Anomaly
                {
                    Fingerprint = $"CUM|STMT|{b.Id}", Kind = AnomalyKinds.Cumulative, Severity = InsightSeverity.Medium,
                    Title = $"{b.Subcontractor} {b.InvoiceNo}: cumulative statement lower than {a.InvoiceNo}",
                    Explanation = (down.Count > 0 ? $"{down.Count} lines claim less to date than on {a.InvoiceNo}. " : "") + (b.ClaimedAmount < a.ClaimedAmount ? $"Claimed to date SAR {b.ClaimedAmount:N0} < SAR {a.ClaimedAmount:N0}." : ""),
                    SuggestedAction = "Statements are cumulative: ask which lines were withdrawn and why.", Subcontractor = b.Subcontractor, Score = down.Count,
                    Evidence = new() { new(EvidenceKinds.Statement, b.Id, b.InvoiceNo, NavKey: b.Subcontractor + "|" + b.InvoiceNo), new(EvidenceKinds.Statement, a.Id, a.InvoiceNo, NavKey: a.Subcontractor + "|" + a.InvoiceNo) },
                };
            }
        }
        // ledger: a cumulative invoice that states less than the subcontractor's earlier invoices for the key
        var all = Aggregate(LedgerRules.Superseded(s.Claims.Where(c => i.In(c.Building))).Where(c => !c.Rework), new Dictionary<string, Room>());
        foreach (var cum in aggs.Where(a => a.Cumulative))
        {
            var earlier = all.Where(a => ClaimDating.Norm(a.Sub) == ClaimDating.Norm(cum.Sub) && a.Key == cum.Key && a.Inv < cum.Inv).ToList();
            var was = earlier.Sum(a => a.Qty);
            if (earlier.Count == 0 || cum.Qty >= was - 0.01) continue;
            yield return new Anomaly
            {
                Fingerprint = $"CUM|LEDGER|{ClaimDating.Norm(cum.Sub)}|{cum.Inv}|{cum.Key}", Kind = AnomalyKinds.Cumulative, Severity = InsightSeverity.Medium,
                Title = $"{cum.Sub} {InvText(cum.Inv)} (cumulative) states {F(cum.Qty)} for {cum.Room} {cum.Stage} {cum.Item}, earlier invoices {F(was)}",
                Explanation = "A cumulative invoice replaces his earlier lines for the key, so his total for the room went down.",
                SuggestedAction = "Check whether earlier work was withdrawn or the cumulative block is incomplete.", Subcontractor = cum.Sub, InvoiceNo = cum.Inv,
                Room = cum.Room, Stage = cum.Stage, Item = cum.Item, Score = was - cum.Qty, Evidence = cum.Lines.Concat(earlier.SelectMany(e => e.Lines)).Select(Ev).ToList(),
            };
        }
    }

    // ------------------------------------------------------------------ 10. round numbers / first digits (information only)

    internal static IEnumerable<Anomaly> NumberPatterns(List<Agg> aggs, InsightsData d)
    {
        var minN = (int)d.Threshold(InsightThresholds.BenfordMinN);
        var roundShare = d.Threshold(InsightThresholds.RoundShare);
        foreach (var g in aggs.Where(a => a.Qty > 0).GroupBy(a => ClaimDating.Norm(a.Sub)))
        {
            var vals = g.Select(a => a.Qty).ToList();
            var name = g.First().Sub;
            if (vals.Count >= minN)
            {
                var (chi, dev, n, counts) = RobustStats.Benford(vals);
                if (dev)
                {
                    var top = Enumerable.Range(1, 9).Select(dg => (dg, Obs: counts[dg] / (double)n, Exp: Math.Log10(1 + 1.0 / dg))).OrderByDescending(x => Math.Abs(x.Obs - x.Exp)).First();
                    yield return new Anomaly
                    {
                        Fingerprint = $"BENFORD|{g.Key}", Kind = AnomalyKinds.Benford, Severity = InsightSeverity.Info,
                        Title = $"{name}: first digits of {n} claim quantities deviate from the usual pattern (chi2 {chi:0})",
                        Explanation = $"Counts of naturally occurring quantities follow Benford's law (30 % start with 1). Here digit {top.dg} starts {top.Obs:P0} of the quantities (expected {top.Exp:P0}). " +
                                      "Room counts repeat by unit type, so this is information only - it is worth a look together with other warnings.",
                        SuggestedAction = "No action on its own.", Subcontractor = name, Score = chi,
                        Evidence = g.OrderByDescending(a => a.Qty).Take(10).SelectMany(a => a.Lines.Take(1)).Select(Ev).ToList(),
                    };
                }
            }
            var big = vals.Where(v => v >= 10).ToList();
            if (big.Count >= 20)
            {
                var round = big.Count(v => Math.Abs(v % 10) < 1e-9) / (double)big.Count;
                if (round >= roundShare)
                    yield return new Anomaly
                    {
                        Fingerprint = $"ROUND|{g.Key}", Kind = AnomalyKinds.RoundNumbers, Severity = InsightSeverity.Info,
                        Title = $"{name}: {round:P0} of quantities of 10 or more are round tens",
                        Explanation = $"{big.Count(v => Math.Abs(v % 10) < 1e-9)} of {big.Count} claim quantities are multiples of 10. Counted points are rarely round; estimated ones often are.",
                        SuggestedAction = "Spot-check a few of the round claims against the drawings.", Subcontractor = name, Score = round,
                        Evidence = g.Where(a => a.Qty >= 10 && Math.Abs(a.Qty % 10) < 1e-9).Take(15).SelectMany(a => a.Lines.Take(1)).Select(Ev).ToList(),
                    };
            }
        }
    }

    // ------------------------------------------------------------------ 11. rate on invoice <> contract rate

    internal static IEnumerable<Anomaly> RateChecks(AnomalyInputs i)
    {
        var s = i.Project;
        var tol = i.Data.Threshold(InsightThresholds.RateTolerance);
        var items = s.ContractItems.GroupBy(c => (C: c.ContractNo.Trim().ToUpperInvariant(), N: c.ItemNo.Trim().ToUpperInvariant())).ToDictionary(g => g.Key, g => g.First());
        bool Differs(double a, double b) => b > 0 && a > 0 && Math.Abs(a - b) / b > tol;
        foreach (var h in s.SubInvoices.Where(x => InvoiceKinds.IsSubcontractor(x) && x.Status != SubInvoiceStatus.Rejected))
        {
            var bad = s.SubInvoiceLines.Where(l => l.SubInvoiceId == h.Id && l.Kind == "ITEM" && items.TryGetValue((h.ContractNo.Trim().ToUpperInvariant(), l.ItemNo.Trim().ToUpperInvariant()), out var ci) && Differs(l.Rate, ci.Rate))
                .Select(l => (L: l, C: items[(h.ContractNo.Trim().ToUpperInvariant(), l.ItemNo.Trim().ToUpperInvariant())])).ToList();
            if (bad.Count == 0) continue;
            var value = bad.Sum(b => (b.L.Rate - b.C.Rate) * b.L.CumQty * (b.L.StagePct > 0 ? b.L.StagePct : 1));
            yield return new Anomaly
            {
                Fingerprint = $"RATE|SUBINV|{h.Id}", Kind = AnomalyKinds.Rate, Severity = InsightSeverity.High,
                Title = $"{h.Title}: {bad.Select(b => b.L.ItemNo).Distinct().Count()} items priced off the contract rate (SAR {value:N0} to date)",
                Explanation = string.Join("; ", bad.GroupBy(b => b.L.ItemNo).Take(8).Select(g => $"item {g.Key}: invoice {g.First().L.Rate:N2} vs contract {g.First().C.Rate:N2}")),
                SuggestedAction = "Use the contract rate (Contracts page) and rebuild the invoice; a different rate needs a variation.",
                Subcontractor = h.Subcontractor, InvoiceNo = h.InvoiceNo, Score = Math.Abs(value),
                Evidence = new List<Evidence> { InvEv(h) }.Concat(bad.Select(b => b.C).DistinctBy(c => c.Id).Take(20).Select(c => new Evidence(EvidenceKinds.ContractItem, c.Id, $"{c.ContractNo} item {c.ItemNo} @ {c.Rate:N2}", NavKey: c.ContractNo))).ToList(),
            };
        }
        // the invoice template (head-office layout) carries rates too
        foreach (var g in s.TemplateRows.Where(r => r.Kind == "ITEM" && r.Rate > 0).GroupBy(r => r.ContractNo.Trim().ToUpperInvariant()))
        {
            var bad = g.Where(r => items.TryGetValue((g.Key, r.ItemNo.Trim().ToUpperInvariant()), out var ci) && Differs(r.Rate, ci.Rate))
                .GroupBy(r => r.ItemNo).Select(x => (Item: x.Key, Rate: x.First().Rate, C: items[(g.Key, x.Key.Trim().ToUpperInvariant())])).ToList();
            if (bad.Count == 0) continue;
            yield return new Anomaly
            {
                Fingerprint = $"RATE|TEMPLATE|{g.Key}", Kind = AnomalyKinds.Rate, Severity = InsightSeverity.Medium,
                Title = $"Invoice template {g.Key}: {bad.Count} items with a rate different from the contract schedule",
                Explanation = string.Join("; ", bad.Take(10).Select(b => $"item {b.Item}: template {b.Rate:N2} vs contract {b.C.Rate:N2}")),
                SuggestedAction = "Check which one is the signed rate (signed PDF schedule) and correct the other.",
                Score = bad.Count, Evidence = bad.Take(30).Select(b => new Evidence(EvidenceKinds.ContractItem, b.C.Id, $"{b.C.ContractNo} item {b.Item}: {b.Rate:N2} vs {b.C.Rate:N2}", NavKey: b.C.ContractNo)).ToList(),
            };
        }
        // legacy statements: line rate vs the chain line rate
        var qty = s.Lines.ToDictionary(l => l.Id);
        foreach (var inv in s.Invoices.Where(x => x.Status != InvoiceStatus.Rejected))
        {
            var bad = s.InvoiceLines.Where(l => l.InvoiceId == inv.Id && qty.TryGetValue(l.LineId, out var q) && Differs(l.Rate, q.Rate)).ToList();
            if (bad.Count == 0) continue;
            yield return new Anomaly
            {
                Fingerprint = $"RATE|STMT|{inv.Id}", Kind = AnomalyKinds.Rate, Severity = InsightSeverity.Medium,
                Title = $"{inv.Subcontractor} {inv.InvoiceNo}: {bad.Count} lines at a rate other than the agreed rate",
                Explanation = string.Join("; ", bad.Take(6).Select(l => $"{qty[l.LineId].Room} {qty[l.LineId].System}: {l.Rate:N2} vs {qty[l.LineId].Rate:N2}")),
                SuggestedAction = "Certify at the agreed rate.", Subcontractor = inv.Subcontractor, Score = bad.Sum(l => Math.Abs(l.Rate - qty[l.LineId].Rate) * l.CumQty),
                Evidence = new() { new(EvidenceKinds.Statement, inv.Id, inv.InvoiceNo, NavKey: inv.Subcontractor + "|" + inv.InvoiceNo) },
            };
        }
    }
}
