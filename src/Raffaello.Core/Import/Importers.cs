using System.Data;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Rules;
using Raffaello.Core.Seed;

namespace Raffaello.Core.Import;

public enum IssueLevel { Warning, Error }

public sealed record ImportIssue(int Row, IssueLevel Level, string Message)
{
    public string LevelText => Level == IssueLevel.Error ? "ERROR" : "WARN";
}

/// <summary>Parsed file, shown to the user before anything is written. Commit writes in one transaction.</summary>
public sealed class ImportPreview
{
    public string Kind { get; init; } = "";
    public string FileName { get; init; } = "";
    public DataTable Table { get; } = new();
    public List<ImportIssue> Issues { get; } = new();
    public int ValidRows { get; set; }
    public int ErrorCount => Issues.Count(i => i.Level == IssueLevel.Error);
    public int WarningCount => Issues.Count(i => i.Level == IssueLevel.Warning);
    public bool CanCommit => ValidRows > 0;
    public string Summary { get; set; } = "";
    internal Func<Db, int>? CommitAction { get; set; }

    public int Commit(Db db)
    {
        if (CommitAction is null) return 0;
        var n = CommitAction(db);
        db.Insert(new ImportBatch { Kind = Kind, FileName = Path.GetFileName(FileName), Rows = n, Errors = ErrorCount, ImportedAt = DateTime.Now }, $"Imported {Kind}: {Path.GetFileName(FileName)} ({n} rows)");
        return n;
    }

    internal void Columns(params string[] names) { foreach (var n in names) Table.Columns.Add(n); }
}

public interface IImporter
{
    string Kind { get; }
    string Title { get; }
    string Help { get; }
    ImportPreview Parse(TableData table, ProjectSnapshot snapshot, RuleOptions options);
}

/// <summary>Line lookup by BUILDING / LEVEL / ROOM / SYSTEM / STAGE.</summary>
internal sealed class LineIndex
{
    private readonly Dictionary<string, QtyLine> _map;
    public LineIndex(IEnumerable<QtyLine> lines) =>
        _map = lines.GroupBy(Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    public static string Key(QtyLine l) => Key(l.Building, l.Level, l.Room, l.System, l.Stage);
    public static string Key(string b, string lv, string r, string sys, string st) => $"{b}|{lv}|{r}|{sys}|{st}".ToUpperInvariant();
    public QtyLine? Find(string b, string lv, string r, string sys, string st) => _map.GetValueOrDefault(Key(b, lv, r, sys, st));
    public QtyLine? Find(TableRow row) => Find(Building(row), row.Get("LEVEL", "LVL", "FLOOR"), row.Get("ROOM", "UNIT", "ROOM NO", "SPACE"),
        SystemMap.Normalize(row.Get("SYSTEM", "LAYER")), Stages.Normalize(row.Get("STAGE", "FIX")));
    public static string Building(TableRow row)
    {
        var b = row.Get("BUILDING", "BLDG", "TOWER").ToUpperInvariant();
        return b.StartsWith("B") && !b.StartsWith("BASE") ? Buildings.Branded : b.Length == 0 ? Buildings.Hotel : b.StartsWith("H") ? Buildings.Hotel : b;
    }
}

/// <summary>QSEXPORT (AutoCAD LISP v6): one row per block tag with room, system and count. Builds QS per room / system / stage.</summary>
public sealed class QsExportImporter : IImporter
{
    public string Kind => "QSEXPORT";
    public string Title => "QSEXPORT (block tags)";
    public string Help => "Columns: BUILDING, LEVEL, ROOM, ROOM TYPE, BLOCK, SYSTEM (or LAYER), COUNT, optional STAGE. Twin data counts 2 at 2ND FIX; terrace blocks are excluded.";

