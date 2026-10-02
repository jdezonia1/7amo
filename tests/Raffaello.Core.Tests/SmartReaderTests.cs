using Raffaello.Core.AconexWeb;
using Raffaello.Core.Contracts;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using Raffaello.Core.Documents.Smart;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Tests;

/// <summary>Smart document reader: text plausibility, Arabic order, layout / tables, voting, extractors, archive, templates. Synthetic data only.</summary>
public class SmartReaderTests
{
    // ------------------------------------------------------------------ helpers

    internal static OcrWord W(string text, double x, double y, double w = 60, double h = 30, double conf = 0.95) =>
        new() { Text = text, Box = new Box(x, y, w, h), Confidence = conf, Engine = "test" };

    /// <summary>A ruled schedule page: columns No | Description | Unit | Qty | Rate | Total (left to right, like the signed contract scan).</summary>
    internal static OcrPage SchedulePage(IEnumerable<(string No, string Desc, string Unit, string Qty, string Rate, string Total)> rows, bool header = true)
    {
        var xs = new double[] { 100, 200, 1200, 1350, 1500, 1650, 1850 };
        var page = new OcrPage { Width = 2000, Height = 2800, Engine = "test" };
        var all = (header ? new[] { ("رقم", "التوصيف", "الوحدة", "الكمية", "سعر الوحدة", "الاجمالي") } : Array.Empty<(string, string, string, string, string, string)>()).Concat(rows).ToList();
        var y = 200.0;
        var ys = new List<double> { y };
        foreach (var r in all)
        {
            var cells = new[] { r.Item1, r.Item2, r.Item3, r.Item4, r.Item5, r.Item6 };
            for (var c = 0; c < 6; c++)
                if (cells[c].Length > 0)
                    page.Words.Add(W(cells[c], xs[c] + 10, y + 20, Math.Min(xs[c + 1] - xs[c] - 20, 12 * cells[c].Length + 10), 30));
            y += 80;
            ys.Add(y);
        }
        foreach (var yy in ys) page.Rulings.Add(new Ruling(true, xs[0], yy, xs[^1], yy));
        foreach (var x in xs) page.Rulings.Add(new Ruling(false, x, ys[0], x, ys[^1]));
        return page;
    }

    internal static SmartPage Smart(OcrPage ocr, int number = 1, string? type = null)
    {
        var p = new SmartPage { Number = number, Ocr = ocr, Source = TextSource.Ocr, Engine = "test", Text = LayoutBuilder.Text(ocr), ReadingText = LayoutBuilder.ReadingText(ocr.Words), Confidence = ocr.MeanConfidence };
        SmartReader.Classify(p);
        if (type != null) p.Kind = new Classification(type, 1, "test");
        return p;
    }

    private sealed class FakeRereader : IFieldRereader
    {
        private readonly Func<Box, string?> _read;
        public FakeRereader(Func<Box, string?> read) => _read = read;
        public Task<List<FieldCandidate>> RereadAsync(SmartPage page, Box box, bool numeric, CancellationToken ct = default) =>
            Task.FromResult(_read(box) is { } v ? new List<FieldCandidate> { new(v, 0.96, "second-model", box, page.Number) } : new List<FieldCandidate>());
    }

    // ------------------------------------------------------------------ Arabic text

    [Fact]
    public void Visual_to_logical_reverses_arabic_and_keeps_latin_and_numbers()
    {
        // what a recogniser returns for "تركيب مخرج PVC 1st Fix لإرتفاع أقل 4.5 متر" (drawn right-to-left)
        var logical = "تركيب مخرج PVC 1st Fix لارتفاع اقل 4.5 متر";
        var visual = ArabicText.LogicalToVisual(logical);
        Assert.NotEqual(logical, visual);
        Assert.Equal(logical, ArabicText.VisualToLogical(visual));
        Assert.Contains("4.5", visual);
        Assert.Contains("PVC 1st Fix", visual);
        Assert.Equal("Riyadh Cables", ArabicText.VisualToLogical("Riyadh Cables"));
    }

    [Fact]
    public void Digits_separators_and_confusions_normalise()
    {
        Assert.Equal("1,920.5", ArabicText.NormalizeDigits("١٬٩٢٠٫٥"));
        Assert.Equal(1920.5, ArabicText.ParseNumber("١٬٩٢٠٫٥"));
        Assert.Equal(36000, ArabicText.ParseNumber("36,000"));
        Assert.Equal(1.047, ArabicText.ParseNumber("1.047"));
        Assert.Equal("2026", ArabicText.FixDigitConfusions("2O26"));
        Assert.Equal("MOBCO", ArabicText.FixDigitConfusions("MOBCO"));
        Assert.Equal(ArabicText.Normalize("الكمية"), ArabicText.Normalize("الكميّة"));
        Assert.True(ArabicText.CharAccuracy("تركيب و تسليم مخرج", "تركيب وتسليم مخرج") > 0.99);
        Assert.Equal(ArabicText.Fold("أسعار").Length, "أسعار".Length);
    }

