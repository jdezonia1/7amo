using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Seed;

/// <summary>
/// Realistic demo dataset so the app opens populated: hotel enlarged rooms (28 types, 4,401 points per stage:
/// LIGHT 2,697 / POWER 914 / GRMS 350 / DATA 282 / AV 144 / DISABLED 14), branded residences (1BR-A, 1BR-B, 2BR, 3BR-A, 3BR-C),
/// subcontractors MARUF, BANDER SEIF and AWRAD, 389 WIRs, cumulative statements (BANDER SEIF INV-03 = INV-02,
/// AWRAD INV-01 to redo), POs with delivery notes (PCS to M conduits, one PO total that does not add up).
/// Deterministic for a given <c>today</c>.
/// </summary>
public sealed class DemoSeeder
{
    public const int TargetWirCount = 389;
    public static readonly IReadOnlyDictionary<string, int> HotelPoints = new Dictionary<string, int>
    {
        [Systems.Light] = 2697, [Systems.Power] = 914, [Systems.Grms] = 350, [Systems.Data] = 282, [Systems.Av] = 144, [Systems.Disabled] = 14,
    };

    public static readonly string[] HotelRoomTypes =
    {
        "KING DELUXE", "TWIN DELUXE", "KING PREMIER", "ACCESSIBLE KING", "TWIN PREMIER", "KING GRAND", "CORNER KING", "CORNER TWIN",
        "STUDIO SUITE", "JUNIOR SUITE", "JUNIOR SUITE CORNER", "EXECUTIVE SUITE", "DELUXE SUITE", "GARDEN SUITE", "TERRACE SUITE", "FAMILY SUITE",
        "ONE BED SUITE", "ONE BED SUITE CORNER", "TWO BED SUITE", "AMBASSADOR SUITE", "DIPLOMATIC SUITE", "PRESIDENTIAL SUITE", "ROYAL SUITE",
        "WRITERS SUITE", "LANDMARK SUITE", "SIGNATURE SUITE", "PANORAMA SUITE", "RAFFLES SUITE",
    };

    private static readonly Dictionary<string, (int L, int P, int G, int D, int A)> BrandedPoints = new()
    {
        ["1BR-A"] = (34, 26, 9, 6, 3), ["1BR-B"] = (34, 26, 9, 6, 3), ["2BR"] = (48, 36, 13, 9, 4), ["3BR-A"] = (66, 48, 17, 13, 5), ["3BR-C"] = (64, 46, 17, 10, 5),
    };

    private static readonly Dictionary<string, string> SysCode = new()
    {
        [Systems.Light] = "LT", [Systems.Power] = "PW", [Systems.Grms] = "GR", [Systems.Data] = "DT", [Systems.Av] = "AV",
        [Systems.Disabled] = "DS", [Systems.Emergency] = "EM", [Systems.Evacuation] = "EV",
    };

    private static readonly Dictionary<string, double[]> Rates = new()
    {
        [Systems.Light] = new[] { 85.0, 60, 45 }, [Systems.Power] = new[] { 95.0, 70, 40 }, [Systems.Grms] = new[] { 120.0, 150, 180 },
        [Systems.Data] = new[] { 90.0, 110, 60 }, [Systems.Av] = new[] { 110.0, 130, 90 }, [Systems.Disabled] = new[] { 150.0, 120, 200 },
        [Systems.Emergency] = new[] { 100.0, 80, 120 }, [Systems.Evacuation] = new[] { 105.0, 85, 130 },
    };

    private readonly Random _r = new(20260930);
    private readonly DateTime _today;
    public DateTime ProjectStart { get; }
    public DateTime PlannedFinish { get; }

    public DemoSeeder(DateTime today)
    {
        _today = today.Date;
        ProjectStart = Analytics.ProjectAnalytics.WeekStart(_today.AddDays(-7 * 38));
        PlannedFinish = ProjectStart.AddDays(7 * 66);
    }

    public static string StageCode(string stage) => stage == Stages.First ? "1F" : stage == Stages.Second ? "2F" : "FF";
    public static string ItemCodeFor(string system, string stage) => $"E-{SysCode.GetValueOrDefault(system, "XX")}-{StageCode(stage)}";

    private static string StageDescription(string system, string stage) => stage switch
    {
        Stages.First => $"{system} - CONDUIT & BACK BOXES",
        Stages.Second => $"{system} - WIRING & TERMINATION",
        _ => $"{system} - DEVICES & TESTING",
    };

    // ------------------------------------------------------------------ main