    public ImportPreview Parse(TableData t, ProjectSnapshot s, RuleOptions o)
    {
        var p = new ImportPreview { Kind = Kind, FileName = t.Source };
        p.Columns("BUILDING", "LEVEL", "ROOM", "SYSTEM", "STAGE", "POINTS", "ACTION");
        if (!t.Has("ROOM") || !t.Has("COUNT", "QTY")) { p.Issues.Add(new(0, IssueLevel.Error, "Missing ROOM or COUNT column.")); return p; }
        var agg = new Dictionary<string, (string b, string lv, string room, string type, string sys, string st, double pts)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in t.Rows)
        {
            var block = row.Get("BLOCK", "TAG", "BLOCK NAME");
            var room = row.Get("ROOM", "UNIT", "SPACE");
            var sys = SystemMap.Normalize(row.Get("SYSTEM", "LAYER"));
            var count = row.GetNumber("COUNT", "QTY") ?? 0;
            if (room.Length == 0 || sys.Length == 0) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, "ROOM or SYSTEM empty.")); continue; }
            if ((block + " " + room).Contains("TERRACE", StringComparison.OrdinalIgnoreCase) && sys is Systems.Light or Systems.Power)
            { p.Issues.Add(new(row.RowNumber, IssueLevel.Warning, $"Terrace {sys} excluded ({block}).")); continue; }
            var stages = Stages.Normalize(row.Get("STAGE")) is { Length: > 0 } st0 ? new[] { st0 } : Stages.All;
            foreach (var st in stages)
            {
                var pts = count * PointRules.PointsFor(block, st);
                var b = LineIndex.Building(row);
                var lv = row.Get("LEVEL", "LVL", "FLOOR");
                var key = LineIndex.Key(b, lv, room, sys, st);
                var cur = agg.GetValueOrDefault(key, (b, lv, room, row.Get("ROOM TYPE", "TYPE"), sys, st, 0));
                cur.pts += pts;
                agg[key] = cur;
            }
        }
        var idx = new LineIndex(s.Lines);
        var boq = s.BoqItems.GroupBy(b => (b.System, b.Stage)).ToDictionary(g => g.Key, g => g.First());
        var inserts = new List<QtyLine>();
        var updates = new List<(QtyLine line, double qs)>();
        foreach (var a in agg.Values)
        {
            var existing = idx.Find(a.b, a.lv, a.room, a.sys, a.st);
            var action = existing is null ? "NEW" : Math.Abs(existing.QsQty - a.pts) < 0.0001 ? "SAME" : $"QS {existing.QsQty:N0} -> {a.pts:N0}";
            p.Table.Rows.Add(a.b, a.lv, a.room, a.sys, a.st, a.pts, action);
            if (existing is null)
            {
                var item = boq.GetValueOrDefault((a.sys, a.st));
                inserts.Add(new QtyLine
                {
                    Building = a.b, Level = a.lv, Room = a.room, RoomType = a.type, System = a.sys, Stage = a.st, QsQty = a.pts,
                    ItemCode = DemoSeeder.ItemCodeFor(a.sys, a.st), Description = item?.Description ?? $"{a.sys} {a.st}", Rate = item?.Rate ?? 0, Unit = "PT",
                });
            }
            else if (action != "SAME") updates.Add((existing, a.pts));
        }
        p.ValidRows = inserts.Count + updates.Count;
        p.Summary = $"{agg.Count} room/system/stage lines: {inserts.Count} new, {updates.Count} changed.";
        p.CommitAction = db =>
        {
            db.InTransaction(w =>
            {
                w.InsertMany(inserts);
                foreach (var (line, qs) in updates)
                    w.Execute("UPDATE QtyLines SET QsQty=@Qs, UpdatedBy=@By, UpdatedAt=@At, RowVersion=RowVersion+1 WHERE Id=@Id", new { Qs = qs, By = db.User, At = DateTime.Now, line.Id });
            }, $"QSEXPORT import: {inserts.Count} new lines, {updates.Count} QS changes");
            return inserts.Count + updates.Count;
        };
        return p;
    }
}

/// <summary>GIVEN: quantities allocated to a subcontractor per room / system / stage.</summary>
public sealed class AllocationImporter : IImporter
{
    public string Kind => "GIVEN";
    public string Title => "Subcontractor allocations (GIVEN)";
    public string Help => "Columns: SUBCONTRACTOR, BUILDING, LEVEL, ROOM, SYSTEM, STAGE, QTY, REF, DATE. Over-QS lines are posted at full qty with an OVER warning.";

