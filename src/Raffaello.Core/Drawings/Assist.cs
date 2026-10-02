using System.Text.Json.Nodes;
using Raffaello.Core.Documents;

namespace Raffaello.Core.Drawings;

/// <summary>
/// [drawings] Optional Claude vision check of low-confidence hits (only when the user enabled cloud reading): the reference symbol and
/// the candidate are sent side by side; a "no" marks the hit AI REJECTED (not counted, the user can restore it).
/// </summary>
public static class VisionHitVerifier
{
    public static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["same_symbol"] = new JsonObject { ["type"] = "boolean" },
            ["confidence"] = new JsonObject { ["type"] = "number" },
            ["reason"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("same_symbol", "confidence", "reason"),
        ["additionalProperties"] = false,
    };

    /// <summary>Reference (left) and candidate (right) on one white strip, both scaled to 96 px high.</summary>
    public static byte[] PairImage(GrayImage reference, ColorImage sheet, DwgHit hit)
    {
        var pad = Math.Max(hit.W, hit.H) * 0.6;
        var cand = sheet.Crop((int)(hit.X - pad), (int)(hit.Y - pad), (int)(hit.W + 2 * pad), (int)(hit.H + 2 * pad)).Resize(96, 96);
        var refc = reference.ToColor().Resize(96, 96);
        var strip = new ColorImage(96 * 2 + 24, 96);
        for (var y = 0; y < 96; y++)
            for (var x = 0; x < 96; x++)
            {
                var (r, g, b) = refc.Get(x, y); strip.Set(x, y, new Rgb(r, g, b));
                (r, g, b) = cand.Get(x, y); strip.Set(x + 120, y, new Rgb(r, g, b));
            }
        strip.Line(108, 0, 108, 95, new Rgb(0x8B, 0, 0), 2);
        return strip.ToPng();
    }

    public static bool ShouldCheck(DwgHit h, double threshold, double margin) => h.Origin == DwgOrigins.Template && h.Score < threshold + margin;

    /// <summary>Checks the hits within the margin above the threshold; returns how many were rejected.</summary>
    public static async Task<int> VerifyAsync(IVisionReader vision, IReadOnlyList<DwgHit> hits, IReadOnlyDictionary<long, GrayImage> references, ColorImage sheet,
        IReadOnlyDictionary<long, DwgSymbol> symbols, double threshold, double margin, CancellationToken ct = default)
    {
        if (!vision.IsAvailable) return 0;
        var rejected = 0;
        foreach (var h in hits.Where(h => ShouldCheck(h, threshold, margin) && references.ContainsKey(h.SymbolId)))
        {
            ct.ThrowIfCancellationRequested();
            var name = symbols.TryGetValue(h.SymbolId, out var s) ? s.Name : h.SymbolName;
            var req = new VisionRequest
            {
                DocumentKind = "electrical drawing symbol check", MediaType = "image/png", Page = 1, Schema = Schema,
                Content = PairImage(references[h.SymbolId], sheet, h),
                Instructions = $"Left of the red divider: the reference symbol '{name}'. Right: a candidate cut from the drawing (it may be rotated, mirrored or crossed by lines). " +
                               "Is the candidate in the centre of the right image the same electrical symbol as the reference? Different symbols that look similar (single vs twin socket, 1-gang vs 2-gang switch) are NOT the same.",
            };
            try
            {
                var ans = await vision.ExtractAsync(req, ct).ConfigureAwait(false);
                var same = ans?["same_symbol"]?.GetValue<bool>() ?? true;
                h.Status = same ? DwgHitStatus.AiConfirmed : DwgHitStatus.AiRejected;
                h.Note = (ans?["reason"]?.GetValue<string>() ?? "").Trim();
                if (!same) rejected++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { h.Note = "vision check failed: " + ex.Message; }
        }
        return rejected;
    }
}

/// <summary>[drawings] A proposed library entry read from the drawing legend.</summary>
public sealed record LegendProposal(RectD SymbolBox, string Text, string Name, string System, string Item, string Mount, bool IsLight, double SecondFixFactor, bool Excluded);

/// <summary>
/// [drawings] Legend reading: the legend area is split into rows; the left-most ink cluster of a row is the symbol, the rest its
/// description (read by the local OCR engine). Descriptions are mapped to system / item with the house rules (switches under LIGHT,
/// twin data = 2 at 2nd fix, thermostat = GRMS CP-4, terrace / WP excluded). The user confirms each proposal.
/// </summary>
public static class LegendProposer
{
    public static async Task<List<LegendProposal>> ProposeAsync(GrayImage legend, IOcrEngine ocr, CancellationToken ct = default)
    {
        var res = new List<LegendProposal>();
        var ink = legend.Ink();
        var rows = new List<(int Y0, int Y1)>();
        int? start = null;
        for (var y = 0; y <= legend.Height; y++)
        {
            var any = y < legend.Height && Enumerable.Range(0, legend.Width).Any(x => ink[x, y]);
            if (any && start is null) start = y;
            if (!any && start is int s) { if (y - s >= 4) rows.Add((s, y)); start = null; }
        }
        foreach (var (y0, y1) in rows)
        {
            var cols = Enumerable.Range(0, legend.Width).Where(x => Enumerable.Range(y0, y1 - y0).Any(y => ink[x, y])).ToList();
            if (cols.Count == 0) continue;
            // symbol = first cluster of columns, separated from the text by a gap of 2x the row height / 2
            var gap = Math.Max(4, (y1 - y0) / 2);
            var symEnd = cols[0];
            foreach (var c in cols) { if (c - symEnd > gap) break; symEnd = c; }
            var box = new RectD(cols[0], y0, symEnd - cols[0] + 1, y1 - y0);
            var textX = cols.FirstOrDefault(c => c > symEnd + gap, -1);
            var text = "";
            if (textX >= 0 && ocr.IsAvailable)
            {
                var crop = legend.Crop(textX, y0, legend.Width - textX, y1 - y0);
                text = (await ocr.RecognizeAsync(new PageImage { Bytes = crop.ToPng(), MediaType = "image/png", Width = crop.Width, Height = crop.Height, Coverage = 1 }, ct).ConfigureAwait(false)).Trim();
            }
            res.Add(Map(box, text));
        }
        return res;
    }