    // ------------------------------------------------------------------ text-layer plausibility

    [Fact]
    public void Garbage_scanner_layer_is_detected()
    {
        // reversed Arabic, letter O for zero, misspelt MOBCO, glyph garbage - like the signed contract's scanner layer
        var garbage = "عورشم ةكرش ‫Mobco-Raffles-SUB-ELE-O28-2O26 ءابرهكلا لامعأ تايعنصم راعسأ MPBCO ٧٧٧٧٧ ٦٦٦٦ دقعلا ةيقافتا ٧٧٧٧٧٧";
        var q = TextQuality.Score(garbage);
        Assert.True(q.IsGarbage, q.ToString());
        Assert.True(q.ReversedArabic);

        var good = "مشروع شركة اسعار مصنعيات اعمال الكهرباء تركيب و تسليم مخرج من النوع طبقا لاصول الصناعة Mobco-Raffles-SUB-ELE-028-2026";
        Assert.False(TextQuality.Score(good).IsGarbage, TextQuality.Score(good).ToString());
        var dn = "Delivery Note 81064344 Aug 15, 2026 RAF-P.O-E-045-2026 000110 10000337 4X10mm²RM_CU/XL/SW/HF_1kV_B04_BK_ST 3110 001 0013289917 1.047 KM Sub-Total";
        Assert.False(TextQuality.Score(dn).IsGarbage, TextQuality.Score(dn).ToString());
    }

    [Fact]
    public void Pipeline_drops_garbage_layers_so_parsers_never_read_them()
    {
        var t = new DocText { FileName = "x.pdf" };
        t.Pages.Add(new DocPage { Number = 1, HasTextLayer = true, WordCount = 30, Text = "عورشم ةكرش MPBCO O28-2O26 ٧٧٧٧٧ ءابرهكلا لامعأ تايعنصم راعسأ ٦٦٦٦٦ دقعلا ةيقافتا", Source = TextSource.TextLayer });
        t.Pages.Add(new DocPage { Number = 2, HasTextLayer = true, WordCount = 30, Text = "Delivery Note 81064344 Truck No. 3837 LXA RAF-P.O-E-045-2026 Sub-Total 1.047 KM", Source = TextSource.TextLayer });
        var r = ReaderPipeline.DropGarbageLayers(t);
        Assert.True(r.Pages[0].IsScan);
        Assert.Equal("", r.Pages[0].Text);
        Assert.True(r.Pages[0].LayerQuality!.IsGarbage);
        Assert.False(r.Pages[1].IsScan);
    }

    // ------------------------------------------------------------------ layout / tables

    [Fact]
    public void Layout_groups_words_into_lines_with_column_gaps()
    {
        var words = new[] { W("000110", 10, 100, 70), W("10000337", 200, 102, 90), W("0013289917", 600, 98, 110), W("1.047", 760, 100, 50), W("KM", 820, 101, 30), W("Sub-Total", 600, 160, 90) };
        var lines = LayoutBuilder.Lines(words);
        Assert.Equal(2, lines.Count);
        var text = LayoutBuilder.Text(words);
        Assert.Matches(@"000110\s{2,}10000337\s{2,}0013289917\s+1\.047\s+KM", text.Split('\n')[0]);
    }

    [Fact]
    public void Table_from_rulings_and_whitespace()
    {
        var page = SchedulePage(new[] { ("1", "تركيب مخرج", "عدد", "4000", "55", "220,000") });
        var g = TableBuilder.FromRulings(page).Single();
        Assert.Equal(6, g.ColCount);
        Assert.Equal(2, g.RowCount);
        Assert.Equal("220,000", g[1, 5].Text);
        var ws = TableBuilder.FromWhitespace(new OcrPage { Width = 1000, Height = 500, Words = new List<OcrWord>
        {
            W("Item", 10, 10, 40), W("Batch", 300, 10, 50), W("Qty", 600, 10, 30),
            W("000110", 10, 60, 60), W("0013289917", 300, 60, 100), W("1.047", 600, 60, 50),
            W("000350", 10, 110, 60), W("0013278202", 300, 110, 100), W("0.455", 600, 110, 50),
        } });
        Assert.NotNull(ws);
        Assert.Equal(3, ws!.ColCount);
        Assert.Equal("0013278202", ws[2, 1].Text);
    }

    // ------------------------------------------------------------------ voting

