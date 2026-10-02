using ClosedXML.Excel;
using Raffaello.Core.Assemblies;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Tests;

/// <summary>[assemblies] BOQ item breakdown: formulas, parser (EN / AR), calculator, prices, contract labour, store, bulk, exports.</summary>
public class AssembliesTests
{
    // synthetic descriptions in the style of the subcontract schedules (no company files in the repo)
    private const string Pvc1stWallLow = "install, and hand over a PVC 1st Fix outlet for a lighting switch, power socket, or wall-mounted lighting point, in accordance with good engineering practice. Rate shall include wall chasing/cutting for installation at heights below 4.5 m.";
    private const string Pvc1stCeilLow = "install, and hand over a PVC 1st Fix outlet for a lighting switch, power socket, or ceiling lighting point, in accordance with good engineering practice. Rate shall include wall chasing/cutting for installation at heights below 4.5 m.";
    private const string Pvc1stCeilHigh = "install, and hand over a PVC 1st Fix outlet for a lighting switch, power socket, or ceiling lighting point, in accordance with good engineering practice. Rate shall include wall chasing/cutting for installation at heights above 4.5 m.";
    private const string Wire2ndLow = "Wire pulling and termination for 2nd Fix works for a lighting switch, power socket, or wall/ceiling lighting point, for installation heights below 4.5 m. Rate shall include numbering/labeling.";
    private const string Flex3rdLow = "install, and hand over a flexible conduit for the 3rd Fix outlet for a lighting switch, power socket, lighting point, or DALI outlet (wall-mounted or ceiling-mounted), for installation heights below 4.5 m.";
    private const string Acc3rdLow = "install, and hand over a 3rd Fix outlet for a lighting switch, power socket, or wall/ceiling lighting point, for installation heights below 4.5 m. Rate shall include cleaning of outlets and numbering/labeling.";
    private const string Pull4x16 = "Pull and hand over cable 16 mm² (4C or 3×16) including required tagging/labeling. Rate shall include installation of cable ties and handover to the Consultant.";
    private const string Term4x16 = "install, test, and commission cable 16 mm² (4C or 3×16) including required tagging/labeling. Rate shall include installation of glands and handing over to the Consultant.";
    private const string ArabicCeiling = "تركيب و تسليم مخرج من النوع PVC 1st Fix مفتاح انارة أو بريزة أو مخرج إنارة سقفي طبقا لأصول الصناعة و مواصفات المشروع و ذلك لإرتفاع أقل 4.5 متر";

    // ------------------------------------------------------------------ formulas

