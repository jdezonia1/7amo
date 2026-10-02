using System.Text.Json.Nodes;
using Raffaello.Core.Coding;
using Raffaello.Core.Documents;
using Raffaello.Core.Materials;
using static Raffaello.Core.Tests.Phase3Fixtures;

namespace Raffaello.Core.Tests;

public class Phase3ReaderTests
{
    // ------------------------------------------------------------------ fingerprints / units

    [Theory]
    [InlineData("4X10mm²RM_CU/XL/SW/HF_1kV_B04_BK_STD", 4, 10, "CU", false, "LSZH", true, "LV")]
    [InlineData("4X10 CU/XLPE/SWA/LSOH", 4, 10, "CU", false, "LSZH", true, "LV")]
    [InlineData("4C 10mm2 Cu/XLPE/SWA/LSHZ + 1C 10mm2 Cu/LSF G/Y", 4, 10, "CU", false, "LSZH", true, "LV")]
    [InlineData("4x06mm2 CU/MICA/XLPE/SWA/LSZH (FIRE RATED)", 4, 6, "CU", true, "LSZH", true, "LV")]
    [InlineData("4C 50mm2 Fire Rated + 1C 25mm2 Fire Rated/ECC", 4, 50, "CU", true, "", false, "LV")]
    [InlineData("3X500 AL/XLPE/SWA/PVC 17.5 KV", 3, 500, "AL", false, "PVC", true, "MV")]
    [InlineData("3X120 CU/XLPE/SWA/PVC 13.80", 3, 120, "CU", false, "PVC", true, "MV")]
    [InlineData("1-3/C 120mm2 Cu/XLPE/SWA/PVC 13.8kV cable", 3, 120, "CU", false, "PVC", true, "MV")]
    public void Cable_fingerprint_reads_every_spelling(string text, int cores, double size, string cond, bool fire, string sheath, bool armour, string cls)
    {
        var c = Fingerprints.Cable(text);
        Assert.NotNull(c);
        Assert.Equal(cores, c!.Cores);
        Assert.Equal(size, c.Size);
        Assert.Equal(cond, c.Conductor);
        Assert.Equal(fire, c.FireRated);
        Assert.Equal(sheath, c.Sheath);
        Assert.Equal(armour, c.Armoured);
        Assert.Equal(cls, c.Class);
    }

    [Fact]
    public void Cable_fingerprints_match_supplier_and_po_spellings_but_not_fire_rated_or_other_sizes()
    {
        var dn = "4X10mm²RM_CU/XL/SW/HF_1kV_B04_BK_STD";
        Assert.Equal(1.0, Fingerprints.Compare(dn, "4X10 CU/XLPE/SWA/LSOH"));
        Assert.Equal(0, Fingerprints.Compare(dn, "4X10 CU/MICA/XLPE/SWA/LSOH"));
        Assert.Equal(0, Fingerprints.Compare(dn, "4X16 CU/XLPE/SWA/LSOH"));
        Assert.Equal(0, Fingerprints.Compare("3X120 CU/XLPE/SWA/PVC 13.80", "3X120 CU/XLPE/SWA/LSOH"));
        Assert.Equal(Fingerprints.Key("1X10 CU/LSOH (G/Y)"), Fingerprints.Key("1x10mm2 CU/LSF Y/G"));
        Assert.True(Fingerprints.Cable("1X10 CU/LSOH (G/Y)")!.Earth);
        Assert.Equal(2, Fingerprints.Cable("2 x 4C 240mm2 Cu/XLPE/SWA/LSZH + 2 x 1C 120mm2")!.Runs);
        Assert.Null(Fingerprints.Cable("PVC conduit 25mm heavy gauge"));
        Assert.True(Fingerprints.CompareGeneric("PVC CONDUIT 25MM", "25mm PVC conduits heavy gauge") > 0.4);
        Assert.Equal(0, Fingerprints.CompareGeneric("PVC CONDUIT 25MM", "PVC CONDUIT 20MM"));
    }

    [Fact]
    public void Units_normalise_and_convert()
    {
        Assert.Equal(Units.M, Units.Normalize("MT."));
        Assert.Equal(Units.M, Units.Normalize("Mtr"));
        Assert.Equal(Units.Pcs, Units.Normalize("عدد"));
        Assert.Equal(1047, Units.Convert(1.047, "KM", "MT.").Qty, 6);
        var pcs = Units.Convert(10, "PCS", "M", 3);
        Assert.True(pcs.Ok); Assert.Equal(30, pcs.Qty);
        Assert.False(Units.Convert(10, "PCS", "M").Ok);
        Assert.Equal(5, Units.Convert(30, "M", "PCS", 6).Qty);
        Assert.False(Units.Convert(1, "ROLL", "M").Ok);
        Assert.True(Units.Agree("MT", "KM"));
        Assert.False(Units.Agree("M", "PCS"));
        Assert.True(Units.Agree("", "PCS"));
    }