    public ImportPreview Parse(TableData t, ProjectSnapshot s, RuleOptions o)
    {
        var p = new ImportPreview { Kind = Kind, FileName = t.Source };
        p.Columns("SUBCONTRACTOR", "LINE", "QTY", "QS", "FLAG");
        var idx = new LineIndex(s.Lines);
        var given = s.Allocations.GroupBy(a => a.LineId).ToDictionary(g => g.Key, g => g.Sum(a => a.Qty));
        var rows = new List<Allocation>();
        foreach (var row in t.Rows)
        {
            var sub = row.Get("SUBCONTRACTOR", "SUB", "SUBCON").ToUpperInvariant();
            var qty = row.GetNumber("QTY", "GIVEN", "QUANTITY");
            var line = idx.Find(row);
            if (sub.Length == 0 || qty is null) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, "SUBCONTRACTOR or QTY missing.")); continue; }
            if (line is null) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, "No QS line for this BUILDING / LEVEL / ROOM / SYSTEM / STAGE.")); continue; }
            var total = given.GetValueOrDefault(line.Id) + qty.Value;
            given[line.Id] = total;
            var post = PostingRules.Post(total, line.QsQty);
            if (post.IsOver) p.Issues.Add(new(row.RowNumber, IssueLevel.Warning, $"GIVEN {total:N0} > QS {line.QsQty:N0}: posted at full qty, OVER."));
            p.Table.Rows.Add(sub, $"{line.Level} {line.Room} {line.System} {line.Stage}", qty, line.QsQty, post.IsOver ? "OVER" : "OK");
            rows.Add(new Allocation { LineId = line.Id, Subcontractor = sub, Qty = qty.Value, Ref = row.Get("REF", "REFERENCE"), GivenAt = row.GetDate("DATE") ?? DateTime.Today });
        }
        p.ValidRows = rows.Count;
        p.Summary = $"{rows.Count} allocations ready.";
        p.CommitAction = db => db.InsertMany(rows, $"GIVEN import: {rows.Count} allocations");
        return p;
    }
}

/// <summary>WIR register export (Aconex): one row per WIR line.</summary>
public sealed class WirImporter : IImporter
{
    public string Kind => "WIR";
    public string Title => "WIR register";
    public string Help => "Columns: WIR NO, DATE, SUBCONTRACTOR, BUILDING, LEVEL, ROOM, SYSTEM, STAGE, QTY, STATUS, APPROVED DATE, REWORK (Y/N). EMT is rework and never counts as progress.";

