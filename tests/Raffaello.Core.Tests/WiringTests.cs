using System.Text.Json.Nodes;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Raffaello.Core.Assemblies;
using Raffaello.Core.Assistant;
using Raffaello.Core.Cables;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using Raffaello.Core.Domain;
using Raffaello.Core.Drawings;
using Raffaello.Core.Insights;
using Raffaello.Core.Materials;
using Raffaello.Core.Notify;
using Raffaello.Core.Queue;
using Raffaello.Core.Remote;
using Raffaello.Core.Statements;
using Raffaello.Core.Trust;
using Raffaello.Core.Wiring;

namespace Raffaello.Core.Tests;

/// <summary>Cross-module wiring (handoff step 2): assistant <- insights / documents / contract rules, assemblies <- drawings,
/// insights <- assemblies, cables <- drawings / document reader, data reset, statement preview bypass. Synthetic data only.</summary>
public class WiringAssistantTests
{
    private static JsonObject Run(AssistantEnv env, string tool, JsonObject input)
    {
        var o = new AssistantTools(env.Data).Execute(tool, input, "toolu_x", 0);
        Assert.False(o.IsError, o.Content);
        return JsonNode.Parse(o.Content)!.AsObject();
    }

    private static AssistantEnv With(AssistantEnv env, Func<AssistantData, AssistantData> f) => new()
    {
        Project = env.Project, Store = env.Store, Materials = env.Materials, Dir = env.Dir, Data = f(env.Data),
    };

    private static AssistantData Copy(AssistantData d, Func<IEnumerable<AnomalyItem>>? anomalies = null, IDocumentSearch? docs = null, Func<IDocumentStore?>? contractDocs = null) => new()
    {
        Project = d.Project, Store = d.Store, Settings = d.Settings, Materials = d.Materials, DraftFolder = d.DraftFolder,
        Anomalies = anomalies ?? d.Anomalies, Documents = docs ?? d.Documents, ContractDocs = contractDocs ?? d.ContractDocs,
    };

    [Fact]
    public void List_anomalies_uses_the_insights_engine_with_explanation_and_evidence()
    {
        using var env0 = AssistantEnv.Create();
        var env = With(env0, d => Copy(d, anomalies: () => InsightsAnomalies.ToItems(InsightsEngine.All(new AnomalyInputs { Project = d.Project.Snapshot, Today = DateTime.Today }))));
        var res = Run(env, AssistantTools.ListAnomalies, new JsonObject());
        Assert.Equal("insights engine", (string?)res["source"]);
        var items = res["items"]!.AsArray();
        Assert.NotEmpty(items);
        // P2-106 2ND FIX LIGHT is claimed by ROOTS and ABRAG: the insights SHARED KEY warning, explained, with the ledger lines as evidence
        var shared = items.First(i => (string?)i!["kind"] == AnomalyKinds.MultiSub)!;
        Assert.False(string.IsNullOrWhiteSpace((string?)shared["explanation"]));
        Assert.False(string.IsNullOrWhiteSpace((string?)shared["suggested_action"]));
        var ev = shared["evidence"]!.AsArray();
        Assert.Contains(ev, e => ((string?)e!["cite"])!.StartsWith("[[ledger:", StringComparison.Ordinal));
        Assert.Contains("[[room:P2-106]]", res["cite_tokens"]!.ToJsonString());
    }

    [Fact]
    public void List_anomalies_falls_back_to_the_built_in_checks_when_insights_fails()
    {
        using var env0 = AssistantEnv.Create();
        var env = With(env0, d => Copy(d, anomalies: () => throw new InvalidOperationException("insights store offline")));
        var res = Run(env, AssistantTools.ListAnomalies, new JsonObject());
        Assert.StartsWith("built-in checks (insights engine not available: insights store offline", (string?)res["source"]);
        Assert.Contains(res["items"]!.AsArray(), i => (string?)i!["kind"] == "OVER CAP");
    }

    [Fact]
    public void Search_documents_uses_the_fts_archive_with_page_and_record_citations()
    {
        using var env0 = AssistantEnv.Create();
        var docs = new SqliteDocumentStore((Db)env0.Project.Store);
        docs.EnsureSchema();
        var rec = docs.SaveRead(new DocRecord { FileName = "SUB-TEST-001 signed.pdf", DocType = "CONTRACT", LinkedTable = "Contract", LinkedKey = "SUB-TEST-001", StoredPath = "/evidence/contract.pdf", Sha256 = "abc" },
            new[]
            {
                new DocPageText { Page = 1, Text = "Subcontract agreement between the parties" },
                new DocPageText { Page = 3, Text = "Delay penalty SAR 5000 per week capped at 10 percent of the subcontract value" },
            }, Array.Empty<DocField>());
        var env = With(env0, d => Copy(d, docs: new ArchiveDocumentSearch(docs)));
        var res = Run(env, AssistantTools.SearchDocuments, new JsonObject { ["query"] = "penalty week" });
        Assert.Contains("document archive", (string?)res["searched"]);
        var hit = res["hits"]!.AsArray().First(h => (string?)h!["kind"] == "doc")!;
        Assert.Equal(3, (int)hit["page"]!);
        Assert.Equal($"[[doc:docrec{rec.Id}#p3]]", (string?)hit["cite"]);
        Assert.Equal("[[contract:SUB-TEST-001]]", (string?)hit["record_cite"]);
        Assert.Contains("penalty", ((string?)hit["snippet"])!, StringComparison.OrdinalIgnoreCase);
        // the citation opens the evidence file at the page
        var o = new AssistantTools(env.Data).Execute(AssistantTools.SearchDocuments, new JsonObject { ["query"] = "penalty week" }, "t", 0);
        var cite = o.Citations.First(c => c.Kind == "doc");
        Assert.Equal("/evidence/contract.pdf", cite.Path);
        Assert.Equal(3, cite.Page);
    }

