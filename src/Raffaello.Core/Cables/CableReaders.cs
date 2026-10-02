using System.Globalization;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace Raffaello.Core.Cables;

public sealed class CableReadOptions
{
    /// <summary>Building for runs / panels whose names do not say it (BR / HT tokens win).</summary>
    public string Building { get; init; } = "";
    /// <summary>Remembered schedule column mappings.</summary>
    public IReadOnlyList<CableImportProfile> Profiles { get; init; } = Array.Empty<CableImportProfile>();
    /// <summary>Column mapping chosen by the user for a schedule (FIELD -> header); overrides detection.</summary>
    public IReadOnlyDictionary<string, string>? Mapping { get; init; }
    /// <summary>Pages to read (1-based); null = all.</summary>
    public IReadOnlyCollection<int>? Pages { get; init; }
}

/// <summary>Runs and panels read from one file, for review before they are saved to the register.</summary>
public sealed class CableReadResult
{
    public string FileName { get; init; } = "";
    public string Kind { get; set; } = "";
    public List<CableRun> Runs { get; } = new();
    public List<CablePanel> Panels { get; } = new();
    public List<string> Issues { get; } = new();
    /// <summary>Schedule: the FIELD -> header mapping used (show it, let the user change it, remember it).</summary>
    public Dictionary<string, string> Mapping { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string HeaderSignature { get; set; } = "";
    public List<string> Headers { get; } = new();
    public int Pages { get; set; }
    public string Summary => $"{Kind} {Path.GetFileName(FileName)}: {Runs.Count} runs ({Runs.Count(r => r.Confidence >= 0.8)} high confidence, {Runs.Count(r => r.SizeKey.Length == 0)} without size), " +
                             $"{Panels.Count} panels, {Pages} page(s), {Issues.Count} notes";
}

/// <summary>
/// [cables] Readers for the register: cable schedules (Excel / CSV, columns by header, remembered), SLD PDFs (vector text + lines, PdfPig),
/// DWG / DXF (ACadSharp: block attributes, texts, lines / polylines) and scanned SLDs (page image -> OCR words + rulings from the document
/// reader's OCR engine). Geometry is analysed by <see cref="SldAnalyzer"/>; nothing is saved here.
/// </summary>
public static class CableReaders
{
    public static CableReadResult Read(string path, CableReadOptions? o = null)
    {
        o ??= new CableReadOptions();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" or ".xlsm" or ".csv" => CableScheduleImporter.Read(path, o),
            ".pdf" => ReadPdf(path, o),
            ".dxf" or ".dwg" => CadSld.Read(path, o),
            _ => throw new InvalidOperationException($"{Path.GetFileName(path)}: cable schedules (.xlsx / .csv), SLD PDFs and DWG / DXF drawings can be read."),
        };
    }

    // ------------------------------------------------------------------ PDF (vector)

    public static CableReadResult ReadPdf(string path, CableReadOptions o)
    {
        var res = new CableReadResult { FileName = path, Kind = CableSources.SldPdf };
        using var doc = PdfDocument.Open(path);
        res.Pages = doc.NumberOfPages;
        var scans = new List<int>();
        foreach (var page in doc.GetPages())
        {
            if (o.Pages != null && !o.Pages.Contains(page.Number)) continue;
            var sp = PdfPage(page);
            if (sp.Texts.Count < 3) { scans.Add(page.Number); continue; }
            AddPage(res, SldAnalyzer.Analyze(sp), sp.Number, CableSources.SldPdf, Path.GetFileName(path), o);
        }
        if (scans.Count > 0) res.Issues.Add($"pages without a text layer (scans) - use READ SCANNED SLD (OCR): {string.Join(", ", scans)}");
        return res;
    }

    public static SldPage PdfPage(UglyToad.PdfPig.Content.Page page)
    {
        var h = page.Height;
        var sp = new SldPage { Number = page.Number, Width = page.Width, Height = page.Height };
        foreach (var w in page.GetWords())
        {
            var b = w.BoundingBox;
            if (string.IsNullOrWhiteSpace(w.Text)) continue;
            sp.Texts.Add(new SldText(w.Text, b.Left, h - b.Top, Math.Max(b.Width, 0.1), Math.Max(b.Height, 0.1)));
        }
        foreach (var path in page.Paths)
        {
            if (!path.IsStroked && !path.IsFilled) continue;
            foreach (var sub in path)
            {
                if (sub.IsDrawnAsRectangle)
                {
                    var r = sub.GetBoundingRectangle();
                    if (r is { } rr && rr.Width > 0 && rr.Height > 0)
                    {
                        // thin filled rectangles are drawn lines (bus bars); others are boxes
                        if (rr.Height < 1.5 && rr.Width > 4) sp.Segments.Add(new SldSegment(rr.Left, h - (rr.Bottom + rr.Height / 2), rr.Right, h - (rr.Bottom + rr.Height / 2)));
                        else if (rr.Width < 1.5 && rr.Height > 4) sp.Segments.Add(new SldSegment(rr.Left + rr.Width / 2, h - rr.Bottom, rr.Left + rr.Width / 2, h - rr.Top));
                        else sp.Rects.Add(new SldRect(rr.Left, h - rr.Top, rr.Width, rr.Height));
                    }
                    continue;
                }
                PdfPoint? start = null, last = null;
                foreach (var c in sub.Commands)
                {
                    switch (c)
                    {
                        case PdfSubpath.Move m: start = last = m.Location; break;
                        case PdfSubpath.Line l:
                            sp.Segments.Add(new SldSegment(l.From.X, h - l.From.Y, l.To.X, h - l.To.Y));
                            start ??= l.From;
                            last = l.To;
                            break;
                        case PdfSubpath.Close when start is { } s0 && last is { } l0:
                            sp.Segments.Add(new SldSegment(l0.X, h - l0.Y, s0.X, h - s0.Y));
                            last = start;
                            break;
                    }
                }
                // closed 4-corner paths are boxes too
                var pts = sub.Commands.OfType<PdfSubpath.Line>().ToList();
                if (pts.Count is 3 or 4 && sub.IsClosed())
                {
                    var bb = sub.GetBoundingRectangle();
                    if (bb is { } b2 && b2.Width > 2 && b2.Height > 2 && pts.All(p => Math.Abs(p.From.X - p.To.X) < 0.01 || Math.Abs(p.From.Y - p.To.Y) < 0.01))
                        sp.Rects.Add(new SldRect(b2.Left, h - b2.Top, b2.Width, b2.Height));
                }
            }
        }
        return sp;
    }

