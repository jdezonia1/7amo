using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Ledger;
using Raffaello.Core.Materials;
using Raffaello.Core.Plans;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Reports;

/// <summary>Everything the project reports read (modules that are not available simply stay empty).</summary>
public sealed class ReportInputs
{
    public required ProjectSnapshot Project { get; init; }
    public MaterialsSnapshot? Materials { get; init; }
    public MaterialsSettings? MaterialsSettings { get; init; }
    public List<Variation> Variations { get; init; } = new();
    public List<VariationLine> VariationLines { get; init; } = new();
    public List<StatusBoardRow> InvoiceBoard { get; init; } = new();
    public string? Building { get; init; }
    public DateTime AsOf { get; init; } = DateTime.Today;
}

public sealed record ReportDefinition(string Key, string Name, string Description);

/// <summary>
/// [phase6] The management reports, from real data: weekly progress, subcontractor scorecards, cash flow, materials status,
/// VO register and the invoice status board. Each report is a list of house-style sheets (Excel: #A6A6A6 headers, bold black)
/// and the same sheets render to PDF.
/// </summary>
public static class ProjectReports
{
    public static readonly ReportDefinition[] All =
    {
        new("WEEKLY", "Weekly progress", "PROJECT QTY vs claimed per stage, system and level; rooms by status; checks pending"),
        new("SCORECARDS", "Subcontractor scorecards", "Claims, over-cap keys, OVER lines, >4.5 m and 15 m claims accepted / rejected, invoices"),
        new("CASHFLOW", "Cash flow", "Claimed vs certified per subcontractor and per month"),
        new("MATERIALS", "Materials status", "PO delivered %, DNs without MIR, MIR status, 3-way match exceptions"),
        new("VO", "VO register", "Variations / EIs with status, value and ageing"),
        new("INVOICES", "Invoice status board", "Every open invoice revision with its Aconex step, with whom, due date"),
    };

    public static List<ExportSheet> Build(string key, ReportInputs i) => key switch
    {
        "WEEKLY" => WeeklyProgress(i),
        "SCORECARDS" => Scorecards(i),
        "CASHFLOW" => CashFlow(i),
        "MATERIALS" => MaterialsStatus(i),
        "VO" => VoRegister(i),
        "INVOICES" => new List<ExportSheet> { StatusBoard.ToSheet(i.InvoiceBoard, i.AsOf) },
        _ => throw new ArgumentException($"Unknown report {key}"),
    };

    private static bool In(ReportInputs i, string? building) => i.Building is null || string.IsNullOrEmpty(building) || string.Equals(building, i.Building, StringComparison.OrdinalIgnoreCase);
    private static string Scope(ReportInputs i) => $"{i.Building ?? "ALL BUILDINGS"}  |  as of {i.AsOf:dd MMM yyyy}";

    // ------------------------------------------------------------------ weekly progress