    [Fact]
    public void Get_contract_terms_lists_the_obligations_calendar_of_the_signed_contract()
    {
        using var env0 = AssistantEnv.Create();
        var docs = new SqliteDocumentStore((Db)env0.Project.Store);
        docs.EnsureSchema();
        var due = DateTime.Today.AddDays(10);
        docs.Insert(new ContractTerms { ContractNo = "SUB-TEST-001", Subcontractor = "ROOTS", CompletionDate = due, DelayPenaltyPerWeek = 5000, DelayPenaltyCapPct = 0.10, WarrantyMonths = 12, RetentionPct = 0.10 });
        var env = With(env0, d => Copy(d, contractDocs: () => docs));
        var res = Run(env, AssistantTools.GetContractTerms, new JsonObject { ["subcontractor"] = "ROOTS" });
        var ob = res["obligations"]!.AsArray();
        var handover = ob.First(o => (string?)o!["kind"] == ObligationKinds.Handover)!;
        Assert.Equal(10, (int)handover["days_left"]!);
        Assert.Equal("DUE SOON", (string?)handover["status"]);
        Assert.Contains(ob, o => (string?)o!["kind"] == ObligationKinds.PenaltyStart);
        Assert.Contains(ob, o => (string?)o!["kind"] == ObligationKinds.PenaltyCap);   // value from the contract items / contract
        Assert.Equal(12, (int)res["signed_contract_terms"]![0]!["warranty_months"]!);
    }

    [Fact]
    public void Morning_brief_obligations_come_from_the_contract_rules_calendar()
    {
        using var env = AssistantEnv.Create();
        var today = new DateTime(2026, 10, 2, 7, 0, 0);
        var ob = ObligationsCalendar.Build(new[] { new ContractTerms { ContractNo = "SUB-TEST-001", Subcontractor = "ROOTS", CompletionDate = today.Date.AddDays(5) } }, Array.Empty<ContractRule>());
        var brief = MorningBriefBuilder.Build(new BriefInputs { Snapshot = env.Project.Snapshot, Now = today, Obligations = ob, Owner = "tester" });
        var sec = brief.Sections.Single(s => s.Code == "OBLIGATIONS");
        Assert.Contains(sec.Items, i => i.Title.Contains(ObligationKinds.Handover) && i.Module == "Contracts" && i.Key == "SUB-TEST-001" && i.Severity == "CHECK");
        // far obligations are not due yet
        var later = ObligationsCalendar.Build(new[] { new ContractTerms { ContractNo = "X", CompletionDate = today.Date.AddDays(90) } }, Array.Empty<ContractRule>());
        Assert.DoesNotContain(MorningBriefBuilder.Build(new BriefInputs { Snapshot = env.Project.Snapshot, Now = today, Obligations = later }).Sections.Single(s => s.Code == "OBLIGATIONS").Items, i => i.Title.Contains("X"));
        // without the calendar the CONTRACT queue items are used; through the hub the calendar wins
        var q = new[] { new QueueItem(Verdict.Due, "CONTRACT", "HANDOVER: Q-1 SUB", "from the queue", new NavTarget("Contracts", Key: "Q-1"), 1) };
        Assert.Contains(MorningBriefBuilder.Build(new BriefInputs { Snapshot = env.Project.Snapshot, Now = today, Queue = q }).Sections.Single(s => s.Code == "OBLIGATIONS").Items, i => i.Title == "HANDOVER: Q-1 SUB");
        var hub = new NotificationHub(env.Store, () => Array.Empty<INotificationChannel>()) { Clock = () => today, Obligations = () => ob };
        var b2 = hub.BuildBrief("tester", env.Project.Snapshot, q, "en");
        var items = b2.Sections.Single(s => s.Code == "OBLIGATIONS").Items;
        Assert.Contains(items, i => i.Key == "SUB-TEST-001");
        Assert.DoesNotContain(items, i => i.Title == "HANDOVER: Q-1 SUB");
    }

