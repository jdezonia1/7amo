using System.Globalization;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using Raffaello.Core.Documents.Smart;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Contracts;

/// <summary>One difference between the signed PDF and the Excel / current contract items, with the user's choice.</summary>
public sealed class ContractItemDiff
{
    public string ItemNo { get; init; } = "";
    /// <summary>QTY / RATE / UNIT / DESCRIPTION / ONLY IN PDF / ONLY IN EXCEL.</summary>
    public string Field { get; init; } = "";
    public string PdfValue { get; init; } = "";
    public string ExcelValue { get; init; } = "";
    /// <summary>PDF reading confidence / status of that field.</summary>
    public double PdfConfidence { get; init; }
    public string PdfStatus { get; init; } = "";
    public int Page { get; init; }
    /// <summary>PDF / EXCEL / EDIT ("" = not decided).</summary>
    public string Choice { get; set; } = "";
    public string EditedValue { get; set; } = "";
    /// <summary>The value the contract record gets.</summary>
    public string Final => Choice switch { "PDF" => PdfValue, "EXCEL" => ExcelValue, "EDIT" => EditedValue, _ => "" };
    /// <summary>A suggested default: the signed PDF wins when it was read confidently, else the Excel.</summary>
    public string Suggested => Field is "ONLY IN PDF" ? "PDF" : Field is "ONLY IN EXCEL" ? "EXCEL" : PdfStatus is FieldStatus.Agreed or FieldStatus.Validated or FieldStatus.Single && PdfConfidence >= 0.9 ? "PDF" : "EXCEL";
}

public sealed class ContractPdfReadResult
{
    public SmartDocument Document { get; init; } = new();
    public ContractBodyRead Body { get; init; } = new();
    public ScheduleRead Schedule { get; init; } = new();
    public List<ContractItem> PdfItems { get; } = new();
    public List<ContractItem> ExcelItems { get; } = new();
    public string ExcelSource { get; set; } = "";
    public List<ContractItemDiff> Diffs { get; } = new();
    public List<ContractRule> Rules { get; } = new();
    public string ContractNo { get; set; } = "";
    public double PdfTotal => PdfItems.Sum(i => i.Qty * i.Rate);
    public double ExcelTotal => ExcelItems.Sum(i => i.Qty * i.Rate);
    public int Undecided => Diffs.Count(d => d.Choice.Length == 0);

    public string Summary =>
        $"{ContractNo}: {PdfItems.Count} items read from the signed PDF ({Schedule.Items.Count(i => i.NeedsReview)} to review), SAR {PdfTotal:N2}" +
        (ExcelItems.Count > 0 ? $" | Excel {ExcelItems.Count} items, SAR {ExcelTotal:N2} | {Diffs.Count} difference(s)" : "") +
        $" | {Body.Clauses.Count} clauses, {Rules.Count} rules";
}

