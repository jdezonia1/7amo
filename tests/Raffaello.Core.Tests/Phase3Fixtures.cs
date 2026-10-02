using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Materials;
using Raffaello.Core.Settings;

namespace Raffaello.Core.Tests;

/// <summary>Synthetic supplier documents (layout text as the PDF text layer produces it). No company files.</summary>
internal static class Phase3Fixtures
{
    public const string PoNo = "ABC-P.O-E-001-2026";

    public static readonly string[] PoText =
    {
        "                    ABC-P.O-E-001-2026",
        "        PR Scope of Work: 100200300",
        "        Lumpsum    ☐       Re-Measurable  ☒",
        "        Advance: 10%       Exclude VAT In Advance: Yes ☐ No☒",
        "        Retention: 5%     Exclude VAT In Retention: Yes ☒ No☐",
        "        Tolerance: 0%",
        "        From:        MOBCO Test Construction                 To:          Test Cables Co. (C.R 1234567)",
        "        Project:     Test Hotel                              SOW          Supply of LV Cables",
        "        P.O Ref:     ABC-P.O-E-001-2026                      Att:         Someone",
        "        Date:        26-Apr-26                               Quote. Ref:",
        "           No.                       Description                       Unit          Qty       Unit Price       Total Price",
        "           01      4X10 CU/XLPE/SWA/LSOH                               MT.        1000.00      SAR 28.432     SAR 28,432.00",
        "           02      4X16 CU/MICA/XLPE/SWA/LSOH                          MT.         500.00      SAR 47.242     SAR 23,621.00",
        "           03      PVC CONDUIT 25MM HEAVY GAUGE                        MT.         600.00      SAR 1.667      SAR 1,000.20",
        "             Total Price                     (arabic words)  SAR 53,053.20",
        "              VAT 15%                        (arabic words)  SAR 7,957.98",
        "          Grand Total Price                  (arabic words)  SAR 61,011.18",
        "      Payment  Terms",
        "       1)100% Before Delivery by LC.",
        "      Penalty",
        "       2% Per week ,Maximum  10% from Total P.O .",
        "      Delivery",
        "      6-8 weeks from PO date.",
        "      General Conditions",
        "      6- During supply, a tolerance of plus or minus 5 % of the cable quantity is allowed for each item.",
        "Sr # Resource Code            Resource Name                                           Boq #                     Unit      Requested Qty          Approved Qty",
        "1      120101011063           4x10mm2 CU/XLPE/SWA/LSZH                                B2-01-01-00-2-27-         Mtr               600.000                 600.000",
        "                              11- 4x10mm2 CU/XLPE/SWA/LSZH                            R-2",
        "2      120101011063           4x10mm2 CU/XLPE/SWA/LSZH                                B6-01-01-00-6-26-         Mtr               400.000                 400.000",
        "                              4x10mm2 CU/XLPE/SWA/LSZH (FIRE RATED) D-4",
    };

    public static readonly string[] DnText =
    {
        "                                                                            Delivery    Note",
        "                                                                                      81000001",
        "       Aug 15, 2026   ABC-P.O-E-001-2026    Apr 15, 2026       40000001        Apr 16, 2026     10000001",
        "          Plant       REW                    Truck No.     1234 ABC            Transporter    XYZ",
        "     Item   Item Code Description                             S.Loc DN      Batch     Quantity  Cust. Mat",
        "    000110 10000337   4X10mm²RM_CU/XL/SW/HF_1kV_B04_BK_ST     3110  001  0013289917     0.600 KM",
        "                      D",
        "                                                                        Sub-Total    0.600   KM",
        "    000120 10000338   4X16mm²RC_CU/MICA/XL/SW/HF_1kV_B04     3110  001  0013289918     0.300 KM",
        "                                                                        Sub-Total    0.300   KM",
        "         Tel: 966-11-0000000             Email: sales@test-cables.com",
    };

    public static readonly string[] MirText =
    {
        " Doc No:               DG-XXX-261-0410-MOB-MIR-EL-000999",
        " Revision:             00",
        " Date:                 18-Aug-2026",
        "(MIR) Material Inspection Request",
        " Material Approval Request Submittal Ref:                           DG-XXX-261-0410-MOB-MAT-EL-000071",
        " Item                                                               Description                                                            Quantity           Unit",
        " 1         MIR for LV cables - Test Cables                                    As per DN        Pcs",
        " Delivery Note No: 81000001   PO ABC-P.O-E-001-2026",
    };

    public static DocText Doc(string name, params string[][] pages)
    {
        var d = new DocText { FileName = name };
        for (var i = 0; i < pages.Length; i++)
            d.Pages.Add(new DocPage { Number = i + 1, Text = string.Join("\n", pages[i]), Source = TextSource.TextLayer, HasTextLayer = true, WordCount = 100 });
        return d;
    }

    public static string[] QtyListPage => new[]
    {
        "NO    SIZE       DESCRIPTION           QTY/M",
        " 1   4X10     CU/XLPE/SWA/LSOH        1000",
        " 2   4X16   CU/MICA/XLPE/SWA/LSOH      500",
        " 3   1X10        CU/LSOH (G/Y)        2000",
    };

