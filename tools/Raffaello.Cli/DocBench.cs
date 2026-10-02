using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Raffaello.Core.Contracts;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using Raffaello.Core.Documents.Smart;
using Raffaello.Core.Materials;
using Raffaello.Ocr;

namespace Raffaello.Cli;

/// <summary>
/// raffaello-cli doc-bench --samples DIR --out DIR [--truth FILE.json] [--only contract2,contract1,mir,po,statement] [--pages 4-23] [--no-cache]
/// Runs the offline reader on real sample documents and measures it against ground truth (the contract Excel, the digital DN, and a
/// hand-transcribed truth file). Results go only to --out (results.md, results.json, per-item CSV). Engines: PDF text layer,
/// PaddleOCR (single pass), ensemble (PaddleOCR + second-model re-reads + arithmetic voting), Claude vision (only when
/// ANTHROPIC_API_KEY is set). Windows OCR is not available on Linux.
/// raffaello-cli read-doc FILE [--pages 1-3] : prints what the smart reader sees (type, source, quality, text).
/// </summary>
public static class DocBench
{
    public static bool Handles(string cmd) => cmd is "doc-bench" or "read-doc";

    private sealed class Ctx
    {
        public string Samples = "";
        public string Out = "";
        public JsonNode? Truth;
        public HashSet<string> Only = new();
        public Func<int, bool>? Pages;
        public PaddleOcrEngine Paddle = null!;
        public ILayoutOcrEngine Engine = null!;
        public PdfiumRasterizer Raster = new();
        public IVisionReader? Vision;
        public readonly StringBuilder Md = new();
        public readonly JsonObject Json = new();
        public string? Find(string pattern) => Directory.EnumerateFiles(Samples).Where(f => Regex.IsMatch(Path.GetFileName(f), pattern, RegexOptions.IgnoreCase)).OrderBy(f => f.Length).FirstOrDefault();
        public SmartReaderOptions Options(Func<int, bool>? pages = null) => new()
        {
            Rasterizer = Raster, Engines = new[] { Engine }, Pages = pages ?? Pages, Progress = new Progress<string>(s => Console.Error.WriteLine("  " + s)),
        };
    }

    public static int Run(string[] args)
    {
        var opts = ParseOptions(args.Skip(1).ToArray(), out var positional);
        if (args[0] == "read-doc") return ReadDoc(positional.FirstOrDefault() ?? throw new ArgumentException("read-doc FILE"), opts);
        var c = new Ctx
        {
            Samples = opts.GetValueOrDefault("samples") ?? throw new ArgumentException("--samples DIR is required"),
            Out = opts.GetValueOrDefault("out") ?? throw new ArgumentException("--out DIR is required"),
        };
        Directory.CreateDirectory(c.Out);
        if (opts.TryGetValue("truth", out var tf) && File.Exists(tf)) c.Truth = JsonNode.Parse(File.ReadAllText(tf));
        if (opts.TryGetValue("only", out var only)) c.Only = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (opts.TryGetValue("pages", out var pg)) c.Pages = PageFilter(pg);
        c.Paddle = new PaddleOcrEngine();
        if (!c.Paddle.IsAvailable) { Console.Error.WriteLine("PaddleOCR is not available: " + c.Paddle.LoadError); return 1; }
        c.Engine = opts.ContainsKey("no-cache") ? c.Paddle : new CachedOcrEngine(c.Paddle, Path.Combine(c.Out, "ocr-cache"));
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(key)) c.Vision = new ClaudeVisionReader(true, key, null);

        c.Md.AppendLine("# Raffaello document reader - benchmark").AppendLine();
        c.Md.AppendLine($"Run {DateTime.Now:yyyy-MM-dd HH:mm}, machine {Environment.MachineName} ({Environment.OSVersion.Platform}, {Environment.ProcessorCount} cores). " +
                        $"Engines: PDF text layer (PdfPig), {c.Paddle.Name}, ensemble (PaddleOCR Arabic model + English-model re-reads + arithmetic / validator voting), " +
                        $"Windows OCR: n/a on Linux, Claude vision: {(c.Vision is null ? "not run (no ANTHROPIC_API_KEY)" : "enabled")}.").AppendLine();
        var sw = Stopwatch.StartNew();
        bool Want(string k) => c.Only.Count == 0 || c.Only.Contains(k);
        try
        {
            if (Want("contract2")) Contract2(c).GetAwaiter().GetResult();
            if (Want("contract1")) Contract1(c).GetAwaiter().GetResult();
            if (Want("mir")) Mir(c).GetAwaiter().GetResult();
            if (Want("po")) Po(c).GetAwaiter().GetResult();
            if (Want("statement")) Statement(c).GetAwaiter().GetResult();
        }
        finally
        {
            c.Md.AppendLine().AppendLine($"Total run time {sw.Elapsed.TotalMinutes:0.0} min{(c.Engine is CachedOcrEngine ce ? $" (OCR cache: {ce.Hits} hits, {ce.Misses} misses)" : "")}.");
            File.WriteAllText(Path.Combine(c.Out, "results.md"), c.Md.ToString());
            File.WriteAllText(Path.Combine(c.Out, "results.json"), c.Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine(c.Md.ToString());
            Console.WriteLine($"results written to {c.Out}");
        }
        return 0;
    }