    [Fact]
    public void Selected_records_name_the_record_with_its_citation_token()
    {
        var line = new ClaimLine { Id = 42, Subcontractor = "ROOTS", InvoiceNo = 3, Room = "p2-106", Stage = Stages.Second, Item = "LIGHT", Qty = 4, SitePct = 1, WirPct = 0.5, WirNo = "WIR-EL-0007" };
        var text = SelectedRecords.LedgerLine(line);
        Assert.Contains("ledger line #42", text);
        Assert.Contains("[[room:P2-106]]", text);
        Assert.Contains("[[invoice:SUB-1|ROOTS|3]]", SelectedRecords.Invoice(new SubInvoice { ContractNo = "SUB-1", Subcontractor = "ROOTS", InvoiceNo = 3, Status = SubInvoiceStatus.Rejected }));
        Assert.Contains("[[dn:81064344]]", SelectedRecords.Dn("81064344", "CABLES", "RAF-PO-1"));
        Assert.Contains("[[po:RAF-PO-1]]", SelectedRecords.Po("RAF-PO-1"));
        Assert.Contains("[[variation:VO-007]]", SelectedRecords.Variation("VO-007", "extra sockets", "DRAFT"));
        Assert.Contains("[[cable:C-0001]]", SelectedRecords.CableRun(new CableRun { Ref = "C-0001", FromName = "A", ToName = "B", DesignLength = 50 }));
        Assert.Contains("[[takeoff:9]]", SelectedRecords.Takeoff(new DwgSheet { SheetNo = "E-101", Revision = "B" }, new DwgTakeoff { Id = 9, Hits = 12 }));
        Assert.Equal("", SelectedRecords.Room(""));
        // the assistant receives it with each question
        Assert.Contains("selected record: ledger line #42", new ScreenContext("ROOMS & LEDGER", SelectedRecord: text).Describe());
    }
}

public class WiringAssembliesInsightsTests
{
    private static readonly Dictionary<string, string> RoomTypes = new(StringComparer.OrdinalIgnoreCase) { ["R1"] = "2BR", ["R2"] = "2BR", ["C1"] = "CORRIDOR" };

    private static DrawingRouteLengths Table()
    {
        var light = new DwgSymbol { Id = 1, Name = "DOWNLIGHT", System = "LIGHT" };
        var socket = new DwgSymbol { Id = 2, Name = "SOCKET", System = "POWER" };
        var conduitL = new DwgLinearClass { Id = 10, Name = "CONDUIT LIGHT", System = "LIGHT" };
        var tray = new DwgLinearClass { Id = 11, Name = "TRAY 300", System = "" };   // no system: not a per-point route
        var hits = new List<DwgHit>
        {
            new() { SymbolId = 1, Room = "R1" }, new() { SymbolId = 1, Room = "R1" }, new() { SymbolId = 1, Room = "R2" }, new() { SymbolId = 1, Room = "R2" },
            new() { SymbolId = 1, Room = "C1" }, new() { SymbolId = 1, Room = "C1", Status = DwgHitStatus.Removed },
            new() { SymbolId = 2, Room = "R1" },
        };
        var runs = new List<DwgRun>
        {
            new() { ClassId = 10, Room = "R1", LengthM = 14 }, new() { ClassId = 10, Room = "R2", LengthM = 10 }, new() { ClassId = 10, Room = "C1", LengthM = 6 },
            new() { ClassId = 11, Room = "C1", LengthM = 80 },
        };
        var t = new DrawingRouteLengths();
        t.AddTakeoff(5, hits, runs, new Dictionary<long, DwgSymbol> { [1] = light, [2] = socket }, new Dictionary<long, DwgLinearClass> { [10] = conduitL, [11] = tray }, RoomTypes);
        return t;
    }

    [Fact]
    public void Drawing_route_lengths_average_per_system_and_room_type()
    {
        var t = Table();
        var spec = new ItemSpec { ItemType = ItemTypes.LightingPoint };
        var all = t.Find(spec)!;
        Assert.Equal(6.0, all.Metres);                    // 30 m of LIGHT conduit / 5 counted LIGHT points (removed hit left out)
        Assert.Contains("drawings takeoff", all.Source);
        Assert.Contains("5 LIGHT point(s)", all.Source);
        var twoBr = t.Find(spec, "2BR")!;
        Assert.Equal(6.0, twoBr.Metres);                  // 24 m / 4 points in 2BR rooms
        Assert.Contains("in 2BR rooms", twoBr.Source);
        Assert.Equal(6.0, t.Find(spec, "CORRIDOR")!.Metres);
        Assert.Null(t.Find(new ItemSpec { ItemType = ItemTypes.SocketPoint }));   // no POWER route measured: template default
        // exact cable lengths win over the ratio
        t.AddPointLengths(new[] { new PointLength { System = "LIGHT", RoomType = "2BR", TotalM = 9 }, new PointLength { System = "LIGHT", RoomType = "2BR", TotalM = 11 } });
        var exact = t.Find(spec, "2BR")!;
        Assert.Equal(10.0, exact.Metres);
        Assert.Contains("cable lengths", exact.Source);
    }

