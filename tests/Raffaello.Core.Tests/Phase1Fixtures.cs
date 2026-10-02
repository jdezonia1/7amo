using ClosedXML.Excel;
using Raffaello.Core.Contracts;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Tests;

/// <summary>Small synthetic workbooks that mimic the structure of the real project files (no real data).</summary>
internal static class Phase1Fixtures
{
    public const string Contract = "SUB-TEST-001";

    public static string Tracker()
    {
        var path = Path.Combine(TestData.TempDir(), "tracker.xlsm");
        using var wb = new XLWorkbook();
        var rooms = wb.Worksheets.Add("ROOMS");
        var rh = new[] { "LOCATION", "PLOT", "FLOOR", "LEVEL", "UNIT", "UNIT TYPE", "DWG UNIT TYPE", "PLAN" };
        for (var i = 0; i < rh.Length; i++) rooms.Cell(1, i + 1).Value = rh[i];
        object[][] rr =
        {
            new object[] { "P9-101", 9, 1, "Level 01", "101", "1BR-A", "1 Bedroom Unit", "L01" },
            new object[] { "P9-102", 9, 1, "Level 01", "102", "2BR", "2 Bedroom Unit", "L01" },
            new object[] { "P9-BS1", 9, -1, "Basement 1", "BS1", "Public", "", "BS1" },
            new object[] { "P9-GF", 9, 0, "Ground Floor", "GF", "Public", "", "L00" },
        };
        for (var r = 0; r < rr.Length; r++) for (var c = 0; c < rr[r].Length; c++) rooms.Cell(r + 2, c + 1).Value = XLCellValue.FromObject(rr[r][c]);

        var pq = wb.Worksheets.Add("PROJECT QTY");
        pq.Cell(1, 2).Value = "PROJECT QUANTITY - TEST";
        pq.Cell(2, 2).Value = "KEY  (stage|item - read by the tracker, do not edit)";
        var keys = new[] { "1ST FIX|POWER", "1ST FIX|LIGHT", "2ND FIX|POWER", "2ND FIX|LIGHT", "CEILING|LIGHT" };
        for (var i = 0; i < keys.Length; i++) { pq.Cell(2, 12 + i).Value = keys[i]; pq.Cell(4, 12 + i).Value = "no"; }
        var ph = new[] { "PLOT", "FLOOR", "LEVEL", "UNIT No.", "LOCATION", "UNIT TYPE", "QTY SOURCE", "TOTAL No.", "TRAY m" };
        for (var i = 0; i < ph.Length; i++) pq.Cell(6, 2 + i).Value = ph[i];
        object[][] pqRows =
        {
            new object[] { 9, 1, "Level 01", "101", "P9-101", "1BR-A", "TAKEOFF", 0, 0, 0, 40, 50, 40, 50, 0 },
            new object[] { 9, 1, "Level 01", "102", "P9-102", "2BR", "TAKEOFF", 0, 0, 0, 60, 70, 60, 70, 4 },
            new object[] { 9, -1, "Basement 1", "BS1", "P9-BS1", "Public", "PUBLIC", 0, 0, 0, 10, 30, 10, 30, 0 },
        };
        for (var r = 0; r < pqRows.Length; r++) for (var c = 0; c < pqRows[r].Length; c++) pq.Cell(7 + r, 2 + c).Value = XLCellValue.FromObject(pqRows[r][c]);

        var led = wb.Worksheets.Add("LEDGER");
        led.Cell(1, 1).Value = "LEDGER  -  every claim line, every subcontractor";
        var lh = new[] { "SUBCONTRACTOR", "INVOICE", "STAGE", "FLOOR", "LOCATION", "ITEM", "UNIT", "QTY", "SITE %", "QTY AFTER SITE %", "WIR %", "QTY AFTER WIR %", "RATE", "INVOICE AMOUNT", "NOTES" };
        for (var i = 0; i < lh.Length; i++) led.Cell(2, i + 1).Value = lh[i];
        object[][] lr =
        {
            new object[] { "SUBA", 1, "1ST FIX", "Level 01", "P9-101", "POWER", "no", 30, 1, 30, 1, 30, 0, 0, "" },
            new object[] { "SUBA", 1, "1ST FIX", "Level 01", "P9-101", "LIGHT", "no", 50, 1, 50, 0.8, 40, 0, 0, "" },
            new object[] { "SUBB", 1, "1ST FIX", "Level 01", "P9-101", "POWER", "no", 15, 1, 15, 1, 15, 0, 0, "over by 5" },
            new object[] { "SUBA", 2, "2ND FIX", "Level 01", "P9-102", "POWER", "no", 60, 0.5, 30, 0.5, 15, 0, 0, "" },
            new object[] { "SUBA", 2, "1ST FIX", "Basement 1", "P9-BS1", "LIGHT", "no", 20, 1, 20, 1, 20, 0, 0, "" },
        };
        for (var r = 0; r < lr.Length; r++) for (var c = 0; c < lr[r].Length; c++) led.Cell(3 + r, c + 1).Value = XLCellValue.FromObject(lr[r][c]);
        wb.SaveAs(path);
        return path;
    }