    // ------------------------------------------------------------------ PO

    [Fact]
    public void Po_reader_reads_header_terms_lines_totals_and_scope_sheet()
    {
        var r = PoReader.Parse(Doc("po.pdf", PoText));
        var h = r.Value.Header;
        Assert.Equal(PoNo, h.PoNo);
        Assert.Equal("Test Cables Co.", h.Supplier);
        Assert.Equal(new DateTime(2026, 4, 26), h.PoDate);
        Assert.Equal("Supply of LV Cables", h.Scope);
        Assert.Equal(0.10, h.AdvancePct, 6);
        Assert.Equal(0.05, h.RetentionPct, 6);
        Assert.Equal(0.0, h.ToleranceHeaderPct);
        Assert.Equal(0.05, h.ToleranceClausePct!.Value, 6);
        Assert.Equal(0.02, h.PenaltyPctPerWeek, 6);
        Assert.Equal(0.10, h.PenaltyMaxPct, 6);
        Assert.True(h.Remeasurable);
        Assert.Contains("Before Delivery", h.PaymentTerms);
        Assert.Equal(3, r.Value.Lines.Count);
        Assert.Equal(53053.20, r.Value.LinesTotal, 2);
        Assert.Equal(53053.20, h.StatedTotal, 2);
        Assert.Equal(61011.18, h.StatedGrandTotal, 2);
        Assert.Equal(Units.M, r.Value.Lines[0].Unit);
        Assert.Equal(2, r.Value.Scope.Count);
        Assert.Equal("B2-01-01-00-2-27-R-2", r.Value.Scope[0].BoqCode);
        Assert.Equal("B6-01-01-00-6-26-D-4", r.Value.Scope[1].BoqCode);
        Assert.DoesNotContain(r.Issues, i => i.Level == IssueLevel.Error);
        Assert.Contains(r.Issues, i => i.Code == "TOLERANCE_CONFLICT");
        Assert.DoesNotContain(r.Issues, i => i.Code == "SOW_QTY");
        Assert.Equal(0.05, h.EffectiveTolerancePct, 6);
    }