    public ImportPreview Parse(TableData t, ProjectSnapshot s, RuleOptions o)
    {
        var p = new ImportPreview { Kind = Kind, FileName = t.Source };
        p.Columns("WIR NO", "STATUS", "LINE", "QTY", "REWORK");
        var idx = new LineIndex(s.Lines);
        var existing = s.Wirs.ToDictionary(w => w.WirNo, StringComparer.OrdinalIgnoreCase);
        var newWirs = new Dictionary<string, Wir>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<(string wirNo, WirLine line)>();
        foreach (var row in t.Rows)
        {
            var no = row.Get("WIR NO", "WIR", "DOCUMENT NO", "DOC NO");
            if (no.Length == 0) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, "WIR NO missing.")); continue; }
            var rawSystem = row.Get("SYSTEM", "LAYER");
            var isEmt = SystemMap.Normalize(rawSystem) == Systems.Emt;
            var rework = isEmt || row.Get("REWORK").StartsWith("Y", StringComparison.OrdinalIgnoreCase);
            var line = idx.Find(row);
            if (line is null && isEmt)
            {
                // EMT rows carry the system they rework in a SYSTEM 2 / FOR column; fall back to a LIGHT line in that room
                line = idx.Find(LineIndex.Building(row), row.Get("LEVEL"), row.Get("ROOM"), SystemMap.Normalize(row.Get("FOR SYSTEM", "SYSTEM 2")), Stages.Normalize(row.Get("STAGE")));
            }
            if (line is null) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, $"{no}: no QS line for this room / system / stage.")); continue; }
            if (existing.ContainsKey(no)) { p.Issues.Add(new(row.RowNumber, IssueLevel.Warning, $"{no} already in register - skipped.")); continue; }
            if (!newWirs.TryGetValue(no, out var wir))
            {
                var status = row.Get("STATUS").ToUpperInvariant();
                wir = new Wir
                {
                    WirNo = no, Kind = no.StartsWith("MIR", StringComparison.OrdinalIgnoreCase) ? "MIR" : "WIR", Subcontractor = row.Get("SUBCONTRACTOR", "SUB").ToUpperInvariant(),
                    Building = line.Building, Level = line.Level, System = isEmt ? Systems.Emt : line.System, Stage = line.Stage,
                    Description = row.Get("DESCRIPTION", "TITLE") is { Length: > 0 } d ? d : $"{line.Building} {line.Level} {line.System} {line.Stage}",
                    AconexNo = row.Get("ACONEX NO", "DOCUMENT NO"), SubmittedAt = row.GetDate("DATE", "SUBMITTED") ?? DateTime.Today,
                    Status = status.StartsWith("A") || status.Contains("APPROVED") ? WirStatus.Approved : status.StartsWith("R") || status.StartsWith("C") ? WirStatus.Rejected : WirStatus.Open,
                    ApprovedAt = row.GetDate("APPROVED DATE", "RESPONSE DATE"),
                };
                newWirs[no] = wir;
            }
            var qty = row.GetNumber("QTY", "QUANTITY") ?? 0;
            if (rework) p.Issues.Add(new(row.RowNumber, IssueLevel.Warning, $"{no}: rework / EMT {qty:N0} - never counts as progress."));
            p.Table.Rows.Add(no, wir.Status, $"{line.Level} {line.Room} {line.System} {line.Stage}", qty, rework ? "Y" : "");
            lines.Add((no, new WirLine { LineId = line.Id, Qty = qty, IsRework = rework }));
        }
        p.ValidRows = lines.Count;
        p.Summary = $"{newWirs.Count} new WIRs, {lines.Count} lines.";
        p.CommitAction = db =>
        {
            db.InTransaction(w =>
            {
                foreach (var wir in newWirs.Values) w.Insert(wir);
                foreach (var (no, l) in lines) { l.WirId = newWirs[no].Id; w.Insert(l); }
            }, $"WIR import: {newWirs.Count} WIRs");
            return lines.Count;
        };
        return p;
    }
}

/// <summary>Subcontractor statement (cumulative). Flags OVER-cap, claims above WIR and copies of an earlier statement.</summary>
public sealed class InvoiceImporter : IImporter
{
    public string Kind => "INVOICE";
    public string Title => "Subcontractor statement / invoice";
    public string Help => "Columns: SUBCONTRACTOR, INVOICE NO, DATE, BUILDING, LEVEL, ROOM, SYSTEM, STAGE, CUM QTY, RATE. Quantities are cumulative to date.";

