using System.Globalization;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Drawings;

public sealed record ChangeRegion(RectD Box, int Area, string Kind);

/// <summary>[drawings] Result of comparing two revisions of a sheet (coordinates of the OLD sheet).</summary>
public sealed class RevisionCompareResult
{
    public Affine2D NewToOld { get; init; }
    public double AlignScore { get; init; }
    public List<ChangeRegion> Added { get; } = new();
    public List<ChangeRegion> Removed { get; } = new();
    public List<DwgHit> AddedHits { get; } = new();
    public List<DwgHit> RemovedHits { get; } = new();
    public List<DwgRevisionDelta> Deltas { get; } = new();
    public ColorImage? Overlay { get; set; }
    public List<string> AffectedRooms => Deltas.Select(d => d.Room).Distinct().OrderBy(r => r).ToList();
}

/// <summary>
/// [drawings] Drawing revision compare: aligns the new revision to the old one, highlights added (red) / removed (blue) ink,
/// matches the symbol hits of both takeoffs (added / removed symbols) and lists the count deltas per room x stage x item -
/// which feed "update PROJECT QTY (diff)" and "create variation draft".
/// </summary>
public static class RevisionComparer
{
    public static RevisionCompareResult Compare(GrayImage oldImg, GrayImage newImg, IReadOnlyList<DwgHit> oldHits, IReadOnlyList<DwgHit> newHits,
        IReadOnlyDictionary<long, DwgSymbol> symbols, DrawingSettings settings, RoomAssigner? rooms = null, Affine2D? newToOld = null, int tolerancePx = 3, int minArea = 30)
    {
        double score = 1;
        if (newToOld is null)
        {
            var reg = Registration.Align(oldImg, newImg);
            newToOld = reg.MovingToFixed; score = reg.Score;
        }
        var t = newToOld.Value;
        var res = new RevisionCompareResult { NewToOld = t, AlignScore = score };
        var warped = Registration.Warp(newImg, t, oldImg.Width, oldImg.Height);
        var oldInk = oldImg.Ink(); var newInk = warped.Ink();
        var added = newInk.AndNot(oldInk.Dilate(tolerancePx)).Open(1);
        var removed = oldInk.AndNot(newInk.Dilate(tolerancePx)).Open(1);
        foreach (var c in added.Dilate(4).Components(minArea)) res.Added.Add(new ChangeRegion(new RectD(c.X, c.Y, c.W, c.H), c.Area, "ADDED"));
        foreach (var c in removed.Dilate(4).Components(minArea)) res.Removed.Add(new ChangeRegion(new RectD(c.X, c.Y, c.W, c.H), c.Area, "REMOVED"));

        // symbols: new hits mapped to the old frame, matched by symbol within a tolerance
        var mapped = newHits.Where(h => DwgHitStatus.Counts(h.Status)).Select(h =>
        {
            var c = t.Apply(h.Center); var k = t.Scale;
            return new DwgHit { SymbolId = h.SymbolId, SymbolName = h.SymbolName, X = c.X - h.W * k / 2, Y = c.Y - h.H * k / 2, W = h.W * k, H = h.H * k, Score = h.Score, MountingHeightM = h.MountingHeightM, Status = h.Status };
        }).ToList();
        var olds = oldHits.Where(h => DwgHitStatus.Counts(h.Status)).ToList();
        var usedOld = new bool[olds.Count];
        foreach (var n in mapped)
        {
            var best = -1; var bd = double.MaxValue;
            for (var i = 0; i < olds.Count; i++)
            {
                if (usedOld[i] || olds[i].SymbolId != n.SymbolId) continue;
                var d = olds[i].Center.DistanceTo(n.Center);
                if (d < Math.Max(olds[i].W, olds[i].H) * 0.6 && d < bd) { bd = d; best = i; }
            }
            if (best >= 0) usedOld[best] = true; else res.AddedHits.Add(n);
        }
        for (var i = 0; i < olds.Count; i++) if (!usedOld[i]) res.RemovedHits.Add(olds[i]);
        if (rooms is { IsEmpty: false })
        {
            foreach (var h in mapped.Concat(olds)) h.Room = rooms.RoomAt(h.Center);
        }
        var before = TakeoffCounter.Count(olds, symbols, settings);
        var after = TakeoffCounter.Count(mapped, symbols, settings);
        foreach (var k in before.Keys.Concat(after.Keys).Distinct().OrderBy(k => k.Room).ThenBy(k => k.Stage).ThenBy(k => k.Item))
        {
            double o = before.GetValueOrDefault(k), n = after.GetValueOrDefault(k);
            if (Math.Abs(o - n) < 1e-9) continue;
            res.Deltas.Add(new DwgRevisionDelta { Room = k.Room, Stage = k.Stage, Item = k.Item, OldQty = o, NewQty = n, Delta = n - o });
        }
        res.Overlay = Overlay(oldImg, added, removed, res, symbols);
        return res;
    }