    // =================================================================== contract 2: scanned rate schedule vs the contract Excel

    private static async Task Contract2(Ctx c)
    {
        var pdf = c.Find(@"contract_2.*\.pdf$");
        var xlsx = c.Find(@"contract_excel.*\.xlsx$");
        if (pdf is null || xlsx is null) { c.Md.AppendLine("## Contract 2 - skipped (contract_2*.pdf / contract_excel*.xlsx not found)").AppendLine(); return; }
        var truth = ContractScheduleExcel.Read(xlsx);
        Console.Error.WriteLine($"contract 2: {truth.Items.Count} truth items from Excel");
        var sw = Stopwatch.StartNew();
        var doc = await SmartReader.ReadAsync(pdf, c.Options()).ConfigureAwait(false);
        var ocrTime = sw.Elapsed;
        var schedulePages = doc.Pages.Where(p => p.Kind.Type == DocTypes.RateSchedule).ToList();

        // 1. text layer as-is (what a reader that trusts the scanner layer would get)
        var layerPages = doc.Pages.Select(p => new SmartPage { Number = p.Number, Base = p.Base, Text = p.Base.Text, ReadingText = p.Base.Text, Source = TextSource.TextLayer, Kind = p.Kind }).ToList();
        var layerRead = await ScheduleExtractor.ReadAsync(layerPages.Where(p => p.Number >= 4), null).ConfigureAwait(false);
        // 2. PaddleOCR single pass, no re-reads
        var single = await ScheduleExtractor.ReadAsync(schedulePages, null).ConfigureAwait(false);
        // 3. ensemble: re-reads with the second model + arithmetic voting
        sw.Restart();
        var ensemble = await ScheduleExtractor.ReadAsync(schedulePages, new EngineRereader(new[] { c.Engine })).ConfigureAwait(false);
        var ensTime = sw.Elapsed;

        var rows = new List<(string Engine, ScheduleScore S)>
        {
            ("PDF text layer (scanner OCR, used as-is)", Score(truth, layerRead)),
            ("PaddleOCR single pass", Score(truth, single)),
            ("Ensemble (Paddle + re-reads + voting)", Score(truth, ensemble)),
        };
        var layerQ = doc.Pages.Where(p => p.Base.LayerQuality != null).Select(p => p.Base.LayerQuality!.Score).DefaultIfEmpty(0).Average();
        c.Md.AppendLine("## 1. Signed contract PDF (scan, 23 pages) - rate schedule vs contract Excel").AppendLine();
        c.Md.AppendLine($"Ground truth: {truth.Items.Count} items, {truth.Sections.Count} sections, total SAR {truth.Items.Sum(i => i.Qty * i.Rate):N2}. " +
                        $"Text-layer plausibility (mean over pages) {layerQ:0.00} - {doc.Pages.Count(p => p.Base.LayerQuality?.IsGarbage == true)} of {doc.Pages.Count} layers flagged garbage and discarded. " +
                        $"Pages classified RATE SCHEDULE: {DocClassifier.Segments(doc.Pages.Select(p => (p.Number, p.Kind)).ToList()).Where(s => s.Type == DocTypes.RateSchedule).Select(s => s.Pages).DefaultIfEmpty("-").Aggregate((a, b) => a + "," + b)}. " +
                        $"OCR {ocrTime.TotalMinutes:0.0} min ({ocrTime.TotalSeconds / Math.Max(1, doc.Pages.Count(p => p.Source == TextSource.Ocr)):0} s/page), ensemble re-reads {ensemble.Rereads} crops in {ensTime.TotalSeconds:0} s.").AppendLine();
        c.Md.AppendLine("| Engine | Items found | Item no. | Qty | Rate | Unit | Total (q×r) | Description chars | Rows flagged for review | Wrong numbers NOT flagged |");
        c.Md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (e, s) in rows)
            c.Md.AppendLine($"| {e} | {s.Found}/{s.Truth} | {s.ItemNoRecall:P1} | {s.Qty:P1} | {s.Rate:P1} | {s.Unit:P1} | {s.Total:P1} | {s.Desc:P1} | {s.Flagged} | {s.SilentWrong} |");
        c.Md.AppendLine();
        var ens = rows[2].S;
        c.Md.AppendLine($"Ensemble: stated schedule total {(ensemble.StatedTotal is double st ? $"SAR {st:N2}" : "not read")}, rows sum SAR {ensemble.LinesTotal:N2} vs Excel SAR {truth.Items.Sum(i => i.Qty * i.Rate):N2}; " +
                        $"{ens.Derived} values derived from the arithmetic (flagged), {ens.Conflicts} conflicts left to the user. Issues: {string.Join("; ", ensemble.Issues.Select(i => i.Message))}").AppendLine();
        c.Md.AppendLine("Item no. = share of Excel items found with that number; Qty / Rate / Unit / Total = exact match on the matched items; Description chars = mean character accuracy " +
                        "(1 - edit distance / length on the normalised Arabic: diacritics, alef/ya/ta-marbuta variants and spaces ignored). 'Wrong numbers NOT flagged' = qty or rate wrong while the row was not marked for review - the number that matters for trust.").AppendLine();
        WriteItemsCsv(Path.Combine(c.Out, "contract2_items.csv"), truth, ensemble);
        c.Json["contract2"] = new JsonObject
        {
            ["truthItems"] = truth.Items.Count,
            ["engines"] = new JsonArray(rows.Select(r => (JsonNode)r.S.ToJson(r.Engine)).ToArray()),
            ["ocrMinutes"] = Math.Round(ocrTime.TotalMinutes, 1),
            ["statedTotal"] = ensemble.StatedTotal,
        };

