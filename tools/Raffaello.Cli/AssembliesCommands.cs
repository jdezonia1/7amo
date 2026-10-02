using System.Globalization;
using System.Text;
using Raffaello.Core.Assemblies;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;

namespace Raffaello.Cli;

/// <summary>
/// [assemblies] Verification of the BOQ item breakdown module against real files (outputs contain project data - keep --out outside the repo):
///   raffaello-cli asm-coverage --hotel FILE --residence FILE [--epromise FILE] [--sample 400] [--out DIR]
///   raffaello-cli asm-examples --hotel FILE --epromise FILE --po PDF --out DIR
///   raffaello-cli asm-bulk     --hotel FILE --po PDF --out DIR
/// </summary>
public static class AssembliesCommands
{
    private static readonly HashSet<string> Names = new() { "asm-coverage", "asm-examples", "asm-bulk" };

    public static bool Handles(string cmd) => Names.Contains(cmd);

    public static int Run(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
            if (args[i].StartsWith("--", StringComparison.Ordinal)) opts[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        try
        {
            switch (args[0])
            {
                case "asm-coverage": Coverage(opts); break;
                case "asm-examples": Examples(opts); break;
                case "asm-bulk": Bulk(opts); break;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex);
            return 1;
        }
    }

    private static List<ContractItem> Contract(string path, string no, string sub) => ContractLinkImporter.Read(path, no, sub).Items;

    /// <summary>E-Promise rows of the electrical trades (BOQ item part 26 / 27 / 28).</summary>
    private static List<BoqItem> Electrical(string path) =>
        EPromiseImporter.Read(path).Items.Where(b => System.Text.RegularExpressions.Regex.IsMatch(b.ItemCode, @"-2[678]-") && b.Description.Trim().Length > 2).ToList();

    private static void Coverage(Dictionary<string, string> o)
    {
        var outDir = o.GetValueOrDefault("out");
        var report = new StringBuilder();
        void Line(string s) { Console.WriteLine(s); report.AppendLine(s); }
        void Report(string title, List<(string Code, string Desc, string Unit, ItemSpec Spec)> rows)
        {
            var (total, ok, byType) = ItemParser.Coverage(rows.Select(r => r.Spec));
            Line($"== {title}: {ok}/{total} recognised = {(total == 0 ? 0 : ok / (double)total):P1}");
            foreach (var (t, n) in byType) Line($"   {t,-22} {n,5}");
            foreach (var r in rows.Where(r => !r.Spec.Recognised).Take(25)) Line($"   UNKNOWN {r.Code}: {Short(r.Desc, 110)}");
            var low = rows.Where(r => r.Spec.Recognised && r.Spec.Confidence < 0.6).ToList();
            if (low.Count > 0) Line($"   low confidence: {low.Count} (e.g. {string.Join(" | ", low.Take(3).Select(r => r.Code + " " + Short(r.Desc, 50)))})");
            if (outDir != null)
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllLines(Path.Combine(outDir, $"parse_{title.Replace(' ', '_')}.tsv"),
                    rows.Select(r => string.Join('\t', r.Code, r.Unit, r.Spec.ItemType, r.Spec.System, r.Spec.Conduit, r.Spec.ConduitSizeMm, r.Spec.Mount, r.Spec.Height, r.Spec.StagesText, r.Spec.Supply,
                        r.Spec.CableCores, r.Spec.CableSizeMm2, r.Spec.Confidence.ToString("0.00", CultureInfo.InvariantCulture), string.Join("; ", r.Spec.AlsoCovers), string.Join("; ", r.Spec.Notes), Short(r.Desc, 200))));
            }
        }
        if (o.TryGetValue("hotel", out var hotel))
            Report("HOTEL contract", Contract(hotel, "HOTEL", "SUB").Select(i => (i.ItemNo, i.Description, i.Unit, ItemParser.Parse(i.Description, i.Unit, ItemSourceKind.Contract))).ToList());
        if (o.TryGetValue("residence", out var res))
            Report("RESIDENCE contract", Contract(res, "RESIDENCE", "SUB").Select(i => (i.ItemNo, i.Description, i.Unit, ItemParser.Parse(i.Description, i.Unit, ItemSourceKind.Contract))).ToList());
        if (o.TryGetValue("arabic", out var ar))
        {
            var rows = Raffaello.Core.Import.TableReader.Read(ar).Rows.Select(r => (Code: r.Get("رقم"), Desc: r.Get("التوصيف"), Unit: r.Get("الوحدة")))
                .Where(r => r.Code.Length > 0 && r.Desc.Length > 10).Select(r => (r.Code, r.Desc, r.Unit, ItemParser.Parse(r.Desc, r.Unit, ItemSourceKind.Contract))).ToList();
            Report("ARABIC contract", rows);
        }
        if (o.TryGetValue("epromise", out var ep))
        {
            var all = Electrical(ep);
            var n = int.Parse(o.GetValueOrDefault("sample", "400"), CultureInfo.InvariantCulture);
            var rnd = new Random(7);
            var sample = all.Count <= n ? all : all.OrderBy(_ => rnd.Next()).Take(n).ToList();
            Line($"E-Promise electrical rows (26/27/28): {all.Count}, sample {sample.Count}");
            Report("E-PROMISE electrical sample", sample.Select(b => (b.ItemCode, b.Description, b.Unit, ItemParser.Parse(b.Description, b.Unit, ItemSourceKind.Boq))).ToList());
            Report("E-PROMISE electrical all", all.Select(b => (b.ItemCode, b.Description, b.Unit, ItemParser.Parse(b.Description, b.Unit, ItemSourceKind.Boq))).ToList());
        }
        if (outDir != null) File.WriteAllText(Path.Combine(outDir, "coverage.txt"), report.ToString());
    }