    /// <summary>Old revision in grey, added ink dark red, removed ink blue, changed symbols boxed.</summary>
    public static ColorImage Overlay(GrayImage oldImg, BitMask added, BitMask removed, RevisionCompareResult r, IReadOnlyDictionary<long, DwgSymbol> symbols)
    {
        var img = new ColorImage(oldImg.Width, oldImg.Height);
        for (var i = 0; i < oldImg.Data.Length; i++)
        {
            var v = (byte)(255 - (255 - oldImg.Data[i]) * 0.45);
            img.Data[i * 3] = v; img.Data[i * 3 + 1] = v; img.Data[i * 3 + 2] = v;
        }
        img.Tint(removed, new Rgb(0, 0x44, 0xDD), 0.9);
        img.Tint(added, new Rgb(0xC0, 0, 0), 0.9);
        foreach (var h in r.AddedHits) img.Rect(h.X - 4, h.Y - 4, h.W + 8, h.H + 8, new Rgb(0xC0, 0, 0), 2);
        foreach (var h in r.RemovedHits) img.Rect(h.X - 4, h.Y - 4, h.W + 8, h.H + 8, new Rgb(0, 0x44, 0xDD), 2);
        return img;
    }

    /// <summary>Count deltas as PROJECT QTY proposals: the NEW revision's counts per room for the keys that changed.</summary>
    public static List<QtyProposal> Proposals(RevisionCompareResult r, IEnumerable<Domain.RoomQty> current, string building)
    {
        var counts = r.Deltas.Where(d => d.Room != RoomAssigner.Unassigned).ToDictionary(d => (d.Room, d.Stage, d.Item), d => d.NewQty);
        return QtyDiff.Build(counts, current, building).Where(p => p.Status != "SAME").ToList();
    }

    /// <summary>
    /// Creates a DRAFT variation through the variations module: one ADDITION / OMISSION line per stage x item (rooms in the text),
    /// rates left at 0 for the user to price from the contract / BOQ. Additive only - nothing else is changed.
    /// </summary>
    public static Variation CreateVariationDraft(IVariationStore store, IReadOnlyList<DwgRevisionDelta> deltas, DwgSheet oldSheet, DwgSheet newSheet, string? title = null)
    {
        if (deltas.Count == 0) throw new InvalidOperationException("No count changes between the two revisions - nothing to put in a variation.");
        var v = store.Create(new Variation
        {
            Type = VariationTypes.Vo, Building = newSheet.Building, Status = VariationStatus.Draft,
            Title = title ?? $"Drawing {newSheet.SheetNo} rev {oldSheet.Revision} -> rev {newSheet.Revision}",
            Description = $"Quantities changed between {oldSheet.FileName} (rev {oldSheet.Revision}) and {newSheet.FileName} (rev {newSheet.Revision}), counted by Raffaello drawing compare.",
            ConsultantRef = newSheet.SheetNo, RateSource = "CONTRACT", Notes = "Draft from DRAWINGS > REVISION COMPARE - price the lines and attach the revised drawing.",
        });
        var lines = new List<VariationLine>();
        foreach (var g in deltas.GroupBy(d => (d.Stage, d.Item)).OrderBy(g => g.Key.Item).ThenBy(g => g.Key.Stage))
        {
            foreach (var sign in new[] { 1, -1 })
            {
                var part = g.Where(d => Math.Sign(d.Delta) == sign).ToList();
                if (part.Count == 0) continue;
                var qty = Math.Abs(part.Sum(d => d.Delta));
                var rooms = string.Join(", ", part.Select(d => $"{d.Room} {(d.Delta > 0 ? "+" : "")}{d.Delta.ToString("0.##", CultureInfo.InvariantCulture)}"));
                lines.Add(new VariationLine
                {
                    Kind = sign > 0 ? VariationLineKinds.Addition : VariationLineKinds.Omission, SourceKind = "", ItemCode = g.Key.Item,
                    Description = $"{g.Key.Item} - {g.Key.Stage} ({rooms})", Unit = part.First().Unit is "m" ? "M" : "PT", Qty = qty, Rate = 0,
                    Notes = $"rev {oldSheet.Revision} -> {newSheet.Revision}",
                });
            }
        }
        store.Save(v, lines);
        return store.Get(v.Id) ?? v;
    }
}