    /// <summary>Description -> system / item / mount (house rules).</summary>
    public static LegendProposal Map(RectD box, string text)
    {
        var t = (text ?? "").ToUpperInvariant();
        bool Has(params string[] k) => k.Any(t.Contains);
        string sys, item; var mount = "W"; var light = false; double second = 1; var excl = false;
        if (Has("THERMOSTAT", "GRMS", "CP-4", "CP4", "DND", "KEY CARD")) { sys = "GRMS"; item = "GRMS"; }
        else if (Has("SWITCH", "DIMMER")) { sys = "LIGHT"; item = "LIGHT"; }
        else if (Has("DOWNLIGHT", "LIGHT", "LUMINAIRE", "PENDANT", "FITTING", "LED")) { sys = "LIGHT"; item = "LIGHT"; mount = "C"; light = true; }
        else if (Has("EMERGENCY", "EXIT")) { sys = "EMERGENCY LIGHT"; item = "EMERGENCY LIGHT"; mount = "C"; }
        else if (Has("DATA", "TELEPHONE", "RJ45", "CAT6", "WAP", "ACCESS POINT")) { sys = "DATA"; item = "DATA"; if (Has("TWIN", "DOUBLE", "\"T\"")) second = 2; if (Has("WAP", "ACCESS POINT")) mount = "C"; }
        else if (Has("TV", "HDMI", "AV", "SPEAKER", "SOUNDBAR")) { sys = "AV"; item = "AV"; }
        else if (Has("ISOLATOR", "SPUR", "SOCKET", "OUTLET", "POWER", "FCU", "DP SWITCH")) { sys = "POWER"; item = "POWER"; if (Has("FCU", "HIGH LEVEL", "HIGH-LEVEL")) mount = "C"; }
        else if (Has("SMOKE", "DETECTOR", "FIRE")) { sys = "FIRE"; item = "FIRE"; mount = "C"; }
        else { sys = "OTHER"; item = "OTHER"; }
        if (Has("TERRACE", "WEATHERPROOF", " WP", "IP65")) excl = true;
        var name = t.Length > 0 ? (t.Length > 40 ? t[..40] : t) : $"SYMBOL {box.X:0}-{box.Y:0}";
        return new LegendProposal(box, text ?? "", name, sys, item, mount, light, second, excl);
    }
}