    [Fact]
    public void Assembly_breakdown_takes_route_len_from_the_drawings_and_says_so()
    {
        var lib = DefaultLibrary.Build();
        var template = lib.ForType(ItemTypes.LightingPoint)!;
        var spec = new ItemSpec { ItemType = ItemTypes.LightingPoint, System = "LIGHT" };
        var t = Table();
        var with = AssemblyCalculator.Calculate(spec, template, new CalcContext { Globals = lib.Globals, RouteLengthFor = s => t.Find(s) });
        Assert.Equal(6.0, with.Variables["route_len"]);
        Assert.Contains("drawings takeoff", with.RouteLengthSource);
        var conduit = with.Lines.Single(l => l.Key == "conduit");
        Assert.Equal(6.0 + with.Variables["drop_len"], conduit.QtyPerUnit, 6);
        var without = AssemblyCalculator.Calculate(spec, template, new CalcContext { Globals = lib.Globals, RouteLengthFor = _ => null });
        Assert.Contains("template default", without.RouteLengthSource);
        Assert.Equal(template.Params.Single(p => p.Name == "route_len").Value, without.Variables["route_len"]);
        // a failing drawings module never breaks the breakdown
        var broken = AssemblyCalculator.Calculate(spec, template, new CalcContext { Globals = lib.Globals, RouteLengthFor = _ => throw new IOException("share offline") });
        Assert.Contains("template default", broken.RouteLengthSource);
        // an override still wins and is named
        var over = AssemblyCalculator.Calculate(spec, template, new CalcContext { Globals = lib.Globals, RouteLengthFor = s => t.Find(s), Overrides = new Dictionary<string, double> { ["route_len"] = 9 } });
        Assert.Equal(9, over.Variables["route_len"]);
        Assert.Contains("item override", over.RouteLengthSource);
    }

    [Fact]
    public void Route_length_hook_reads_the_stored_takeoffs_and_caches_them()
    {
        var db = TestData.NewDb();
        db.Insert(new Room { Building = Buildings.Branded, Code = "R1", RoomType = "2BR" });
        var store = new DrawingStoreSelector(() => db);
        var sym = store.SaveSymbol(new DwgSymbol { Name = "DOWNLIGHT", System = "LIGHT" });
        var cls = store.SaveLinearClass(new DwgLinearClass { Name = "CONDUIT LIGHT", System = "LIGHT", StyleKeys = "x" });
        var sheet = store.SaveSheet(new DwgSheet { SheetNo = "E-101", FileName = "E-101.pdf", Building = Buildings.Branded, Level = "L01" });
        store.SaveTakeoff(new DwgTakeoff { SheetId = sheet.Id, RunAt = DateTime.Now },
            new[] { new DwgHit { SymbolId = sym.Id, Room = "R1" }, new DwgHit { SymbolId = sym.Id, Room = "R1" } },
            new[] { new DwgRun { ClassId = cls.Id, Room = "R1", LengthM = 15 } });
        var snap = new ProjectSnapshot { Rooms = db.All<Room>() };
        var now = new DateTime(2026, 10, 2, 9, 0, 0);
        var loads = 0;
        var hook = DrawingRouteLengths.Hook(() => { loads++; return store; }, () => snap, TimeSpan.FromMinutes(2), () => now);
        var hit = hook(new ItemSpec { ItemType = ItemTypes.LightingPoint })!;
        Assert.Equal(7.5, hit.Metres);
        Assert.Contains("1 sheet(s)", hit.Source);
        hook(new ItemSpec { ItemType = ItemTypes.LightingPoint });
        Assert.Equal(1, loads);
        now = now.AddMinutes(3);
        hook(new ItemSpec { ItemType = ItemTypes.LightingPoint });
        Assert.Equal(2, loads);
        // the service passes it to every breakdown
        var svc = new AssemblyService(new SqliteAssemblyStore(db.Path, "tester"), () => snap, () => null) { RouteLengthFor = hook };
        svc.Reload();
        var b = svc.Run(AssemblySource.FromText("Lighting point complete with PVC conduit and wiring", "No."), new ItemSpec { ItemType = ItemTypes.LightingPoint, System = "LIGHT" });
        Assert.Contains("drawings takeoff", b.RouteLengthSource);
        Assert.Equal(7.5, b.Variables["route_len"]);
    }

