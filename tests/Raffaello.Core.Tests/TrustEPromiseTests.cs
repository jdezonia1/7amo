using ClosedXML.Excel;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Integrations.EPromise;

namespace Raffaello.Core.Tests;

/// <summary>[trust] Certified invoice -> E-Promise (ERP) import layout.</summary>
public sealed class TrustEPromiseTests
{
    private static ProjectSnapshot Snapshot(string status = SubInvoiceStatus.Approved, bool locked = true)
    {
        var inv = new SubInvoice { Id = 1, Subcontractor = "SUB-A", ContractNo = "SUB-ELE-001-2026", InvoiceNo = 3, Revision = 1, Status = status, Locked = locked };
        return new ProjectSnapshot
        {
            SubInvoices = new() { inv },
            Contracts = new() { new Contract { ContractNo = "SUB-ELE-001-2026", Subcontractor = "SUB-A", VendorNo = "V-1001" } },
            BoqItems = new()
            {
                new BoqItem { ItemCode = "B6-01-01-00-6-26-V-5", Bill = "B6", Job = "J-0001", Description = "Lighting points to apartment", Wbs = "WBS-1", CostCode = "E-210", BudgetResourceCode = "R-55", BudgetResource = "Labour - electrical" },
                new BoqItem { ItemCode = "B6-01-02-00-7-1", Bill = "B6", Job = "J-0001", Description = "Small power points", Wbs = "WBS-2", CostCode = "E-220", BudgetResourceCode = "R-56", BudgetResource = "Labour - electrical" },
            },
            SubInvoiceLines = new()
            {
                new SubInvoiceLine { SubInvoiceId = 1, RowOrder = 1, Kind = "SECTION", Description = "LIGHTING" },
                new SubInvoiceLine { SubInvoiceId = 1, RowOrder = 2, ItemNo = "12", BoqCode = "B6-01-01-00-6-26-V-5", Description = "Lighting point 2nd fix", Unit = "No", Rate = 55, StagePct = 0.9, CurrQty = 40, PrevQty = 10, CumQty = 50 },
                new SubInvoiceLine { SubInvoiceId = 1, RowOrder = 3, ItemNo = "13", BoqCode = "B6-01-01-00-6-26-V-5", Description = "Lighting point 1st fix", Unit = "No", Rate = 30, StagePct = 0.9, CurrQty = 20, CumQty = 20 },
                new SubInvoiceLine { SubInvoiceId = 1, RowOrder = 4, ItemNo = "20", BoqCode = "B6-01-02-00-7-1", CostCode = "E-999", Description = "Power point", Unit = "No", Rate = 60, StagePct = 0.7, CurrQty = 0, CumQty = 5 },
                new SubInvoiceLine { SubInvoiceId = 1, RowOrder = 5, ItemNo = "99", BoqCode = "", Description = "No code", Unit = "No", Rate = 10, StagePct = 1, CurrQty = 2, CumQty = 2 },
            },
        };
    }

    [Fact]
    public void Splits_the_boq_code_and_fills_erp_codes_from_the_budget_list()
    {
        var s = Snapshot();
        var res = EPromiseExporter.Build(s, s.SubInvoices[0], new EPromiseExportConfig());
        Assert.False(res.Blocked);
        Assert.Equal(3, res.Rows.Count);   // zero current qty skipped, section rows never exported
        var r = res.Rows[0];
        Assert.Equal("J-0001", r[EPromiseFields.JobNo]);
        Assert.Equal("B6", r[EPromiseFields.Bill]);
        Assert.Equal("01", r[EPromiseFields.Section]);
        Assert.Equal("01", r[EPromiseFields.Page]);
        Assert.Equal("00", r[EPromiseFields.Rev]);
        Assert.Equal("6-26-V-5", r[EPromiseFields.Item]);
        Assert.Equal("E-210", r[EPromiseFields.Activity]);
        Assert.Equal("R-55", r[EPromiseFields.BudgetResourceCode]);
        Assert.Equal("Lighting points to apartment", r[EPromiseFields.BoqDescription]);
        Assert.Equal(40.0, r[EPromiseFields.Qty]);
        Assert.Equal(1980.0, r[EPromiseFields.Amount]);   // 40 x 55 x 0.9
        Assert.Equal("SUB-ELE-001-2026 INV-03 Rev 1", r[EPromiseFields.InvoiceRef]);
        Assert.Equal(1980 + 540 + 20, res.TotalAmount);
        Assert.Contains(res.Issues, i => i.Contains("no project code"));
    }