    [Fact]
    public void Vote_agrees_flags_conflicts_and_uses_validators()
    {
        var agreed = FieldVote.Decide("QTY", new[] { new FieldCandidate("1,000", 0.8, "a"), new FieldCandidate("1000", 0.9, "b") });
        Assert.Equal(FieldStatus.Agreed, agreed.Status);

        var conflict = FieldVote.Decide("QTY", new[] { new FieldCandidate("100", 0.9, "a"), new FieldCandidate("400", 0.9, "b") });
        Assert.Equal(FieldStatus.Conflict, conflict.Status);
        Assert.True(conflict.NeedsReview);
        Assert.Equal(2, conflict.Candidates.Count);

        var validated = FieldVote.Decide("QTY", new[] { new FieldCandidate("100", 0.9, "a"), new FieldCandidate("400", 0.7, "b") }, v => v == "400");
        Assert.Equal(FieldStatus.Validated, validated.Status);
        Assert.Equal("400", validated.Value);

        var none = FieldVote.Decide("QTY", new[] { new FieldCandidate("100", 0.9, "a") }, v => v == "5");
        Assert.Equal(FieldStatus.Conflict, none.Status);
        Assert.Equal(FieldStatus.Missing, FieldVote.Decide("X", Array.Empty<FieldCandidate>()).Status);
    }

    // ------------------------------------------------------------------ rate schedule

    [Fact]
    public async Task Schedule_reads_sections_items_and_checks_arithmetic()
    {
        var page = SchedulePage(new[]
        {
            ("", "Lighting and Power", "", "", "", ""),
            ("1", "تركيب و تسليم مخرج من النوع PVC 1st Fix جداري لإرتفاع أقل 4.5 متر", "عدد", "4000", "55", "220,000"),
            ("2", "تركيب و تسليم مخرج من النوع PVC 1st Fix جداري لإرتفاع فوق 4.5 متر", "عدد", "400", "57", "22,800"),
            ("", "Led Strip", "", "", "", ""),
            ("3", "تركيب مخرج Linear lighting", "م ط", "1000", "36", "36,000"),
        });
        var read = await ScheduleExtractor.ReadAsync(new[] { Smart(page, 4, DocTypes.RateSchedule) });
        Assert.Equal(3, read.Items.Count);
        Assert.Equal(new[] { "Lighting and Power", "Led Strip" }, read.Sections.Select(s => s.Section));
        Assert.All(read.Items, i => Assert.True(i.ArithmeticOk));
        Assert.Equal("م.ط", read.Items[2].Unit.Value);
        Assert.Equal("Led Strip", read.Items[2].Section);
        var items = read.ToContractItems("C-1");
        Assert.Equal("1ST FIX", items[0].FixStage);
        Assert.Equal(HeightBands.High, items[1].HeightBand);
        Assert.DoesNotContain(read.Issues, i => i.Code == "ARITH");
    }

    [Fact]
    public async Task Schedule_misread_number_is_corrected_by_a_second_reading_and_the_arithmetic()
    {
        // the total under a stamp was read as "22,300"; the second model reads "22,800" which satisfies 400 x 57
        var page = SchedulePage(new[] { ("2", "تركيب مخرج", "عدد", "400", "57", "22,300") });
        var smart = Smart(page, 1, DocTypes.RateSchedule);
        var totalBox = page.Words.Single(w => w.Text == "22,300").Box;
        var rr = new FakeRereader(b => b.Equals(totalBox) ? "22,800" : null);
        var read = await ScheduleExtractor.ReadAsync(new[] { smart }, rr);
        var it = read.Items.Single();
        Assert.Equal(22800, it.TotalValue);
        Assert.Equal(FieldStatus.Validated, it.Total.Status);
        Assert.Contains("corrected", it.Total.Note);
    }

    [Fact]
    public async Task Schedule_unresolvable_numbers_stay_conflicts_never_silently_picked()
    {
        var page = SchedulePage(new[] { ("5", "تركيب", "عدد", "2000", "3", "56,000") });
        var read = await ScheduleExtractor.ReadAsync(new[] { Smart(page, 1, DocTypes.RateSchedule) }, new FakeRereader(_ => null));
        var it = read.Items.Single();
        Assert.True(it.NeedsReview);
        Assert.Contains(it.Fields, f => f.Status == FieldStatus.Conflict);
        Assert.Contains(read.Issues, i => i.Code == "ARITH");
    }

    [Fact]
    public async Task Schedule_smudged_total_is_derived_only_when_qty_and_rate_agree_between_models()
    {
        var page = SchedulePage(new[] { ("13", "سحب سلك", "عدد", "2000", "28", "*") });
        var smart = Smart(page, 1, DocTypes.RateSchedule);
        var read = await ScheduleExtractor.ReadAsync(new[] { smart }, new FakeRereader(b =>
            page.Words.FirstOrDefault(w => w.Box.Equals(b))?.Text is "2000" or "28" ? page.Words.First(w => w.Box.Equals(b)).Text : null));
        var it = read.Items.Single();
        Assert.Equal(56000, it.TotalValue);
        Assert.Equal(FieldStatus.Derived, it.Total.Status);
        Assert.True(it.NeedsReview);
    }