    public static List<ExportSheet> WeeklyProgress(ReportInputs i)
    {
        var s = i.Project;
        var rooms = s.Rooms.Where(r => In(i, r.Building)).Select(r => r.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claims = s.Claims.Where(c => In(i, c.Building)).ToList();
        var qty = s.RoomQtys.Where(q => In(i, q.Building)).ToList();
        var bal = LedgerRules.Balances(qty, claims);
        var latestInv = claims.Where(c => c.InvoiceNo > 0).Select(c => c.InvoiceNo).DefaultIfEmpty(0).Max();
        ExportSheet Group(string name, Func<RoomBalance, string> key) => new()
        {
            Name = name, Title = $"WEEKLY PROGRESS - {name}", Subtitle = Scope(i) + "  |  stages are never added together",
            Columns = new() { new(name == "BY STAGE" ? "STAGE" : name == "BY SYSTEM" ? "STAGE | SYSTEM" : "LEVEL | STAGE"), new("PROJECT QTY", ColumnKind.Number), new("CLAIMED (ALL SUBS)", ColumnKind.Number),
                new("WITHIN CAP", ColumnKind.Number), new("REMAINING", ColumnKind.Number), new("% USED", ColumnKind.Percent), new("KEYS OVER CAP", ColumnKind.Integer) },
            Rows = bal.Values.GroupBy(key).OrderBy(g => g.Key).Select(g =>
            {
                var cap = g.Sum(b => b.ProjectQty);
                var within = g.Where(b => b.HasCap).Sum(b => Math.Clamp(b.Claimed, 0, b.ProjectQty));
                return new object?[] { g.Key, cap, g.Sum(b => b.Claimed), within, cap - within, cap <= 0 ? 0 : within / cap, g.Count(b => b.HasCap && b.IsOver) };
            }).ToList(),
        };
        var levelOf = s.Rooms.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Level, StringComparer.OrdinalIgnoreCase);
        var info = RoomStatusCalc.Compute(new ProjectSnapshot { Rooms = s.Rooms.Where(r => In(i, r.Building)).ToList(), RoomQtys = qty, Claims = claims });
        var status = new ExportSheet
        {
            Name = "ROOMS", Title = "ROOMS BY STATUS", Subtitle = Scope(i),
            Columns = new List<ExportColumn> { new("LEVEL") }.Concat(RoomStatusKinds.All.Select(k => new ExportColumn(k, ColumnKind.Integer))).Append(new ExportColumn("ROOMS", ColumnKind.Integer)).ToList(),
            Rows = info.Values.Where(x => x.Room != null).GroupBy(x => x.Room!.Level).OrderBy(g => g.Key)
                .Select(g => new object?[] { g.Key }.Concat(RoomStatusKinds.All.Select(k => (object?)g.Count(x => x.Status == k))).Append(g.Count()).ToArray()).ToList(),
        };
        var thisInv = new ExportSheet
        {
            Name = "LATEST INVOICE", Title = $"CLAIMS IN THE LATEST INVOICE NO. ({latestInv}) BY SUBCONTRACTOR AND STAGE", Subtitle = Scope(i),
            Columns = new() { new("SUBCONTRACTOR"), new("STAGE"), new("LINES", ColumnKind.Integer), new("PLAN QTY", ColumnKind.Number), new("AFTER SITE % x WIR %", ColumnKind.Number) },
            Rows = LedgerRules.Effective(claims).Where(c => c.InvoiceNo == latestInv).GroupBy(c => (c.Subcontractor, c.Stage)).OrderBy(g => g.Key.Subcontractor).ThenBy(g => g.Key.Stage)
                .Select(g => new object?[] { g.Key.Subcontractor, g.Key.Stage, g.Count(), g.Sum(c => c.Qty), g.Sum(c => c.QtyAfterWir) }).ToList(),
        };
        var pending = new ExportSheet
        {
            Name = "CHECKS PENDING", Title = "HEIGHT / LENGTH CHECKS PENDING (held out of invoices)", Subtitle = Scope(i),
            Columns = new() { new("SUBCONTRACTOR"), new("INVOICE"), new("ROOM"), new("STAGE"), new("ITEM"), new("CHECK"), new("CLAIMED", ColumnKind.Number) },
            Rows = claims.Where(c => HeightCheck.IsPending(c) || LengthCheck.IsPending(c))
                .Select(c => new object?[] { c.Subcontractor, CumulativeSplit.InvoiceLabel(c), c.Room, c.Stage, c.Item, HeightCheck.IsPending(c) ? ">4.5 M" : "15 M", HeightCheck.IsPending(c) ? c.QtyAbove45 : c.LengthClaimedQty }).ToList(),
        };
        _ = rooms; _ = levelOf;
        return new List<ExportSheet> { Group("BY STAGE", b => b.Stage), Group("BY SYSTEM", b => $"{b.Stage} | {b.Item}"),
            Group("BY LEVEL", b => $"{levelOf.GetValueOrDefault(b.Room, "NOT IN ROOMS")} | {b.Stage}"), status, thisInv, pending };
    }

    // ------------------------------------------------------------------ scorecards