    [Fact]
    public void Grouping_by_boq_code_sums_quantities_and_amounts()
    {
        var s = Snapshot();
        var res = EPromiseExporter.Build(s, s.SubInvoices[0], new EPromiseExportConfig { GroupByBoqCode = true });
        var lighting = res.Rows.Single(r => (string?)r[EPromiseFields.BoqCode] == "B6-01-01-00-6-26-V-5");
        Assert.Equal(60.0, lighting[EPromiseFields.Qty]);
        Assert.Equal(2520.0, lighting[EPromiseFields.Amount]);
        Assert.Equal(42.0, lighting[EPromiseFields.Rate]);   // amount / qty
        Assert.Equal("12, 13", lighting[EPromiseFields.ItemNo]);
    }

    [Fact]
    public void Cumulative_basis_and_line_codes_override_the_budget_list()
    {
        var s = Snapshot();
        var res = EPromiseExporter.Build(s, s.SubInvoices[0], new EPromiseExportConfig { QtyBasis = "CUMULATIVE" });
        var power = res.Rows.Single(r => (string?)r[EPromiseFields.BoqCode] == "B6-01-02-00-7-1");
        Assert.Equal(5.0, power[EPromiseFields.Qty]);
        Assert.Equal("E-999", power[EPromiseFields.CostCode]);
        Assert.Equal(210.0, power[EPromiseFields.Amount]);
    }

    [Fact]
    public void Uncertified_revisions_are_blocked_unless_forced()
    {
        var s = Snapshot(SubInvoiceStatus.Submitted, locked: false);
        var res = EPromiseExporter.Build(s, s.SubInvoices[0], new EPromiseExportConfig());
        Assert.True(res.Blocked);
        Assert.Empty(res.Rows);
        Assert.Throws<InvalidOperationException>(() => EPromiseExporter.WriteCsv(Path.Combine(TestData.TempDir(), "x.csv"), res, new EPromiseExportConfig()));
        Assert.NotEmpty(EPromiseExporter.Build(s, s.SubInvoices[0], new EPromiseExportConfig(), force: true).Rows);
    }

    [Fact]
    public void Writes_xlsx_and_csv_in_the_configured_layout()
    {
        var s = Snapshot();
        var cfg = new EPromiseExportConfig
        {
            Columns = new()
            {
                new("JOB NO", EPromiseFields.JobNo), new("BOQ", EPromiseFields.BoqCode), new("SECTION", EPromiseFields.Section),
                new("QTY", EPromiseFields.Qty), new("AMOUNT", EPromiseFields.Amount), new("SOURCE", EPromiseFields.Constant, "RAFFAELLO"),
                new("DESC", EPromiseFields.Description),
            },
        };
        Assert.Empty(cfg.Validate());
        var res = EPromiseExporter.Build(s, s.SubInvoices[0], cfg);
        var dir = TestData.TempDir();
        var xlsx = Path.Combine(dir, "erp.xlsx");
        EPromiseExporter.WriteXlsx(xlsx, res, cfg);
        using (var wb = new XLWorkbook(xlsx))
        {
            var ws = wb.Worksheet(1);
            Assert.Equal("E promise - Resource", ws.Name);
            Assert.Equal("JOB NO", ws.Cell(1, 1).GetString());
            Assert.Equal("01", ws.Cell(2, 3).GetString());    // kept as text
            Assert.Equal(1980, ws.Cell(2, 5).GetDouble());
            Assert.Equal("RAFFAELLO", ws.Cell(2, 6).GetString());
            Assert.Equal(4, ws.LastRowUsed()!.RowNumber());
        }
        var csv = Path.Combine(dir, "erp.csv");
        EPromiseExporter.WriteCsv(csv, res, cfg);
        var lines = File.ReadAllLines(csv);
        Assert.Equal("JOB NO,BOQ,SECTION,QTY,AMOUNT,SOURCE,DESC", lines[0].TrimStart('﻿'));
        Assert.Equal("J-0001,B6-01-01-00-6-26-V-5,01,40,1980,RAFFAELLO,Lighting point 2nd fix", lines[1]);

        // the mapping file round-trips
        var path = Path.Combine(dir, "epromise-export.json");
        cfg.Save(path);
        var back = EPromiseExportConfig.Load(path);
        Assert.Equal("RAFFAELLO", back.Columns[5].Constant);
        Assert.Contains(new EPromiseExportConfig { Columns = new() { new("X", "Nope") } }.Validate(), p => p.Contains("unknown field"));
    }
}