/// <summary>
/// CONTRACT PDF IMPORT: a signed contract PDF (usually a scan) becomes the contract record - header terms, clauses, the rate schedule
/// as contract items and the rules - cross-checked item by item against the Excel schedule (the link workbook already imported, or a
/// contract Excel). The user resolves each difference (PDF / Excel / edit); the PDF is kept as evidence (SHA-256) linked to the contract.
/// </summary>
public static class ContractPdfImport
{
    public static async Task<ContractPdfReadResult> ReadAsync(string pdfPath, SmartReaderOptions options, IEnumerable<ContractItem>? excelItems = null,
        string? contractNo = null, IDocumentStore? store = null, CancellationToken ct = default)
    {
        var doc = await SmartReader.ReadAsync(pdfPath, options, ct).ConfigureAwait(false);
        var bodyPages = doc.Pages.Where(p => p.Kind.Type is DocTypes.Subcontract || p.Kind.Type == DocTypes.Other && p.Number <= 4).ToList();
        var body = ContractBodyExtractor.Read(bodyPages);
        var no = contractNo ?? (body.Terms.ContractNo.Length > 0 ? body.Terms.ContractNo : "");
        body.Terms.ContractNo = no;
        var schedulePages = doc.Pages.Where(p => p.Kind.Type == DocTypes.RateSchedule).ToList();
        var template = store?.FindTemplate(DocTypes.RateSchedule, Issuer(doc), string.Join("\n", schedulePages.Take(1).Select(p => p.ReadingText)));
        var rereader = new EngineRereader(options.Engines);
        var schedule = await ScheduleExtractor.ReadAsync(schedulePages, rereader, template, ct).ConfigureAwait(false);
        if (options.CanUseVision) await VisionEscalation.ScheduleAsync(pdfPath, doc, schedule, options.Vision!, ct).ConfigureAwait(false);
        var res = new ContractPdfReadResult { Document = doc, Body = body, Schedule = schedule, ContractNo = no };
        res.PdfItems.AddRange(schedule.ToContractItems(no));
        body.Terms.SourceFile = doc.FileName;
        if (excelItems != null) res.ExcelItems.AddRange(excelItems);
        Compare(res);
        res.Rules.AddRange(ContractRuleBuilder.Build(body.Terms, body.Clauses, res.PdfItems.Count > 0 ? res.PdfItems : res.ExcelItems, body.Payments));
        foreach (var c in body.Clauses)
            c.RulesFound = string.Join(", ", res.Rules.Where(r => r.ClauseNo == c.ClauseNo).Select(r => r.RuleType).Distinct());
        return res;
    }

    public static string Issuer(SmartDocument doc)
    {
        var t = ArabicText.Normalize(string.Join("\n", doc.Pages.Take(3).Select(p => p.ReadingText + " " + p.Text)));
        if (t.Contains("RIYADH-CABLES") || t.Contains("RIYADH CABLES") || t.Contains("SAUDI MODERN COMPANY")) return "RIYADH CABLES";
        if (t.Contains("MOBCO") || t.Contains("موبكو")) return "MOBCO";
        return "";
    }

    /// <summary>Item-by-item comparison: qty, rate, unit, description (character similarity below 80 %), items on one side only.</summary>
    public static void Compare(ContractPdfReadResult res)
    {
        res.Diffs.Clear();
        if (res.ExcelItems.Count == 0) return;
        var pdf = res.Schedule.Items.Where(i => i.ItemNo.Length > 0).GroupBy(i => i.ItemNo).ToDictionary(g => g.Key, g => g.First());
        var xl = res.ExcelItems.GroupBy(i => i.ItemNo).ToDictionary(g => g.Key, g => g.First());
        foreach (var no in xl.Keys.Union(pdf.Keys).OrderBy(k => int.TryParse(k, out var v) ? v : int.MaxValue).ThenBy(k => k))
        {
            pdf.TryGetValue(no, out var p);
            xl.TryGetValue(no, out var x);
            if (p is null) { res.Diffs.Add(new ContractItemDiff { ItemNo = no, Field = "ONLY IN EXCEL", ExcelValue = $"{x!.Qty:0.###} {x.Unit} x {x.Rate:0.###}" }); continue; }
            if (x is null) { res.Diffs.Add(new ContractItemDiff { ItemNo = no, Field = "ONLY IN PDF", PdfValue = $"{p.QtyValue:0.###} {p.Unit.Value} x {p.RateValue:0.###}", Page = p.Page, PdfConfidence = p.Confidence }); continue; }
            void Num(string field, FieldResult f, double excel)
            {
                if (f.Number is double v && Math.Abs(v - excel) < 0.0005) return;
                res.Diffs.Add(new ContractItemDiff
                {
                    ItemNo = no, Field = field, PdfValue = f.Value, ExcelValue = excel.ToString("0.###", CultureInfo.InvariantCulture), PdfConfidence = f.Confidence, PdfStatus = f.Status, Page = p.Page,
                });
            }
            Num("QTY", p.Qty, x.Qty);
            Num("RATE", p.Rate, x.Rate);
            var pu = DocValidators.Unit(p.Unit.Value); var xu = DocValidators.Unit(x.Unit);
            if (pu != xu)
                res.Diffs.Add(new ContractItemDiff { ItemNo = no, Field = "UNIT", PdfValue = p.Unit.Value, ExcelValue = x.Unit, PdfConfidence = p.Unit.Confidence, PdfStatus = p.Unit.Status, Page = p.Page });
            if (x.Description.Length > 0 && ArabicText.CharAccuracy(x.Description, p.Description.Value) < 0.8)
                res.Diffs.Add(new ContractItemDiff { ItemNo = no, Field = "DESCRIPTION", PdfValue = p.Description.Value, ExcelValue = x.Description, PdfConfidence = p.Description.Confidence, PdfStatus = p.Description.Status, Page = p.Page });
        }
        foreach (var d in res.Diffs) d.Choice = "";
    }