    [Fact]
    public void Schedule_roles_from_content_when_header_is_unreadable()
    {
        var page = SchedulePage(new[]
        {
            ("1", "تركيب و تسليم مخرج من النوع جداري", "عدد", "4000", "55", "220000"),
            ("2", "تركيب و تسليم مخرج من النوع سقفي", "عدد", "400", "57", "22800"),
            ("3", "سحب سلك جداري او سقفي", "عدد", "7000", "28", "196000"),
        }, header: false);
        var g = TableBuilder.FromRulings(page).Single();
        var roles = ScheduleExtractor.RolesFromContent(g);
        Assert.Equal(new[] { ColumnRoles.No, ColumnRoles.Desc, ColumnRoles.Unit, ColumnRoles.Qty, ColumnRoles.Rate, ColumnRoles.Total }, roles);
    }

    // ------------------------------------------------------------------ classifier / validators

    [Fact]
    public void Classifier_types_pages()
    {
        Assert.Equal(DocTypes.RateSchedule, DocClassifier.Classify("رقم التوصيف الوحدة الكمية سعر الوحدة الاجمالي اسعار مصنعيات اعمال الكهرباء").Type);
        Assert.Equal(DocTypes.Subcontract, DocClassifier.Classify("انه في يوم الثلاثاء تم توقيع هذا العقد بين المقاول الرئيسي ومقاول الباطن الطرف الاول الطرف الثاني بند 1 شروط عامة").Type);
        Assert.Equal(DocTypes.Dn, DocClassifier.Classify("Delivery Note Delivery Number / Date 81064344 Batch Quantity Sub-Total 1.047 KM Truck No 3837").Type);
        Assert.Equal(DocTypes.AconexScreenshot, DocClassifier.Classify("ORACLE Aconex Search Workflows Workflow No.: WF-008795 Step Name Assigned To Date Due Step Status Step Outcome").Type);
        Assert.Equal(DocTypes.TestCert, DocClassifier.Classify("TEST CERTIFICATE Drum No. Qty (PC) CONDUCTOR RESISTANCE").Type);
        Assert.Equal(DocTypes.MarkedDrawing, DocClassifier.Classify("36 Point Socket Power 9 Point IT box Total 84 Point 84 x 3 = 252").Type);
        var seg = DocClassifier.Segments(new[] { (1, new Classification(DocTypes.Subcontract, 1, "")), (2, new Classification(DocTypes.RateSchedule, 1, "")), (3, new Classification(DocTypes.RateSchedule, 1, "")), (4, Classification.Unknown) });
        Assert.Equal("RATE SCHEDULE p2-4", seg[1].ToString());
    }

    [Fact]
    public void Validators_units_dates_codes_sequence()
    {
        Assert.Equal("م.ط", DocValidators.Unit("م ط"));
        Assert.Equal("م.ط", DocValidators.Unit("م .ط"));
        Assert.Equal("No.", DocValidators.Unit("Nos"));
        Assert.Null(DocValidators.Unit("CONDUCTOR"));
        Assert.Equal(new DateTime(2026, 5, 12), DocValidators.Date("12 مايو 2026"));
        Assert.Equal(new DateTime(2026, 5, 18), DocValidators.Date("18-May-26"));
        Assert.Equal(new DateTime(2026, 8, 15), DocValidators.Date("15.08.2026"));
        Assert.True(DocValidators.IsBoqCode("B6-01-01-00-6-26-V-5"));
        Assert.False(DocValidators.IsBoqCode("B6-01-01"));
        var (missing, dup) = DocValidators.Sequence(new[] { 1, 2, 4, 4, 5 });
        Assert.Equal(new[] { 3 }, missing);
        Assert.Equal(new[] { 4 }, dup);
        Assert.True(DocValidators.VatOk(9367585.11, 1405137.77));
        Assert.True(DocValidators.GrandTotalOk(9367585.11, 1405137.77, 10772722.88));
    }

    // ------------------------------------------------------------------ contract body