    public static List<ExportSheet> Scorecards(ReportInputs i)
    {
        var s = i.Project;
        var claims = s.Claims.Where(c => In(i, c.Building)).ToList();
        var bal = LedgerRules.Balances(s.RoomQtys.Where(q => In(i, q.Building)), claims);
        var rows = new List<object?[]>();
        foreach (var g in LedgerRules.Effective(claims).GroupBy(c => c.Subcontractor).OrderBy(g => g.Key))
        {
            var sub = g.Key;
            var keys = g.Select(c => c.Key).ToHashSet();
            var overKeys = bal.Values.Count(b => b.HasCap && b.IsOver && keys.Contains(LedgerKeys.Key(b.Room, b.Stage, b.Item)));
            var h = g.Where(c => c.QtyAbove45 != 0).ToList();
            var l = g.Where(c => c.LengthApplies).ToList();
            var inv = s.SubInvoices.Where(x => x.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase) && InvoiceKinds.IsSubcontractor(x)).ToList();
            var certified = inv.Where(x => x.Status == SubInvoiceStatus.Approved).GroupBy(x => (x.ContractNo, x.InvoiceNo)).Select(x => x.OrderByDescending(r => r.Revision).First())
                .Sum(x => InvoiceTotals.Of(x, s.SubInvoiceLines.Where(li => li.SubInvoiceId == x.Id)).CurrGross);
            rows.Add(new object?[]
            {
                sub, g.Count(), g.Select(c => c.InvoiceNo).Distinct().Count(), g.Sum(c => c.Qty), g.Sum(c => c.QtyAfterWir), g.Count(c => c.IsOver), overKeys,
                h.Sum(c => c.QtyAbove45), h.Sum(HeightCheck.AcceptedHigh), h.Where(c => c.HeightStatus == CheckStatus.Rejected).Sum(c => c.QtyAbove45) + h.Where(c => c.HeightStatus == CheckStatus.Partly).Sum(c => c.QtyAbove45 - c.QtyAbove45Accepted),
                h.Count(HeightCheck.IsPending),
                l.Sum(c => c.LengthClaimedQty - c.Qty), l.Sum(c => LengthCheck.InvoiceBaseQty(c) - c.Qty), l.Sum(LengthCheck.RejectedExtra), l.Count(LengthCheck.IsPending),
                inv.Count(x => x.Status == SubInvoiceStatus.Approved), inv.Count(x => x.Status == SubInvoiceStatus.Rejected), certified,
            });
        }
        return new List<ExportSheet>
        {
            new()
            {
                Name = "SCORECARDS", Title = "SUBCONTRACTOR SCORECARDS", Subtitle = Scope(i) + "  |  15 m figures are the EXTRA points over the plan quantity",
                Columns = new() { new("SUBCONTRACTOR"), new("LINES", ColumnKind.Integer), new("INVOICES", ColumnKind.Integer), new("PLAN QTY", ColumnKind.Number), new("AFTER SITE x WIR", ColumnKind.Number),
                    new("OVER LINES", ColumnKind.Integer), new("KEYS OVER CAP", ColumnKind.Integer), new(">4.5 M CLAIMED", ColumnKind.Number), new(">4.5 M ACCEPTED", ColumnKind.Number), new(">4.5 M REJECTED", ColumnKind.Number),
                    new(">4.5 M PENDING", ColumnKind.Integer), new("15 M EXTRA CLAIMED", ColumnKind.Number), new("15 M EXTRA ACCEPTED", ColumnKind.Number), new("15 M EXTRA REJECTED", ColumnKind.Number),
                    new("15 M PENDING", ColumnKind.Integer), new("INV APPROVED", ColumnKind.Integer), new("INV REJECTED", ColumnKind.Integer), new("CERTIFIED SAR", ColumnKind.Money) },
                Rows = rows,
            },
        };
    }

    // ------------------------------------------------------------------ cash flow

    public static List<ExportSheet> CashFlow(ReportInputs i)
    {
        var s = i.Project;
        var invs = s.SubInvoices.GroupBy(x => (x.Kind, x.ContractNo, x.Subcontractor, x.InvoiceNo)).Select(g => g.OrderByDescending(x => x.Revision).First()).ToList();
        var totals = invs.ToDictionary(x => x.Id, x => InvoiceTotals.Of(x, s.SubInvoiceLines.Where(l => l.SubInvoiceId == x.Id)));
        var bySub = invs.GroupBy(x => (InvoiceKinds.Of(x), x.Subcontractor, x.ContractNo)).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Subcontractor)
            .Select(g => new object?[]
            {
                g.Key.Item1, g.Key.Subcontractor, g.Key.ContractNo, g.Count(), g.Sum(x => totals[x.Id].CurrGross),
                g.Where(x => x.Status is SubInvoiceStatus.Submitted or SubInvoiceStatus.Approved).Sum(x => totals[x.Id].CurrGross),
                g.Where(x => x.Status == SubInvoiceStatus.Approved).Sum(x => totals[x.Id].CurrGross),
                g.Where(x => x.Status == SubInvoiceStatus.Approved).Sum(x => totals[x.Id].NetInclVatCurr),
            }).ToList();
        var byMonth = invs.GroupBy(x => new DateTime((x.SubmittedAt ?? x.CreatedAt).Year, (x.SubmittedAt ?? x.CreatedAt).Month, 1)).OrderBy(g => g.Key)
            .Select(g => new object?[]
            {
                g.Key, g.Sum(x => totals[x.Id].CurrGross), g.Where(x => x.Status == SubInvoiceStatus.Approved).Sum(x => totals[x.Id].CurrGross),
                g.Where(x => x.Status == SubInvoiceStatus.Approved).Sum(x => totals[x.Id].CurrRetention),
            }).ToList();
        decimal run = 0; decimal runCert = 0;
        foreach (var r in byMonth) { run += Convert.ToDecimal(r[1]); runCert += Convert.ToDecimal(r[2]); }
        return new List<ExportSheet>
        {
            new()
            {
                Name = "BY SUBCONTRACTOR", Title = "CASH FLOW - CLAIMED vs CERTIFIED", Subtitle = Scope(i) + "  |  latest revision of each invoice; certified = approved",
                Columns = new() { new("KIND"), new("SUBCONTRACTOR / SUPPLIER"), new("CONTRACT / PO"), new("INVOICES", ColumnKind.Integer), new("CLAIMED GROSS", ColumnKind.Money), new("SUBMITTED", ColumnKind.Money),
                    new("CERTIFIED GROSS", ColumnKind.Money), new("CERTIFIED NET INCL VAT", ColumnKind.Money) },
                Rows = bySub,
                TotalRow = new object?[] { "TOTAL", null, null, bySub.Sum(r => Convert.ToInt32(r[3])), bySub.Sum(r => Convert.ToDouble(r[4])), bySub.Sum(r => Convert.ToDouble(r[5])), bySub.Sum(r => Convert.ToDouble(r[6])), bySub.Sum(r => Convert.ToDouble(r[7])) },
            },
            new()
            {
                Name = "BY MONTH", Title = "CASH FLOW BY MONTH (submitted / created)", Subtitle = Scope(i),
                Columns = new() { new("MONTH", ColumnKind.Date), new("CLAIMED GROSS", ColumnKind.Money), new("CERTIFIED GROSS", ColumnKind.Money), new("RETENTION HELD", ColumnKind.Money) },
                Rows = byMonth,
            },
        };
    }

    // ------------------------------------------------------------------ materials

    public static List<ExportSheet> MaterialsStatus(ReportInputs i)
    {
        var m = i.Materials;
        if (m is null) return new List<ExportSheet> { Note("MATERIALS", "Materials are not available for this data source.") };
        var match = ThreeWayMatcher.Match(m, i.MaterialsSettings ?? new MaterialsSettings());
        var byPoLine = match.Rows.Where(r => r.PoLine != null).GroupBy(r => r.PoLine!.Id).ToDictionary(g => g.Key, g => g.Sum(r => r.Qty));
        var poRows = m.Pos.OrderBy(p => p.Supplier).ThenBy(p => p.PoNo).Select(p =>
        {
            var lines = m.LinesOf(p).ToList();
            var amount = lines.Sum(l => l.Amount);
            var delivered = lines.Sum(l => Math.Min(byPoLine.GetValueOrDefault(l.Id), l.Qty * 10) * l.Rate);
            var dns = m.Dns.Where(d => MaterialsSnapshot.PoKey(d.PoNo) == MaterialsSnapshot.PoKey(p.PoNo)).ToList();
            var withMir = dns.Count(d => m.MirDns.Any(x => x.DnNo.Trim().Equals(d.DnNo.Trim(), StringComparison.OrdinalIgnoreCase)));
            var over = match.Rows.Count(r => r.Po?.Id == p.Id && r.Status == MatchStatus.OverPo);
            return new object?[] { p.Supplier, p.PoNo, p.PoDate, lines.Count, amount, delivered, amount <= 0 ? 0 : delivered / amount, dns.Count, dns.Count - withMir, over };
        }).ToList();
        var mirRows = m.Mirs.OrderBy(x => x.MirNo).Select(x => new object?[]
        {
            x.MirNo, x.Revision, x.MirDate, x.Supplier, x.Status, string.Join(", ", m.MirDns.Where(d => d.MirId == x.Id).Select(d => d.DnNo)), m.MirEvidence.Count(e => e.MirId == x.Id),
        }).ToList();
        var exceptions = match.Rows.Where(r => r.Status != MatchStatus.Matched).Select(r => new object?[]
        {
            r.Status, r.Dn.Supplier, r.Dn.DnNo, r.Dn.DnDate, r.Dn.PoNo, r.Line.Description, r.Line.Qty, r.Line.Unit, r.MirNos, string.Join("; ", r.Notes),
        }).ToList();
        return new List<ExportSheet>
        {
            new()
            {
                Name = "PO STATUS", Title = "MATERIALS - PO DELIVERED", Subtitle = $"as of {i.AsOf:dd MMM yyyy}  |  delivered valued at PO rates, DN quantities converted to the PO unit",
                Columns = new() { new("SUPPLIER"), new("PO"), new("PO DATE", ColumnKind.Date), new("LINES", ColumnKind.Integer), new("PO AMOUNT", ColumnKind.Money), new("DELIVERED", ColumnKind.Money),
                    new("DELIVERED %", ColumnKind.Percent), new("DNS", ColumnKind.Integer), new("DNS WITHOUT MIR", ColumnKind.Integer), new("LINES OVER PO", ColumnKind.Integer) },
                Rows = poRows,
            },
            new()
            {
                Name = "MIR STATUS", Title = "MIR STATUS", Subtitle = $"as of {i.AsOf:dd MMM yyyy}",
                Columns = new() { new("MIR"), new("REV"), new("DATE", ColumnKind.Date), new("SUPPLIER"), new("STATUS"), new("DNS", Width: 40), new("EVIDENCE ROWS", ColumnKind.Integer) },
                Rows = mirRows,
            },
            new()
            {
                Name = "EXCEPTIONS", Title = "3-WAY MATCH EXCEPTIONS (only these need you)", Subtitle = match.Summary,
                Columns = new() { new("STATUS"), new("SUPPLIER"), new("DN"), new("DN DATE", ColumnKind.Date), new("PO"), new("DESCRIPTION", Width: 50), new("QTY", ColumnKind.Number), new("UNIT"), new("MIR"), new("NOTES", Width: 50) },
                Rows = exceptions,
            },
        };
    }

    // ------------------------------------------------------------------ VO register

    public static List<ExportSheet> VoRegister(ReportInputs i)
    {
        var rows = i.Variations.Where(v => In(i, v.Building)).OrderBy(v => v.Number).Select(v =>
        {
            var t = VariationMath.Totals(i.VariationLines.Where(l => l.VariationId == v.Id));
            var age = v.SubmittedAt is { } sub && !VariationStatus.IsClosed(v.Status) ? (int)(i.AsOf - sub.Date).TotalDays : (int?)null;
            return new object?[] { v.Number, v.Type, v.Date, v.ConsultantRef, v.Title, v.Building, v.Status, t.AddTotal, -Math.Abs(t.Omissions), t.Net, v.ApprovedAmount, v.SubmittedAt, age, v.AconexWorkflowNo };
        }).ToList();
        return new List<ExportSheet>
        {
            new()
            {
                Name = "VO REGISTER", Title = "VARIATIONS / EI REGISTER", Subtitle = Scope(i) + "  |  ageing = days since submission while not decided",
                Columns = new() { new("NO."), new("TYPE"), new("DATE", ColumnKind.Date), new("CONSULTANT REF"), new("TITLE", Width: 40), new("BUILDING"), new("STATUS"), new("ADDITIONS", ColumnKind.Money),
                    new("OMISSIONS", ColumnKind.Money), new("NET", ColumnKind.Money), new("APPROVED", ColumnKind.Money), new("SUBMITTED", ColumnKind.Date), new("DAYS OPEN", ColumnKind.Integer), new("ACONEX") },
                Rows = rows,
                TotalRow = new object?[] { "TOTAL", null, null, null, null, null, null, rows.Sum(r => Convert.ToDouble(r[7])), rows.Sum(r => Convert.ToDouble(r[8])), rows.Sum(r => Convert.ToDouble(r[9])),
                    rows.Sum(r => r[10] is double d ? d : 0), null, null, null },
            },
        };
    }

    private static ExportSheet Note(string name, string text) => new() { Name = name, Title = name, Columns = new() { new("NOTE", Width: 80) }, Rows = new() { new object?[] { text } } };

    // ------------------------------------------------------------------ PDF

    /// <summary>Renders sheets as a landscape PDF in the house style (grey #A6A6A6 header, bold black, dark red titles).</summary>
    public static void ExportPdf(string path, string title, IReadOnlyList<ExportSheet> sheets, DateTime? stamp = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var when = stamp ?? DateTime.Now;
        Document.Create(doc =>
        {
            foreach (var sh in sheets)
                doc.Page(page =>
                {
                    page.Size(sh.Columns.Count > 9 ? PageSizes.A3.Landscape() : PageSizes.A4.Landscape());
                    page.Margin(22);
                    page.DefaultTextStyle(x => x.FontSize(7.5f));
                    page.Header().Column(c =>
                    {
                        c.Item().Text(title).FontSize(9).FontColor(Colors.Grey.Darken2);
                        c.Item().Text(sh.Title ?? sh.Name).FontSize(14).Bold().FontColor("#8B0000");
                        if (sh.Subtitle is { Length: > 0 } st) c.Item().Text(st).Italic();
                    });
                    page.Content().PaddingTop(6).Table(tb =>
                    {
                        tb.ColumnsDefinition(cd => { foreach (var col in sh.Columns) if (col.Width >= 30) cd.RelativeColumn(3); else cd.RelativeColumn(); });
                        tb.Header(h => { foreach (var col in sh.Columns) h.Cell().Background("#A6A6A6").Padding(2).Text(col.Header).Bold().FontColor(Colors.Black); });
                        foreach (var row in sh.Rows.Concat(sh.TotalRow is null ? Array.Empty<object?[]>() : new[] { sh.TotalRow }))
                        {
                            var total = ReferenceEquals(row, sh.TotalRow);
                            for (var k = 0; k < sh.Columns.Count; k++)
                            {
                                var v = k < row.Length ? row[k] : null;
                                var cell = tb.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(2);
                                if (total) cell = cell.Background("#F4E3E3");
                                var txt = Fmt(v, sh.Columns[k].Kind);
                                var t = v is double or int or long or decimal ? cell.AlignRight().Text(txt) : cell.Text(txt);
                                if (total) t.Bold();
                            }
                        }
                    });
                    page.Footer().Row(r =>
                    {
                        r.RelativeItem().Text($"Raffaello  |  {when:dd-MMM-yyyy HH:mm}").FontColor(Colors.Grey.Darken1);
                        r.ConstantItem(80).AlignRight().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
                    });
                });
        }).WithMetadata(new DocumentMetadata { Title = title, Author = "Raffaello", Creator = "Raffaello", Producer = "Raffaello", CreationDate = when, ModifiedDate = when }).GeneratePdf(path);
    }

    private static string Fmt(object? v, ColumnKind k) => v switch
    {
        null => "",
        DateTime d => d.ToString("dd-MMM-yy"),
        double d when k == ColumnKind.Percent => d.ToString("P1"),
        double d when k == ColumnKind.Integer => d.ToString("N0"),
        double d => d.ToString("N2"),
        int n => n.ToString("N0"),
        long n => n.ToString("N0"),
        decimal d => d.ToString("N2"),
        _ => v.ToString() ?? "",
    };
}