    /// <summary>Applies the suggested choice to every undecided difference (the user can still change each one).</summary>
    public static void AcceptSuggestions(ContractPdfReadResult res)
    {
        foreach (var d in res.Diffs.Where(d => d.Choice.Length == 0)) d.Choice = d.Suggested;
    }

    /// <summary>The contract items after the user's choices (PDF items, corrected by the Excel / edits per difference).</summary>
    public static List<ContractItem> Resolve(ContractPdfReadResult res)
    {
        if (res.Undecided > 0) throw new InvalidOperationException($"{res.Undecided} difference(s) still need a choice (PDF / Excel / edit).");
        var baseItems = res.PdfItems.Count > 0 ? res.PdfItems : res.ExcelItems;
        var items = baseItems.Select(Clone).ToDictionary(i => i.ItemNo);
        var xl = res.ExcelItems.GroupBy(i => i.ItemNo).ToDictionary(g => g.Key, g => g.First());
        foreach (var d in res.Diffs)
        {
            switch (d.Field)
            {
                case "ONLY IN EXCEL" when d.Choice == "EXCEL": items[d.ItemNo] = Clone(xl[d.ItemNo]); break;
                case "ONLY IN PDF" when d.Choice == "EXCEL": items.Remove(d.ItemNo); break;
                case "QTY" when items.TryGetValue(d.ItemNo, out var i1): i1.Qty = ArabicText.ParseNumber(d.Final) ?? i1.Qty; break;
                case "RATE" when items.TryGetValue(d.ItemNo, out var i2): i2.Rate = ArabicText.ParseNumber(d.Final) ?? i2.Rate; break;
                case "UNIT" when items.TryGetValue(d.ItemNo, out var i3): i3.Unit = d.Final; break;
                case "DESCRIPTION" when items.TryGetValue(d.ItemNo, out var i4): i4.Description = d.Final; break;
            }
        }
        var list = items.Values.OrderBy(i => int.TryParse(i.ItemNo, out var v) ? v : int.MaxValue).ToList();
        for (var k = 0; k < list.Count; k++)
        {
            list[k].Order = k + 1;
            list[k].ContractNo = res.ContractNo;
            ContractAttributeParser.Apply(list[k], ContractAttributeParser.Parse(list[k].Description, list[k].Unit));
            list[k].StagePct = ContractAttributeParser.DefaultStagePct(list[k]);
        }
        ContractAttributeParser.InferHeightPairs(list);
        return list;
    }

    private static ContractItem Clone(ContractItem i) => new()
    {
        ContractNo = i.ContractNo, ItemNo = i.ItemNo, Order = i.Order, Section = i.Section, Description = i.Description, Unit = i.Unit, Qty = i.Qty, Rate = i.Rate,
        FixStage = i.FixStage, ConduitType = i.ConduitType, Mount = i.Mount, HeightBand = i.HeightBand, Is2ndFixPulling = i.Is2ndFixPulling, IsHomerun = i.IsHomerun,
        Systems = i.Systems, Category = i.Category, SizeKey = i.SizeKey, StagePct = i.StagePct, ParseNotes = i.ParseNotes,
    };