    internal const string ContractBody = """
        The Raffles Hotel and Branded Residences مشروع
        ( Mobco-Raffles-SUB-ELE-O28-2O26 )
        انه في يوم الثلاثاء الموافق 12 مايو 2026 تم توقيع هذا العقد بين:
        المقاول الرئيسي: شركة مؤنس محمد الشايب وشركاه للأعمال المدنية (موبكو) س.ت 1010178838 ، عنوانها الدائم الرياض
        مقاول الباطن : شركة روتس لاندسكيب سجل تجاري رقم : 7051467475 طرف ثاني
        تنفيذ الأعمال الكهربائيه (مصنعيات فقط)
        جوال 0563921461
        بند 4 الكميات والأسعار
        الاسعار المذكورة في العقد غير شاملة الضريبة المضافة 15%
        بند 5 شروط الدفع
        أ- 90% دفعه تصرف بمستخلص مع تنفيذ 1st Fix والاستلام من الاستشاري
        ب- 90% دفعه تصرف بمستخلص مع تنفيذ 2nd Fix والاستلام من الاستشاري
        ت- 90% دفعه تصرف بمستخلص مع تنفيذ 3rd Fix والاستلام من الاستشاري
        ث- 10% دفعه تصرف عند التسليم الابتدائي للمشروع بدون ملاحظات
        بالنسبة لأعمال الكابل تراي والكابلات واللوحات تكون صرف الدفعات كالتالي
        أ- 70% دفعة بعد أعمال التركيب وسحب الكابلات
        ب- 20% دفعة بعد أعمال الاختبار والتوصيل
        ت- 10% دفعه تصرف عند التسليم الابتدائي للمشروع بدون ملاحظات
        بند 6 غرامات التأخير
        يلتزم مقاول الباطن بدفع غرامة تأخير مقدارها 500 ريال عن كل أسبوع تأخير بحد أقصى 10% من اجمالي قيمة الأعمال
        بند 8 الضمان
        يضمن مقاول الباطن جميع الأعمال مدة سنة من تاريخ التسليم
        بند 7 الغاء اتفاقية العقد
        للطرف الأول الحق في الغاء اتفاقية العقد
        """;

    internal static SmartPage TextPage(string text, int number = 2, string type = DocTypes.Subcontract) =>
        new() { Number = number, Text = text, ReadingText = text, Source = TextSource.Ocr, Kind = new Classification(type, 1, "test"), Confidence = 0.9 };

    [Fact]
    public void Contract_body_terms_and_clauses()
    {
        var b = ContractBodyExtractor.Read(new[] { TextPage(ContractBody) });
        var t = b.Terms;
        Assert.Equal("Mobco-Raffles-SUB-ELE-028-2026", t.ContractNo);
        Assert.Equal(new DateTime(2026, 5, 12), t.ContractDate);
        Assert.Equal("1010178838", t.FirstPartyCr);
        Assert.Equal("7051467475", t.SubcontractorCr);
        Assert.Contains("روتس لاندسكيب", t.Subcontractor);
        Assert.True(t.LabourOnly);
        Assert.Equal("EXCLUDED", t.VatTreatment);
        Assert.Equal(0.15, t.VatPct, 6);
        Assert.Equal("1ST FIX 90%; 2ND FIX 90%; 3RD FIX 90%; HANDOVER 10%", t.PaymentTerms);
        Assert.Equal("INSTALLATION 70%; TEST & TERMINATION 20%; HANDOVER 10%", t.TrayPaymentTerms);
        Assert.Equal(500, t.DelayPenaltyPerWeek);
        Assert.Equal(0.10, t.DelayPenaltyCapPct!.Value, 6);
        Assert.Equal(12, t.WarrantyMonths);
        Assert.Equal(new[] { "4", "5", "6", "8", "7" }, b.Clauses.Select(c => c.ClauseNo));
        Assert.Contains("الغاء", t.Termination + b.Clauses.First(c => c.ClauseNo == "7").Title);
    }

    // ------------------------------------------------------------------ Aconex screenshot

    [Fact]
    public void Aconex_screenshot_becomes_workflow_steps()
    {
        var words = new List<OcrWord>
        {
            W("Step Name", 400, 100, 90, 20), W("Assigned To", 530, 100, 90, 20), W("Date In", 660, 100, 50, 20), W("Date Due", 715, 100, 60, 20),
            W("Date", 830, 90, 40, 18), W("Completed", 830, 110, 70, 18), W("Step", 900, 90, 40, 18), W("Status", 900, 110, 50, 18), W("Step", 960, 90, 40, 18), W("Outcome", 960, 110, 60, 18),
            W("Workflow No.: WF-008795 Name: Electrical_PR_PO_Approval", 60, 150, 400, 20),
            W("Procurement", 400, 200, 90, 18), W("Director", 400, 220, 70, 18), W("Mr Zeinhom mohamed - Mobco", 530, 200, 120, 18), W("18/05/2026", 660, 200, 50, 18), W("19/05/2026", 715, 200, 55, 18),
            W("18/05/2026", 830, 200, 60, 18), W("Completed", 900, 200, 55, 18), W("A - Approved", 960, 200, 60, 18),
            W("Top Management", 400, 280, 110, 18), W("Apps Support - Mobco", 530, 280, 110, 18), W("10/06/2026", 660, 280, 50, 18), W("13/06/2026", 715, 280, 55, 18),
            W("Overdue", 900, 280, 50, 18), W("Pending", 960, 280, 50, 18),
        };
        var page = new OcrPage { Width = 1100, Height = 800, Words = words };
        var r = AconexScreenshotExtractor.Read(Smart(page, 1, DocTypes.AconexScreenshot), new AconexConfig(), new DateTime(2026, 6, 20));
        Assert.Equal("WF-008795", r.WorkflowNo);
        Assert.NotNull(r.Result);
        Assert.Equal(2, r.Result!.Steps.Count);
        Assert.Equal("Procurement Director", r.Result.Steps[0].StepName);
        Assert.Equal(new DateTime(2026, 5, 18), r.Result.Steps[0].DateCompleted);
        Assert.Equal(StepStatuses.Overdue, r.Result.Steps[1].StepStatus);
        Assert.Equal("Top Management", r.Result.CurrentStep);
    }