    public void Seed(IProjectStore db)
    {
        var plan = BuildPlan();
        db.Batch(w =>
        {
            foreach (var s in plan.Subs) w.Insert(s);
            foreach (var c in plan.Contracts) w.Insert(c);
            foreach (var b in plan.Boq) w.Insert(b);
            foreach (var r in plan.Rooms) w.Insert(r);
            foreach (var l in plan.Lines) w.Insert(l.Line);
            foreach (var l in plan.Lines) foreach (var a in l.Allocs) { a.LineId = l.Line.Id; w.Insert(a); }

            SeedWirs(w, plan);
            SeedInvoices(w, plan);
            SeedMaterials(w, plan);
            SeedAconex(w, plan);
            foreach (var i in plan.Imports) w.Insert(i);
        }, "Demo data seeded");

        // a little recent activity for the feed
        var events = new (int daysAgo, int hour, string action, string text)[]
        {
            (6, 9, "IMPORT", "Imported QSEXPORT v6 - HOTEL L3 (1,184 blocks)"),
            (5, 11, "APPROVE", "WIR batch approved by consultant - BANDER SEIF GRMS L2"),
            (4, 14, "IMPORT", "Imported DN-0103-05 (WIRING DEVICES) - 6 lines"),
            (3, 10, "STATEMENT", "BANDER SEIF INV-03 received"),
            (2, 16, "UPDATE", "PROJECT QTY updated for E-GR-FF (HOTEL)"),
            (1, 8, "IMPORT", "Imported WIR register (Aconex export, 12 new)"),
            (0, 9, "STATEMENT", "MARUF INV-04 received - awaiting certification"),
        };
        var oldClock = db.Clock;
        foreach (var e in events)
        {
            var at = _today.AddDays(-e.daysAgo).AddHours(e.hour).AddMinutes(_r.Next(0, 59));
            db.Clock = () => at;
            db.LogEvent(e.action, e.text);
        }
        db.Clock = oldClock;
        db.SetMeta("ProjectStart", ProjectStart.ToString("yyyy-MM-dd"));
        db.SetMeta("PlannedFinish", PlannedFinish.ToString("yyyy-MM-dd"));
        db.SetMeta("SeededAt", DateTime.Now.ToString("s"));
    }

    // ------------------------------------------------------------------ plan

    private sealed class LinePlan
    {
        public required QtyLine Line { get; init; }
        public List<Allocation> Allocs { get; } = new();
        public string Sub { get; set; } = "";
        public double Progress { get; set; }
        public double DoneTarget { get; set; }
        public int LevelIndex { get; set; }
    }

    private sealed class Plan
    {
        public List<Subcontractor> Subs { get; } = new();
        public List<Contract> Contracts { get; } = new();
        public List<BoqItem> Boq { get; } = new();
        public List<Room> Rooms { get; } = new();
        public List<LinePlan> Lines { get; } = new();
        public List<ImportBatch> Imports { get; } = new();
        public List<Wir> Wirs { get; } = new();
        public Dictionary<long, Dictionary<long, double>> DoneEvents { get; } = new(); // lineId -> (wirId -> qty)
        public Dictionary<long, Wir> WirById { get; } = new();
    }