    private static AssemblyService Service(Dictionary<string, string> o, string dbPath)
    {
        var items = o.TryGetValue("hotel", out var hotel) ? Contract(hotel, "SUB-ELE-028-2026", "ROOTS") : new List<ContractItem>();
        var boq = o.TryGetValue("epromise", out var ep) ? Electrical(ep) : new List<BoqItem>();
        var mats = new MaterialsSnapshot();
        if (o.TryGetValue("po", out var po))
        {
            var r = PoReader.ReadAsync(po).GetAwaiter().GetResult();
            r.Value.Header.Id = 1;
            foreach (var l in r.Value.Lines) l.PoId = 1;
            mats = new MaterialsSnapshot { Pos = { r.Value.Header }, PoLines = r.Value.Lines.ToList() };
            Console.WriteLine($"PO {r.Value.Header.PoNo}: {r.Value.Lines.Count} priced lines");
        }
        var id = 0L;
        foreach (var i in items) i.Id = ++id;
        foreach (var b in boq) b.Id = ++id;
        var snap = new ProjectSnapshot { ContractItems = items, BoqItems = boq, Contracts = { new Contract { ContractNo = "SUB-ELE-028-2026", Subcontractor = "ROOTS", Building = Buildings.Hotel } } };
        if (File.Exists(dbPath)) File.Delete(dbPath);
        var svc = new AssemblyService(new SqliteAssemblyStore(dbPath, "cli"), () => snap, () => mats);
        svc.Reload();
        Console.WriteLine($"library: {svc.Library.Templates.Count} templates, {svc.Library.Globals.Count} global parameters; contract labour items {svc.Labour.Entries.Count}; PO prices {svc.Prices.PoLines}");
        return svc;
    }