    // ------------------------------------------------------------------ DN photo layout, evidence, statement

    [Fact]
    public void Dn_photo_layout_text_is_parsed()
    {
        var text = """
              SAUDI MODERN COMPANY FOR SPECIALIZED WIRES & CABLES INDUSTRY
                                      Delivery Note
                                      Delivery Number / Date 81064344 / 15.08.2026
                                      Reference Number / Date
                                      RAF-P,O-E-045-2026 /15.04.2026
                                      Order Number / Date
                                      40222645 / 16.04.2026
                                      Customer No: 10016197
            Item   Cust. Mat   Material Code   Description                          SLoc  DN   Batch        Quantity
            000110             10000337        4X10mm²RM_CU/XL/SW/HF_1kV_B04_BK_STD  3110  001  0013289917   1.047 KM
                                                                                      Sub-Total  1.047   KM
            P.O. BOX 26862, RIYADH  Email: rcgc@riyadh-cables.com
            """;
        var t = new DocText { FileName = "photo" };
        t.Pages.Add(new DocPage { Number = 1, Text = text, Source = TextSource.Ocr, WordCount = 60 });
        var r = DnReader.Parse(t);
        Assert.Equal("81064344", r.Value.Header.DnNo);
        Assert.Equal(new DateTime(2026, 8, 15), r.Value.Header.DnDate);
        Assert.Equal("RAF-P.O-E-045-2026", r.Value.Header.PoNo);
        Assert.Equal("40222645", r.Value.Header.OrderNo);
        var l = Assert.Single(r.Value.Lines);
        Assert.Equal("0013289917", l.Batch);
        Assert.Equal(1047, l.Qty, 6);
    }

    [Fact]
    public void Certificates_labels_and_statement_drafts()
    {
        var cert = TextPage("TEST CERTIFICATE\nP.O.Ref.  : STC-PO-E-099-2026\nDrum No.  Qty (PC) Test Description\n0013333193  92  CONDUCTOR RESISTANCE", 10, DocTypes.TestCert);
        var c = Assert.Single(EvidenceExtractor.TestCertificate(cert));
        Assert.Equal("0013333193", c.DrumNo);
        Assert.Equal(92, c.Qty);
        Assert.Equal("STC-PO-E-099-2026", c.PoRef);
        var label = TextPage("Description: 4X95mm²SM_CU/XL/SW/HF_1kV_B04_BK_STD\nBatch: 0013306700\nQuantity: 354 M\nPlant: 1100", 26, DocTypes.DrumLabel);
        var l = Assert.Single(EvidenceExtractor.DrumLabel(label));
        Assert.Equal("0013306700", l.Batch);
        Assert.Equal(354, l.Qty);

        var drawing = TextPage("2BR-P2-L01-No-106\n36 Point Socket Power\n9 point IT box\n39 Point Lighting box\nTotal 84 Point\n18 Point installtion Pip pvc GRMS\n84 x 3 = 252\n18 x 3 = 54\n(P2-106 & P3-103) (P4-04)", 5, DocTypes.MarkedDrawing);
        var m = SiteStatementExtractor.ReadDrawing(drawing);
        Assert.Equal(36, m.PerUnit["POWER"]);
        Assert.Equal(9, m.PerUnit["DATA"]);
        Assert.Equal(39, m.PerUnit["LIGHT"]);
        Assert.Equal(18, m.PerUnit["GRMS"]);
        Assert.Contains(m.Products, p => p.A == 84 && p.B == 3 && p.Result == 252 && p.Ok);
        Assert.Contains("P3-103", m.Rooms);

        var sumWords = new List<OcrWord> { W("Install First Fix walls 2BR (Power+light+GRMS+IT)", 300, 300, 400, 30, 0.6), W("%100", 720, 300, 50, 30, 0.6), W("P2-106", 20, 300, 60, 30, 0.5), W("Install 2nd Fix pull wires 2BR", 300, 400, 300, 30, 0.6), W("%60", 720, 400, 40, 30, 0.6) };
        var sum = Smart(new OcrPage { Width = 1000, Height = 1400, Words = sumWords }, 1, DocTypes.SiteStatement);
        var d = SiteStatementExtractor.Read(new[] { sum }, Array.Empty<SmartPage>());
        Assert.Equal(2, d.Rows.Count);
        Assert.Equal("1ST FIX", d.Rows[0].Stage);
        Assert.Equal("2BR", d.Rows[0].UnitType);
        Assert.Contains("GRMS", d.Rows[0].Systems);
        Assert.Equal(1, d.Rows[0].Pct);
        Assert.Contains("P2-106", d.Rows[0].Rooms);
        Assert.Equal("2ND FIX", d.Rows[1].Stage);
        Assert.Equal(0.6, d.Rows[1].Pct);
    }