    private Plan BuildPlan()
    {
        var p = new Plan();
        p.Subs.Add(new Subcontractor { Name = "MARUF", Trade = "ELECTRICAL", Scope = "HOTEL LIGHT / POWER / DISABLED", Contact = "Site engineer - Maruf" });
        p.Subs.Add(new Subcontractor { Name = "BANDER SEIF", Trade = "ELV", Scope = "HOTEL GRMS / DATA / AV / EMERGENCY", Contact = "Bander Seif est." });
        p.Subs.Add(new Subcontractor { Name = "AWRAD", Trade = "ELECTRICAL + ELV", Scope = "BRANDED RESIDENCES - ALL SYSTEMS", Contact = "Awrad contracting" });

        // ---------------- hotel enlarged rooms: 28 types x 2 rooms on L1..L5
        var hotelRooms = new List<Room>();
        for (var k = 0; k < 56; k++)
        {
            var lvl = 1 + k / 12;
            var type = HotelRoomTypes[k % 28];
            var room = new Room { Building = Buildings.Hotel, Level = $"L{lvl}", Code = $"{lvl}{(k % 12) + 1:00}", RoomType = $"ER-{(k % 28) + 1:00} {type}", Zone = k % 12 < 6 ? "EAST" : "WEST" };
            hotelRooms.Add(room);
        }
        p.Rooms.AddRange(hotelRooms);
        var weights = hotelRooms.Select((r, k) => 0.75 + 0.045 * (k % 28)).ToArray();
        var hotelCounts = new Dictionary<string, int[]>();
        foreach (var (sys, total) in HotelPoints)
        {
            if (sys == Systems.Disabled)
            {
                var arr = new int[56];
                for (var i = 0; i < 7; i++) arr[3 + 8 * i] = 2; // accessible rooms
                hotelCounts[sys] = arr;
            }
            else hotelCounts[sys] = Distribute(total, weights);
        }
        for (var k = 0; k < hotelRooms.Count; k++)
            foreach (var sys in HotelPoints.Keys)
                AddLines(p, hotelRooms[k], sys, hotelCounts[sys][k], sys is Systems.Light or Systems.Power or Systems.Disabled ? "MARUF" : "BANDER SEIF");

        // hotel public areas
        var pub = new (string lvl, string code, string type)[]
        {
            ("B1", "B1-PLANT", "PLANT ROOM"), ("B1", "B1-BOH", "BOH CORRIDOR"), ("GF", "GF-LOBBY", "LOBBY"),
            ("GF", "GF-BALLROOM", "BALLROOM"), ("GF", "GF-ADD", "ALL DAY DINING"), ("GF", "GF-SPA", "SPA"),
        };
        foreach (var (lvl, code, type) in pub)
        {
            var room = new Room { Building = Buildings.Hotel, Level = lvl, Code = code, RoomType = type, Zone = "PUBLIC" };
            p.Rooms.Add(room);
            AddLines(p, room, Systems.Light, _r.Next(40, 190), "MARUF");
            AddLines(p, room, Systems.Power, _r.Next(20, 90), "MARUF");
            AddLines(p, room, Systems.Data, _r.Next(6, 40), "BANDER SEIF");
            AddLines(p, room, Systems.Emergency, _r.Next(10, 45), "BANDER SEIF");
            if (lvl == "GF") AddLines(p, room, Systems.Evacuation, _r.Next(6, 20), "BANDER SEIF");
        }

        // ---------------- branded residences: 6 units per level L1..L5
        var unitTypes = new[] { "1BR-A", "1BR-B", "2BR", "2BR", "3BR-A", "3BR-C" };
        for (var lvl = 1; lvl <= 5; lvl++)
            for (var u = 0; u < 6; u++)
            {
                var t = unitTypes[u];
                var room = new Room { Building = Buildings.Branded, Level = $"L{lvl}", Code = $"B-{lvl}{u + 1:00}", RoomType = t, Zone = u < 3 ? "NORTH" : "SOUTH" };
                p.Rooms.Add(room);
                var pts = BrandedPoints[t];
                AddLines(p, room, Systems.Light, pts.L, "AWRAD");
                AddLines(p, room, Systems.Power, pts.P, "AWRAD");
                AddLines(p, room, Systems.Grms, pts.G, "AWRAD");
                AddLines(p, room, Systems.Data, pts.D, "AWRAD");
                AddLines(p, room, Systems.Av, pts.A, "AWRAD");
            }
        var brPub = new Room { Building = Buildings.Branded, Level = "GF", Code = "BR-GF-LOBBY", RoomType = "PUBLIC AREA", Zone = "PUBLIC" };
        var brB1 = new Room { Building = Buildings.Branded, Level = "B1", Code = "BR-B1-PARKING", RoomType = "PARKING", Zone = "PUBLIC" };
        p.Rooms.Add(brPub); p.Rooms.Add(brB1);
        AddLines(p, brPub, Systems.Light, 96, "AWRAD"); AddLines(p, brPub, Systems.Power, 38, "AWRAD"); AddLines(p, brPub, Systems.Data, 22, "AWRAD");
        AddLines(p, brB1, Systems.Power, 32, "AWRAD"); AddLines(p, brB1, Systems.Light, 140, "AWRAD"); AddLines(p, brB1, Systems.Emergency, 36, "AWRAD");

        // ---------------- BOQ items + PROJECT QTY
        foreach (var g in p.Lines.GroupBy(l => (l.Line.Building, l.Line.ItemCode)))
        {
            var first = g.First().Line;
            var qs = g.Sum(l => l.Line.QsQty);
            p.Boq.Add(new BoqItem
            {
                ItemCode = $"{(g.Key.Building == Buildings.Hotel ? "H" : "B")}-{g.Key.ItemCode}",
                Bill = g.Key.Building == Buildings.Hotel ? "BILL 16 - HOTEL ELECTRICAL" : "BILL 21 - BRANDED ELECTRICAL",
                Description = StageDescription(first.System, first.Stage), System = first.System, Stage = first.Stage, Unit = "PT",
                BoqQty = Math.Round(qs * 1.04), ProjectQty = _r.NextDouble() < 0.6 ? Math.Round(qs * 0.99) : null, Rate = first.Rate,
            });
        }

        // ---------------- contracts
        foreach (var s in p.Subs)
        {
            var value = p.Lines.Where(l => l.Sub == s.Name).Sum(l => l.Line.QsQty * l.Line.Rate);
            p.Contracts.Add(new Contract
            {
                Subcontractor = s.Name, ContractNo = $"SC-RAF-EL-{p.Contracts.Count + 1:000}", Scope = s.Scope, Value = Math.Round(value / 100.0) * 100,
                RetentionPct = 0.10, AdvancePct = s.Name == "AWRAD" ? 0.0 : 0.10, SignedAt = ProjectStart.AddDays(-30 + 12 * p.Contracts.Count), Status = "ACTIVE",
            });
        }

        p.Imports.Add(new ImportBatch { Kind = "QSEXPORT", FileName = "HOTEL_SUBCONTRACTOR_QTY.xlsx", Rows = 17222, Errors = 0, ImportedAt = _today.AddDays(-12).AddHours(10) });
        p.Imports.Add(new ImportBatch { Kind = "QSEXPORT", FileName = "HOTEL_ENLARGED_ROOMS_QS.xlsx", Rows = 4401, Errors = 0, ImportedAt = _today.AddDays(-9).AddHours(15) });
        p.Imports.Add(new ImportBatch { Kind = "WIR", FileName = "ACONEX_WIR_REGISTER.xlsx", Rows = 389, Errors = 2, ImportedAt = _today.AddDays(-1).AddHours(8) });
        p.Imports.Add(new ImportBatch { Kind = "DN", FileName = "DN-0103-05.pdf.xlsx", Rows = 6, Errors = 0, ImportedAt = _today.AddDays(-4).AddHours(14) });
        p.Imports.Add(new ImportBatch { Kind = "INVOICE", FileName = "MARUF_INV-04.xlsx", Rows = 212, Errors = 0, ImportedAt = _today.AddHours(9) });
        return p;
    }