    [Fact]
    public void Po_reader_flags_amount_errors_missing_lines_and_wrong_totals()
    {
        var text = PoText.Select(l => l.Replace("SAR 23,621.00", "SAR 23,620.00")).Where(l => !l.Contains("PVC CONDUIT")).ToArray();
        text = text.Select(l => l.Replace("           02 ", "           04 ")).ToArray();
        var r = PoReader.Parse(Doc("po.pdf", text));
        Assert.Contains(r.Issues, i => i.Code == "LINE_AMOUNT" && i.Line == 4);
        Assert.Contains(r.Issues, i => i.Code == "LINE_GAP" && i.Message.Contains("2, 3"));
        Assert.Contains(r.Issues, i => i.Code == "TOTAL");
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void Po_reader_reads_excel_po()
    {
        var path = Path.Combine(TestData.TempDir(), "po.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.Worksheets.Add("PO");
            var h = new[] { "NO", "DESCRIPTION", "UNIT", "QTY", "UNIT PRICE", "TOTAL PRICE", "BOQ CODE" };
            for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
            ws.Cell(2, 1).Value = 1; ws.Cell(2, 2).Value = "4X10 CU/XLPE/SWA/LSOH"; ws.Cell(2, 3).Value = "MT"; ws.Cell(2, 4).Value = 100; ws.Cell(2, 5).Value = 28.5; ws.Cell(2, 6).Value = 2850; ws.Cell(2, 7).Value = "B6-01-01-00-6-26-D-4";
            ws.Cell(3, 2).Value = "Total"; ws.Cell(3, 6).Value = 2850;
            wb.SaveAs(path);
        }
        var r = PoReader.ReadExcel(path);
        Assert.Single(r.Value.Lines);
        Assert.Equal(2850, r.Value.Header.StatedTotal);
        Assert.Equal(CodeStatus.Document, r.Value.Lines[0].CodeStatus);
        Assert.DoesNotContain(r.Issues, i => i.Code is "TOTAL" or "LINE_AMOUNT");
    }

    // ------------------------------------------------------------------ DN

    [Fact]
    public void Dn_reader_reads_header_lines_batches_and_converts_km()
    {
        var r = DnReader.Parse(Doc("dn.pdf", DnText));
        var h = r.Value.Header;
        Assert.Equal("81000001", h.DnNo);
        Assert.Equal(PoNo, h.PoNo);
        Assert.Equal(new DateTime(2026, 8, 15), h.DnDate);
        Assert.Equal(new DateTime(2026, 4, 15), h.PoDate);
        Assert.Equal("40000001", h.OrderNo);
        Assert.Equal("Test Cables", h.Supplier);
        Assert.Equal(2, r.Value.Lines.Count);
        var l = r.Value.Lines[0];
        Assert.EndsWith("_STD", l.Description);
        Assert.Equal("0013289917", l.Batch);
        Assert.Equal(0.6, l.RawQty, 6);
        Assert.Equal(Units.Km, l.RawUnit);
        Assert.Equal(600, l.Qty, 6);
        Assert.Equal(Units.M, l.Unit);
        Assert.Equal(2, r.Value.SubTotals.Count);
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void Dn_reader_flags_subtotal_mismatch()
    {
        var text = DnText.Select(l => l.Replace("Sub-Total    0.300", "Sub-Total    0.350")).ToArray();
        var r = DnReader.Parse(Doc("dn.pdf", text));
        Assert.Contains(r.Issues, i => i.Code == "SUBTOTAL" && i.Line == 2);
    }

    [Fact]
    public void Dn_pdf_round_trip_through_pdfpig_layout()
    {
        var path = DnPdf();
        var text = PdfTextReader.Read(path);
        Assert.True(text.Pages[0].HasTextLayer);
        var r = DnReader.Parse(text);
        Assert.Equal("81000777", r.Value.Header.DnNo);
        Assert.Equal(PoNo, r.Value.Header.PoNo);
        Assert.Equal(2, r.Value.Lines.Count);
        Assert.Equal(250, r.Value.Lines[0].Qty, 6);
        Assert.Equal("0013289998", r.Value.Lines[1].Batch);
        Assert.True(Fingerprints.Cable(r.Value.Lines[1].Description)!.FireRated);
    }

    [Fact]
    public void Dn_reader_reads_excel_with_several_dns()
    {
        var path = Path.Combine(TestData.TempDir(), "dn.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.Worksheets.Add("DN");
            var h = new[] { "DN NO", "DATE", "PO", "SUPPLIER", "DESCRIPTION", "BATCH", "QTY", "UNIT" };
            for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
            object[][] rows = { new object[] { "D1", "2026-08-01", PoNo, "Test Cables", "4X10 CU/XLPE/SWA/LSOH", "B1", 0.5, "KM" }, new object[] { "D2", "2026-08-02", PoNo, "Test Cables", "PVC CONDUIT 25MM", "", 20, "PCS" } };
            for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) ws.Cell(r + 2, c + 1).Value = ClosedXML.Excel.XLCellValue.FromObject(rows[r][c]);
            wb.SaveAs(path);
        }
        var list = DnReader.ReadExcel(path);
        Assert.Equal(2, list.Count);
        Assert.Equal(500, list[0].Value.Lines[0].Qty);
        Assert.Contains(list[1].Issues, i => i.Code == "BATCH");
    }

    // ------------------------------------------------------------------ MIR

    [Fact]
    public void Mir_reader_reads_form_dn_refs_and_quantity_list()
    {
        var r = MirReader.Parse(Doc("mir.pdf", MirText, QtyListPage));
        var h = r.Value.Header;
        Assert.Equal("DG-XXX-261-0410-MOB-MIR-EL-000999", h.MirNo);
        Assert.Equal("00", h.Revision);
        Assert.Equal(new DateTime(2026, 8, 18), h.MirDate);
        Assert.Equal("DG-XXX-261-0410-MOB-MAT-EL-000071", h.MarRef);
        Assert.Single(r.Value.Dns);
        Assert.Equal("81000001", r.Value.Dns[0].DnNo);
        Assert.Equal(PoNo, r.Value.Dns[0].PoNo);
        Assert.Equal(3, r.Value.Evidence.Count(e => e.Kind == "REQUIRED"));
        Assert.Contains("QTY LIST", h.PageKinds);
    }

    [Fact]
    public void Mir_classifies_photo_and_scan_pages_without_ocr()
    {
        var d = Doc("mir.pdf", MirText);
        PageImage Img(int w, int h) => new() { Width = w, Height = h, Coverage = 1, MediaType = "image/jpeg", Bytes = new byte[] { 1 } };
        d.Pages.Add(new DocPage { Number = 2, DominantImage = Img(960, 1280) });
        d.Pages.Add(new DocPage { Number = 3, Text = string.Join("\n", QtyListPage), Source = TextSource.TextLayer, HasTextLayer = true, WordCount = 50 });
        d.Pages.Add(new DocPage { Number = 4, DominantImage = Img(1653, 2338) });
        d.Pages.Add(new DocPage { Number = 5, DominantImage = Img(960, 1280) });
        MirReader.Classify(d);
        Assert.Equal("DN PHOTO?", d.Pages[1].Kind);
        Assert.Equal("QTY LIST", d.Pages[2].Kind);
        Assert.Equal("TEST CERT?", d.Pages[3].Kind);
        Assert.Equal("DRUM LABEL?", d.Pages[4].Kind);
    }

    // ------------------------------------------------------------------ vision reader

    private sealed class FakeVision : IVisionReader
    {
        public JsonNode Answer { get; init; } = new JsonObject();
        public List<VisionRequest> Requests { get; } = new();
        public bool IsAvailable => true;
        public string Status => "fake";
        public Task<JsonNode?> ExtractAsync(VisionRequest request, CancellationToken ct = default) { Requests.Add(request); return Task.FromResult<JsonNode?>(Answer.DeepClone()); }
    }

    [Fact]
    public async Task Dn_photo_goes_through_vision_and_the_same_validation()
    {
        var img = Path.Combine(TestData.TempDir(), "dn-photo.jpg");
        await File.WriteAllBytesAsync(img, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        var vision = new FakeVision
        {
            Answer = JsonNode.Parse("""{"dn_no":"81009999","po_no":"ABC-P.O-E-001-2026","date":"Aug 20, 2026","supplier":"Test Cables","lines":[{"item":"10","code":"1","description":"4X10mm2 CU/XL/SW/HF 1kV","batch":"","qty":0.4,"unit":"KM"}]}""")!,
        };
        var r = await DnReader.ReadAsync(img, new ReaderOptions { Vision = vision });
        Assert.Single(vision.Requests);
        Assert.Equal("image/jpeg", vision.Requests[0].MediaType);
        Assert.Equal("81009999", r.Value.Header.DnNo);
        Assert.Equal(TextSource.Vision, r.PageSources[1]);
        Assert.Equal(400, r.Value.Lines[0].Qty, 6);
        Assert.Contains(r.Issues, i => i.Code == "BATCH");
    }

    [Fact]
    public void Claude_vision_reader_is_off_without_opt_in_or_key_and_builds_structured_requests()
    {
        var off = new ClaudeVisionReader(false, "sk-test", null);
        Assert.False(off.IsAvailable);
        Assert.Contains("off", off.Status);
        var on = new ClaudeVisionReader(true, "sk-test", null);
        Assert.True(on.IsAvailable);
        Assert.Equal("claude-opus-5-5", on.Model);
        var body = on.BuildBody(new VisionRequest { DocumentKind = "delivery note", Page = 2, Content = new byte[] { 1, 2 }, MediaType = "application/pdf", Schema = DnReader.Schema });
        Assert.Equal("document", body["messages"]![0]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        var img = on.BuildBody(new VisionRequest { Content = new byte[] { 1 }, MediaType = "image/png", Schema = DnReader.Schema });
        Assert.Equal("image", img["messages"]![0]!["content"]![0]!["type"]!.GetValue<string>());
        var parsed = ClaudeVisionReader.ParseAnswer("""{"stop_reason":"end_turn","content":[{"type":"text","text":"{\"dn_no\":\"1\"}"}]}""");
        Assert.Equal("1", parsed!["dn_no"]!.GetValue<string>());
        Assert.Throws<Raffaello.Core.Ai.AnthropicException>(() => ClaudeVisionReader.ParseAnswer("""{"stop_reason":"refusal","content":[]}"""));
    }

    [Fact]
    public void Layout_text_keeps_columns_apart()
    {
        var t = PdfTextReader.Read(DnPdf());
        var line = t.Pages[0].Lines.First(l => l.Contains("000110"));
        Assert.Matches(@"000110\s+10000337\s+4X10.*\s{2,}3110", line);
    }
}
