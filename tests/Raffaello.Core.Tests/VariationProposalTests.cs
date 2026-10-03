using ClosedXML.Excel;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Tests;

/// <summary>MOBCO commercial proposal (03-Oct): omissions negative, compounding markups, layout from EI-04 / EI-13.</summary>
public class VariationProposalTests
{
    [Fact]
    public void Markups_compound_like_ei_07()
    {
        var v = new Variation { MarkupGrPct = 0, MarkupEngPct = 0.08, MarkupOhPct = 0.09, VatPct = 0.15 };
        var lines = new[] { new VariationLine { Kind = VariationLineKinds.Addition, ItemCode = "X", Description = "d", Unit = "item", Qty = 1, Rate = 9_898_846 } };
        var t = VariationMath.Proposal(v, lines);
        Assert.Equal(11_652_921.51, t.ExclVat, 1);          // 9,898,846 + 8% + 9% (sheet shows 11,652,921.74 from unrounded steps)
        Assert.Equal(Math.Round(t.ExclVat * 0.15, 2), t.Vat, 2);
    }

    [Fact]
    public void Omissions_are_negative_and_variance_nets_them()
    {
        var v = new Variation { MarkupGrPct = 0.05, MarkupEngPct = 0.08, MarkupOhPct = 0.09 };
        var lines = new[]
        {
            new VariationLine { Kind = VariationLineKinds.Omission, ItemCode = "B6-01-01-00-6-26-B-12", Description = "VingCard Signature", Unit = "nr", Qty = 109, Rate = 2238.50 },
            new VariationLine { Kind = VariationLineKinds.Addition, ItemCode = "B6-01-01-00-6-26-B-12", Description = "VingCard Essence", Unit = "nr", Qty = 109, Rate = 3155.56 },
        };
        var t = VariationMath.Proposal(v, lines);
        Assert.Equal(-243_996.50, t.Omitted, 2);
        Assert.Equal(343_956.04, t.Additional, 2);
        Assert.Equal(99_959.54, t.Variance, 2);
        Assert.Equal(Math.Round(99_959.54 * 1.05 * 1.08 * 1.09, 0), Math.Round(t.ExclVat, 0));
    }

    [Fact]
    public void Proposal_sheet_groups_by_bill_and_section()
    {
        var v = new Variation { Number = "VO-001", ConsultantRef = "EI-13", Title = "Security descope", LetterRef = "L0135", Date = new DateTime(2026, 4, 15) };
        var lines = new List<VariationLine>
        {
            new() { Kind = VariationLineKinds.Omission, ItemCode = "B6-01-01-00-6-26-U-9", Description = "Luggage scanner", Unit = "nr", Qty = 2, Rate = 400_000, Order = 1 },
            new() { Kind = VariationLineKinds.Omission, ItemCode = "B3-01-01-00-3-26-V-9", Description = "Metal detector", Unit = "nr", Qty = 4, Rate = 61_360, Order = 2 },
        };
        var path = Path.Combine(Path.GetTempPath(), $"prop_{Guid.NewGuid():N}.xlsx");
        try
        {
            VariationExporter.SubmissionExcel(path, v, lines, Array.Empty<VariationDoc>(), new VariationHeaderInfo());
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("PROPOSAL");
            var text = ws.CellsUsed().Select(c => c.GetString()).ToList();
            Assert.Contains("OMITTED ITEMS", text);
            Assert.Contains("Bill No. 03 - Hotel", text);
            Assert.Contains("Bill No. 06 - Main Building - Branded Residences", text);
            Assert.Contains("SECTION R - ELECTRICAL INSTALLATIONS", text);
            Assert.Contains("EI-13 - Security descope (_L0135)", text);
            Assert.Contains("U (p9)", text);
            Assert.DoesNotContain("ADDITIONAL ITEMS", text);
        }
        finally { File.Delete(path); }
    }
}