    private void AddLines(Plan p, Room room, string system, int qs, string sub)
    {
        if (qs <= 0) return;
        var levelIndex = Array.IndexOf(Analytics.ProjectAnalytics.LevelOrder, room.Level);
        for (var si = 0; si < 3; si++)
        {
            var stage = Stages.All[si];
            var progress = StageProgress(room.Building, levelIndex, si);
            var line = new QtyLine
            {
                Building = room.Building, Level = room.Level, Room = room.Code, RoomType = room.RoomType, System = system, Stage = stage,
                ItemCode = ItemCodeFor(system, stage), Description = StageDescription(system, stage), Unit = "PT", QsQty = qs,
                Rate = Rates.TryGetValue(system, out var rr) ? rr[si] : 80,
            };
            // PROJECT QTY: ~60% filled; a few caps tighter than QS
            var u = _r.NextDouble();
            line.ProjectQty = u < 0.52 ? qs : u < 0.60 ? Math.Max(1, Math.Floor(qs * 0.9)) : null;

            var lp = new LinePlan { Line = line, Sub = sub, Progress = progress, LevelIndex = levelIndex };
            var givenFrac = Math.Min(1.0, progress + 0.2 + _r.NextDouble() * 0.1);
            if (progress <= 0.001 && _r.NextDouble() < 0.6) givenFrac = 0;
            var given = Math.Round(qs * givenFrac);
            if (_r.NextDouble() < 0.03 && given > 0) given = qs + Math.Max(1, Math.Ceiling(qs * 0.08)); // exceeded line: posted at full qty
            if (given > 0)
                lp.Allocs.Add(new Allocation { Subcontractor = sub, Qty = given, Ref = $"GIVEN-{sub[..2]}-{room.Level}-{StageCode(stage)}", GivenAt = ProjectStart.AddDays(7 * (si * 6 + Math.Max(0, levelIndex - 1) * 2) + _r.Next(0, 6)) });
            var done = Math.Min(given, Math.Round(qs * progress));
            if (_r.NextDouble() < 0.02 && given > 0) done = given + Math.Max(1, Math.Round(qs * 0.05)); // done > given
            lp.DoneTarget = done;
            var noise = (_r.NextDouble() - 0.35) * 0.3;
            line.SitePct = Math.Round(Math.Clamp(progress + noise * (progress > 0 ? 1 : 0.2), 0, 1), 2);
            p.Lines.Add(lp);
        }
    }

    private double StageProgress(string building, int levelIndex, int stageIndex)
    {
        // levelIndex: B1=0, GF=1, L1=2 ... L5=6
        var lvl = Math.Max(0, levelIndex - 1);
        var first = Math.Clamp(1.05 - 0.11 * lvl, 0, 1);
        var p = stageIndex switch { 0 => first, 1 => first - 0.32, _ => first - 0.68 };
        if (building == Buildings.Branded) p -= 0.12;
        p += (_r.NextDouble() - 0.5) * 0.08;
        return Math.Round(Math.Clamp(p, 0, 1), 3);
    }

    private static int[] Distribute(int total, double[] weights)
    {
        var sum = weights.Sum();
        var raw = weights.Select(w => total * w / sum).ToArray();
        var floor = raw.Select(x => (int)Math.Floor(x)).ToArray();
        var rem = total - floor.Sum();
        foreach (var i in raw.Select((x, i) => (frac: x - Math.Floor(x), i)).OrderByDescending(t => t.frac).Take(rem).Select(t => t.i)) floor[i]++;
        return floor;
    }

    // ------------------------------------------------------------------ WIRs