    // ------------------------------------------------------------------ scanned SLD (OCR)

    /// <summary>Scanned SLD pages: rendered by the document reader's rasterizer, words + rulings from its layout OCR engine.</summary>
    public static async Task<CableReadResult> ReadScanAsync(string path, IPageRasterizer rasterizer, ILayoutOcrEngine ocr, CableReadOptions? o = null, CancellationToken ct = default)
    {
        o ??= new CableReadOptions();
        var res = new CableReadResult { FileName = path, Kind = CableSources.SldScan };
        var count = PdfTextReader.PageCount(path);
        res.Pages = count;
        for (var n = 1; n <= count; n++)
        {
            if (o.Pages != null && !o.Pages.Contains(n)) continue;
            var img = await rasterizer.RenderAsync(path, n, 300, ct).ConfigureAwait(false);
            if (img is null) { res.Issues.Add($"page {n}: could not be rendered"); continue; }
            var page = await ocr.RecognizeLayoutAsync(img, new OcrHints { Script = Scripts.Latin, DetectRulings = true, KeepImage = false, Dpi = 300 }, ct).ConfigureAwait(false);
            AddPage(res, SldAnalyzer.Analyze(FromOcr(page, n)), n, CableSources.SldScan, Path.GetFileName(path), o);
        }
        return res;
    }

    public static SldPage FromOcr(OcrPage page, int number)
    {
        var sp = new SldPage { Number = number, Width = page.Width, Height = page.Height, FromScan = true };
        foreach (var w in page.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)))
            sp.Texts.Add(new SldText(w.Text, w.Box.X, w.Box.Y, w.Box.W, w.Box.H));
        foreach (var r in page.Rulings) sp.Segments.Add(new SldSegment(r.X1, r.Y1, r.X2, r.Y2));
        return sp;
    }

    // ------------------------------------------------------------------ edges -> runs / panels

    internal static void AddPage(CableReadResult res, SldPageResult page, int pageNo, string kind, string doc, CableReadOptions o)
    {
        foreach (var i in page.Issues) res.Issues.Add(i);
        var panels = res.Panels.ToDictionary(p => p.Key);
        CablePanel Panel(SldNode n)
        {
            var key = PanelNames.KeyOf(n.Name, o.Building);
            if (panels.TryGetValue(key, out var p)) return p;
            p = CableService.NewPanel(n.Name, key, kind, doc, CableStatus.Proposed, pageNo, o.Building);
            panels[p.Key] = p;
            res.Panels.Add(p);
            return p;
        }
        foreach (var n in page.Nodes) Panel(n);
        foreach (var (node, parent) in page.FedFrom)
        {
            var p = Panel(node);
            if (p.ParentKey.Length == 0) p.ParentKey = PanelNames.KeyOf(parent, o.Building);
        }
        foreach (var e in page.Edges)
        {
            var from = Panel(e.From);
            var to = Panel(e.To);
            if (to.ParentKey.Length == 0) to.ParentKey = from.Key;
            var phase = e.Sizes.Where(s => !s.IsEarthOnly && (s.Cores > 1 || s.SingleCoreBundle)).OrderByDescending(s => s.Mm2).FirstOrDefault()
                        ?? e.Sizes.FirstOrDefault(s => !s.IsEarthOnly) ?? e.Sizes.FirstOrDefault();
            var earth = phase?.EarthKey is { Length: > 0 } ek ? ek : e.Sizes.FirstOrDefault(s => s != phase && s.Cores == 1)?.Key ?? "";
            if (res.Runs.Any(r => r.FromKey == from.Key && r.ToKey == to.Key && r.SizeKey == (phase?.Key ?? ""))) continue;
            res.Runs.Add(new CableRun
            {
                Building = from.Building.Length > 0 ? from.Building : to.Building.Length > 0 ? to.Building : o.Building, Level = to.Level.Length > 0 ? to.Level : from.Level,
                FromKey = from.Key, FromName = from.Name, ToKey = to.Key, ToName = to.Name, Cores = phase?.Cores ?? 0, SizeMm2 = phase?.Mm2 ?? 0, SizeKey = phase?.Key ?? "",
                Conductor = phase?.Conductor is { Length: > 0 } c ? c : "CU", Insulation = phase?.Insulation ?? "", EarthSizeKey = earth, DesignLength = e.Length, Breaker = e.Breaker,
                Status = CableStatus.Proposed, SourceKind = kind, SourceDoc = doc, SourcePage = pageNo, Confidence = Math.Round(e.Confidence, 2), Notes = e.How,
            });
        }
    }

    internal static string Inv(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