        // header terms + clauses from the first pages
        var body = ContractBodyExtractor.Read(doc.Pages.Where(p => p.Kind.Type == DocTypes.Subcontract || p.Number <= 3));
        HeaderTable(c, "Contract 2 header (pages 1-3)", body, "contract2Header");
    }

    private sealed class ScheduleScore
    {
        public int Truth, Found, Matched, Flagged, SilentWrong, Derived, Conflicts;
        public double ItemNoRecall, Qty, Rate, Unit, Total, Desc;
        public JsonObject ToJson(string engine) => new()
        {
            ["engine"] = engine, ["truth"] = Truth, ["found"] = Found, ["itemNoRecall"] = Math.Round(ItemNoRecall, 4), ["qty"] = Math.Round(Qty, 4), ["rate"] = Math.Round(Rate, 4),
            ["unit"] = Math.Round(Unit, 4), ["total"] = Math.Round(Total, 4), ["descChars"] = Math.Round(Desc, 4), ["flagged"] = Flagged, ["silentWrong"] = SilentWrong,
            ["derived"] = Derived, ["conflicts"] = Conflicts,
        };
    }

    private static ScheduleScore Score(ContractScheduleExcel.Result truth, ScheduleRead read)
    {
        var s = new ScheduleScore { Truth = truth.Items.Count, Found = read.Items.Count };
        var byNo = read.Items.Where(i => i.ItemNo.Length > 0).GroupBy(i => i.ItemNo).ToDictionary(g => g.Key, g => g.First());
        int q = 0, r = 0, u = 0, t = 0; double d = 0;
        foreach (var ti in truth.Items)
        {
            if (!byNo.TryGetValue(ti.ItemNo, out var ri)) continue;
            s.Matched++;
            var qOk = Math.Abs(ri.QtyValue - ti.Qty) < 0.001;
            var rOk = Math.Abs(ri.RateValue - ti.Rate) < 0.001;
            if (qOk) q++;
            if (rOk) r++;
            if (DocValidators.Unit(ri.Unit.Value) == DocValidators.Unit(ti.Unit) && DocValidators.Unit(ti.Unit) != null) u++;
            if (Math.Abs(ri.TotalValue - ti.Qty * ti.Rate) < 0.01) t++;
            d += ArabicText.CharAccuracy(ti.Description, ri.Description.Value);
            if (ri.NeedsReview) s.Flagged++;
            else if (!qOk || !rOk) s.SilentWrong++;
            s.Derived += ri.Fields.Count(f => f.Status == FieldStatus.Derived);
            s.Conflicts += ri.Fields.Count(f => f.Status == FieldStatus.Conflict);
        }
        var n = Math.Max(1, s.Truth);
        s.ItemNoRecall = s.Matched / (double)n;
        s.Qty = q / (double)n; s.Rate = r / (double)n; s.Unit = u / (double)n; s.Total = t / (double)n; s.Desc = d / n;
        return s;
    }

    private static void WriteItemsCsv(string path, ContractScheduleExcel.Result truth, ScheduleRead read)
    {
        var sb = new StringBuilder("item,truth_qty,read_qty,qty_status,truth_rate,read_rate,rate_status,truth_unit,read_unit,read_total,total_status,desc_char_acc,needs_review,page,note\n");
        var byNo = read.Items.Where(i => i.ItemNo.Length > 0).GroupBy(i => i.ItemNo).ToDictionary(g => g.Key, g => g.First());
        foreach (var ti in truth.Items)
        {
            byNo.TryGetValue(ti.ItemNo, out var ri);
            sb.Append(CultureInfo.InvariantCulture, $"{ti.ItemNo},{ti.Qty},{ri?.QtyValue},{ri?.Qty.Status},{ti.Rate},{ri?.RateValue},{ri?.Rate.Status},{ti.Unit},{ri?.Unit.Value},{ri?.TotalValue},{ri?.Total.Status},");
            sb.Append(CultureInfo.InvariantCulture, $"{(ri is null ? 0 : ArabicText.CharAccuracy(ti.Description, ri.Description.Value)):0.000},{ri?.NeedsReview},{ri?.Page},\"{string.Join(" | ", ri?.Fields.Where(f => f.Note.Length > 0).Select(f => f.Name + ": " + f.Note) ?? Array.Empty<string>()).Replace("\"", "'")}\"\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static void HeaderTable(Ctx c, string title, ContractBodyRead body, string jsonKey)
    {
        var truth = c.Truth?["contractHeader"]?.AsObject();
        c.Md.AppendLine($"### {title}").AppendLine();
        c.Md.AppendLine("| Field | Read | Truth | OK |").AppendLine("|---|---|---|---|");
        int ok = 0, n = 0;
        var j = new JsonObject();
        foreach (var kv in truth ?? new JsonObject())
        {
            var expected = kv.Value?.GetValue<string>() ?? "";
            var got = body.Fields.GetValueOrDefault(kv.Key)?.Value ?? "";
            var good = FieldMatches(kv.Key, expected, got);
            n++; if (good) ok++;
            c.Md.AppendLine($"| {kv.Key} | {Esc(got)} | {Esc(expected)} | {(good ? "yes" : "NO")} |");
            j[kv.Key] = new JsonObject { ["read"] = got, ["truth"] = expected, ["ok"] = good };
        }
        c.Md.AppendLine().AppendLine($"Header fields correct: {ok}/{n}. Clauses found: {body.Clauses.Count} ({string.Join(", ", body.Clauses.Select(x => $"{x.ClauseNo} {Trim(x.Title, 24)}"))}).").AppendLine();
        j["correct"] = ok; j["total"] = n; j["clauses"] = body.Clauses.Count;
        c.Json[jsonKey] = j;
    }

    private static bool FieldMatches(string field, string expected, string got)
    {
        if (expected.Length == 0) return got.Length == 0;
        var e = ArabicText.Normalize(expected).Replace(" ", ""); var g = ArabicText.Normalize(got).Replace(" ", "");
        if (field is "Subcontractor" or "FirstParty") return g.Contains(e) || e.Contains(g) && g.Length >= 4;
        if (field.EndsWith("Date")) return DocValidators.Date(expected) == DocValidators.Date(got) && DocValidators.Date(got) != null;
        return e == g || ArabicText.ParseNumber(expected) is double x && ArabicText.ParseNumber(got) is double y && Math.Abs(x - y) < 0.001 && !expected.Contains(';');
    }

    // =================================================================== contract 1: Aconex screenshot + Arabic contract body

    private static async Task Contract1(Ctx c)
    {
        var pdf = c.Find(@"contract_1.*\.pdf$");
        if (pdf is null) { c.Md.AppendLine("## Contract 1 - skipped (contract_1*.pdf not found)").AppendLine(); return; }
        var doc = await SmartReader.ReadAsync(pdf, c.Options(_ => true)).ConfigureAwait(false);
        c.Md.AppendLine("## 2. Contract 1 (4-page scan): Aconex workflow screenshot + Arabic contract body").AppendLine();
        var types = c.Truth?["contract1"]?["pageTypes"]?.AsObject();
        ClassTable(c, doc, types?.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()) ?? new());
        foreach (var p in doc.Pages) c.Md.AppendLine($"- p{p.Number}: {p.Kind.Type} ({p.Kind.Confidence:0.00}; {p.Kind.Reason}) - {p.Source} conf {p.Confidence:0.00}{(p.Notes.Count > 0 ? "; " + string.Join("; ", p.Notes) : "")}");
        c.Md.AppendLine();
        var body = ContractBodyExtractor.Read(doc.Pages.Where(p => p.Kind.Type == DocTypes.Subcontract));
        HeaderTable(c, "Contract 1 header terms", body, "contract1Header");
        var expClauses = c.Truth?["contract1"]?["clauses"]?.GetValue<int>() ?? 0;
        c.Md.AppendLine($"Clauses: {body.Clauses.Count} found / {expClauses} in the document; payments read: {string.Join("; ", body.Payments.Select(p => $"{p.Group} {p.Stage} {p.Pct:P0}"))}.").AppendLine();
        var shot = doc.Pages.FirstOrDefault(p => p.Kind.Type == DocTypes.AconexScreenshot) ?? doc.Pages[0];
        WorkflowTable(c, "Workflow screenshot (p1, page rotated 90°)", AconexScreenshotExtractor.Read(shot), c.Truth?["contract1"]?["workflow"], "contract1Workflow");
    }

    private static void WorkflowTable(Ctx c, string title, AconexScreenshotRead read, JsonNode? truth, string key)
    {
        c.Md.AppendLine($"### {title}").AppendLine();
        var steps = read.Result?.Steps ?? new();
        var exp = truth?["steps"]?.AsArray().Select(s => s!).ToList() ?? new();
        c.Md.AppendLine($"Workflow no. read: '{read.WorkflowNo}' (truth {truth?["no"]?.GetValue<string>()}), name '{read.WorkflowName}', state {read.Result?.State}, current step '{read.Result?.CurrentStep}'. Steps read {steps.Count} / {exp.Count}.").AppendLine();
        c.Md.AppendLine("| Step (truth) | Step read | Date in | Due | Completed | Status | Outcome | Cells OK |").AppendLine("|---|---|---|---|---|---|---|---|");
        int cells = 0, ok = 0;
        foreach (var e in exp)
        {
            var name = e["step"]!.GetValue<string>();
            var s = steps.OrderByDescending(x => Sim(x.StepName, name)).FirstOrDefault(x => Sim(x.StepName, name) > 0.6);
            bool D(string k, DateTime? v) => (e[k]!.GetValue<string>() is var t && t.Length == 0 ? v is null : v?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) == t);
            var checks = new[]
            {
                s != null, D("dateIn", s?.DateIn), D("due", s?.DateDue), D("completed", s?.DateCompleted),
                s != null && s.StepStatusText.Contains(e["status"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase),
                s != null && Sim(s.StepOutcome, e["outcome"]!.GetValue<string>()) > 0.8,
            };
            cells += checks.Length; ok += checks.Count(x => x);
            c.Md.AppendLine($"| {name} | {s?.StepName} | {s?.DateIn:dd/MM/yyyy} | {s?.DateDue:dd/MM/yyyy} | {s?.DateCompleted:dd/MM/yyyy} | {s?.StepStatusText} | {s?.StepOutcome} | {checks.Count(x => x)}/{checks.Length} |");
        }
        c.Md.AppendLine().AppendLine($"Workflow cells correct: {ok}/{cells}.").AppendLine();
        c.Json[key] = new JsonObject { ["cellsOk"] = ok, ["cells"] = cells, ["steps"] = steps.Count, ["workflowNo"] = read.WorkflowNo };
    }

    private static double Sim(string a, string b)
    {
        var x = ArabicText.Normalize(a).Replace(" ", ""); var y = ArabicText.Normalize(b).Replace(" ", "");
        if (x.Length == 0 || y.Length == 0) return 0;
        return 1 - ArabicText.Levenshtein(x, y) / (double)Math.Max(x.Length, y.Length);
    }

    private static void ClassTable(Ctx c, SmartDocument doc, Dictionary<string, string> expected)
    {
        if (expected.Count == 0) return;
        int ok = 0, n = 0;
        var wrong = new List<string>();
        foreach (var (range, type) in expected)
        {
            var parts = range.Split('-');
            var a = int.Parse(parts[0], CultureInfo.InvariantCulture); var b = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : a;
            for (var p = a; p <= b; p++)
            {
                var page = doc.Pages.FirstOrDefault(x => x.Number == p);
                if (page is null) continue;
                n++;
                if (page.Kind.Type == type) ok++; else wrong.Add($"p{p} {page.Kind.Type} (expected {type})");
            }
        }
        c.Md.AppendLine($"Page classification: {ok}/{n} correct. Segments: {string.Join("; ", doc.Segments)}.{(wrong.Count > 0 ? " Wrong: " + string.Join(", ", wrong.Take(12)) : "")}").AppendLine();
    }

    // =================================================================== MIR bundle: DN photos, certificates, drum labels

    private static async Task Mir(Ctx c)
    {
        var pdf = c.Find(@"MIR.*\.pdf$");
        var dnPdf = c.Find(@"(^|-)dn\.pdf$");
        if (pdf is null) { c.Md.AppendLine("## MIR - skipped (MIR*.pdf not found)").AppendLine(); return; }
        var doc = await SmartReader.ReadAsync(pdf, c.Options(_ => true)).ConfigureAwait(false);
        c.Md.AppendLine("## 3. MIR bundle (46 pages): DN phone photos, test certificates, drum labels").AppendLine();
        var types = c.Truth?["mir"]?["pageTypes"]?.AsObject();
        ClassTable(c, doc, types?.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()) ?? new());

        // DN truth: the digital DN (81064344) + hand-transcribed DNs on p7 / p8
        var truthDns = new List<(int Page, string Dn, List<(string Item, string Batch, double Qty)> Lines)>();
        if (dnPdf != null)
        {
            var d = DnReader.Parse(PdfTextReader.Read(dnPdf));
            truthDns.Add((6, d.Value.Header.DnNo, d.Value.Lines.Select(l => (l.ItemNo, l.Batch, l.RawQty)).ToList()));
        }
        foreach (var t in c.Truth?["mir"]?["dns"]?.AsArray() ?? new JsonArray())
            truthDns.Add((t!["page"]!.GetValue<int>(), t["dn"]!.GetValue<string>(), t["lines"]!.AsArray().Select(l => (l!["item"]!.GetValue<string>(), l["batch"]!.GetValue<string>(), l["qty"]!.GetValue<double>())).ToList()));
        c.Md.AppendLine("| Page | DN truth | DN read | Lines read / truth | Items OK | Batches OK | Qty OK | Lines flagged |").AppendLine("|---|---|---|---|---|---|---|---|");
        int allLines = 0, okItem = 0, okBatch = 0, okQty = 0;
        var dnBatches = new HashSet<string>();
        var jd = new JsonArray();
        foreach (var (page, dn, lines) in truthDns)
        {
            var sp = doc.Pages.First(p => p.Number == page);
            var one = new DocText { FileName = $"p{page}" };
            one.Pages.Add(doc.ToDocText().Pages.First(p => p.Number == page));
            var r = DnReader.Parse(one);
            var got = r.Value.Lines;
            int it = 0, ba = 0, qt = 0;
            foreach (var (item, batch, qty) in lines)
            {
                var m = got.FirstOrDefault(g => g.Batch == batch) ?? got.FirstOrDefault(g => g.ItemNo == item && Math.Abs(g.RawQty - qty) < 0.0005);
                if (m is null) continue;
                if (m.ItemNo == item) it++;
                if (m.Batch == batch) ba++;
                if (Math.Abs(m.RawQty - qty) < 0.0005) qt++;
            }
            foreach (var l in lines) dnBatches.Add(l.Batch);
            allLines += lines.Count; okItem += it; okBatch += ba; okQty += qt;
            var flagged = r.Issues.Count(i => i.Line != null);
            c.Md.AppendLine($"| p{page} | {dn} | {r.Value.Header.DnNo} | {got.Count}/{lines.Count} | {it}/{lines.Count} | {ba}/{lines.Count} | {qt}/{lines.Count} | {flagged} |");
            jd.Add(new JsonObject { ["page"] = page, ["dn"] = dn, ["dnRead"] = r.Value.Header.DnNo, ["lines"] = lines.Count, ["read"] = got.Count, ["item"] = it, ["batch"] = ba, ["qty"] = qt, ["steps"] = string.Join(", ", sp.Ocr?.Steps ?? new()) });
            File.WriteAllText(Path.Combine(c.Out, $"mir_p{page}_text.txt"), sp.Text);
        }
        c.Md.AppendLine().AppendLine($"DN photo lines (offline): item {okItem}/{allLines}, batch {okBatch}/{allLines}, qty {okQty}/{allLines}.").AppendLine();

        var certs = doc.Pages.Where(p => p.Kind.Type == DocTypes.TestCert || p.Number is >= 10 and <= 25).SelectMany(EvidenceExtractor.TestCertificate).ToList();
        var labels = doc.Pages.Where(p => p.Number >= 26).SelectMany(EvidenceExtractor.DrumLabel).ToList();
        var certPages = doc.Pages.Count(p => p.Number is >= 10 and <= 25);
        var labelPages = doc.Pages.Count(p => p.Number >= 26);
        var certsWithQty = certs.Count(e => e.Qty > 0);
        var labelMatch = labels.Count(l => dnBatches.Contains(l.Batch));
        c.Md.AppendLine($"Test certificates (p10-25): {certs.Count} drum rows read on {certs.Select(x => x.Page).Distinct().Count()}/{certPages} pages, {certsWithQty} with a quantity; PO refs: {string.Join(", ", certs.Select(x => x.PoRef).Where(x => x.Length > 0).Distinct())}.");
        c.Md.AppendLine($"Drum labels (p26-46): batch read on {labels.Count}/{labelPages} photos; {labelMatch} of them match a batch on the three DNs ({string.Join(", ", labels.Select(l => l.Batch + (dnBatches.Contains(l.Batch) ? "" : "?")).Take(25))}).").AppendLine();
        c.Json["mir"] = new JsonObject
        {
            ["dns"] = jd, ["lines"] = allLines, ["itemOk"] = okItem, ["batchOk"] = okBatch, ["qtyOk"] = okQty,
            ["certRows"] = certs.Count, ["certPages"] = certPages, ["labelsRead"] = labels.Count, ["labelPages"] = labelPages, ["labelsMatchingDn"] = labelMatch,
        };
    }

    // =================================================================== PO (p1 Aconex screenshot scan, rest text)

    private static async Task Po(Ctx c)
    {
        var pdf = c.Find(@"(^|-)po\.pdf$");
        if (pdf is null) { c.Md.AppendLine("## PO - skipped").AppendLine(); return; }
        var doc = await SmartReader.ReadAsync(pdf, c.Options(_ => true)).ConfigureAwait(false);
        var r = PoReader.Parse(doc.ToDocText());
        var t = c.Truth?["po"];
        var h = r.Value.Header;
        c.Md.AppendLine("## 4. Purchase order (11 pages; p1 = scanned Aconex approval screenshot, the rest text)").AppendLine();
        c.Md.AppendLine($"Pages: {string.Join("; ", doc.Pages.Select(p => $"p{p.Number} {p.Kind.Type}/{p.Source}"))}").AppendLine();
        var checks = new List<(string F, string Got, string Exp, bool Ok)>
        {
            ("PO no", h.PoNo, t?["PoNo"]?.GetValue<string>() ?? "", h.PoNo == t?["PoNo"]?.GetValue<string>()),
            ("Supplier", h.Supplier, t?["Supplier"]?.GetValue<string>() ?? "", h.Supplier.StartsWith(t?["Supplier"]?.GetValue<string>() ?? "?", StringComparison.OrdinalIgnoreCase)),
            ("PO date", h.PoDate?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) ?? "", t?["PoDate"]?.GetValue<string>() ?? "", h.PoDate == DocValidators.Date(t?["PoDate"]?.GetValue<string>())),
            ("Stated total", h.StatedTotal.ToString("N2", CultureInfo.InvariantCulture), (t?["StatedTotal"]?.GetValue<double>() ?? 0).ToString("N2", CultureInfo.InvariantCulture), Math.Abs(h.StatedTotal - (t?["StatedTotal"]?.GetValue<double>() ?? -1)) < 0.01),
            ("VAT", h.StatedVat.ToString("N2", CultureInfo.InvariantCulture), (t?["StatedVat"]?.GetValue<double>() ?? 0).ToString("N2", CultureInfo.InvariantCulture), Math.Abs(h.StatedVat - (t?["StatedVat"]?.GetValue<double>() ?? -1)) < 0.01),
            ("Grand total", h.StatedGrandTotal.ToString("N2", CultureInfo.InvariantCulture), (t?["StatedGrandTotal"]?.GetValue<double>() ?? 0).ToString("N2", CultureInfo.InvariantCulture), Math.Abs(h.StatedGrandTotal - (t?["StatedGrandTotal"]?.GetValue<double>() ?? -1)) < 0.01),
            ("Lines sum = total", r.Value.LinesTotal.ToString("N2", CultureInfo.InvariantCulture), "", Math.Abs(r.Value.LinesTotal - h.StatedTotal) < 0.05),
        };
        c.Md.AppendLine("| Field | Read | Truth | OK |").AppendLine("|---|---|---|---|");
        foreach (var x in checks) c.Md.AppendLine($"| {x.F} | {x.Got} | {x.Exp} | {(x.Ok ? "yes" : "NO")} |");
        c.Md.AppendLine().AppendLine($"{r.Value.Lines.Count} PO lines; VAT check {(DocValidators.VatOk(h.StatedTotal, h.StatedVat) ? "15 % OK" : "FAILED")}.").AppendLine();
        var shot = doc.Pages.FirstOrDefault(p => p.Kind.Type == DocTypes.AconexScreenshot);
        if (shot != null) WorkflowTable(c, "PO approval workflow screenshot (p1)", AconexScreenshotExtractor.Read(shot), t?["workflow"], "poWorkflow");
        c.Json["po"] = new JsonObject { ["fieldsOk"] = checks.Count(x => x.Ok), ["fields"] = checks.Count, ["lines"] = r.Value.Lines.Count, ["page1"] = doc.Pages[0].Kind.Type };
    }

    // =================================================================== site statement (handwriting)

    private static async Task Statement(Ctx c)
    {
        var pdf = c.Find(@"site_stat.*\.pdf$");
        if (pdf is null) { c.Md.AppendLine("## Site statement - skipped").AppendLine(); return; }
        var t = c.Truth?["statement"];
        var drawingPage = t?["drawingPage"]?.GetValue<int>() ?? 5;
        var doc = await SmartReader.ReadAsync(pdf, c.Options(p => p == 1 || p == drawingPage)).ConfigureAwait(false);
        var draft = SiteStatementExtractor.Read(doc.Pages.Where(p => p.Number == 1), doc.Pages.Where(p => p.Number == drawingPage));
        c.Md.AppendLine("## 5. Site statement (handwritten summary p1 + marked drawing p5)").AppendLine();
        c.Md.AppendLine($"p1 type {doc.Pages[0].Kind.Type}, OCR conf {doc.Pages[0].Confidence:0.00}; p{drawingPage} type {doc.Pages.Last().Kind.Type}, conf {doc.Pages.Last().Confidence:0.00}.").AppendLine();
        var expRows = t?["rows"]?.GetValue<int>() ?? 0;
        var stages = t?["stages"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? new();
        var pcts = t?["pcts"]?.AsArray().Select(x => x!.GetValue<double>()).ToList() ?? new();
        var rooms = t?["rooms"]?.AsArray().Select(x => x!.GetValue<string>()).ToHashSet() ?? new();
        var readRooms = SiteStatementExtractor.Rooms(doc.Pages[0].ReadingText + "\n" + doc.Pages[0].Text).ToHashSet();
        var roomHit = rooms.Count(readRooms.Contains);
        int stageOk = 0, pctOk = 0;
        for (var i = 0; i < Math.Min(draft.Rows.Count, stages.Count); i++)
        {
            if (draft.Rows[i].Stage == stages[i]) stageOk++;
            if (draft.Rows[i].Pct is double p && i < pcts.Count && Math.Abs(p - pcts[i]) < 0.001) pctOk++;
        }
        c.Md.AppendLine($"Summary rows recognised {draft.Rows.Count}/{expRows}; stage correct {stageOk}/{stages.Count} (by position); % correct {pctOk}/{pcts.Count}; " +
                        $"room IDs found {roomHit}/{rooms.Count} ({string.Join(", ", readRooms.Take(30))}{(readRooms.Except(rooms).Any() ? "; not in truth: " + string.Join(", ", readRooms.Except(rooms)) : "")}).");
        var mc = draft.Drawings.FirstOrDefault() ?? new MarkedCounts();
        var counts = t?["counts"]?.AsObject();
        var countOk = counts?.Count(kv => mc.PerUnit.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value!.GetValue<double>()) < 0.01) ?? 0;
        var products = t?["products"]?.AsArray().Select(a => a!.AsArray().Select(x => x!.GetValue<double>()).ToArray()).ToList() ?? new();
        var prodOk = products.Count(p => mc.Products.Any(q => q.A == p[0] && q.B == p[1] && q.Result == p[2]));
        c.Md.AppendLine($"Marked drawing p{drawingPage}: counts {countOk}/{counts?.Count ?? 0} ({string.Join(", ", mc.PerUnit.Select(kv => $"{kv.Key} {kv.Value}"))}), products {prodOk}/{products.Count} ({string.Join(", ", mc.Products.Select(p => $"{p.A}x{p.B}={p.Result}"))}), rooms {string.Join(", ", mc.Rooms)}.").AppendLine();
        File.WriteAllText(Path.Combine(c.Out, "statement_p1_text.txt"), doc.Pages[0].ReadingText);
        File.WriteAllText(Path.Combine(c.Out, $"statement_p{drawingPage}_text.txt"), doc.Pages.Last().ReadingText);
        c.Json["statement"] = new JsonObject { ["rows"] = draft.Rows.Count, ["rowsTruth"] = expRows, ["stageOk"] = stageOk, ["pctOk"] = pctOk, ["roomsFound"] = roomHit, ["roomsTruth"] = rooms.Count, ["countsOk"] = countOk, ["productsOk"] = prodOk };
    }

    // =================================================================== read-doc

    private static int ReadDoc(string path, Dictionary<string, string> opts)
    {
        using var paddle = new PaddleOcrEngine();
        var o = new SmartReaderOptions
        {
            Rasterizer = new PdfiumRasterizer(), Engines = new ILayoutOcrEngine[] { paddle },
            Pages = opts.TryGetValue("pages", out var pg) ? PageFilter(pg) : null, Progress = new Progress<string>(s => Console.Error.WriteLine(s)),
        };
        var doc = SmartReader.ReadAsync(path, o).GetAwaiter().GetResult();
        Console.WriteLine(doc.Summary);
        foreach (var p in doc.Pages)
        {
            Console.WriteLine($"===== PAGE {p.Number}: {p.Kind.Type} ({p.Kind.Confidence:0.00}: {p.Kind.Reason}) source {p.Source} {p.Engine} conf {p.Confidence:0.00} quality {p.Quality}");
            foreach (var n in p.Notes) Console.WriteLine("  note: " + n);
            Console.WriteLine(opts.ContainsKey("reading") ? p.ReadingText : p.Text);
        }
        return 0;
    }

    // =================================================================== helpers

    private static Func<int, bool> PageFilter(string spec)
    {
        var set = new HashSet<int>();
        foreach (var part in spec.Split(','))
        {
            var ab = part.Split('-');
            var a = int.Parse(ab[0], CultureInfo.InvariantCulture);
            var b = ab.Length > 1 ? int.Parse(ab[1], CultureInfo.InvariantCulture) : a;
            for (var i = a; i <= b; i++) set.Add(i);
        }
        return set.Contains;
    }

    private static Dictionary<string, string> ParseOptions(string[] a, out List<string> positional)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        positional = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--"))
            {
                var k = a[i][2..];
                if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) { d[k] = a[i + 1]; i++; }
                else d[k] = "true";
            }
            else positional.Add(a[i]);
        }
        return d;
    }

    private static string Esc(string s) => s.Replace("|", "/").Replace("\n", " ");
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