    private static (ProjectSnapshot Snap, MaterialsSnapshot Mats) ReconData()
    {
        var snap = new ProjectSnapshot
        {
            RoomQtys = { new RoomQty { Building = Buildings.Branded, Room = "R1", Stage = Stages.First, Item = "LIGHT", Qty = 100 }, new RoomQty { Building = Buildings.Branded, Room = "R1", Stage = Stages.Second, Item = "LIGHT", Qty = 100 } },
            Claims =
            {
                new ClaimLine { Id = 1, Building = Buildings.Branded, Subcontractor = "ROOTS", InvoiceNo = 1, Room = "R1", Stage = Stages.First, Item = "LIGHT", Qty = 40, SitePct = 1, WirPct = 1 },
                new ClaimLine { Id = 2, Building = Buildings.Branded, Subcontractor = "ROOTS", InvoiceNo = 1, Room = "R1", Stage = Stages.Second, Item = "LIGHT", Qty = 20, SitePct = 1, WirPct = 1 },
            },
        };
        var mats = new MaterialsSnapshot
        {
            Pos = { new MatPo { Id = 1, PoNo = "RAF-PO-9", Supplier = "PIPES" } },
            PoLines = { new MatPoLine { Id = 10, PoId = 1, LineNo = 1, Description = "PVC conduit 20 mm", Unit = "M", Qty = 1500, Rate = 2 } },
            Dns = { new MatDn { Id = 5, DnNo = "DN-1", Supplier = "PIPES", PoNo = "RAF-PO-9" } },
            DnLines = { new MatDnLine { Id = 20, DnId = 5, PoLineId = 10, Description = "PVC conduit 20 mm", Qty = 300, Unit = "M" } },
        };
        return (snap, mats);
    }

    [Fact]
    public void Insights_reconciliation_uses_the_assemblies_templates_instead_of_default_norms()
    {
        var (snap, mats) = ReconData();
        var lib = DefaultLibrary.Build();
        Breakdown? Run(ItemSpec s) => lib.ForType(s.ItemType) is { } t ? AssemblyCalculator.Calculate(s, t, new CalcContext { Globals = lib.Globals }) : null;
        var lines = AssemblyConsumption.Build(snap, mats, Run);
        var conduit = lines.Single(l => l.Material.Contains("conduit", StringComparison.OrdinalIgnoreCase) && l.Unit == "M");
        // 40 installed 1st-fix LIGHT points x (route_len + drop_len) x (1 + waste); whole project = 100 points
        var t = lib.ForType(ItemTypes.LightingPoint)!;
        var perPoint = (t.Params.Single(p => p.Name == "route_len").Value + t.Params.Single(p => p.Name == "drop_len").Value) * 1.05;
        Assert.Equal(40 * perPoint, conduit.Theoretical, 1);
        Assert.Equal(100 * perPoint, conduit.TheoreticalTotal, 1);
        Assert.Equal(300, conduit.Delivered, 1);                   // DN line through the matched PO line
        Assert.StartsWith("ASSEMBLY", conduit.Source);
        Assert.Contains(lines, l => l.Material.Contains("wire", StringComparison.OrdinalIgnoreCase) && l.Theoretical > 0);   // 2nd fix wires from 20 points

        var r = MaterialReconciliation.Build(snap, mats, Array.Empty<InsightNorm>(), null, lines);
        Assert.False(r.UsingDefaultNorms);
        Assert.True(r.UsingAssemblies);
        var row = r.Rows.Single(x => x.Material == conduit.Material);
        Assert.StartsWith("ASSEMBLY", row.NormSource);
        Assert.Equal(conduit.Theoretical, row.Theoretical, 3);
        Assert.Contains(r.Notes, n => n.Contains("Assemblies templates"));
        Assert.DoesNotContain(r.Rows, x => x.NormSource == "DEFAULT");

        // user norms win for their material, the assemblies cover the rest
        var norms = new[] { new InsightNorm { Material = "CONDUIT 20MM", Unit = "M", System = "LIGHT", Stage = Stages.First, PerPoint = 4, Source = "USER" } };
        var r2 = MaterialReconciliation.Build(snap, mats, norms, null, lines);
        Assert.Contains(r2.Rows, x => x.Material == "CONDUIT 20MM" && x.NormSource == "USER" && Math.Abs(x.Theoretical - 160) < 1e-6);
        Assert.DoesNotContain(r2.Rows, x => x.Material == conduit.Material);
        Assert.Contains(r2.Rows, x => x.NormSource.StartsWith("ASSEMBLY"));
        // no templates: the default norms as before
        Assert.True(MaterialReconciliation.Build(snap, mats, Array.Empty<InsightNorm>(), null, AssemblyConsumption.Build(snap, mats, _ => null)).UsingDefaultNorms);
    }

    [Fact]
    public void Assembly_consumption_through_the_service()
    {
        var (snap, mats) = ReconData();
        var path = Path.Combine(TestData.TempDir(), "asm.db");
        var svc = new AssemblyService(new SqliteAssemblyStore(path, "tester"), () => snap, () => mats);
        svc.Reload();
        var lines = AssemblyConsumption.Build(svc, snap, mats);
        Assert.Contains(lines, l => l.Material.Contains("conduit", StringComparison.OrdinalIgnoreCase) && l.Theoretical > 0 && l.PoRefs.Contains("RAF-PO-9 L1"));
    }
}