    private void SeedWirs(IStoreBatch w, Plan p)
    {
        var groups = p.Lines.GroupBy(l => (l.Sub, l.Line.Building, l.Line.Level, l.Line.System, l.Line.Stage)).ToList();
        var doneGroups = groups.Where(g => g.Sum(l => l.DoneTarget) > 0).ToList();
        var openGroups = groups.Where(g => g.Sum(l => l.Allocs.Sum(a => a.Qty) - l.DoneTarget) > 0 && _r.NextDouble() < 0.45).ToList();
        const int rejected = 9;
        var extra = TargetWirCount - doneGroups.Count - openGroups.Count - rejected;
        while (extra < 0 && openGroups.Count > 0) { openGroups.RemoveAt(openGroups.Count - 1); extra++; }
        var weights = doneGroups.Select(g => g.Sum(l => l.DoneTarget)).ToArray();
        var extraPer = extra > 0 && doneGroups.Count > 0 ? Distribute(extra, weights) : new int[doneGroups.Count];

        var drafts = new List<(Wir wir, List<WirLine> lines)>();
        for (var gi = 0; gi < doneGroups.Count; gi++)
        {
            var g = doneGroups[gi];
            var n = 1 + extraPer[gi];
            var stageIdx = Array.IndexOf(Stages.All, g.Key.Stage);
            var lvl = Math.Max(0, g.First().LevelIndex - 1);
            var start = ProjectStart.AddDays(7 * (2 + stageIdx * 9 + lvl * 2.5));
            var progress = g.Average(l => l.Progress);
            var available = (_today.AddDays(-6) - start).TotalDays;
            var span = Math.Max(14, progress >= 0.97 ? Math.Min(available, 7 * 16) : available);
            for (var k = 0; k < n; k++)
            {
                var submitted = start.AddDays(span * (k + 0.5) / n + _r.Next(-2, 3));
                if (submitted > _today.AddDays(-4)) submitted = _today.AddDays(-4 - _r.Next(0, 6));
                if (submitted < ProjectStart) submitted = ProjectStart.AddDays(_r.Next(3, 10));
                var wir = NewWir(g.Key.Sub, g.Key.Building, g.Key.Level, g.Key.System, g.Key.Stage, submitted);
                wir.Status = WirStatus.Approved;
                wir.ApprovedAt = submitted.AddDays(_r.Next(2, 11));
                if (wir.ApprovedAt > _today) wir.ApprovedAt = _today.AddDays(-1);
                var lines = new List<WirLine>();
                foreach (var l in g)
                {
                    if (l.DoneTarget <= 0) continue;
                    var share = Math.Floor(l.DoneTarget / n);
                    var q = k == n - 1 ? l.DoneTarget - share * (n - 1) : share;
                    if (q > 0) lines.Add(new WirLine { LineId = l.Line.Id, Qty = q });
                }
                if (_r.NextDouble() < 0.06 && lines.Count > 0)
                    lines.Add(new WirLine { LineId = lines[0].LineId, Qty = Math.Max(1, Math.Round(lines[0].Qty * 0.2)), IsRework = true });
                drafts.Add((wir, lines));
            }
        }
        foreach (var g in openGroups)
        {
            var submitted = _today.AddDays(-_r.Next(1, 46));
            var wir = NewWir(g.Key.Sub, g.Key.Building, g.Key.Level, g.Key.System, g.Key.Stage, submitted);
            var lines = g.Where(l => l.Allocs.Sum(a => a.Qty) > l.DoneTarget)
                         .Select(l => new WirLine { LineId = l.Line.Id, Qty = Math.Max(1, Math.Round((l.Allocs.Sum(a => a.Qty) - l.DoneTarget) * 0.6)) }).ToList();
            drafts.Add((wir, lines));
        }
        for (var i = 0; i < rejected && doneGroups.Count > 0; i++)
        {
            var g = doneGroups[_r.Next(doneGroups.Count)];
            var submitted = _today.AddDays(-_r.Next(10, 120));
            var wir = NewWir(g.Key.Sub, g.Key.Building, g.Key.Level, g.Key.System, g.Key.Stage, submitted);
            wir.Status = WirStatus.Rejected;
            wir.ApprovedAt = submitted.AddDays(_r.Next(2, 8));
            wir.Description += " - REJECTED: SNAGS NOT CLOSED";
            drafts.Add((wir, g.Take(3).Select(l => new WirLine { LineId = l.Line.Id, Qty = Math.Max(1, Math.Round(l.Line.QsQty * 0.3)) }).ToList()));
        }

        var n2 = 0;
        foreach (var (wir, lines) in drafts.OrderBy(d => d.wir.SubmittedAt))
        {
            n2++;
            wir.WirNo = $"WIR-EL-{n2:0000}";
            wir.AconexNo = $"RAF-MOB-WIR-EL-{n2:000000}";
            w.Insert(wir);
            p.Wirs.Add(wir);
            foreach (var l in lines) { l.WirId = wir.Id; w.Insert(l); }
            if (wir.Status == WirStatus.Approved)
                foreach (var l in lines.Where(x => !x.IsRework))
                {
                    if (!p.DoneEvents.TryGetValue(l.LineId, out var d)) p.DoneEvents[l.LineId] = d = new();
                    d[wir.Id] = d.GetValueOrDefault(wir.Id) + l.Qty;
                }
        }

        // MIRs (material inspection requests) - not part of the 389 WIRs
        for (var i = 1; i <= 42; i++)
        {
            var submitted = ProjectStart.AddDays(7 + i * 6 + _r.Next(0, 4));
            if (submitted > _today) submitted = _today.AddDays(-_r.Next(1, 5));
            var mir = new Wir
            {
                WirNo = $"MIR-EL-{i:0000}", Kind = "MIR", Subcontractor = "MOBCO STORES", Building = i % 3 == 0 ? Buildings.Branded : Buildings.Hotel,
                Level = "GF", System = new[] { Systems.Power, Systems.Light, Systems.Grms, Systems.Data }[i % 4], Stage = Stages.First,
                Description = "MATERIAL INSPECTION - " + new[] { "CABLES", "CONDUITS", "WIRING DEVICES", "GRMS DEVICES", "CABLE TRAY" }[i % 5],
                AconexNo = $"RAF-MOB-MIR-EL-{i:000000}", SubmittedAt = submitted,
                Status = (_today - submitted).TotalDays > 12 ? WirStatus.Approved : WirStatus.Open,
            };
            if (mir.Status == WirStatus.Approved) mir.ApprovedAt = submitted.AddDays(_r.Next(2, 8));
            w.Insert(mir);
        }
    }

    private Wir NewWir(string sub, string building, string level, string system, string stage, DateTime submitted) => new()
    {
        Kind = "WIR", Subcontractor = sub, Building = building, Level = level, System = system, Stage = stage,
        Description = $"{building} {level} {system} {stage}", SubmittedAt = submitted.Date, Status = WirStatus.Open,
    };