    /// <summary>
    /// Writes the contract record: contract header (value, signed date, retention), contract items (existing items keep their id, BOQ links
    /// and confirmed attributes), terms, clauses and rules, and the PDF as evidence (document record + page text for search + fields).
    /// </summary>
    public static DocRecord Commit(ContractPdfReadResult res, IProjectStore project, IDocumentStore docs, string subcontractor, string? building, string evidencePath, string user)
    {
        var no = res.ContractNo;
        if (no.Length == 0) throw new InvalidOperationException("Contract number missing - enter it before saving.");
        var items = Resolve(res);
        var oldItems = project.All<ContractItem>().Where(i => i.ContractNo == no).ToDictionary(i => i.ItemNo);
        var contract = project.All<Contract>().FirstOrDefault(c => c.ContractNo == no);
        var t = res.Body.Terms;
        project.Batch(w =>
        {
            if (contract is null)
            {
                contract = new Contract { ContractNo = no, Subcontractor = subcontractor.Trim().ToUpperInvariant(), Building = building ?? Buildings.Branded, Status = "ACTIVE", SourceFile = res.Document.FileName };
                contract.Value = Math.Round(items.Sum(i => i.Qty * i.Rate), 2);
                if ((t.SignedDate ?? t.ContractDate) is DateTime d) contract.SignedAt = d;
                contract.RetentionPct = t.RetentionPct ?? 0;
                contract.Scope = t.Scope.Length > 0 ? t.Scope : contract.Scope;
                w.Insert(contract);
            }
            else
            {
                contract.Value = Math.Round(items.Sum(i => i.Qty * i.Rate), 2);
                if ((t.SignedDate ?? t.ContractDate) is DateTime d) contract.SignedAt = d;
                if (t.RetentionPct is double rp) contract.RetentionPct = rp;
                if (t.Scope.Length > 0) contract.Scope = t.Scope;
                w.Update(contract);
            }
            foreach (var i in items)
            {
                if (oldItems.Remove(i.ItemNo, out var old))
                {
                    i.Id = old.Id; i.RowVersion = old.RowVersion;
                    if (old.AttributesConfirmed)
                    {
                        (i.FixStage, i.ConduitType, i.Mount, i.HeightBand, i.Systems, i.Category, i.SizeKey, i.StagePct) = (old.FixStage, old.ConduitType, old.Mount, old.HeightBand, old.Systems, old.Category, old.SizeKey, old.StagePct);
                        i.AttributesConfirmed = true;
                    }
                    if (i.Section.Length == 0) i.Section = old.Section;
                    w.Update(i);
                }
                else w.Insert(i);
            }
            // items that are no longer in the signed schedule are kept (they may carry BOQ links / claims) but flagged
            foreach (var gone in oldItems.Values)
            {
                if (!gone.ParseNotes.Contains("not in the signed PDF")) { gone.ParseNotes = (gone.ParseNotes + " | not in the signed PDF").Trim(' ', '|'); w.Update(gone); }
            }
        }, $"Contract {no} imported from the signed PDF {res.Document.FileName}: {items.Count} items, SAR {items.Sum(i => i.Qty * i.Rate):N2}");

        var rec = Archive(res.Document, DocTypes.Subcontract, nameof(Contract), no, evidencePath, user);
        var pages = PageTexts(res.Document);
        var fields = Fields(res);
        rec = docs.SaveRead(rec, pages, fields);
        t.SourceDocId = rec.Id;
        t.Subcontractor = t.Subcontractor.Length > 0 ? t.Subcontractor : subcontractor;
        t.Status = "CONFIRMED";
        foreach (var r in res.Rules) { r.SourceDocId = rec.Id; r.ContractNo = no; r.Status = "CONFIRMED"; }
        docs.SaveContractIntelligence(t, res.Body.Clauses, res.Rules);
        return rec;
    }