    public ImportPreview Parse(TableData t, ProjectSnapshot s, RuleOptions o)
    {
        var p = new ImportPreview { Kind = Kind, FileName = t.Source };
        p.Columns("INVOICE", "LINE", "CUM QTY", "CAP", "FLAG");
        var idx = new LineIndex(s.Lines);
        var invoices = new Dictionary<string, Invoice>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<(string key, InvoiceLine line)>();
        foreach (var row in t.Rows)
        {
            var sub = row.Get("SUBCONTRACTOR", "SUB").ToUpperInvariant();
            var no = row.Get("INVOICE NO", "INVOICE", "INV NO", "STATEMENT");
            var line = idx.Find(row);
            var cum = row.GetNumber("CUM QTY", "CUMULATIVE", "QTY TO DATE", "QTY");
            if (sub.Length == 0 || no.Length == 0 || cum is null) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, "SUBCONTRACTOR, INVOICE NO or CUM QTY missing.")); continue; }
            if (line is null) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, "No QS line for this room / system / stage.")); continue; }
            var key = sub + "|" + no;
            if (s.Invoices.Any(i => i.Subcontractor == sub && string.Equals(i.InvoiceNo, no, StringComparison.OrdinalIgnoreCase)))
            { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, $"{sub} {no} already exists.")); continue; }
            if (!invoices.ContainsKey(key)) invoices[key] = new Invoice { Subcontractor = sub, InvoiceNo = no, InvDate = row.GetDate("DATE", "INVOICE DATE") ?? DateTime.Today };
            var cap = line.ProjectQty ?? line.QsQty;
            var flag = "OK";
            if (cum > cap) { flag = "OVER"; p.Issues.Add(new(row.RowNumber, IssueLevel.Warning, $"{no}: CUM {cum:N0} > PROJECT QTY {cap:N0} - posted at full qty, OVER.")); }
            var rate = row.GetNumber("RATE", "UNIT RATE") ?? line.Rate;
            p.Table.Rows.Add($"{sub} {no}", $"{line.Level} {line.Room} {line.System} {line.Stage}", cum, cap, flag);
            lines.Add((key, new InvoiceLine { LineId = line.Id, CumQty = cum.Value, Rate = rate }));
        }
        // copy check against earlier statements
        var tmpInv = invoices.Values.Select((inv, i) => { inv.Id = -1 - i; return inv; }).ToList();
        var tmpLines = lines.Select(x => new InvoiceLine { InvoiceId = invoices[x.key].Id, LineId = x.line.LineId, CumQty = x.line.CumQty }).ToList();
        foreach (var c in InvoiceCopyDetector.Detect(s.Invoices.Concat(tmpInv), s.InvoiceLines.Concat(tmpLines)).Where(c => c.Invoice.Id < 0))
            p.Issues.Add(new(0, IssueLevel.Warning, c.Message + " - check before accepting."));
        foreach (var inv in invoices.Values) { inv.Id = 0; inv.ClaimedAmount = Math.Round(lines.Where(l => l.key == inv.Subcontractor + "|" + inv.InvoiceNo).Sum(l => l.line.CumQty * l.line.Rate), 2); }
        p.ValidRows = lines.Count;
        p.Summary = $"{invoices.Count} statement(s), {lines.Count} lines.";
        p.CommitAction = db =>
        {
            db.InTransaction(w =>
            {
                foreach (var inv in invoices.Values) w.Insert(inv);
                foreach (var (key, l) in lines) { l.InvoiceId = invoices[key].Id; w.Insert(l); }
            }, $"Statement import: {string.Join(", ", invoices.Keys)}");
            return lines.Count;
        };
        return p;
    }
}

/// <summary>Purchase order lines. Checks that the lines add up to the stated PO total.</summary>
public sealed class PoImporter : IImporter
{
    public string Kind => "PO";
    public string Title => "Purchase order lines";
    public string Help => "Columns: PO NO, SUPPLIER, PO DATE, LINE, ITEM, DESCRIPTION, UNIT, QTY, RATE, STATED TOTAL, optional LENGTH PER PCS.";