public class WiringCablesTests
{
    private static (Db Db, CableService Svc, CableRun Run) Register()
    {
        var (db, svc) = CablesServiceTests.NewService();
        svc.Import(new[] { CablesServiceTests.Claim("SUBA", 1, "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "4x16", 145) }, null, "INV 1");
        return (db, svc, svc.Load().Runs.Single());
    }

    [Fact]
    public void Measured_length_from_a_traced_route_between_two_panel_labels()
    {
        var (_, svc, run) = Register();
        var sheet = new DwgSheet { Id = 7, SheetNo = "E-201", Revision = "A", Building = Buildings.Hotel, MetresPerUnit = 0.05 };
        var routes = new[]
        {
            new SheetRoutes(sheet,
                new[]
                {
                    new DwgRun { Id = 1, Points = "100,100 1100,100 1100,500", LengthM = 70 },
                    new DwgRun { Id = 2, Points = "100,900 400,900", LengthM = 15 },   // ends near no panel name
                },
                new[] { new SheetLabel("SMDB-HT-Z1-LB2-CM-01", new PointD(90, 80)), new SheetLabel("LDB HT Z1 LB2 03", new PointD(1110, 520)), new SheetLabel("NOTE 3", new PointD(400, 905)) }),
        };
        var p = Assert.Single(CableMeasuredLengths.Propose(svc.Load(), routes));
        Assert.Equal(run.Id, p.Run.Id);
        Assert.Equal(70, p.MeasuredM);
        Assert.True(p.Changes);
        Assert.Contains("E-201 rev A", p.How);
        Assert.Equal(new long[] { 1 }, p.DwgRunIds.ToArray());

        // the user confirms: measured length written (audited) and the flags use it
        Assert.Equal(1, CableMeasuredLengths.Apply(svc, new[] { p }, "qs"));
        var stored = svc.Load().Runs.Single();
        Assert.Equal(70, stored.MeasuredLength);
        Assert.Contains("measured 70 m from drawing E-201 rev A", stored.Notes);
        Assert.Contains(svc.Flags(), f => f.Code == CableFlagCodes.OverLength && f.Message.Contains("measured length 70"));
        // nothing to change the second time
        Assert.False(CableMeasuredLengths.Propose(svc.Load(), routes).Single().Changes);
    }

    [Fact]
    public void Circuit_text_and_split_routes_are_added_up_per_sheet()
    {
        var (_, svc, run) = Register();
        var sheet = new DwgSheet { Id = 8, SheetNo = "E-202", Revision = "0", Building = Buildings.Hotel };
        var routes = new[]
        {
            new SheetRoutes(sheet,
                new[]
                {
                    new DwgRun { Id = 1, Points = "0,0 10,0", LengthM = 40, Circuit = "SMDB-HT-Z1-LB2-CM-01 TO LDB-HT-Z1-LB2-03" },
                    new DwgRun { Id = 2, Points = "0,0 10,0", LengthM = 25, Note = "LDB-HT-Z1-LB2-03 -> SMDB HT Z1 LB2 CM 01" },
                },
                Array.Empty<SheetLabel>()),
        };
        var p = Assert.Single(CableMeasuredLengths.Propose(svc.Load(), routes));
        Assert.Equal(65, p.MeasuredM);
        Assert.Equal(run.Id, p.Run.Id);
        Assert.Contains("2 traced run(s)", p.How);
        Assert.Equal(("DB-L1-01", "DB-L2-02"), CableMeasuredLengths.PairFromText("DB-L1-01 > DB-L2-02") is { } x ? (x.From, x.To) : default);
        Assert.Null(CableMeasuredLengths.PairFromText("ROUTE 3"));
    }

    [Fact]
    public void Words_on_one_line_are_joined_into_panel_labels()
    {
        var labels = CableMeasuredLengths.MergeWords(new[] { ("SMDB", new RectD(10, 10, 30, 8)), ("HT-Z1-LB2-CM-01", new RectD(44, 10, 80, 8)), ("FAR", new RectD(400, 10, 20, 8)) });
        Assert.Contains(labels, l => l.Text == "SMDB HT-Z1-LB2-CM-01");
        Assert.Contains(labels, l => l.Text == "FAR");
    }

    private sealed class FakeOcr : ILayoutOcrEngine
    {
        public string Name { get; init; } = "fake";
        public bool IsAvailable { get; init; } = true;
        public bool Throws { get; init; }
        public int Calls;
        public Task<string> RecognizeAsync(PageImage image, CancellationToken ct = default) => Task.FromResult("");
        public Task<OcrPage> RecognizeLayoutAsync(PageImage image, OcrHints hints, CancellationToken ct = default)
        {
            Calls++;
            if (Throws) throw new InvalidOperationException("model missing");
            return Task.FromResult(new OcrPage { Width = image.Width, Height = image.Height, Engine = Name, Words = { new OcrWord { Text = "SMDB-HT-Z1-LB2-CM-01", Box = new Box(10, 10, 100, 12), Confidence = 0.9 } } });
        }
    }

    private sealed class FakeRaster : IPageRasterizer
    {
        public string Name => "fake";
        public int Calls;
        public Task<PageImage?> RenderAsync(string pdfPath, int pageNumber, int dpi, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<PageImage?>(new PageImage { Width = 1000, Height = 700, Bytes = new byte[] { 1 } });
        }
    }

    [Fact]
    public async Task Scanned_sld_goes_through_the_document_rasterizer_and_the_first_available_layout_ocr()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var pdf = Path.Combine(TestData.TempDir(), "sld-scan.pdf");
        Document.Create(d => d.Page(p => p.Content().Text(""))).GeneratePdf(pdf);
        var missing = new FakeOcr { Name = "paddle", IsAvailable = false };
        var broken = new FakeOcr { Name = "broken", Throws = true };
        var windows = new FakeOcr { Name = "windows" };
        var ocr = new FirstAvailableLayoutOcr(missing, broken, windows);
        Assert.True(ocr.IsAvailable);
        Assert.Equal("broken", ocr.Name);
        var raster = new FakeRaster();
        var res = await CableReaders.ReadScanAsync(pdf, raster, ocr);
        Assert.Equal(CableSources.SldScan, res.Kind);
        Assert.Equal(1, raster.Calls);
        Assert.Equal(0, missing.Calls);
        Assert.Equal(1, broken.Calls);    // failed -> the next engine read the page
        Assert.Equal(1, windows.Calls);
        Assert.False(new FirstAvailableLayoutOcr(missing).IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FirstAvailableLayoutOcr(broken).RecognizeLayoutAsync(new PageImage(), OcrHints.Default));
    }
}

public class WiringResetAndPreviewTests
{
    [Fact]
    public void Module_entity_lists_cover_every_module_and_keep_the_trust_records()
    {
        foreach (var t in InsightsEntities.All.Concat(Assemblies.AssemblyEntities.All).Concat(DrawingEntities.All).Concat(CableStore.EntityTypes)
                     .Concat(DocumentEntities.All).Concat(AssistantEntityTypes.All).Concat(TrustEntities.All))
            Assert.Contains(t, ModuleEntities.All);
        Assert.All(TrustEntities.All, t => Assert.DoesNotContain(t, ModuleEntities.Resettable));
        Assert.Contains(typeof(Portal.PortalCompanySetting), ModuleEntities.ResetKept);
        Assert.Contains(typeof(InsightDismissal), ModuleEntities.Resettable);
        Assert.Contains(typeof(Assemblies.AsmTemplate), ModuleEntities.Resettable);
    }

    [Fact]
    public void Reset_clears_every_module_table_keeps_signatures_and_the_chain_continues()
    {
        var db = TestData.NewDb();
        db.Insert(new Room { Building = Buildings.Hotel, Code = "H-1" });
        var roomId = db.All<Room>().Single().Id;
        new SqliteInsightsStore(db.Path, "tester").Dismiss("FP|1", "X", "a warning", "checked on site");
        var asm = new SqliteAssemblyStore(db.Path, "tester");
        asm.EnsureSchema();
        asm.SeedDefaults();
        asm.SavePrice(new Assemblies.AsmPrice { Source = PriceSources.Manual, Description = "PVC conduit 20 mm", Unit = "M", Price = 2 });
        Assert.Single(asm.Prices());
        var dwg = new DrawingStoreSelector(() => db);
        dwg.SaveSymbol(new DwgSymbol { Name = "S", System = "LIGHT" });
        var (_, cables) = (db, new CableService(CableStore.For(db)));
        cables.Store.EnsureSchema();
        cables.Import(new[] { CablesServiceTests.Claim("SUBA", 1, "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "4x16", 145) }, null, "INV 1");
        var docs = new SqliteDocumentStore(db);
        docs.EnsureSchema();
        docs.SaveRead(new DocRecord { FileName = "c.pdf", Sha256 = "x" }, new[] { new DocPageText { Page = 1, Text = "retention clause" } }, Array.Empty<DocField>());
        Assert.NotEmpty(docs.Search("retention"));
        var assistant = new SqliteAssistantStore(db.Path, "tester");
        assistant.EnsureSchema();
        assistant.Insert(new AssistantReminder { Owner = "tester", Text = "check", Due = DateTime.Today });
        var trust = new SqliteTrustStore(db);
        var keys = new SigningKeyStore(TestData.TempDir(), new PassphraseKeyProtector("pw") { Iterations = 1000 });
        trust.RegisterKey(keys.GetOrCreate("tester"), new SignerInfo("tester", "Tester", "QS"));

        db.ClearAll();

        Assert.Empty(new SqliteInsightsStore(db.Path, "tester").Load().Dismissals);
        Assert.Empty(new SqliteAssemblyStore(db.Path, "tester").Prices());
        Assert.Empty(dwg.Symbols());
        Assert.Empty(cables.Load().Claims);
        Assert.Empty(cables.Load().Runs);
        Assert.Empty(docs.All<DocRecord>());
        Assert.Empty(docs.Search("retention"));                  // the FTS index is cleared too
        Assert.Empty(assistant.All<AssistantReminder>());
        Assert.Single(trust.Keys());                            // signing keys / signatures survive a reset
        // ids continue: a new record never takes the id of a cleared (possibly signed) one
        var again = db.Insert(new Room { Building = Buildings.Hotel, Code = "H-2" });
        Assert.True(again.Id > roomId);
        var rep = SqliteAuditChain.Verify(db.Path);
        Assert.True(rep.Ok, rep.ToText());
        Assert.NotNull(rep.ResetAt);
    }

    private static StatementImportResult Preview(Db db, ProjectSnapshot snap)
    {
        var res = new StatementImportResult { Subcontractor = "ROOTS", StatementNo = "ST-ROOTS-02" };
        var line = new ClaimLine { Building = Buildings.Hotel, Subcontractor = "ROOTS", InvoiceNo = 2, Room = "H-101", Stage = Stages.First, Item = "LIGHT", Qty = 10, QtyAbove45 = 4, SitePct = 1, WirPct = 1, Source = "STATEMENT" };
        res.Claims.Add(line);
        res.CableClaims.Add(CablesServiceTests.Claim("ROOTS", 2, "ldb-ht-z1-lb2-3", "SMDB-HT-Z1-LB2-CM-01", "4X16", 140, key: "ST|ROOTS|2|1"));
        CableHooks.AnnotateStatement(res, db);
        return res;
    }

    [Fact]
    public void Statement_preview_bypasses_cable_flags_and_contract_warnings_with_a_reason()
    {
        var db = TestData.NewDb();
        db.Insert(new Contract { ContractNo = "SUB-T-1", Subcontractor = "ROOTS", Building = Buildings.Hotel });
        var cables = new CableService(CableStore.For(db));
        cables.Store.EnsureSchema();
        cables.Import(new[] { CablesServiceTests.Claim("SAFIA", 1, "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "4x16", 145) }, null, "INV 1");
        var docs = new SqliteDocumentStore(db);
        docs.EnsureSchema();
        docs.Insert(new ContractRule { ContractNo = "SUB-T-1", RuleType = RuleTypes.HeightBand, ParamsJson = "{\"meters\":4.5}", Summary = "Rates split at 4.5 m", ClauseNo = "7", SourcePage = 4 });
        var snap = new ProjectSnapshot { Contracts = db.All<Contract>() };

        var res = Preview(db, snap);
        StatementPreviewChecks.RuleWarnings(res, snap, docs);
        var dup = Assert.Single(res.CableFlags, f => f.Code == CableFlagCodes.Duplicate);
        var height = Assert.Single(res.RuleWarnings, w => w.Code == "HEIGHT_CHECK");
        Assert.False(dup.IsBypassed);
        Assert.Throws<ArgumentException>(() => StatementPreviewChecks.BypassCableFlags(db, new[] { dup }, " "));

        Assert.Equal(1, StatementPreviewChecks.BypassCableFlags(db, new[] { dup }, "re-pulled after damage, agreed with the engineer"));
        Assert.Equal(1, StatementPreviewChecks.BypassRuleWarnings(docs, new[] { height }, "checked on site with the consultant", DateTime.Now));
        StatementPreviewChecks.Refresh(res, db, snap, docs);
        Assert.True(res.CableFlags.Single(f => f.Code == CableFlagCodes.Duplicate).IsBypassed);
        Assert.True(res.RuleWarnings.Single(w => w.Code == "HEIGHT_CHECK").IsBypassed);

        // recorded exactly as on the Cables page / invoice WARNINGS tab: who, why, audited - and it sticks to the claim once posted
        var d = cables.Load().Decisions.Single();
        Assert.Equal("tester", d.By);
        Assert.Contains("re-pulled", d.Reason);
        var b = docs.All<RuleBypass>().Single();
        Assert.Equal(RuleContexts.Ledger, b.Context);
        Assert.Equal("tester", b.BypassedBy);
        Assert.Contains(db.RecentAudit(50), a => a.Summary.Contains("Cable flag bypassed") && a.Summary.Contains("re-pulled"));
        Assert.Contains(db.RecentAudit(50), a => a.Summary.StartsWith("BYPASS HEIGHT_BAND"));
        Assert.True(docs.RuleEngine().CheckClaim("SUB-T-1", res.Claims[0]).Single(w => w.Code == "HEIGHT_CHECK").IsBypassed);
        // a fresh preview of the same statement shows them bypassed
        var again = Preview(db, snap);
        StatementPreviewChecks.RuleWarnings(again, snap, docs);
        Assert.True(again.CableFlags.Single(f => f.Code == CableFlagCodes.Duplicate).IsBypassed);
        Assert.All(again.RuleWarnings, w => Assert.True(w.IsBypassed));
    }
}