    /// <summary>Confirm &amp; learn: stores / refines the schedule layout template for this issuer so the next contract of the same form reads deterministically.</summary>
    public static ReadTemplate LearnTemplate(ContractPdfReadResult res, IDocumentStore docs)
    {
        var issuer = Issuer(res.Document);
        var pages = res.Document.Pages.Where(p => p.Kind.Type == DocTypes.RateSchedule).ToList();
        var existing = docs.All<ReadTemplate>().FirstOrDefault(t => t.DocType == DocTypes.RateSchedule && string.Equals(t.Issuer, issuer, StringComparison.OrdinalIgnoreCase));
        var t = TemplateLearner.LearnSchedule(existing, issuer, res.Schedule, pages);
        return t.Id == 0 ? docs.Insert(t, $"Template learned: {t.DocType} / {t.Issuer}") : docs.Update(t, $"Template refined: {t.DocType} / {t.Issuer} ({t.Confirmations} confirmations)");
    }

    /// <summary>Document record for a read file (evidence path = where the original is kept).</summary>
    public static DocRecord Archive(SmartDocument doc, string type, string linkedTable, string linkedKey, string evidencePath, string user) => new()
    {
        FileName = doc.FileName, Sha256 = doc.Sha256, Bytes = doc.Bytes, Pages = doc.Pages.Count, DocType = type, Segments = string.Join("; ", doc.Segments),
        Issuer = Issuer(doc), StoredPath = evidencePath, LinkedTable = linkedTable, LinkedKey = linkedKey,
        Engines = string.Join(", ", doc.Pages.Select(p => p.Engine).Where(e => e.Length > 0).Distinct()), Status = "CONFIRMED",
        Confidence = doc.Pages.Count == 0 ? 0 : Math.Round(doc.Pages.Average(p => p.Confidence), 3), ReadAt = DateTime.Now, Notes = user.Length > 0 ? "read by " + user : "",
    };

    public static List<DocPageText> PageTexts(SmartDocument doc) => doc.Pages.Select(p => new DocPageText
    {
        Page = p.Number, Kind = p.Kind.Type, Source = p.Source, Engine = p.Engine, Quality = p.Quality?.Score ?? 0, Confidence = Math.Round(p.Confidence, 3),
        Text = p.ReadingText.Length > 0 ? p.ReadingText : p.Text, NormText = SearchText(p.ReadingText + "\n" + p.Text),
    }).ToList();

    /// <summary>Search form of a page: codes with letter O for zero repaired ("SUB-ELE-O28" -> "SUB-ELE-028"), then normalised.</summary>
    public static string SearchText(string text) => ArabicText.Normalize(ContractBodyExtractor.FixCodes(text));

    public static List<DocField> Fields(ContractPdfReadResult res)
    {
        var list = new List<DocField>();
        foreach (var (name, f) in res.Body.Fields) list.Add(Field(res.Document, f, "", name));
        foreach (var it in res.Schedule.Items)
            foreach (var f in it.Fields.Where(f => f.Status != FieldStatus.Missing))
                list.Add(Field(res.Document, f, "item " + it.ItemNo, f.Name));
        return list;
    }

    public static DocField Field(SmartDocument doc, FieldResult f, string row, string name)
    {
        var page = doc.Pages.FirstOrDefault(p => p.Number == f.Page);
        var w = page?.Ocr?.Width ?? page?.Base.WidthPt ?? 1;
        var h = page?.Ocr?.Height ?? page?.Base.HeightPt ?? 1;
        var b = f.Box?.Relative(w, h);
        return new DocField
        {
            Page = f.Page, RowKey = row, Field = name, Value = f.Value, Confidence = Math.Round(f.Confidence, 3), Status = f.Status, Note = f.Note,
            X = b?.X ?? 0, Y = b?.Y ?? 0, W = b?.W ?? 0, H = b?.H ?? 0,
            Alternatives = string.Join(" | ", f.Candidates.Select(c => $"{c.Value} ({c.Engine} {c.Confidence:0.00})").Distinct()),
        };
    }
}