    private double DoneAt(Plan p, long lineId, DateTime at)
    {
        if (!p.DoneEvents.TryGetValue(lineId, out var ev)) return 0;
        if (p.WirById.Count != p.Wirs.Count) { p.WirById.Clear(); foreach (var x in p.Wirs) p.WirById[x.Id] = x; }
        return ev.Where(kv => p.WirById[kv.Key].ApprovedAt <= at).Sum(kv => kv.Value);
    }

    // ------------------------------------------------------------------ statements

    private void SeedInvoices(IStoreBatch w, Plan p)
    {
        var monthEnd = new DateTime(_today.Year, _today.Month, 1).AddDays(-1);
        var schedule = new List<(string sub, string no, DateTime date, string status)>
        {
            ("MARUF", "INV-01", monthEnd.AddMonths(-3).AddDays(-5), InvoiceStatus.Certified),
            ("MARUF", "INV-02", monthEnd.AddMonths(-2).AddDays(-5), InvoiceStatus.Certified),
            ("MARUF", "INV-03", monthEnd.AddMonths(-1).AddDays(-5), InvoiceStatus.Certified),
            ("MARUF", "INV-04", _today.AddDays(-1), InvoiceStatus.Received),
            ("BANDER SEIF", "INV-01", monthEnd.AddMonths(-3), InvoiceStatus.Certified),
            ("BANDER SEIF", "INV-02", monthEnd.AddMonths(-2), InvoiceStatus.Certified),
            ("BANDER SEIF", "INV-03", _today.AddDays(-3), InvoiceStatus.Received), // copy of INV-02
            ("AWRAD", "INV-01", monthEnd.AddMonths(-1).AddDays(-8), InvoiceStatus.Redo),
        };
        var latestBySub = schedule.GroupBy(s => s.sub).ToDictionary(g => g.Key, g => g.Max(x => x.date));
        Dictionary<long, double>? banderInv02 = null;

        foreach (var (sub, no, date, status) in schedule)
        {
            var inv = new Invoice { Subcontractor = sub, InvoiceNo = no, InvDate = date, Status = status };
            if (status == InvoiceStatus.Redo) inv.Notes = "Awrad invoice 1 must be redone: quantities claimed on SITE % instead of WIR %.";
            var isLatest = date == latestBySub[sub];
            var lines = new List<InvoiceLine>();
            if (sub == "BANDER SEIF" && no == "INV-03" && banderInv02 != null)
            {
                inv.Notes = "Received by email; same figures as INV-02.";
                foreach (var kv in banderInv02)
                {
                    var lp = p.Lines.First(l => l.Line.Id == kv.Key);
                    lines.Add(new InvoiceLine { LineId = kv.Key, CumQty = kv.Value, Rate = lp.Line.Rate });
                }
            }
            else
            {
                foreach (var lp in p.Lines.Where(l => l.Sub == sub))
                {
                    var done = DoneAt(p, lp.Line.Id, date);
                    double claim = done;
                    if (status == InvoiceStatus.Redo) claim = Math.Round(lp.Line.QsQty * lp.Line.SitePct);
                    else if (isLatest)
                    {
                        var u = _r.NextDouble();
                        if (u < 0.05 && lp.Allocs.Count > 0) claim = done + Math.Max(1, Math.Round(lp.Line.QsQty * 0.2));
                        else if (u < 0.065 && lp.Allocs.Count > 0) claim = (lp.Line.ProjectQty ?? lp.Line.QsQty) + Math.Max(1, Math.Round(lp.Line.QsQty * 0.06));
                    }
                    if (claim > 0) lines.Add(new InvoiceLine { LineId = lp.Line.Id, CumQty = claim, Rate = lp.Line.Rate });
                }
                if (sub == "BANDER SEIF" && no == "INV-02") banderInv02 = lines.ToDictionary(l => l.LineId, l => l.CumQty);
            }
            inv.ClaimedAmount = Math.Round(lines.Sum(l => l.CumQty * l.Rate), 2);
            if (status == InvoiceStatus.Certified)
            {
                inv.CertifiedAt = date.AddDays(_r.Next(9, 24));
                if (inv.CertifiedAt > _today) inv.CertifiedAt = _today.AddDays(-1);
                foreach (var l in lines)
                {
                    var lp = p.Lines.First(x => x.Line.Id == l.LineId);
                    l.CertifiedQty = ClaimRules.CertifiableQty(l.CumQty, DoneAt(p, l.LineId, inv.CertifiedAt.Value), lp.Line.ProjectQty ?? lp.Line.QsQty);
                }
                inv.CertifiedAmount = Math.Round(lines.Sum(l => l.CertifiedQty * l.Rate), 2);
            }
            w.Insert(inv);
            foreach (var l in lines) { l.InvoiceId = inv.Id; }
            w.InsertMany(lines);
        }
    }

    // ------------------------------------------------------------------ materials