    public ImportPreview Parse(TableData t, ProjectSnapshot s, RuleOptions o)
    {
        var p = new ImportPreview { Kind = Kind, FileName = t.Source };
        p.Columns("PO NO", "LINE", "DESCRIPTION", "UNIT", "QTY", "RATE", "AMOUNT");
        var pos = new Dictionary<string, PurchaseOrder>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<(string po, PoLine line)>();
        foreach (var row in t.Rows)
        {
            var no = row.Get("PO NO", "PO", "PO NUMBER");
            var qty = row.GetNumber("QTY", "QUANTITY");
            var rate = row.GetNumber("RATE", "UNIT PRICE", "UNIT RATE");
            if (no.Length == 0 || qty is null || rate is null) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, "PO NO, QTY or RATE missing.")); continue; }
            if (s.PurchaseOrders.Any(x => string.Equals(x.PoNo, no, StringComparison.OrdinalIgnoreCase))) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, $"{no} already exists.")); continue; }
            if (!pos.TryGetValue(no, out var po))
                pos[no] = po = new PurchaseOrder { PoNo = no, Supplier = row.Get("SUPPLIER", "VENDOR"), PoDate = row.GetDate("PO DATE", "DATE") ?? DateTime.Today, StatedTotal = row.GetNumber("STATED TOTAL", "PO TOTAL", "TOTAL") ?? 0 };
            else if (po.StatedTotal == 0) po.StatedTotal = row.GetNumber("STATED TOTAL", "PO TOTAL", "TOTAL") ?? 0;
            var line = new PoLine
            {
                LineNo = (int)(row.GetNumber("LINE", "LINE NO", "S NO") ?? lines.Count(l => l.po == no) + 1), ItemCode = row.Get("ITEM", "ITEM CODE"),
                Description = row.Get("DESCRIPTION", "DESC"), Unit = row.Get("UNIT", "UOM").ToUpperInvariant(), Qty = qty.Value, Rate = rate.Value,
                LengthPerPcs = row.GetNumber("LENGTH PER PCS", "PCS LENGTH") ?? 0, System = SystemMap.Normalize(row.Get("SYSTEM")),
            };
            p.Table.Rows.Add(no, line.LineNo, line.Description, line.Unit, line.Qty, line.Rate, line.Qty * line.Rate);
            lines.Add((no, line));
        }
        foreach (var po in pos.Values)
        {
            var check = MaterialRules.CheckPoTotal(lines.Where(l => l.po == po.PoNo).Select(l => (l.line.Qty, l.line.Rate)), po.StatedTotal);
            if (po.StatedTotal > 0 && !check.Matches) p.Issues.Add(new(0, IssueLevel.Warning, $"{po.PoNo}: lines total {check.LinesTotal:N2} vs stated {check.StatedTotal:N2} (diff {check.Difference:N2})."));
            if (po.StatedTotal == 0) po.StatedTotal = check.LinesTotal;
        }
        p.ValidRows = lines.Count;
        p.Summary = $"{pos.Count} PO(s), {lines.Count} lines.";
        p.CommitAction = db =>
        {
            db.InTransaction(w =>
            {
                foreach (var po in pos.Values) w.Insert(po);
                foreach (var (no, l) in lines) { l.PoId = pos[no].Id; w.Insert(l); }
            }, $"PO import: {string.Join(", ", pos.Keys)}");
            return lines.Count;
        };
        return p;
    }
}

/// <summary>Delivery notes against PO lines. Pipes delivered in PCS are converted to M (default 6 m per PCS).</summary>
public sealed class DnImporter : IImporter
{
    public string Kind => "DN";
    public string Title => "Delivery notes";
    public string Help => "Columns: DN NO, DATE, PO NO, LINE (or ITEM), QTY, UNIT, MIR NO. PCS are converted to M when the PO line is in M.";