    public sealed class Env : IDisposable
    {
        public required Db Db { get; init; }
        public required SqliteMaterialsStore Store { get; init; }
        public required ProjectService Project { get; init; }
        public required MaterialsService Service { get; init; }
        public string Dir { get; init; } = "";
        public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>Empty project (no demo seed) with the phase-3 tables in the same file.</summary>
    public static Env NewEnv()
    {
        var dir = TestData.TempDir();
        var settings = new AppSettings { DataFilePath = Path.Combine(dir, "p3.db"), SeedDemoData = false, UserName = "tester" };
        var project = new ProjectService(settings);
        project.Initialize();
        var store = new SqliteMaterialsStore(() => (Db)project.Store);
        var svc = new MaterialsService(project, store, new MaterialsSettings());
        svc.Reload();
        return new Env { Db = (Db)project.Store, Store = store, Project = project, Service = svc, Dir = dir };
    }

    /// <summary>Saves the synthetic PO (coded from its scope sheet) and returns it.</summary>
    public static MatPo SavePo(Env env)
    {
        var po = PoReader.Parse(Doc("po.pdf", PoText));
        env.Service.CodePoLines(po.Value);
        return env.Service.CommitPo(po.Value);
    }

    public static MatDn SaveDn(Env env, string dnNo = "81000001", double qty10Km = 0.6, string date = "Aug 15, 2026", string batch = "0013289917")
    {
        var lines = DnText.Select(l => l.Replace("81000001", dnNo).Replace("0.600", qty10Km.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("Aug 15, 2026", date).Replace("0013289917", batch).Replace("0013289918", batch + "B")).ToArray();
        var dn = DnReader.Parse(Doc("dn.pdf", lines));
        return env.Service.CommitDn(dn.Value);
    }

    /// <summary>A real PDF (QuestPDF) with a DN table, to test the PdfPig text layer + layout reconstruction end to end.</summary>
    public static string DnPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var path = Path.Combine(TestData.TempDir(), "dn-synthetic.pdf");
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape()); page.Margin(30); page.DefaultTextStyle(x => x.FontSize(9));
            page.Content().Column(col =>
            {
                col.Item().AlignRight().Text("Delivery Note");
                col.Item().AlignRight().Text("81000777");
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.ConstantColumn(80); c.ConstantColumn(110); c.ConstantColumn(90); c.ConstantColumn(80); c.ConstantColumn(90); c.ConstantColumn(80); });
                    foreach (var s in new[] { "Aug 15, 2026", "ABC-P.O-E-001-2026", "Apr 15, 2026", "40000002", "Apr 16, 2026", "10000001" }) t.Cell().Text(s);
                });
                col.Item().PaddingTop(10).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.ConstantColumn(50); c.ConstantColumn(65); c.ConstantColumn(260); c.ConstantColumn(40); c.ConstantColumn(30); c.ConstantColumn(80); c.ConstantColumn(50); c.ConstantColumn(30); });
                    foreach (var s in new[] { "Item", "Item Code", "Description", "S.Loc", "DN", "Batch", "Quantity", "" }) t.Cell().Text(s);
                    foreach (var s in new[] { "000110", "10000337", "4X10mm²RM_CU/XL/SW/HF_1kV_B04", "3110", "001", "0013289999", "0.250", "KM" }) t.Cell().Text(s);
                    foreach (var s in new[] { "000120", "10000338", "4X16mm²RC_CU/MICA/XL/SW/HF_1kV", "3110", "001", "0013289998", "0.125", "KM" }) t.Cell().Text(s);
                });
                col.Item().PaddingTop(20).Text("Email: rcgc@test-cables.com");
            });
        })).GeneratePdf(path);
        return path;
    }

    /// <summary>Owner BOQ workbook with headings, EN and AR descriptions.</summary>
    public static string OwnerBoq()
    {
        var path = Path.Combine(TestData.TempDir(), "owner-boq.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("BILL 6");
        ws.Cell(1, 1).Value = "OWNER BOQ - TEST";
        var h = new[] { "ITEM", "BOQ CODE", "DESCRIPTION", "UNIT", "QTY", "RATE", "AMOUNT" };
        for (var i = 0; i < h.Length; i++) ws.Cell(3, i + 1).Value = h[i];
        object[][] rows =
        {
            new object[] { "", "", "LV CABLES", "", "", "", "" },
            new object[] { "1", "B6-01-01-00-6-26-D-4", "4C 10mm2 Cu/XLPE/SWA/LSZH + 1C 10mm2 Cu/LSF", "m", 1000, 40, 40000 },
            new object[] { "2", "B6-01-01-00-6-26-C-4", "4C 16mm2 Cu/XLPE/SWA/LSZH + 1C 16mm2 Cu/LSF", "m", 500, 55, 27500 },
            new object[] { "", "", "FIRE ALARM SYSTEM", "", "", "", "" },
            new object[] { "3", "B6-01-01-00-6-28-A-1", "Addressable smoke detector complete", "no", 50, 300, 15000 },
            new object[] { "4", "B6-01-01-00-6-28-A-2", "Manual call point", "no", 10, 250, 2500 },
            new object[] { "5", "B6-01-01-00-6-29-A-1", "مقبس مزدوج 13 أمبير", "no", 20, 90, 1800 },
            new object[] { "6", "B6-01-01-00-6-29-A-2", "Cable tray 300mm hot dip galvanised", "m", 100, 120, 12000 },
            new object[] { "7", "B6-01-01-00-6-29-A-3", "Sundry item for the works", "item", 1, 500, 999 },
        };
        for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) ws.Cell(r + 4, c + 1).Value = XLCellValue.FromObject(rows[r][c]);
        wb.SaveAs(path);
        return path;
    }
}