    private void SeedMaterials(IStoreBatch w, Plan p)
    {
        // device POs sized from the final-fix points so DELIVERED sits on the chain lines
        double FinalQs(string sys) => p.Lines.Where(l => l.Line.Stage == Stages.Final && l.Line.System == sys).Sum(l => l.Line.QsQty);

        var pos = new List<(PurchaseOrder po, List<PoLine> lines, double statedOffset)>
        {
            (new PurchaseOrder { PoNo = "PO-RAF-0101", Supplier = "RIYADH CABLES CO.", Description = "LSZH CABLES", PoDate = ProjectStart.AddDays(10) }, new()
            {
                new PoLine { LineNo = 1, ItemCode = "CBL-1.5", Description = "CABLE 1.5MM2 LSZH", Unit = "M", Qty = 42000, Rate = 2.85, System = Systems.Light },
                new PoLine { LineNo = 2, ItemCode = "CBL-2.5", Description = "CABLE 2.5MM2 LSZH", Unit = "M", Qty = 36000, Rate = 4.10, System = Systems.Power },
                new PoLine { LineNo = 3, ItemCode = "CBL-4.0", Description = "CABLE 4MM2 LSZH", Unit = "M", Qty = 12000, Rate = 6.40, System = Systems.Power },
                new PoLine { LineNo = 4, ItemCode = "CAT6A", Description = "CAT6A U/FTP LSZH", Unit = "M", Qty = 18000, Rate = 3.60, System = Systems.Data },
            }, 0),
            (new PurchaseOrder { PoNo = "PO-RAF-0102", Supplier = "SAUDI PVC INDUSTRIES", Description = "CONDUITS (DELIVERED IN PCS)", PoDate = ProjectStart.AddDays(4) }, new()
            {
                new PoLine { LineNo = 1, ItemCode = "PVC-20", Description = "PVC CONDUIT 20MM HEAVY GAUGE", Unit = "M", Qty = 24000, Rate = 1.95, System = Systems.Light },
                new PoLine { LineNo = 2, ItemCode = "PVC-25", Description = "PVC CONDUIT 25MM HEAVY GAUGE", Unit = "M", Qty = 9000, Rate = 2.70, System = Systems.Power },
                new PoLine { LineNo = 3, ItemCode = "GI-20", Description = "GI CONDUIT 20MM (3M LENGTHS)", Unit = "M", Qty = 3000, Rate = 9.50, System = Systems.Power, LengthPerPcs = 3.0 },
            }, 0),
            (new PurchaseOrder { PoNo = "PO-RAF-0103", Supplier = "LEGRAND KSA", Description = "WIRING DEVICES", PoDate = ProjectStart.AddDays(70) }, new()
            {
                new PoLine { LineNo = 1, ItemCode = "SW-1G", Description = "SWITCH 1G 10A (LIGHT POINTS)", Unit = "NO", Qty = Math.Round(FinalQs(Systems.Light) * 0.42), Rate = 22, System = Systems.Light },
                new PoLine { LineNo = 2, ItemCode = "SKT-TW", Description = "TWIN SOCKET 13A", Unit = "NO", Qty = Math.Round(FinalQs(Systems.Power) * 0.55), Rate = 38, System = Systems.Power },
                new PoLine { LineNo = 3, ItemCode = "DATA-TW", Description = "TWIN DATA OUTLET CAT6A", Unit = "NO", Qty = Math.Round(FinalQs(Systems.Data) * 0.5), Rate = 41, System = Systems.Data },
                new PoLine { LineNo = 4, ItemCode = "TV-OUT", Description = "TV / AV OUTLET", Unit = "NO", Qty = Math.Round(FinalQs(Systems.Av) * 0.6), Rate = 35, System = Systems.Av },
            }, 1250),
            (new PurchaseOrder { PoNo = "PO-RAF-0104", Supplier = "HONEYWELL KSA", Description = "GRMS DEVICES", PoDate = ProjectStart.AddDays(84) }, new()
            {
                new PoLine { LineNo = 1, ItemCode = "CP-4", Description = "CP-4 THERMOSTAT", Unit = "NO", Qty = 92, Rate = 640, System = Systems.Grms },
                new PoLine { LineNo = 2, ItemCode = "RCU", Description = "ROOM CONTROL UNIT", Unit = "NO", Qty = 92, Rate = 1850, System = Systems.Grms },
                new PoLine { LineNo = 3, ItemCode = "DND", Description = "DND / MUR PANEL", Unit = "NO", Qty = 92, Rate = 310, System = Systems.Grms },
            }, 0),
            (new PurchaseOrder { PoNo = "PO-RAF-0105", Supplier = "AL FANAR", Description = "CABLE TRAY", PoDate = ProjectStart.AddDays(20) }, new()
            {
                new PoLine { LineNo = 1, ItemCode = "CT-300", Description = "CABLE TRAY 300MM HDG", Unit = "M", Qty = 1200, Rate = 58, System = Systems.Power },
                new PoLine { LineNo = 2, ItemCode = "CT-150", Description = "CABLE TRAY 150MM HDG", Unit = "M", Qty = 1800, Rate = 41, System = Systems.Data },
            }, 0),
        };

        var mir = 0;
        foreach (var (po, lines, offset) in pos)
        {
            po.StatedTotal = Math.Round(lines.Sum(l => l.Qty * l.Rate), 2) + offset;
            w.Insert(po);
            foreach (var l in lines) { l.PoId = po.Id; w.Insert(l); }

            var dnCount = po.PoNo switch { "PO-RAF-0101" => 7, "PO-RAF-0102" => 8, "PO-RAF-0103" => 6, "PO-RAF-0104" => 4, _ => 5 };
            var first = po.PoDate.AddDays(10);
            var span = Math.Max(30, (_today.AddDays(-5) - first).TotalDays);
            var dns = new List<DeliveryNote>();
            for (var k = 0; k < dnCount; k++)
            {
                var dn = new DeliveryNote { PoId = po.Id, DnNo = $"DN-{po.PoNo[^4..]}-{k + 1:00}", DnDate = first.AddDays(span * k / dnCount + _r.Next(0, 5)), MirNo = $"MIR-EL-{++mir % 42 + 1:0000}" };
                if (dn.DnDate > _today) dn.DnDate = _today.AddDays(-1);
                w.Insert(dn);
                dns.Add(dn);
            }

            foreach (var l in lines)
            {
                var deviceSystem = po.PoNo is "PO-RAF-0103" or "PO-RAF-0104" ? l.System : null;
                if (deviceSystem != null && po.PoNo == "PO-RAF-0103")
                {
                    // issue devices to final-fix chain lines (DELIVERED link), spread over the DNs
                    var chainLines = p.Lines.Where(x => x.Line.Stage == Stages.Final && x.Line.System == deviceSystem && x.Allocs.Count > 0).ToList();
                    var share = l.Qty / Math.Max(1, FinalQs(deviceSystem));
                    var idx = 0;
                    double total = 0;
                    foreach (var cl in chainLines)
                    {
                        var q = Math.Round(cl.Allocs.Sum(a => a.Qty) * Math.Min(1.0, cl.Progress + 0.3));
                        if (q <= 0) continue;
                        var dn = dns[Math.Min(dns.Count - 1, idx++ * dns.Count / Math.Max(1, chainLines.Count))];
                        w.Insert(new DnLine { DnId = dn.Id, PoLineId = l.Id, Qty = q * 1.0, RawQty = q, RawUnit = "NO", LineId = cl.Line.Id });
                        total += q;
                    }
                    _ = share; _ = total;
                    continue;
                }
                var target = po.PoNo switch
                {
                    "PO-RAF-0101" => 0.78, "PO-RAF-0102" => 0.92, "PO-RAF-0104" => 0.55, _ => 0.7,
                };
                if (l.ItemCode == "PVC-20") target = 1.04; // delivered > PO: OVER alarm
                var remaining = l.Qty * target;
                for (var k = 0; k < dns.Count && remaining > 0; k++)
                {
                    var portion = k == dns.Count - 1 ? remaining : Math.Round(l.Qty * target / dns.Count * (0.6 + _r.NextDouble() * 0.8));
                    portion = Math.Min(portion, remaining);
                    if (portion <= 0) continue;
                    remaining -= portion;
                    if (po.PoNo == "PO-RAF-0102")
                    {
                        var len = l.LengthPerPcs > 0 ? l.LengthPerPcs : UnitConverter.DefaultPipeLength;
                        var pcs = Math.Round(portion / len);
                        w.Insert(new DnLine { DnId = dns[k].Id, PoLineId = l.Id, RawQty = pcs, RawUnit = "PCS", Qty = UnitConverter.PcsToM(pcs, len) });
                    }
                    else w.Insert(new DnLine { DnId = dns[k].Id, PoLineId = l.Id, RawQty = portion, RawUnit = l.Unit, Qty = portion });
                }
            }
        }
    }