    public ImportPreview Parse(TableData t, ProjectSnapshot s, RuleOptions o)
    {
        var p = new ImportPreview { Kind = Kind, FileName = t.Source };
        p.Columns("DN NO", "PO NO", "LINE", "RAW", "QTY (PO UNIT)", "DELIVERED / PO");
        var dns = new Dictionary<string, DeliveryNote>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<(string dn, DnLine line)>();
        var delivered = s.DnLines.GroupBy(d => d.PoLineId).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
        foreach (var row in t.Rows)
        {
            var dnNo = row.Get("DN NO", "DN", "DELIVERY NOTE");
            var poNo = row.Get("PO NO", "PO");
            var po = s.PurchaseOrders.FirstOrDefault(x => string.Equals(x.PoNo, poNo, StringComparison.OrdinalIgnoreCase));
            if (dnNo.Length == 0 || po is null) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, po is null ? $"PO {poNo} not found." : "DN NO missing.")); continue; }
            if (s.DeliveryNotes.Any(d => string.Equals(d.DnNo, dnNo, StringComparison.OrdinalIgnoreCase))) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, $"{dnNo} already imported.")); continue; }
            var poLines = s.PoLines.Where(l => l.PoId == po.Id).ToList();
            var lineNo = row.GetNumber("LINE", "LINE NO");
            var item = row.Get("ITEM", "ITEM CODE");
            var pl = lineNo.HasValue ? poLines.FirstOrDefault(l => l.LineNo == (int)lineNo.Value) : poLines.FirstOrDefault(l => string.Equals(l.ItemCode, item, StringComparison.OrdinalIgnoreCase));
            var raw = row.GetNumber("QTY", "QUANTITY");
            if (pl is null || raw is null) { p.Issues.Add(new(row.RowNumber, IssueLevel.Error, "PO line or QTY not found.")); continue; }
            var unit = row.Get("UNIT", "UOM").ToUpperInvariant();
            if (unit.Length == 0) unit = pl.Unit;
            var len = pl.LengthPerPcs > 0 ? pl.LengthPerPcs : o.PipeLengthM;
            var qty = UnitConverter.ToPoUnit(raw.Value, unit, pl.Unit, len);
            if (Math.Abs(qty - raw.Value) > 0.0001) p.Issues.Add(new(row.RowNumber, IssueLevel.Warning, $"{dnNo}: {raw:N0} {unit} converted to {qty:N0} {pl.Unit} ({len:N1} m per PCS)."));
            var total = delivered.GetValueOrDefault(pl.Id) + qty;
            delivered[pl.Id] = total;
            if (MaterialRules.IsOverPo(total, pl.Qty)) p.Issues.Add(new(row.RowNumber, IssueLevel.Warning, $"{po.PoNo} line {pl.LineNo}: delivered {total:N0} > PO {pl.Qty:N0} - OVER alarm."));
            if (!dns.ContainsKey(dnNo)) dns[dnNo] = new DeliveryNote { DnNo = dnNo, PoId = po.Id, DnDate = row.GetDate("DATE", "DN DATE") ?? DateTime.Today, MirNo = row.Get("MIR NO", "MIR") };
            p.Table.Rows.Add(dnNo, po.PoNo, pl.LineNo, $"{raw:N0} {unit}", qty, $"{total:N0} / {pl.Qty:N0}");
            lines.Add((dnNo, new DnLine { PoLineId = pl.Id, RawQty = raw.Value, RawUnit = unit, Qty = qty }));
        }
        p.ValidRows = lines.Count;
        p.Summary = $"{dns.Count} DN(s), {lines.Count} lines.";
        p.CommitAction = db =>
        {
            db.InTransaction(w =>
            {
                foreach (var dn in dns.Values) w.Insert(dn);
                foreach (var (no, l) in lines) { l.DnId = dns[no].Id; w.Insert(l); }
            }, $"DN import: {string.Join(", ", dns.Keys)}");
            return lines.Count;
        };
        return p;
    }
}

public static class ImporterRegistry
{
    public static readonly IReadOnlyList<IImporter> All = new IImporter[]
    {
        new QsExportImporter(), new AllocationImporter(), new WirImporter(), new InvoiceImporter(), new PoImporter(), new DnImporter(),
    };

    public static IImporter? ByKind(string kind) => All.FirstOrDefault(i => string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>Guesses the importer from the header row.</summary>
    public static IImporter Guess(TableData t)
    {
        if (t.Has("DN NO", "DELIVERY NOTE")) return ByKind("DN")!;
        if (t.Has("PO NO") && t.Has("RATE", "UNIT PRICE")) return ByKind("PO")!;
        if (t.Has("INVOICE NO", "STATEMENT", "INV NO")) return ByKind("INVOICE")!;
        if (t.Has("WIR NO", "WIR")) return ByKind("WIR")!;
        if (t.Has("BLOCK", "TAG", "BLOCK NAME")) return ByKind("QSEXPORT")!;
        return ByKind("GIVEN")!;
    }
}