    // ------------------------------------------------------------------ import, archive, search, templates

    private static (Db Db, SqliteDocumentStore Docs) Stores()
    {
        var db = new Db(Path.Combine(TestData.TempDir(), "smart.db"), "mohamed", "PC1");
        db.EnsureSchema();
        var docs = new SqliteDocumentStore(db);
        docs.EnsureSchema();
        return (db, docs);
    }

    private static async Task<ContractPdfReadResult> ReadResult()
    {
        var page = SchedulePage(new[]
        {
            ("1", "تركيب و تسليم مخرج من النوع PVC 1st Fix لإرتفاع أقل 4.5 متر", "عدد", "4000", "55", "220,000"),
            ("2", "تركيب و تسليم مخرج من النوع PVC 1st Fix لإرتفاع فوق 4.5 متر", "عدد", "400", "57", "22,800"),
            ("11", "سحب سلك 2nd Fix وفي حاله طول النقطه اكثر من 15 متر يتم احتساب نقطه جديده كل 15 متر اضافية", "عدد", "7000", "28", "196,000"),
        });
        var sched = Smart(page, 4, DocTypes.RateSchedule);
        var body = TextPage(ContractBody);
        var doc = new SmartDocument { FileName = "signed.pdf", Sha256 = "abc123", Path = "signed.pdf" };
        doc.Pages.Add(body); doc.Pages.Add(sched);
        var schedule = await ScheduleExtractor.ReadAsync(new[] { sched });
        var b = ContractBodyExtractor.Read(new[] { body });
        var res = new ContractPdfReadResult { Document = doc, Body = b, Schedule = schedule, ContractNo = b.Terms.ContractNo };
        res.PdfItems.AddRange(schedule.ToContractItems(res.ContractNo));
        res.ExcelItems.AddRange(new[]
        {
            new ContractItem { ContractNo = res.ContractNo, ItemNo = "1", Description = "تركيب و تسليم مخرج من النوع PVC 1st Fix لإرتفاع أقل 4.5 متر", Unit = "عدد", Qty = 4000, Rate = 55 },
            new ContractItem { ContractNo = res.ContractNo, ItemNo = "2", Description = "تركيب و تسليم مخرج من النوع PVC 1st Fix لإرتفاع فوق 4.5 متر", Unit = "عدد", Qty = 400, Rate = 58 },
            new ContractItem { ContractNo = res.ContractNo, ItemNo = "3", Description = "بند غير موجود في الملف الموقع", Unit = "عدد", Qty = 10, Rate = 5 },
        });
        ContractPdfImport.Compare(res);
        res.Rules.AddRange(ContractRuleBuilder.Build(b.Terms, b.Clauses, res.PdfItems, b.Payments));
        return res;
    }

    [Fact]
    public async Task Contract_pdf_cross_check_lists_differences_and_needs_every_choice()
    {
        var res = await ReadResult();
        Assert.Contains(res.Diffs, d => d.ItemNo == "2" && d.Field == "RATE" && d.PdfValue == "57" && d.ExcelValue == "58");
        Assert.Contains(res.Diffs, d => d.ItemNo == "3" && d.Field == "ONLY IN EXCEL");
        Assert.Contains(res.Diffs, d => d.ItemNo == "11" && d.Field == "ONLY IN PDF");
        Assert.Throws<InvalidOperationException>(() => ContractPdfImport.Resolve(res));
        res.Diffs.Single(d => d.ItemNo == "2" && d.Field == "RATE").Choice = "EDIT";
        res.Diffs.Single(d => d.ItemNo == "2" && d.Field == "RATE").EditedValue = "57.5";
        ContractPdfImport.AcceptSuggestions(res);
        var items = ContractPdfImport.Resolve(res);
        Assert.Equal(57.5, items.Single(i => i.ItemNo == "2").Rate);
        Assert.Contains(items, i => i.ItemNo == "3");    // ONLY IN EXCEL -> suggested EXCEL -> kept
        Assert.Contains(items, i => i.ItemNo == "11");   // ONLY IN PDF -> kept
    }

