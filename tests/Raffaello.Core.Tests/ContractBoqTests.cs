using ClosedXML.Excel;
using Raffaello.Core.Boq;

namespace Raffaello.Core.Tests;

/// <summary>Contract BOQ ("Modified BOQ rev 0") print layout and project code matching (03-Oct, real file structure).</summary>
public class ContractBoqTests
{
    private static string Workbook()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cboq_{Guid.NewGuid():N}.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Bill No. 06 - Main Building-BR");
        ws.Cell(1, 1).Value = "Diriyah Gate Company Limited"; ws.Cell(1, 8).Value = "Diriyah Gate Company Limited";
        ws.Cell(6, 2).Value = "SECTION R - ELECTRICAL INSTALLATIONS"; ws.Cell(6, 9).Value = "SECTION R - ELECTRICAL INSTALLATIONS";
        string[] h = { "Ref", "Description", "Quantity", "Unit", "Rate", "Total" };
        for (var i = 0; i < 6; i++) { ws.Cell(8, 1 + i).Value = h[i]; ws.Cell(8, 8 + i).Value = i == 1 ? "" : h[i]; }
        ws.Cell(10, 2).Value = "Supply and installation of sub-main distribution boards";      // wrapped line, original block only
        void Item(int r, string reff, string desc, double q, string u, double rate, object total)
        {
            ws.Cell(r, 1).Value = reff; ws.Cell(r, 2).Value = "generally";
            ws.Cell(r, 8).Value = reff; ws.Cell(r, 9).Value = desc; ws.Cell(r, 10).Value = q; ws.Cell(r, 11).Value = u; ws.Cell(r, 12).Value = rate;
            if (total is double d) ws.Cell(r, 13).Value = d; else ws.Cell(r, 13).Value = total.ToString();
        }
        Item(11, "A", "SMDB-BR-Z1-L00-TN-01, 350A, 17 ways", 1, "Nr", 44165, 44165d);
        Item(12, "B", "Allowance for digital kWh metering", 90, "nr", 2238.5, 201465d);
        Item(13, "C", "Additional earthing points", 0, "nr", 150, "Rate Only");
        ws.Cell(20, 11).Value = "To Collection"; ws.Cell(20, 13).Value = 245630;
        ws.Cell(21, 13).Value = "B6.R / Page 3";
        ws.Cell(22, 8).Value = "Ref"; ws.Cell(22, 10).Value = "Quantity";                       // header repeated on the next page
        Item(24, "A", "EDB-BR-Z1-LB1-03, 40A, TP", 2, "nr", 5000, 10000d);
        ws.Cell(30, 13).Value = "B6.R / Page 4";
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Reads_the_merged_block_with_section_page_and_ref()
    {
        var path = Workbook();
        try
        {
            var res = BoqImporter.Read(path);
            var items = res.Rows.Where(r => !r.IsHeading).ToList();
            Assert.Equal(4, items.Count);
            var a = items[0];
            Assert.Equal(("B6", "R", 3, "A"), (a.Bill, a.Section, a.Page, a.Ref));
            Assert.Equal("SMDB-BR-Z1-L00-TN-01, 350A, 17 ways", a.Description);               // merged description, not "generally"
            Assert.Equal("nr", a.Unit);
            Assert.Equal("B6.R / Page 3 / A", a.PageRef);
            var c = items[2];
            Assert.True(c.RateOnly); Assert.Equal(0, c.Amount); Assert.Equal(150, c.Rate);
            Assert.Equal(4, items[3].Page);
            Assert.DoesNotContain(res.Rows, r => r.Description.Contains("generally"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Matches_project_codes_on_the_page_and_via_a_learned_page_map()
    {
        var rows = new List<BoqLine>
        {
            new() { Bill = "B6", Section = "R", Page = 3, Ref = "A", Description = "SMDB-BR-Z1-L00-TN-01, 350A, 17 ways" },
            // B2: project code pages restart per division - BOQ page R/3 = division 27 page 1
            new() { Bill = "B2", Section = "R", Page = 3, Ref = "C", Description = "EDB-HT-Z1-LB2-03, 40A, TP" },
            new() { Bill = "B2", Section = "R", Page = 3, Ref = "D", Description = "EDB-HT-Z1-LB2-KT-01, 100A, TP" },
            new() { Bill = "B2", Section = "R", Page = 3, Ref = "E", Description = "Supply only spare fuses" },
        };
        var codes = new[]
        {
            new ProjectCodeRef("B6-01-01-00-6-26-A-3", "SMDB-BR-Z1-L00-TN-01, 350A, 17 ways", "nr"),
            new ProjectCodeRef("B2-01-01-00-2-27-C-1", "EDB-HT-Z1-LB2-03, 40A, TP", "nr"),
            new ProjectCodeRef("B2-01-01-00-2-27-D-1", "EDB-HT-Z1-LB2-KT-01, 100A, TP", "nr"),
            new ProjectCodeRef("B2-01-01-00-2-27-E-1", "spare fuses", "nr"),
            new ProjectCodeRef("B2-01-01-00-2-22-C-3", "WC pan", "nr"),                              // wrong division for section R
        };
        var res = ProjectCodeMatcher.Apply(rows, codes);
        Assert.Equal("B6-01-01-00-6-26-A-3", rows[0].BoqCode);
        Assert.Equal("PROJECT CODE", rows[0].CodeSource);
        Assert.Equal("B2-01-01-00-2-27-C-1", rows[1].BoqCode);
        Assert.Equal("PROJECT CODE (PAGE MAP)", rows[1].CodeSource);
        Assert.Equal("B2-01-01-00-2-27-E-1", rows[3].BoqCode);                                     // weak description, but the page map fits
        Assert.Equal(0, res.Unmatched);
    }

    [Fact]
    public void A_project_code_is_given_to_one_item_only()
    {
        var rows = new List<BoqLine>
        {
            new() { Bill = "B6", Section = "R", Page = 1, Ref = "A", Description = "kWh meter digital" },
            new() { Bill = "B6", Section = "R", Page = 1, Ref = "A", Description = "kWh meter" },
        };
        var res = ProjectCodeMatcher.Apply(rows, new[] { new ProjectCodeRef("B6-01-01-00-6-26-A-1", "digital kWh meter", "nr") });
        Assert.Equal(1, res.Exact + res.Shifted);
        Assert.Single(res.Issues);
    }
}