    public static readonly (string No, string Desc, string Unit, double Qty, double Rate, string[] Codes)[] ContractItems =
    {
        ("1", "install, and hand over a PVC 1st Fix outlet for a lighting switch, power socket, or wall-mounted lighting point, in accordance with good engineering practice. Rate shall include wall chasing/cutting for installation at heights below 4.5 m.", "No.", 1000, 55,
            new[] { "B6-01-01-00-6-26-V-5", "B6-01-01-00-6-26-AS-6", "B6-01-01-00-6-26-AT-6", "B6-01-01-00-6-26-AW-6", "B6-01-01-00-6-26-AZ-6", "B6-01-01-00-6-26-A-8" }),
        ("2", "install, and hand over a PVC 1st Fix outlet for a lighting switch, power socket, or wall-mounted lighting point, in accordance with good engineering practice. Rate shall include wall chasing/cutting for installation at heights above 4.5 m.", "No.", 100, 57,
            new[] { "B6-01-01-00-6-26-V-5", "B6-01-01-00-6-26-AW-6" }),
        ("3", "install, and hand over a PVC 1st Fix outlet for a lighting switch, power socket, or ceiling lighting point, in accordance with good engineering practice. Rate shall include wall chasing/cutting for installation at heights below 4.5 m.", "No.", 300, 55,
            new[] { "B6-01-01-00-6-26-V-5", "B6-01-01-00-6-26-AW-6" }),
        ("11", "Wire pulling and termination for 2nd Fix works for a lighting switch, power socket, or wall/ceiling lighting point, for heights below 4.5 m. If a point is longer than 15 m an extra point is counted for every additional 15 m.", "No.", 1500, 28,
            new[] { "B6-01-01-00-6-26-V-5", "B6-01-01-00-6-26-AW-6", "B6-01-01-00-6-26-AS-6" }),
        ("182", "install, and hand over a PVC 1st Fix outlet for data, telephone, TV, or CCTV system, ceiling-mounted, below 4.5 m.", "No.", 50, 55,
            new[] { "B6-01-01-00-6-26-S-10", "B6-01-01-00-6-26-T-11", "B6-01-01-00-6-26-U-11" }),
        ("183", "install, and hand over a PVC 1st Fix outlet for data, telephone, TV, or CCTV system, wall-mounted, below 4.5 m.", "No.", 50, 55,
            new[] { "B6-01-01-00-6-26-S-10", "B6-01-01-00-6-26-T-11", "B6-01-01-00-6-26-U-11" }),
        ("308", "Installation, connection, testing, and handover of electrical panel (42 ways) – labor only.", "No.", 5, 1076, Array.Empty<string>()),
    };

    public static readonly Dictionary<string, string> BoqDescriptions = new()
    {
        ["B6-01-01-00-6-26-V-5"] = "small power points",
        ["B6-01-01-00-6-26-AS-6"] = "lighting points, to BOH",
        ["B6-01-01-00-6-26-AT-6"] = "lighting points, to FOH - number",
        ["B6-01-01-00-6-26-AW-6"] = "lighting points, to apartment - number",
        ["B6-01-01-00-6-26-AZ-6"] = "lighting points, to balcony",
        ["B6-01-01-00-6-26-A-8"] = "to emergency lighting point",
        ["B6-01-01-00-6-26-S-10"] = "to CCTV camera points",
        ["B6-01-01-00-6-26-T-11"] = "final sub-circuit for IT / Telecoms devices",
        ["B6-01-01-00-6-26-U-11"] = "allowance for IPTV system",
    };