    [Fact]
    public void Formula_evaluates_precedence_functions_and_lazy_if()
    {
        var v = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["conduit"] = 6.5, ["stick_len"] = 3, ["is_pvc"] = 1, ["zero"] = 0 };
        Assert.Equal(3, Formula.Eval("ceil(conduit / stick_len)", v));
        Assert.Equal(7, Formula.Eval("1 + 2 * 3", v));
        Assert.Equal(9, Formula.Eval("(1 + 2) * 3", v));
        Assert.Equal(8, Formula.Eval("2 ^ 3", v));
        Assert.Equal(-2, Formula.Eval("-2", v));
        Assert.Equal(1, Formula.Eval("conduit > 6 && is_pvc == 1", v));
        Assert.Equal(0, Formula.Eval("not is_pvc", v));
        Assert.Equal(5, Formula.Eval("max(1, 5, 3)", v));
        Assert.Equal(1.23, Formula.Eval("round(1.234, 2)", v));
        Assert.Equal(6, Formula.Eval("roundup(5.2, 3)", v));
        // the branch that is not taken may divide by zero
        Assert.Equal(0, Formula.Eval("if(zero > 0, 2 / zero, 0)", v));
        Assert.Equal(2, Formula.Eval("if(is_pvc, 2, 1 / zero)", v));
        Assert.Throws<FormulaException>(() => Formula.Eval("2 / zero", v));
        Assert.Throws<FormulaException>(() => Formula.Eval("unknown_name + 1", v));
        Assert.Throws<FormulaException>(() => Formula.Eval("ceil(1", v));
        Assert.Equal(new[] { "conduit", "stick_len" }, Formula.Names("ceil(conduit / stick_len)"));
        Assert.Equal("", Formula.Validate("1 / (x - 1)", new[] { "x" }));
        Assert.Contains("unknown", Formula.Validate("x + y", new[] { "x" }));
    }

    // ------------------------------------------------------------------ parser

    [Fact]
    public void Parser_reads_contract_point_items_in_english_and_arabic()
    {
        var s = ItemParser.Parse(Pvc1stWallLow, "No.", ItemSourceKind.Contract);
        Assert.Equal(ItemTypes.LightingPoint, s.ItemType);
        Assert.Equal(new[] { ItemTypes.SocketPoint, ItemTypes.SwitchPoint }, s.AlsoCovers);
        Assert.Equal("PVC", s.Conduit);
        Assert.Equal("WALL", s.Mount);
        Assert.Equal("LOW", s.Height);
        Assert.Equal(new[] { StageNames.First }, s.Stages);
        Assert.Equal(SupplyScopes.LabourOnly, s.Supply);
        Assert.Equal("CONCEALED", s.Installation);

        var hi = ItemParser.Parse(Pvc1stCeilHigh, "No.", ItemSourceKind.Contract);
        Assert.Equal("CEILING", hi.Mount);
        Assert.Equal("HIGH", hi.Height);

        var w = ItemParser.Parse(Wire2ndLow, "No.", ItemSourceKind.Contract);
        Assert.Equal(new[] { StageNames.Second }, w.Stages);
        var f = ItemParser.Parse(Flex3rdLow, "No.", ItemSourceKind.Contract);
        Assert.Equal(ItemTypes.FlexDrop, f.ItemType);
        Assert.Equal("LIGHT", f.System);

        var ar = ItemParser.Parse(ArabicCeiling, "عدد", ItemSourceKind.Contract);
        Assert.Equal(ItemTypes.LightingPoint, ar.ItemType);
        Assert.Equal("CEILING", ar.Mount);
        Assert.Equal("LOW", ar.Height);
        Assert.Equal("PVC", ar.Conduit);
        Assert.Equal(new[] { StageNames.First }, ar.Stages);
        Assert.Equal(SupplyScopes.LabourOnly, ar.Supply);
        Assert.Contains(ItemTypes.SocketPoint, ar.AlsoCovers);
    }

    [Theory]
    [InlineData("Pull and hand over cable 16 mm² (4C or 3×16) including tagging", ItemTypes.CableRun, 4, 16)]
    [InlineData("install, test, and commission cable 240 mm² (4C or 3×240) including glands", ItemTypes.CableTermination, 4, 240)]
    [InlineData("install, test, and commission earth cable 16 mm² (1C) including glands", ItemTypes.CableTermination, 1, 16)]
    [InlineData("سحب وتسليم كابل مقاس 16*4C أو 3X16 وعمل الترقيم المطلوب", ItemTypes.CableRun, 4, 16)]
    [InlineData("تركيب وتسليم واختبار كابل مقاس 70*1C أرضي وعمل الترقيم المطلوب", ItemTypes.CableTermination, 1, 70)]
    [InlineData("4C 16mm2 Cu/XLPE/SWA/LSHZ + 1C 16mm2 Cu/LSF G/Y", ItemTypes.CableRun, 4, 16)]
    [InlineData("4C 50mm2 Fire Rated + 1C 25mm2 Fire Rated/ECC", ItemTypes.CableRun, 4, 50)]
    public void Parser_reads_cables(string text, string type, int cores, double size)
    {
        var s = ItemParser.Parse(text, "", ItemSourceKind.Contract);
        Assert.Equal(type, s.ItemType);
        Assert.Equal(cores, s.CableCores);
        Assert.Equal(size, s.CableSizeMm2);
    }

    [Fact]
    public void Parser_reads_cpc_fire_rating_and_owner_boq_items()
    {
        var c = ItemParser.Parse("4C 16mm2 Cu/XLPE/SWA/LSHZ + 1C 16mm2 Cu/LSF G/Y", "m", ItemSourceKind.Boq);
        Assert.False(c.EarthCable);
        Assert.Equal(16, c.CpcSizeMm2);
        Assert.Equal(SupplyScopes.SupplyInstall, c.Supply);
        Assert.True(ItemParser.Parse("4C 50mm2 Fire Rated + 1C 25mm2 Fire Rated/ECC").FireRated);

        Assert.Equal(ItemTypes.SocketPoint, ItemParser.Parse("small power points", "", ItemSourceKind.Boq).ItemType);
        Assert.Equal(ItemTypes.LightingPoint, ItemParser.Parse("lighting points, to apartment - number", "", ItemSourceKind.Boq).ItemType);
        Assert.Equal(ItemTypes.Accessory, ItemParser.Parse("13A twin switched socket outlet - W/P").ItemType);
        Assert.Equal(ItemTypes.Accessory, ItemParser.Parse("10A two way, two gang switch").ItemType);
        Assert.Equal(ItemTypes.Luminaire, ItemParser.Parse("recessed medium beam downlight, 8W, IP20; D6").ItemType);
        Assert.Equal(ItemTypes.Luminaire, ItemParser.Parse("EM5, 3.5 W ceiling surface, IP40").ItemType);
        Assert.Equal(ItemTypes.DataPoint, ItemParser.Parse("data point for security network").ItemType);
        Assert.Equal(ItemTypes.Db, ItemParser.Parse("LDB-BR-Z2-L02-01, 40A").ItemType);
        Assert.Equal(ItemTypes.MainPanel, ItemParser.Parse("ESMDB-HT-Z4-LB1, 400A, 10 ways").ItemType);
        Assert.Equal(ItemTypes.SystemPanel, ItemParser.Parse("42U IT RACK sub-fibre distribution").ItemType);
        Assert.Equal(ItemTypes.Unknown, ItemParser.Parse("generally").ItemType);
        Assert.Equal(SupplyScopes.SupplyInstall, ItemParser.Parse("Supply and install 13A switched socket outlet").Supply);
        Assert.Equal(SupplyScopes.SupplyOnly, ItemParser.Parse("Supply only 13A switched socket outlet").Supply);
    }

    [Fact]
    public void Parser_reads_trays_isolators_panels_and_elv()
    {
        var t = ItemParser.Parse("install, and hand over cable supports (cable tray, cable trunking, or cable ladders) in sizes ranging from 40 cm to 80 cm, including couplers/joints and earthing. For installation heights above 4.5 m", "mt", ItemSourceKind.Contract);
        Assert.Equal(ItemTypes.Tray, t.ItemType);
        Assert.Equal(600, t.TrayWidthMm);
        Assert.Equal("HIGH", t.Height);
        Assert.Equal(ItemTypes.TrayCover, ItemParser.Parse("install, and hand over covers for cable supports (cable tray) in sizes above 80 cm", "mt").ItemType);

        var iso = ItemParser.Parse("install, test, and commission a 60A isolator switch (stand-mounted) including internal and external flexible conduit", "no", ItemSourceKind.Contract);
        Assert.Equal(ItemTypes.Isolator, iso.ItemType);
        Assert.Equal(60, iso.Amps);
        Assert.Equal("STAND", iso.Mount);

        var db = ItemParser.Parse("Installation, connection, testing, and handover of electrical panel (6–12 ways) – labor only", "no", ItemSourceKind.Contract);
        Assert.Equal(ItemTypes.Db, db.ItemType);
        Assert.Equal(12, db.Ways);
        Assert.Equal(SupplyScopes.LabourOnly, db.Supply);
        Assert.Equal(ItemTypes.Db, ItemParser.Parse("تركيب وتوصيل واختبار وتسليم لوحة كهرباء 48 خط ( مصنعيات فقط )").ItemType);

        Assert.Equal(ItemTypes.FireAlarmPoint, ItemParser.Parse("install, and hand over a PVC 1st Fix outlet for fire alarm, speaker, A/V, or BGM system, wall-mounted").ItemType);
        var elv = ItemParser.Parse("install, and hand over an EMT 1st Fix outlet for Access Control point");
        Assert.Equal(ItemTypes.ElvPoint, elv.ItemType);
        Assert.Equal("ACCESS", elv.System);
        Assert.Equal(ItemTypes.ConduitRun, ItemParser.Parse("install, and hand over an RS 1st Fix outlet for BMS system, wall/ceiling-mounted", "MT.").ItemType);
        Assert.Equal(ItemTypes.Homerun, ItemParser.Parse("Wire pulling and termination for 2nd Fix homerun circuit wiring for a lighting point on cable tray or trunking").ItemType);
        Assert.Equal(ItemTypes.ElvCableRun, ItemParser.Parse("Pull fire alarm cable, fiber optic cable, or any low current system cable, installed on cable tray").ItemType);
        Assert.Equal(ItemTypes.FloorBox, ItemParser.Parse("Installation and fixing of 3rd Fix Floor Box (3 compartments)").ItemType);
        Assert.Equal(3, ItemParser.Parse("Installation and fixing of 3rd Fix Floor Box (3 compartments)").Compartments);
        Assert.Equal(ItemTypes.EarthPit, ItemParser.Parse("install, and commission an earth pit (earthing manhole) complete with a 3.5 m copper earth rod").ItemType);
        Assert.Equal(ItemTypes.GrmsPoint, ItemParser.Parse("install, and hand over a PVC 1st Fix outlet for GRMS point, ceiling-mounted").ItemType);
        Assert.Equal(ItemTypes.DaliPoint, ItemParser.Parse("install, and hand over a PVC 1st Fix DALI outlet (wall-mounted)").ItemType);
        Assert.Equal(ItemTypes.LinearLight, ItemParser.Parse("install, and hand over a 3rd Fix outlet for linear lighting fixtures (including installation of aluminum profile)", "mt").ItemType);
        var conduit = ItemParser.Parse("Supply and install 25mm PVC conduit concealed in walls", "m");
        Assert.Equal(ItemTypes.ConduitRun, conduit.ItemType);
        Assert.Equal(25, conduit.ConduitSizeMm);
        var wired = ItemParser.Parse("lighting point wired in 3x2.5mm2 LSOH wires in 25 mm PVC conduit");
        Assert.Equal(3, wired.WireCores);
        Assert.Equal(2.5, wired.WireSizeMm2);
        Assert.Equal(25, wired.ConduitSizeMm);
    }

    // ------------------------------------------------------------------ library + calculator

    private static AssemblyLibrary Lib() => DefaultLibrary.Build();

    private static CalcContext Ctx(AssemblyLibrary lib, ItemSourceKind src = ItemSourceKind.Boq, IPriceResolver? prices = null, ILabourRateResolver? labour = null, double? reference = null, string mode = "AUTO",
        Dictionary<string, double>? overrides = null) =>
        new() { Globals = lib.Globals, Prices = prices, LabourRates = labour, Settings = new AssemblySettings { LabourMode = mode }, Source = src, ReferenceRate = reference, ReferenceLabel = "BOQ", Overrides = overrides };

    [Fact]
    public void Default_library_formulas_are_valid_and_every_type_has_a_template()
    {
        var lib = Lib();
        foreach (var t in lib.Templates) Assert.Empty(TemplateCheck.Problems(t, lib.Globals));
        foreach (var type in ItemTypes.All.Where(t => t != ItemTypes.Unknown)) Assert.NotNull(lib.ForType(type));
        Assert.Equal(lib.Templates.Count, lib.Templates.Select(t => t.Code).Distinct().Count());
        Assert.All(lib.Globals, g => Assert.False(g.Confirmed));
    }

    [Fact]
    public void Lighting_point_breakdown_follows_the_formulas()
    {
        var lib = Lib();
        var spec = ItemParser.Parse("lighting points, to apartment - number", "", ItemSourceKind.Boq);
        var b = AssemblyCalculator.Calculate(spec, lib.ForType(spec.ItemType)!, Ctx(lib));
        BreakdownLine L(string key) => b.Lines.Single(l => l.Key == key);
        Assert.Equal(6.5, L("conduit").QtyPerUnit);                // route 6 + drop 0.5
        Assert.Equal(6.825, L("conduit").TotalQty, 3);              // + 5 % waste
        Assert.Equal(3, L("coupling").QtyPerUnit);                  // ceil(6.5 / 3)
        Assert.Equal(7, L("saddle").QtyPerUnit);                    // ceil(6.5 / 1)
        Assert.Equal(2, L("bend").QtyPerUnit);
        Assert.Equal(1, L("box").QtyPerUnit);
        Assert.Contains("circular box", L("box").Spec);
        Assert.Equal((6.5 + 0.6) * 2, L("wire_ln").QtyPerUnit, 6);   // L + N
        Assert.Equal(6.5 + 0.6, L("wire_e").QtyPerUnit, 6);          // E
        Assert.Contains("1.5mm2", L("wire_ln").Spec);
        Assert.Equal((2 * 3 + 2 * 2 + 2 + 2 * 0.65) * 0.0025, L("glue").QtyPerUnit, 6);
        Assert.Equal(1, L("flex").QtyPerUnit);                      // ceiling drop
        Assert.Equal(LabourBases.Subcontract, b.LabourMode);
        Assert.Contains(b.Lines, l => l.Key == "sub_1st_fix" && l.UnitPrice == 55);   // template default (no contract loaded)
        Assert.DoesNotContain(b.Lines, l => l.Key.StartsWith("hr_", StringComparison.Ordinal));
        Assert.Equal(55 + 28 + 25 + 5, b.Labour);
        Assert.Equal(Variations.VariationMath.BuildUpRate(b.Material, b.Labour, 0, 0.10, 0.10), b.Rate);
        Assert.True(b.DefaultPrices > 0);
        Assert.Equal("NO REFERENCE RATE", b.Verdict);
    }

    [Fact]
    public void Conduit_type_stage_scope_supply_scope_and_overrides_change_the_breakdown()
    {
        var lib = Lib();
        var emt = ItemParser.Parse("Supply and install EMT 1st Fix outlet for a power socket, wall-mounted, below 4.5 m");
        Assert.Equal(ItemTypes.SocketPoint, emt.ItemType);
        var b = AssemblyCalculator.Calculate(emt, lib.ForType(emt.ItemType)!, Ctx(lib));
        Assert.All(b.Lines, l => Assert.Equal(StageNames.First, l.Stage));       // 1st fix only
        Assert.DoesNotContain(b.Lines, l => l.Key is "glue" or "bend");          // no glue, EMT bent on site
        Assert.Contains("EMT conduit 25 mm", b.Lines.Single(l => l.Key == "conduit").Spec);
        Assert.Contains("GI back box", b.Lines.Single(l => l.Key == "box").Spec);
        Assert.Equal(70, b.Labour);                                               // EMT 1st fix default

        // labour-only subcontract item, hours basis: materials are free issue (listed, not in the rate)
        var contract = ItemParser.Parse(Pvc1stCeilLow, "No.", ItemSourceKind.Contract);
        var c = AssemblyCalculator.Calculate(contract, lib.ForType(contract.ItemType)!, Ctx(lib, ItemSourceKind.Contract, reference: 55));
        Assert.Equal(LabourBases.Hours, c.LabourMode);
        Assert.Equal(0, c.Material);
        Assert.True(c.FreeIssueMaterial > 0);
        Assert.All(c.Lines.Where(l => l.Kind == ComponentKinds.Material), l => Assert.Equal(BreakdownFlags.FreeIssue, l.Flag));
        Assert.Equal(1.5 * 18, c.Labour, 6);
        Assert.Equal("OK", c.Verdict);

        // per-item override + reference below cost
        var o = AssemblyCalculator.Calculate(contract, lib.ForType(contract.ItemType)!, Ctx(lib, ItemSourceKind.Contract, reference: 5, overrides: new() { ["route_len"] = 10 }));
        Assert.Equal(10.5, o.Lines.Single(l => l.Key == "conduit").QtyPerUnit);
        Assert.Equal("CONTRACT RATE BELOW COST", o.Verdict);
        Assert.Contains(o.Flags, f => f.Contains("below the direct cost"));

        // supply only: labour excluded
        var supply = ItemParser.Parse("Supply only 13A switched socket outlet");
        var s = AssemblyCalculator.Calculate(supply, lib.ForType(supply.ItemType)!, Ctx(lib));
        Assert.Equal(0, s.Labour);
        Assert.Contains(s.Lines, l => l.Kind == ComponentKinds.Labour && !l.Included);

        // route length hook (Drawings module)
        var hook = new CalcContext { Globals = lib.Globals, RouteLength = t => t == ItemTypes.LightingPoint ? 9 : null };
        var lp = ItemParser.Parse("lighting points");
        Assert.Equal(9.5, AssemblyCalculator.Calculate(lp, lib.ForType(lp.ItemType)!, hook).Lines.Single(l => l.Key == "conduit").QtyPerUnit);
    }

    [Fact]
    public void Cable_run_uses_po_prices_cpc_and_contract_labour()
    {
        var lib = Lib();
        var po = new[]
        {
            new PoPrice("PO-1", "TEST CABLES", new DateTime(2026, 4, 26), 13, "", "4X16 CU/XLPE/SWA/LSOH", "M", 42.089, 0, 7650),
            new PoPrice("PO-1", "TEST CABLES", new DateTime(2026, 4, 26), 14, "", "4X16 CU/MICA/XLPE/SWA/LSOH", "M", 47.242, 0, 2100),
            new PoPrice("PO-1", "TEST CABLES", new DateTime(2026, 4, 26), 33, "", "1X16 CU/LSOH (G/Y)", "KM", 8735, 0, 17.3),
        };
        var book = new PriceBook(Array.Empty<AsmPrice>(), po, new AssemblySettings());
        var items = new List<ContractItem>
        {
            new() { ContractNo = "SUB-1", ItemNo = "55", Order = 55, Description = Term4x16, Unit = "End", Rate = 36 },
            new() { ContractNo = "SUB-1", ItemNo = "85", Order = 85, Description = Pull4x16, Unit = "mt", Rate = 15 },
            new() { ContractNo = "SUB-1", ItemNo = "86", Order = 86, Description = "Pull and hand over cable 10 mm² (4C or 3×10)", Unit = "mt", Rate = 14 },
        };
        var labour = new ContractLabour(items);
        var spec = ItemParser.Parse("4C 16mm2 Cu/XLPE/SWA/LSHZ + 1C 16mm2 Cu/LSF G/Y", "m", ItemSourceKind.Boq);
        var b = AssemblyCalculator.Calculate(spec, lib.ForType(spec.ItemType)!, Ctx(lib, prices: book, labour: labour, reference: 80));
        var cable = b.Lines.Single(l => l.Key == "cable");
        Assert.Equal(42.089, cable.UnitPrice, 3);
        Assert.Equal(PriceSources.Po, cable.PriceSource);
        Assert.Contains("PO-1 line 13", cable.PriceRef);
        var cpc = b.Lines.Single(l => l.Key == "cpc");
        Assert.Equal(8.735, cpc.UnitPrice, 3);                                 // KM -> M
        var pull = b.Lines.Single(l => l.Key == "sub_pull");
        Assert.Equal(15, pull.UnitPrice);
        Assert.Contains("item 85", pull.PriceRef);
        var term = b.Lines.Single(l => l.Key == "sub_term");
        Assert.Equal(36, term.UnitPrice);
        Assert.Equal(2.0 / 30, term.QtyPerUnit, 4);                           // two ends over a 30 m run
        Assert.Equal(Math.Round(1.03 * 42.089, 2), cable.Amount);
        Assert.Equal("OK", b.Verdict);
    }

    [Fact]
    public void Contract_labour_matches_stage_conduit_height_and_mount()
    {
        var items = new List<ContractItem>
        {
            new() { ContractNo = "C", ItemNo = "1", Order = 1, Description = Pvc1stWallLow, Unit = "No.", Rate = 55 },
            new() { ContractNo = "C", ItemNo = "3", Order = 3, Description = Pvc1stCeilLow, Unit = "No.", Rate = 56 },
            new() { ContractNo = "C", ItemNo = "4", Order = 4, Description = Pvc1stCeilHigh, Unit = "No.", Rate = 57 },
            new() { ContractNo = "C", ItemNo = "7", Order = 7, Description = "install, and hand over an EMT 1st Fix outlet for a lighting switch, power socket, lighting point (wall-mounted or ceiling-mounted), heights below 4.5 m.", Unit = "No.", Rate = 70 },
            new() { ContractNo = "C", ItemNo = "11", Order = 11, Description = Wire2ndLow, Unit = "No.", Rate = 28 },
            new() { ContractNo = "C", ItemNo = "17", Order = 17, Description = Acc3rdLow, Unit = "No.", Rate = 25 },
            new() { ContractNo = "C", ItemNo = "288", Order = 288, Description = "Pulling and installation of EV Charging 2nd Fix wiring", Unit = "no", Rate = 99 },
        };
        var cl = new ContractLabour(items);
        var ceilingLow = new ItemSpec { ItemType = ItemTypes.LightingPoint, Conduit = "PVC", Mount = "CEILING", Height = "LOW" };
        Assert.Equal(56, cl.Find(ceilingLow, "1ST FIX")!.Price);
        Assert.Equal(57, cl.Find(new ItemSpec { ItemType = ItemTypes.LightingPoint, Conduit = "PVC", Mount = "CEILING", Height = "HIGH" }, "1ST FIX")!.Price);
        Assert.Equal(70, cl.Find(new ItemSpec { ItemType = ItemTypes.SocketPoint, Conduit = "EMT", Mount = "WALL" }, "1ST FIX")!.Price);
        Assert.Equal(28, cl.Find(new ItemSpec { ItemType = ItemTypes.SocketPoint, System = "POWER" }, "2ND FIX")!.Price);   // not the EV item
        Assert.Equal(25, cl.Find(ceilingLow, "3RD FIX")!.Price);
        Assert.Null(cl.Find(ceilingLow, "FLEX"));
        Assert.Null(cl.Find(new ItemSpec { ItemType = ItemTypes.DataPoint, Conduit = "PVC" }, "1ST FIX"));
    }

    // ------------------------------------------------------------------ prices

    [Fact]
    public void Price_book_order_units_and_price_list_import()
    {
        var manual = new AsmPrice { Source = PriceSources.Manual, Description = "PVC conduit 20 mm", Key = PriceBook.KeyOf("PVC conduit 20 mm"), Unit = "M", Price = 2.2, PriceDate = new DateTime(2026, 9, 1) };
        var po = new[]
        {
            new PoPrice("PO-9", "S", new DateTime(2026, 5, 1), 1, "", "PVC CONDUIT 20MM", "PCS", 9, 3, 1000),
            new PoPrice("PO-2", "S", new DateTime(2026, 5, 1), 2, "", "1X6 CU/LSOH (G/Y) (100Y)", "ROLL", 330, 0, 84),
        };
        var book = new PriceBook(new[] { manual }, po, new AssemblySettings());
        var conduit = book.Find("PVC conduit 20 mm", "M", ComponentKinds.Material)!;
        Assert.Equal(PriceSources.Manual, conduit.Source);                     // manual wins over the PO
        Assert.Equal(2.2, conduit.Price);
        var noManual = new PriceBook(Array.Empty<AsmPrice>(), po, new AssemblySettings());
        Assert.Equal(3, noManual.Find("PVC conduit 20 mm", "M", ComponentKinds.Material)!.Price);   // 9 per 3 m length
        var roll = noManual.Find("1C x 6mm2 CU/LSOH G/Y earth wire", "M", ComponentKinds.Material)!;
        Assert.Equal(Math.Round(330 / 91.44, 4), roll.Price);
        Assert.Null(noManual.Find("1C x 2.5mm2 CU/LSOH wire", "M", ComponentKinds.Material));
        Assert.Equal(91.44, PriceBook.RollLength("1X6 CU/LSOH (G/Y) (100Y)"));
        Assert.Null(PriceBook.ConvertPrice(10, "M", "PCS", "", 0, 3));

        var path = Path.Combine(Path.GetTempPath(), $"asm_prices_{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("PRICES");
                ws.Cell(1, 1).Value = "Item Code"; ws.Cell(1, 2).Value = "Description"; ws.Cell(1, 3).Value = "Unit"; ws.Cell(1, 4).Value = "Unit Price";
                ws.Cell(2, 1).Value = "W25"; ws.Cell(2, 2).Value = "1C x 2.5mm2 CU/LSOH wire"; ws.Cell(2, 3).Value = "M"; ws.Cell(2, 4).Value = 1.35;
                ws.Cell(3, 1).Value = "B1"; ws.Cell(3, 2).Value = "PVC back box 1G 47 mm"; ws.Cell(3, 3).Value = "PCS"; ws.Cell(3, 4).Value = 1.6;
                ws.Cell(4, 2).Value = "no price row";
                wb.SaveAs(path);
            }
            var imp = PriceListImporter.Read(path, "SUPPLIER X", new DateTime(2026, 9, 30));
            Assert.Equal(2, imp.Prices.Count);
            Assert.Single(imp.Issues);
            var withList = new PriceBook(imp.Prices, Array.Empty<PoPrice>(), new AssemblySettings());
            var wire = withList.Find("1C x 2.5mm2 CU/LSOH wire", "M", ComponentKinds.Material)!;
            Assert.Equal(PriceSources.PriceList, wire.Source);
            Assert.Equal(1.35, wire.Price);
            Assert.Equal(new DateTime(2026, 9, 30), wire.Date);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ store

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"asm_{Guid.NewGuid():N}.db");

    [Fact]
    public void Sqlite_store_seeds_saves_and_rejects_bad_formulas()
    {
        var path = TempDb();
        try
        {
            var store = new SqliteAssemblyStore(path, "tester");
            store.EnsureSchema();
            var lib = store.LoadLibrary();
            Assert.Equal(DefaultLibrary.Build().Templates.Count, lib.Templates.Count);
            Assert.NotEmpty(lib.Globals);
            Assert.Equal(0, store.SeedDefaults());                                  // nothing missing

            var light = lib.ByCode("LIGHT-PT")!;
            light.Params.Single(p => p.Name == "route_len").Value = 7.5;
            light.Header.Confirmed = true;
            store.SaveTemplate(light);
            var again = store.LoadLibrary().ByCode("LIGHT-PT")!;
            Assert.Equal(7.5, again.Params.Single(p => p.Name == "route_len").Value);
            Assert.True(again.Header.Confirmed);
            Assert.Equal(light.Components.Count, again.Components.Count);

            again.Components[0].QtyFormula = "route_len + nonsense";
            var ex = Assert.Throws<InvalidOperationException>(() => store.SaveTemplate(again));
            Assert.Contains("nonsense", ex.Message);

            var copy = new AssemblyTemplate { Header = new AsmTemplate { Code = "LIGHT-PT", Name = "dup", ItemType = ItemTypes.LightingPoint }, Components = { new AsmComponent { Key = "a", Name = "a", QtyFormula = "1" } } };
            Assert.Throws<InvalidOperationException>(() => store.SaveTemplate(copy));

            Assert.Equal(1, store.SeedDefaults(resetSeeded: false) + 1);            // user changes kept
            Assert.True(store.SeedDefaults(resetSeeded: true) > 0);                 // restore defaults
            Assert.Equal(6, store.LoadLibrary().ByCode("LIGHT-PT")!.Params.Single(p => p.Name == "route_len").Value);

            var settings = store.LoadSettings();
            settings.OverheadPct = 0.12; settings.ProfitPct = 0.08; settings.LabourMode = LabourBases.Hours;
            store.SaveSettings(settings);
            var s2 = store.LoadSettings();
            Assert.Equal(0.12, s2.OverheadPct); Assert.Equal(0.08, s2.ProfitPct); Assert.Equal(LabourBases.Hours, s2.LabourMode);

            store.ImportPrices(new[] { new AsmPrice { Description = "PVC conduit 20 mm", Unit = "M", Price = 2 } }, "list.xlsx");
            store.ImportPrices(new[] { new AsmPrice { Description = "PVC conduit 25 mm", Unit = "M", Price = 3 }, new AsmPrice { Description = "PVC conduit 32 mm", Unit = "M", Price = 4 } }, "list.xlsx");
            Assert.Equal(2, store.Prices().Count);                                  // same source replaced
            var sp = store.SaveItemSpec(new AsmItemSpec { SourceKind = SourceKinds.Contract, SourceKey = "C|1", SpecJson = new ItemSpec { ItemType = ItemTypes.SocketPoint }.ToJson() });
            var sp2 = store.SaveItemSpec(new AsmItemSpec { SourceKind = SourceKinds.Contract, SourceKey = "C|1", SpecJson = new ItemSpec { ItemType = ItemTypes.SwitchPoint }.ToJson() });
            Assert.Equal(sp.Id, sp2.Id);
            Assert.Single(store.ItemSpecs());
            Assert.Equal(ItemTypes.SwitchPoint, ItemSpec.FromJson(store.ItemSpecs()[0].SpecJson)!.ItemType);
        }
        finally { foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f); }
    }

    // ------------------------------------------------------------------ service, bulk, exports

    private static (AssemblyService Svc, string Path) Service(MaterialsSnapshot? mats = null)
    {
        var path = TempDb();
        var snap = new ProjectSnapshot
        {
            Contracts = { new Contract { ContractNo = "SUB-1", Building = Buildings.Hotel, Subcontractor = "ROOTS" } },
            ContractItems =
            {
                new ContractItem { Id = 1, ContractNo = "SUB-1", ItemNo = "1", Order = 1, Description = Pvc1stCeilLow, Unit = "No.", Qty = 100, Rate = 55, Section = "Lighting and Power" },
                new ContractItem { Id = 2, ContractNo = "SUB-1", ItemNo = "11", Order = 11, Description = Wire2ndLow, Unit = "No.", Qty = 100, Rate = 28, Section = "Lighting and Power" },
                new ContractItem { Id = 3, ContractNo = "SUB-1", ItemNo = "85", Order = 85, Description = Pull4x16, Unit = "mt", Qty = 300, Rate = 15, Section = "CABLES PULLING" },
            },
            BoqItems = { new BoqItem { Id = 9, ItemCode = "B6-01-01-00-6-26-V-5", Description = "small power points", Bill = "B6", Rate = 300 } },
            SubInvoices = { new SubInvoice { Id = 5, ContractNo = "SUB-1", InvoiceNo = 1, Status = SubInvoiceStatus.Approved } },
            SubInvoiceLines = { new SubInvoiceLine { SubInvoiceId = 5, ItemNo = "85", Kind = "ITEM", CumQty = 120 } },
        };
        var svc = new AssemblyService(new SqliteAssemblyStore(path, "tester"), () => snap, () => mats);
        svc.Reload();
        return (svc, path);
    }

    [Fact]
    public void Service_sources_saved_specs_bulk_requirements_and_exports()
    {
        var mats = new MaterialsSnapshot
        {
            Pos = { new MatPo { Id = 1, PoNo = "RAF-PO-1", Supplier = "CABLES", PoDate = new DateTime(2026, 4, 26) } },
            PoLines = { new MatPoLine { Id = 10, PoId = 1, LineNo = 13, Description = "4X16 CU/XLPE/SWA/LSOH", Unit = "M", Qty = 250, Rate = 42.089 } },
            DnLines = { new MatDnLine { Id = 20, DnId = 1, PoLineId = 10, Qty = 200, Unit = "M" } },
        };
        var (svc, path) = Service(mats);
        var outDir = Path.Combine(Path.GetTempPath(), $"asm_out_{Guid.NewGuid():N}");
        try
        {
            var sources = svc.Sources();
            Assert.Equal(4, sources.Count);
            var item1 = sources.Single(s => s.Kind == SourceKinds.Contract && s.Code == "1");
            Assert.Equal("SUB-1|1", item1.Key);
            Assert.Equal(Buildings.Hotel, item1.Building);
            Assert.Equal(4, svc.Coverage(sources).Recognised);

            // the contract item itself is found as the subcontract labour when SUBCONTRACT mode is forced
            var boq = sources.Single(s => s.Kind == SourceKinds.Boq);
            var b = svc.Run(boq);
            Assert.Equal(300, b.ReferenceRate);
            Assert.Contains(b.Lines, l => l.Key == "sub_2nd_fix" && l.PriceSource == PriceSources.Contract && l.UnitPrice == 28);

            // a corrected spec + parameter override is remembered
            var spec = svc.SpecFor(item1);
            spec.ConduitSizeMm = 25;
            svc.SaveSpec(item1, spec, new Dictionary<string, double> { ["route_len"] = 8 }, "", true);
            var again = svc.Run(item1);
            Assert.Contains("25 mm", again.Lines.Single(l => l.Key == "conduit").Spec);
            Assert.Equal(8.5, again.Lines.Single(l => l.Key == "conduit").QtyPerUnit);

            var manual = svc.SetManualPrice("PVC conduit 25 mm", "M", 3.1);
            Assert.True(manual.Id > 0);
            Assert.Equal(PriceSources.Manual, svc.Run(item1).Lines.Single(l => l.Key == "conduit").PriceSource);

            var bulk = svc.Bulk(sources.Where(s => s.Kind == SourceKinds.Contract));
            Assert.Equal(3, bulk.Rows.Count);
            var cable = bulk.Materials.Single(m => m.Spec.StartsWith("4X16mm2", StringComparison.Ordinal));
            Assert.Equal(309, cable.RequiredQty);                                 // 300 m x 1.03
            Assert.Equal(250, cable.OrderedQty);
            Assert.Equal(200, cable.DeliveredQty);
            Assert.Equal(Math.Round(120 * 1.03, 2), cable.InstalledQty);          // approved invoice 120 m
            Assert.Equal("SHORT ON PO", cable.Status);
            Assert.True(cable.FreeIssue);
            var conduit = bulk.Materials.Single(m => m.Spec == "PVC conduit 25 mm");
            Assert.Equal(Math.Round(100 * 8.5 * 1.05, 2), conduit.RequiredQty);
            Assert.Equal("NOT ON ANY PO", conduit.Status);

            Directory.CreateDirectory(outDir);
            var xlsx = Path.Combine(outDir, "bulk.xlsx");
            AssemblyExporter.BulkExcel(xlsx, bulk);
            using (var wb = new XLWorkbook(xlsx))
            {
                Assert.True(wb.Worksheets.Contains("MATERIAL REQUIREMENTS"));
                var ws = wb.Worksheet("ITEMS");
                var header = ws.Row(3).Cell(1);
                Assert.Equal(XLColor.FromHtml("#A6A6A6"), header.Style.Fill.BackgroundColor);
                Assert.True(header.Style.Font.Bold);
            }
            var pdf = Path.Combine(outDir, "rate.pdf");
            AssemblyExporter.RateAnalysisPdf(pdf, new[] { (item1, again), (boq, b) }, new RateSheetInfo("TEST", "tester", new DateTime(2026, 10, 2)));
            Assert.True(new FileInfo(pdf).Length > 1000);
            ExcelExportHelper(outDir, svc, item1, again);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    private static void ExcelExportHelper(string outDir, AssemblyService svc, AssemblySource src, Breakdown b)
    {
        var x = Path.Combine(outDir, "rate.xlsx");
        AssemblyExporter.BreakdownExcel(x, src, b);
        Export.ExcelExporter.Export(Path.Combine(outDir, "lib.xlsx"), AssemblyExporter.TemplateSheets(svc.Library));
        using var wb = new XLWorkbook(x);
        var ws = wb.Worksheet("RATE ANALYSIS");
        Assert.Contains(ws.CellsUsed(), c => c.GetString().StartsWith("BUILT-UP RATE", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_proposal_is_parsed_into_a_template()
    {
        var reply = "Here you go:\n```json\n{\"name\": \"Pendant point\", \"unit\": \"PT\", \"params\": [{\"name\": \"route len\", \"value\": 7, \"unit\": \"m\"}], " +
                    "\"components\": [{\"key\": \"conduit\", \"name\": \"Conduit\", \"spec\": \"PVC conduit 20 mm\", \"unit\": \"m\", \"qty\": \"route_len\", \"waste\": 0.05, \"stage\": \"1ST FIX\", \"kind\": \"MATERIAL\"}, " +
                    "{\"key\": \"hr\", \"name\": \"Labour\", \"unit\": \"HR\", \"qty\": 1.2, \"kind\": \"LABOUR\"}]}\n```";
        var t = ClaudeAssemblyAssist.Parse(reply, new ItemSpec { ItemType = ItemTypes.LightingPoint })!;
        Assert.Equal("AI", t.Header.Origin);
        Assert.False(t.Header.Confirmed);
        Assert.Equal("route_len", t.Params.Single().Name);
        Assert.Equal(3, t.Components.Count);                                      // + subcontract line
        Assert.Equal("1.2", t.Components[1].QtyFormula);
        Assert.Equal(LabourBases.Hours, t.Components[1].LabourBasis);
        Assert.Empty(TemplateCheck.Problems(t, DefaultLibrary.GlobalParams()));
        Assert.Null(ClaudeAssemblyAssist.Parse("no json here", new ItemSpec()));
        Assert.Contains("BOQ ITEM", ClaudeAssemblyAssist.BuildPrompt("pendant light point", "PT", new ItemSpec()));
    }
}