    // ------------------------------------------------------------------ aconex

    private void SeedAconex(IStoreBatch w, Plan p)
    {
        foreach (var wir in p.Wirs)
        {
            w.Insert(new AconexDoc
            {
                DocNo = wir.AconexNo, Title = $"WIR - {wir.Description}", DocType = "WIR", Revision = "0",
                Status = wir.Status == WirStatus.Approved ? "A - APPROVED" : wir.Status == WirStatus.Rejected ? "C - REJECTED" : "UNDER REVIEW",
                DownloadedAt = _r.NextDouble() < 0.62 ? wir.SubmittedAt.AddDays(_r.Next(1, 6)) : null,
            });
        }
        var sdw = new[] { "LIGHTING LAYOUT", "SMALL POWER LAYOUT", "GRMS RISER", "DATA RISER", "CABLE TRAY ROUTING", "DB SCHEDULE", "EMERGENCY LIGHTING", "AV ROOM LAYOUT" };
        for (var i = 1; i <= 28; i++)
            w.Insert(new AconexDoc
            {
                DocNo = $"RAF-MOB-SDW-EL-{i:000000}", Title = $"SHOP DRAWING - {(i % 2 == 0 ? "HOTEL" : "BRANDED")} {sdw[i % sdw.Length]} L{1 + i % 5}", DocType = "SDW",
                Revision = (i % 3).ToString(), Status = i % 4 == 0 ? "B - APPROVED AS NOTED" : i % 5 == 0 ? "C - REVISE & RESUBMIT" : "A - APPROVED",
                DownloadedAt = i % 3 == 0 ? null : ProjectStart.AddDays(i * 7),
            });
        for (var i = 1; i <= 16; i++)
            w.Insert(new AconexDoc
            {
                DocNo = $"RAF-MOB-MAT-EL-{i:000000}", Title = $"MATERIAL SUBMITTAL - {new[] { "LSZH CABLES", "PVC CONDUIT", "WIRING DEVICES", "GRMS DEVICES" }[i % 4]}", DocType = "MAT",
                Revision = "0", Status = "A - APPROVED", DownloadedAt = ProjectStart.AddDays(i * 5),
            });
    }
}