    /// <summary>English link workbook: 'Sr.' header, section rows, item rows followed by BOQ code rows.</summary>
    public static string LinkWorkbook(bool withDescriptions = false)
    {
        var path = Path.Combine(TestData.TempDir(), "contract_links.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Residence");
        ws.Cell(3, 1).Value = "Sr."; ws.Cell(3, 2).Value = "Item Description"; ws.Cell(3, 3).Value = "UNIT"; ws.Cell(3, 4).Value = "QTY"; ws.Cell(3, 5).Value = "Unit Rate"; ws.Cell(3, 6).Value = "Total";
        var r = 4;
        ws.Cell(r++, 2).Value = "Lighting and Power";
        foreach (var it in ContractItems)
        {
            if (it.No == "308") ws.Cell(r++, 2).Value = "Panels";
            ws.Cell(r, 1).Value = it.No; ws.Cell(r, 2).Value = it.Desc; ws.Cell(r, 3).Value = it.Unit; ws.Cell(r, 4).Value = it.Qty.ToString(); ws.Cell(r, 5).Value = it.Rate.ToString();
            r++;
            foreach (var c in it.Codes)
            {
                ws.Cell(r, 2).Value = c;
                if (withDescriptions) ws.Cell(r, 7).Value = BoqDescriptions[c];
                r++;
            }
        }
        wb.SaveAs(path);
        return path;
    }

    /// <summary>Invoice workbook: 'E promise - Resource' budget list + 'TEST INV 1' template layout.</summary>
    public static string InvoiceWorkbook()
    {
        var path = Path.Combine(TestData.TempDir(), "invoice_template.xlsx");
        using var wb = new XLWorkbook();
        var inv = wb.Worksheets.Add("TEST INV 1");
        inv.Cell("L2").Value = "Vendor Name "; inv.Cell("M2").Value = "SUBA";
        inv.Cell("C11").Value = "BOQ item No ";
        inv.Cell("F14").Value = "1-A- 90% payment shall be released with the progress payment certificate.";
        var r = 15;
        foreach (var it in ContractItems)
        {
            if (it.No == "308") { inv.Cell(r++, 3).Value = "Panels"; }
            var codes = it.Codes.Length == 0 ? new[] { "B6-01-01-00-6-26-AL-2" } : it.Codes;
            foreach (var c in codes)
            {
                inv.Cell(r, 2).Value = int.Parse(it.No); inv.Cell(r, 3).Value = c; inv.Cell(r, 4).Value = "160100"; inv.Cell(r, 5).Value = "41080301";
                inv.Cell(r, 6).Value = it.Desc; inv.Cell(r, 7).Value = it.Unit; inv.Cell(r, 8).Value = it.Qty / codes.Length; inv.Cell(r, 9).Value = it.Rate;
                inv.Cell(r, 10).Value = it.No == "308" ? 0.7 : 0.9; inv.Cell(r, 11).FormulaA1 = $"J{r}*I{r}*H{r}";
                r++;
            }
        }
        inv.Cell(r, 3).Value = " Subcontract Value";
        inv.Cell(r, 11).FormulaA1 = $"SUM(K15:K{r - 1})";

        var ep = wb.Worksheets.Add("E promise - Resource");
        var eh = new[] { "Job No", "Bill No.", "Section", "Page No.", "Rev No.", "Item No", "BOQ No", "Description ", "WBS", "Activity", "Budget Resource Code", "Budget Resource" };
        for (var i = 0; i < eh.Length; i++) ep.Cell(1, i + 1).Value = eh[i];
        var er = 2;
        foreach (var (code, desc) in BoqDescriptions.Append(new("B6-01-01-00-6-26-AL-2", "DB-TEST-01, 40A")))
        {
            var p = code.Split('-');
            ep.Cell(er, 1).Value = "P0000"; ep.Cell(er, 2).Value = p[0]; ep.Cell(er, 3).Value = p[1]; ep.Cell(er, 4).Value = p[2]; ep.Cell(er, 5).Value = p[3];
            ep.Cell(er, 6).Value = string.Join('-', p.Skip(4));
            ep.Cell(er, 7).FormulaA1 = $"CONCATENATE(B{er},\"-\",C{er},\"-\",D{er},\"-\",E{er},\"-\",F{er})";
            ep.Cell(er, 8).Value = desc; ep.Cell(er, 9).Value = "01"; ep.Cell(er, 10).Value = "160100"; ep.Cell(er, 11).Value = "41080301"; ep.Cell(er, 12).Value = "Apply";
            er++;
        }
        // a repeated budget row (same code twice)
        for (var c = 1; c <= 12; c++) ep.Cell(er, c).Value = ep.Cell(2, c).Value;
        ep.Cell(er, 7).FormulaA1 = $"CONCATENATE(B{er},\"-\",C{er},\"-\",D{er},\"-\",E{er},\"-\",F{er})";
        wb.SaveAs(path);
        return path;
    }

    public static ContractItem Item(string no, string desc, double rate = 55, string unit = "No.")
    {
        var i = new ContractItem { ContractNo = Contract, ItemNo = no, Description = desc, Rate = rate, Unit = unit, Order = int.TryParse(no, out var n) ? n : 0 };
        ContractAttributeParser.Apply(i, ContractAttributeParser.Parse(desc, unit));
        return i;
    }
}