    private static void Examples(Dictionary<string, string> o)
    {
        var outDir = o["out"];
        Directory.CreateDirectory(outDir);
        var svc = Service(o, Path.Combine(outDir, "assemblies-cli.db"));
        var boq = svc.Sources(SourceKinds.Boq);
        AssemblySource Pick(string contains, string fallback) =>
            boq.FirstOrDefault(s => s.Description.Contains(contains, StringComparison.OrdinalIgnoreCase)) ?? AssemblySource.FromText(fallback);
        var examples = new List<AssemblySource>
        {
            Pick("lighting points, to apartment - number", "lighting points, to apartment - number"),
            Pick("small power points", "small power points"),
            boq.FirstOrDefault(s => s.Description.StartsWith("4C 16mm2", StringComparison.OrdinalIgnoreCase)) ?? AssemblySource.FromText("4C 16mm2 Cu/XLPE/SWA/LSZH + 1C 16mm2 Cu/LSF G/Y", "M"),
        };
        // the matching subcontract items for comparison (labour-only, hours basis)
        var contract = svc.Sources(SourceKinds.Contract);
        examples.AddRange(contract.Where(c => c.Code is "3" or "11" or "17" or "85" or "55"));

        var results = new List<(AssemblySource, Breakdown)>();
        var sheets = new List<Raffaello.Core.Export.ExportSheet>();
        var txt = new StringBuilder();
        var n = 0;
        foreach (var src in examples)
        {
            var b = svc.Run(src);
            results.Add((src, b));
            sheets.Add(AssemblyExporter.BreakdownSheet(src, b, $"EX{++n}"));
            txt.AppendLine(Print(src, b));
        }
        Console.WriteLine(txt);
        File.WriteAllText(Path.Combine(outDir, "examples.txt"), txt.ToString());
        Raffaello.Core.Export.ExcelExporter.Export(Path.Combine(outDir, "rate_analysis_examples.xlsx"), sheets);
        AssemblyExporter.RateAnalysisPdf(Path.Combine(outDir, "rate_analysis_examples.pdf"), results, new RateSheetInfo("RAFFLES - HOTEL / BRANDED", "raffaello-cli", DateTime.Today));
        Raffaello.Core.Export.ExcelExporter.Export(Path.Combine(outDir, "assembly_library.xlsx"), AssemblyExporter.TemplateSheets(svc.Library));
        Console.WriteLine($"written: {outDir}");
    }

    private static void Bulk(Dictionary<string, string> o)
    {
        var outDir = o["out"];
        Directory.CreateDirectory(outDir);
        var svc = Service(o, Path.Combine(outDir, "assemblies-bulk.db"));
        var items = svc.Sources(SourceKinds.Contract).Where(s => s.Qty > 0).ToList();
        var r = svc.Bulk(items);
        Console.WriteLine(r.Summary);
        foreach (var m in r.Materials.OrderByDescending(m => m.Cost).Take(25))
            Console.WriteLine($"  {Short(m.Spec, 55),-55} {m.RequiredQty,14:N1} {m.Unit,-4} ordered {m.OrderedQty,12:N1}  {m.Status}");
        AssemblyExporter.BulkExcel(Path.Combine(outDir, "bulk_requirements.xlsx"), r);
    }

    public static string Print(AssemblySource src, Breakdown b)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#### {src.KindLabel} {src.Code}: {Short(src.Description, 120)}");
        sb.AppendLine($"     spec: {b.Spec.Summary}   template {b.TemplateCode}   labour {b.LabourMode}");
        foreach (var n in b.Spec.Notes) sb.AppendLine($"     parse: {n}");
        sb.AppendLine($"     {"STAGE",-8} {"COMPONENT",-34} {"SPEC",-46} {"UNIT",-4} {"QTY/U",8} {"WASTE",5} {"TOTAL",8} {"PRICE",9} {"AMOUNT",9}  SOURCE");
        foreach (var l in b.Lines)
            sb.AppendLine($"     {l.Stage.Replace(" FIX", ""),-8} {Short(l.Component, 34),-34} {Short(l.Spec, 46),-46} {l.Unit,-4} {l.QtyPerUnit,8:N3} {l.WastePct,5:P0} {l.TotalQty,8:N3} {l.UnitPrice,9:N2} {l.Amount,9:N2}  {l.PriceSource}{(l.Flag.Length > 0 ? " [" + l.Flag + "]" : "")} {Short(l.PriceRef, 50)}");
        sb.AppendLine($"     MATERIAL {b.Material:N2} | LABOUR {b.Labour:N2} | DIRECT {b.Direct:N2} | OH {b.OverheadPct:P0} {b.Overhead:N2} | PROFIT {b.ProfitPct:P0} {b.Profit:N2} | RATE {b.Rate:N2} / {b.Unit}"
                      + (b.ReferenceRate is { } r ? $" | {b.ReferenceLabel} {r:N2} margin {b.Margin:N2} ({b.Verdict})" : " | no reference rate")
                      + (b.FreeIssueMaterial > 0 ? $" | free-issue material {b.FreeIssueMaterial:N2}" : ""));
        foreach (var f in b.Flags) sb.AppendLine($"     FLAG: {f}");
        return sb.ToString();
    }

    private static string Short(string s, int n) => s.Length > n ? s[..(n - 1)] + "~" : s;
}