    [Fact]
    public async Task Contract_pdf_commit_writes_records_evidence_rules_and_search_index()
    {
        var (db, docs) = Stores();
        db.Insert(new ContractItem { ContractNo = "Mobco-Raffles-SUB-ELE-028-2026", ItemNo = "1", Description = "old", Unit = "عدد", Qty = 1, Rate = 1, AttributesConfirmed = true, FixStage = "1ST FIX", Systems = "POWER" });
        var res = await ReadResult();
        ContractPdfImport.AcceptSuggestions(res);
        var rec = ContractPdfImport.Commit(res, db, docs, "ROOTS", Buildings.Branded, "\\\\share\\contracts\\signed.pdf", "mohamed");
        Assert.True(rec.Id > 0);
        var items = db.All<ContractItem>().Where(i => i.ContractNo == res.ContractNo).ToList();
        Assert.Equal(4, items.Count);
        Assert.True(items.Single(i => i.ItemNo == "1").AttributesConfirmed);            // existing confirmed attributes kept
        Assert.Equal(4000, items.Single(i => i.ItemNo == "1").Qty);
        var c = db.All<Contract>().Single(x => x.ContractNo == res.ContractNo);
        Assert.Equal(new DateTime(2026, 5, 12), c.SignedAt);
        Assert.Single(docs.All<ContractTerms>());
        Assert.Equal(5, docs.All<ContractClause>().Count);
        var rules = docs.All<ContractRule>();
        Assert.Contains(rules, r => r.RuleType == RuleTypes.LengthRule && r.ItemNos == "11");
        Assert.Contains(rules, r => r.RuleType == RuleTypes.HeightBand);
        Assert.Contains(rules, r => r.RuleType == RuleTypes.DelayPenalty && r.Num("perWeek") == 500 && r.ClauseNo == "6");
        Assert.Contains(rules, r => r.RuleType == RuleTypes.PaymentStage && r.Str("stage") == "1ST FIX" && Math.Abs(r.Num("pct") - 0.9) < 1e-9);
        Assert.Contains(rules, r => r.RuleType == RuleTypes.ScopeExclusion);
        Assert.NotEmpty(docs.All<DocField>());

        // full-text search: Arabic word, a code, a number
        Assert.NotEmpty(docs.Search("لاندسكيب"));
        Assert.NotEmpty(docs.Search("SUB-ELE-028"));
        var hit = docs.Search("7051467475").First();
        Assert.Equal("Contract", hit.LinkedTable);
        Assert.Equal(res.ContractNo, hit.LinkedKey);
        Assert.Empty(docs.Search("81064344"));

        // re-reading the same file replaces it (no duplicate evidence rows)
        ContractPdfImport.Commit(res, db, docs, "ROOTS", Buildings.Branded, "x", "mohamed");
        Assert.Single(docs.All<DocRecord>());
        Assert.Single(docs.All<ContractTerms>());
    }

    [Fact]
    public void Archive_indexes_materials_documents()
    {
        var (_, docs) = Stores();
        var t = new DocText { FileName = "dn.pdf" };
        t.Pages.Add(new DocPage { Number = 1, Text = "Delivery Note 81064344 RAF-P.O-E-045-2026 batch 0013289917", Kind = "DN" });
        DocArchive.Save(docs, t, "/nonexistent/dn.pdf", DocTypes.Dn, nameof(MatDn), "81064344");
        var hit = Assert.Single(docs.Search("81064344"));
        Assert.Equal(nameof(MatDn), hit.LinkedTable);
        Assert.Single(docs.Search("1064344"));   // substring of a number (trigram index)
        Assert.Single(docs.Search("de"));         // short query falls back to a scan
    }

    [Fact]
    public async Task Template_learned_on_confirm_reads_the_next_document_without_header()
    {
        var (_, docs) = Stores();
        var res = await ReadResult();
        var t = ContractPdfImport.LearnTemplate(res, docs);
        Assert.Equal(1, t.Confirmations);
        Assert.Equal(6, t.ColumnRanges().Count);
        var t2 = ContractPdfImport.LearnTemplate(res, docs);
        Assert.Equal(2, t2.Confirmations);
        Assert.Single(docs.All<ReadTemplate>());

        // next document, same form, header row cut off and no rulings: the template columns read it
        var next = SchedulePage(new[] { ("7", "تركيب مخرج EMT", "عدد", "1000", "70", "70,000") }, header: false);
        next.Rulings.Clear();
        var read = await ScheduleExtractor.ReadAsync(new[] { Smart(next, 1, DocTypes.RateSchedule) }, null, docs.FindTemplate(DocTypes.RateSchedule, "MOBCO", ""));
        var it = Assert.Single(read.Items);
        Assert.Equal("7", it.ItemNo);
        Assert.Equal(70000, it.TotalValue);
        Assert.True(it.ArithmeticOk);
    }
}
