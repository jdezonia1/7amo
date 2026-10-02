namespace Raffaello.Core.Assemblies;

/// <summary>A template with its parameters and components (what the calculator works on).</summary>
public sealed class AssemblyTemplate
{
    public AsmTemplate Header { get; init; } = new();
    public List<AsmParam> Params { get; init; } = new();
    public List<AsmComponent> Components { get; init; } = new();
    public string Code => Header.Code;
    public string ItemType => Header.ItemType;
}

/// <summary>The whole library: global parameters + templates.</summary>
public sealed class AssemblyLibrary
{
    public List<AsmParam> Globals { get; init; } = new();
    public List<AssemblyTemplate> Templates { get; init; } = new();

    public AssemblyTemplate? ByCode(string? code) => string.IsNullOrWhiteSpace(code) ? null
        : Templates.FirstOrDefault(t => t.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Template for an item type (active templates first, user templates before seeded ones).</summary>
    public AssemblyTemplate? ForType(string itemType) =>
        Templates.Where(t => t.ItemType.Equals(itemType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Header.Active).ThenBy(t => t.Header.Origin == "SEED" ? 1 : 0).ThenBy(t => t.Code).FirstOrDefault();
}

/// <summary>
/// [assemblies] Seeded assembly library: sensible electrical defaults for every item type. ALL VALUES ARE DEFAULTS TO BE
/// CONFIRMED BY MOHAMED (route lengths, bends, fixings spacing, waste %, labour hours, indicative prices). Subcontract labour
/// defaults are the HOTEL schedule SUB-ELE-028-2026 rates (55 / 28 / 25 SAR per point ...); when contract items are loaded the
/// matching contract rate is used instead. Indicative material prices are only used when no PO / price-list / manual price exists
/// and are flagged DEFAULT in every breakdown.
/// </summary>
public static class DefaultLibrary
{
    // ------------------------------------------------------------------ indicative prices (SAR, flagged DEFAULT)

    private const string ConduitPrice = "if(is_flex, if(conduit_size<=20,4,if(conduit_size<=25,5.5,8)), if(conduit_size<=20,2,if(conduit_size<=25,3,if(conduit_size<=32,4.5,7))) * if(is_emt,3,if(is_rs,6,1)))";
    private const string CouplingPrice = "if(conduit_size<=20,0.4,if(conduit_size<=25,0.6,1)) * if(is_emt,5,if(is_rs,10,1))";
    private const string BendPrice = "if(conduit_size<=20,0.8,if(conduit_size<=25,1.2,2)) * if(is_rs,10,1)";
    private const string SaddlePrice = "if(is_pvc,0.3,1)";
    private const string BoxPrice = "if(is_pvc, if(gangs>=2,2.5,if(is_ceiling,2.5,1.5)), if(gangs>=2,8,6))";
    private const string AdaptorPrice = "if(is_pvc,0.5,if(is_emt,2,2.5))";
    private const string JbPrice = "if(is_pvc,4,10)";
    private const string WirePrice = "if(wire_size<=1.5,0.9,if(wire_size<=2.5,1.4,if(wire_size<=4,2.2,if(wire_size<=6,3.2,5.5))))";
    private const string GlandPrice = "if(cable_size<=6,12,if(cable_size<=16,22,if(cable_size<=35,35,if(cable_size<=95,70,130))))";
    private const string LugPrice = "if(cable_size<=16,1.5,if(cable_size<=50,4,if(cable_size<=120,10,20)))";

    // ------------------------------------------------------------------ subcontract labour defaults (HOTEL SUB-ELE-028-2026 schedule)

    private const string Sub1st = "if(is_emt, if(is_high,90,70), if(is_high,57,55))";
    private const string Sub2nd = "if(is_high,30,28)";
    private const string Sub3rd = "if(is_high,27,25)";
    private const string SubFlex = "if(is_high,7,5)";
    private const string SubPull =
        "if(cable_cores>=3, if(cable_size>=240,45,if(cable_size>=120,35,if(cable_size>=50,27,if(cable_size>=25,20,if(cable_size>=6,15,if(cable_size>=4,12,10)))))), " +
        "if(cable_cores==2, if(cable_size>=6,10,8), if(cable_size>=150,18,if(cable_size>=120,13,if(cable_size>=70,12,if(cable_size>=50,10,if(cable_size>=25,8,if(cable_size>=10,7,6))))))))";
    private const string SubTerm =
        "if(is_earth || cable_cores==1, if(cable_size>=150,41,if(cable_size>=120,38,if(cable_size>=95,34,if(cable_size>=70,32,if(cable_size>=35,15,if(cable_size>=25,12,if(cable_size>=10,10,if(cable_size>=6,8,if(cable_size>=4,7,6))))))))), " +
        "if(cable_cores==2, if(cable_size>=16,12,9), if(cable_size>=240,296,if(cable_size>=185,237,if(cable_size>=150,166,if(cable_size>=120,142,if(cable_size>=70,119,if(cable_size>=50,83,if(cable_size>=35,59,if(cable_size>=25,41,if(cable_size>=16,36,if(cable_size>=10,27,12))))))))))))";

    public static List<AsmParam> GlobalParams() => new()
    {
        P("stick_len", 3, "m", "Conduit stick length", "couplings = conduit / stick length"),
        P("saddle_spacing", 1, "m", "Saddle / clip spacing", "one fixing per metre of conduit"),
        P("jb_spacing", 10, "m", "Junction / draw box every", "one draw box per 10 m of conduit (fraction per point)"),
        P("glue_per_joint", 0.0025, "L", "Solvent cement per PVC joint", "about 400 joints per litre"),
        P("term_allow", 0.6, "m", "Termination allowance per core", "tails at both ends"),
        P("lab_hr", 18, "SAR/h", "Labour cost per man-hour (HOURS basis)", "blended electrician / helper cost incl. housing + transport, to confirm"),
        P("flex_len", 1, "m", "Flexible conduit per drop", "box to luminaire / device"),
        P("tie_spacing", 0.5, "m", "Cable tie / cleat spacing", ""),
    };

    public static AssemblyLibrary Build()
    {
        var lib = new AssemblyLibrary { Globals = GlobalParams() };
        lib.Templates.AddRange(new[]
        {
            Point("LIGHT-PT", "Lighting point", ItemTypes.LightingPoint, "Ceiling / wall lighting point: conduit + box + 3x1.5 mm² + connector; flexible drop for ceiling points.",
                route: 6, drop: 0.5, bends: 2, csize: 20, cores: 3, wsize: 1.5,
                device: ("Luminaire connector (3-way terminal block) - luminaire supplied separately", "PCS", "1", "1.5"), flexDrop: true),
            Point("SOCKET-PT", "Socket outlet point", ItemTypes.SocketPoint, "Power point: conduit from ceiling/slab down to the box, 3x2.5 mm², 13 A socket.",
                route: 6, drop: 2.7, bends: 2, csize: 25, cores: 3, wsize: 2.5,
                device: ("13A switched socket outlet {gangs_txt} (white)", "PCS", "1", "if(gangs>=2,30,18)"), flexDrop: false),
            Point("SWITCH-PT", "Lighting switch point", ItemTypes.SwitchPoint, "Switch drop: conduit + back box + 3x1.5 mm² (L, switched L, E) + switch plate.",
                route: 4, drop: 1.8, bends: 2, csize: 20, cores: 3, wsize: 1.5,
                device: ("Lighting switch {gangs_txt} 1-way 10A", "PCS", "1", "12 + 6*max(gangs-1,0)"), flexDrop: false),
            Point("DALI-PT", "DALI lighting point", ItemTypes.DaliPoint, "DALI point: conduit + box + L/N/E 1.5 mm² + DA+/DA- pair (5 cores).",
                route: 6, drop: 0.5, bends: 2, csize: 20, cores: 5, wsize: 1.5,
                device: ("Luminaire / DALI connector (5-way terminal block)", "PCS", "1", "2"), flexDrop: true),
            Data(),
            Elv("GRMS-PT", "GRMS point", ItemTypes.GrmsPoint, "GRMS bus / Cat6 cable (confirm cable type with the GRMS supplier)", "2.5", "GRMS device - by GRMS supplier (not in this rate)", 20),
            Elv("FIRE-PT", "Fire alarm / PA point", ItemTypes.FireAlarmPoint, "2C x 1.5mm2 fire resistant (FR) cable", "3.5", "Detector / speaker base - by fire alarm supplier (not in this rate)", 20),
            Elv("ELV-PT", "ELV system point (BMS, access, intercom ...)", ItemTypes.ElvPoint, "ELV cable as per system ({system}) - confirm", "", "Device - by system supplier (not in this rate)", 20),
            FlexDrop(), Homerun(), Linear(), Accessory(), Luminaire(), Isolator(), FloorBox(), CableRun(), Termination(), ElvCable(), ConduitRun(),
            Tray(), TrayCover(), Db(), MainPanel(), SystemPanel(), EarthPit(), EarthBar(), EarthTape(), Lightning(), EvCharger(), FinalConnection(),
        });
        return lib;
    }

    // ------------------------------------------------------------------ builders

    private sealed record C(string Key, string Name, string Spec, string Unit, string Qty, double Waste = 0, string Stage = "", string Kind = ComponentKinds.Material,
        string Price = "", string Basis = "", string LabCat = "", string Note = "");

    private static AsmParam P(string name, double value, string unit, string label, string note = "") =>
        new() { Name = name, Value = value, Unit = unit, Label = label, Note = note, Confirmed = false };

    private static AssemblyTemplate T(string code, string name, string type, string unit, string desc, IEnumerable<AsmParam> pars, IEnumerable<C> comps)
    {
        var order = 0;
        return new AssemblyTemplate
        {
            Header = new AsmTemplate { Code = code, Name = name, ItemType = type, Unit = unit, Description = desc, Origin = "SEED", Active = true, Notes = "Seeded default - to be confirmed by Mohamed" },
            Params = pars.ToList(),
            Components = comps.Select(c => new AsmComponent
            {
                Order = ++order, Key = c.Key, Name = c.Name, Spec = c.Spec, Unit = c.Unit, QtyFormula = c.Qty, WastePct = c.Waste, Stage = c.Stage, Kind = c.Kind,
                DefaultPrice = c.Price, LabourBasis = c.Basis, LabourCategory = c.LabCat, Notes = c.Note,
            }).ToList(),
        };
    }

    private static IEnumerable<C> Labour(string stage, string category, string subPrice, string hours) => new[]
    {
        new C("sub_" + Slug(stage), $"Subcontract labour {stage}", $"{stage} labour (subcontract rate)", "PT", "1", 0, stage, ComponentKinds.Labour, subPrice, LabourBases.Subcontract, category),
        new C("hr_" + Slug(stage), $"Labour hours {stage}", $"{stage} electrician + helper", "HR", hours, 0, stage, ComponentKinds.Labour, "lab_hr", LabourBases.Hours, ""),
    };

    private static string Slug(string s) => s.ToLowerInvariant().Replace(' ', '_').Replace("/", "_");

    /// <summary>1st fix: conduit run per point with its fittings.</summary>
    private static IEnumerable<C> FirstFix(string boxQty = "1") => new[]
    {
        new C("conduit", "Conduit", "{conduit} conduit {conduit_size} mm", "M", "route_len + drop_len", 0.05, StageNames.First, Price: ConduitPrice, Note: "average route per point + drop"),
        new C("coupling", "Couplings", "{conduit} coupling {conduit_size} mm", "PCS", "ceil(conduit / stick_len)", 0.02, StageNames.First, Price: CouplingPrice),
        new C("bend", "Bends", "{conduit} bend {conduit_size} mm", "PCS", "if(is_emt, 0, bends)", 0.02, StageNames.First, Price: BendPrice, Note: "EMT bent on site (no fitting)"),
        new C("saddle", "Saddles / clips", "{conduit} saddle / spacer clip {conduit_size} mm", "PCS", "ceil(conduit / saddle_spacing)", 0.05, StageNames.First, Price: SaddlePrice),
        new C("box", "Box", "{box}", "PCS", boxQty, 0.02, StageNames.First, Price: BoxPrice),
        new C("adaptor", "Adaptors / connectors", "{conduit} adaptor / connector {conduit_size} mm", "PCS", "2", 0.02, StageNames.First, Price: AdaptorPrice),
        new C("jbox", "Junction / draw box share", "{conduit} junction box {conduit_size} mm", "PCS", "conduit / jb_spacing", 0, StageNames.First, Price: JbPrice),
        new C("glue", "Solvent cement (glue)", "PVC solvent cement", "L", "if(is_pvc, (2*coupling + 2*bend + adaptor + 2*jbox) * glue_per_joint, 0)", 0.10, StageNames.First, Price: "40"),
        new C("fixing", "Fixings (plugs + screws)", "Wall plug + screw", "PCS", "saddle + 2*box + 2*jbox", 0.05, StageNames.First, Price: "0.15"),
    };

    /// <summary>2nd fix: L / N / E wires drawn in the conduit with terminations.</summary>
    private static IEnumerable<C> SecondFix(string wireLen = "(conduit + term_allow)") => new[]
    {
        new C("wire_ln", "Wire (live / neutral / control cores)", "1C x {wire_size}mm2 CU/LSOH wire", "M", $"{wireLen} * max(wire_cores - 1, 0)", 0.05, StageNames.Second, Price: WirePrice),
        new C("wire_e", "Wire (earth)", "1C x {wire_size}mm2 CU/LSOH G/Y earth wire", "M", wireLen, 0.05, StageNames.Second, Price: WirePrice),
        new C("ferrule", "Ferrules / core markers", "Core marker / ferrule", "PCS", "wire_cores * 2", 0.05, StageNames.Second, Price: "0.05"),
        new C("sleeve", "Earth sleeve", "Green/yellow earth sleeving", "M", "0.1", 0.1, StageNames.Second, Price: "0.5"),
        new C("label", "Circuit label", "Circuit label / tag", "PCS", "1", 0, StageNames.Second, Price: "0.5"),
    };

    private static AssemblyTemplate Point(string code, string name, string type, string desc, double route, double drop, double bends, int csize, int cores, double wsize,
        (string Spec, string Unit, string Qty, string Price) device, bool flexDrop)
    {
        var pars = new List<AsmParam>
        {
            P("route_len", route, "m", "Average conduit route per point", "from the Drawings module average when available"),
            P("drop_len", drop, "m", "Drop to the box", ""),
            P("bends", bends, "pcs", "Bends per point", ""),
            P("conduit_size", csize, "mm", "Default conduit size", "the item text wins when it states a size"),
            P("wire_cores", cores, "cores", "Cores (L + N + E)", ""),
            P("wire_size", wsize, "mm²", "Wire size", ""),
            P("hr_1st", 1.5, "h", "Man-hours 1st fix per point", "incl. chasing / fixing"), P("hr_2nd", 0.6, "h", "Man-hours 2nd fix per point", ""), P("hr_3rd", 0.4, "h", "Man-hours 3rd fix per point", ""),
        };
        if (flexDrop) pars.Add(P("flex_drop", 1, "0/1", "Flexible drop for ceiling points", "1 = ceiling points get a flexible conduit drop at 3rd fix"));
        var comps = new List<C>();
        comps.AddRange(FirstFix());
        comps.AddRange(Labour(StageNames.First, "1ST FIX", Sub1st, "hr_1st"));
        comps.AddRange(SecondFix());
        comps.AddRange(Labour(StageNames.Second, "2ND FIX", Sub2nd, "hr_2nd"));
        comps.Add(new C("device", "Accessory", device.Spec, device.Unit, device.Qty, 0.01, StageNames.Third, Price: device.Price));
        comps.Add(new C("device_fix", "Accessory screws", "Accessory fixing screws", "PCS", "2", 0.05, StageNames.Third, Price: "0.1"));
        if (flexDrop)
        {
            comps.Add(new C("flex", "Flexible conduit drop", "Flexible conduit 20 mm", "M", "flex_len * is_ceiling * flex_drop", 0.05, StageNames.Third, Price: "4"));
            comps.Add(new C("flex_adaptor", "Flexible adaptors", "Flexible conduit adaptor 20 mm", "PCS", "2 * is_ceiling * flex_drop", 0.02, StageNames.Third, Price: "1.5"));
            comps.Add(new C("sub_flex", "Subcontract labour flexible drop", "3rd fix flexible conduit (subcontract rate)", "PT", "is_ceiling * flex_drop", 0, StageNames.Third, ComponentKinds.Labour, SubFlex, LabourBases.Subcontract, "FLEX"));
        }
        comps.AddRange(Labour(StageNames.Third, "3RD FIX", Sub3rd, "hr_3rd"));
        return T(code, name, type, "PT", desc, pars, comps);
    }

    private static AssemblyTemplate Data()
    {
        var pars = new List<AsmParam>
        {
            P("route_len", 8, "m", "Average conduit route per point", "data points run back to the IDF / rack"),
            P("drop_len", 2.7, "m", "Drop to the box", ""), P("bends", 2, "pcs", "Bends per point", ""),
            P("conduit_size", 25, "mm", "Default conduit size", ""), P("wire_cores", 1, "runs", "Cat6 runs per point", "twin data = 2 at 2nd fix"),
            P("wire_size", 0, "", "(not used)", ""),
            P("rack_allow", 3, "m", "Cable slack at rack + outlet", ""),
            P("hr_1st", 1.5, "h", "Man-hours 1st fix", ""), P("hr_2nd", 0.5, "h", "Man-hours 2nd fix (pull + test)", ""), P("hr_3rd", 0.5, "h", "Man-hours 3rd fix (terminate + faceplate + test)", ""),
        };
        var comps = new List<C>();
        comps.AddRange(FirstFix());
        comps.AddRange(Labour(StageNames.First, "1ST FIX", Sub1st, "hr_1st"));
        comps.Add(new C("cat6", "Data cable", "Cat6 U/UTP LSOH cable", "M", "(conduit + rack_allow) * wire_cores", 0.05, StageNames.Second, Price: "2.5"));
        comps.Add(new C("label", "Cable labels", "Cable label (both ends)", "PCS", "2 * wire_cores", 0, StageNames.Second, Price: "0.5"));
        comps.AddRange(Labour(StageNames.Second, "2ND FIX", Sub2nd, "hr_2nd"));
        comps.Add(new C("module", "RJ45 modules", "Cat6 RJ45 keystone module", "PCS", "wire_cores", 0.01, StageNames.Third, Price: "20"));
        comps.Add(new C("faceplate", "Faceplate", "Data faceplate 1G", "PCS", "1", 0.01, StageNames.Third, Price: "8"));
        comps.Add(new C("panel_port", "Patch panel port share", "Cat6 patch panel port (share)", "PCS", "wire_cores", 0, StageNames.Third, Price: "15"));
        comps.AddRange(Labour(StageNames.Third, "3RD FIX", Sub3rd, "hr_3rd"));
        return T("DATA-PT", "Data / TV / CCTV point", ItemTypes.DataPoint, "PT", "Data point: conduit + back box + Cat6 run + RJ45 module + faceplate + patch panel port share.", pars, comps);
    }

    private static AssemblyTemplate Elv(string code, string name, string type, string cableSpec, string cablePrice, string deviceSpec, int csize)
    {
        var pars = new List<AsmParam>
        {
            P("route_len", 6, "m", "Average conduit route per point", ""), P("drop_len", 1, "m", "Drop to the box", ""), P("bends", 2, "pcs", "Bends per point", ""),
            P("conduit_size", csize, "mm", "Default conduit size", ""), P("wire_cores", 1, "runs", "Cable runs per point", ""), P("wire_size", 1.5, "mm²", "Cable size", ""),
            P("hr_1st", 1.5, "h", "Man-hours 1st fix", ""), P("hr_2nd", 0.5, "h", "Man-hours 2nd fix", ""), P("hr_3rd", 0.3, "h", "Man-hours 3rd fix", ""),
        };
        var comps = new List<C>();
        comps.AddRange(FirstFix());
        comps.AddRange(Labour(StageNames.First, "1ST FIX", Sub1st, "hr_1st"));
        comps.Add(new C("cable", "System cable", cableSpec, "M", "(conduit + 2 * term_allow) * wire_cores", 0.05, StageNames.Second, Price: cablePrice));
        comps.Add(new C("label", "Cable labels", "Cable label (both ends)", "PCS", "2", 0, StageNames.Second, Price: "0.5"));
        comps.AddRange(Labour(StageNames.Second, "2ND FIX", Sub2nd, "hr_2nd"));
        comps.Add(new C("device", "Device", deviceSpec, "PCS", "1", 0, StageNames.Third, Note: "free issue / by system supplier"));
        comps.Add(new C("device_fix", "Device fixings", "Device fixing screws", "PCS", "2", 0.05, StageNames.Third, Price: "0.1"));
        comps.AddRange(Labour(StageNames.Third, "3RD FIX", Sub3rd, "hr_3rd"));
        return T(code, name, type, "PT", $"{name}: conduit + box + system cable; device by the system supplier.", pars, comps);
    }

    private static AssemblyTemplate FlexDrop() => T("FLEX-DROP", "Flexible conduit drop (3rd fix)", ItemTypes.FlexDrop, "PT",
        "Flexible conduit from the box to the luminaire / device with two adaptors.",
        new[] { P("hr_3rd", 0.15, "h", "Hours per drop", "") },
        new[]
        {
            new C("flex", "Flexible conduit", "Flexible conduit 20 mm", "M", "flex_len", 0.05, StageNames.Third, Price: "4"),
            new C("flex_adaptor", "Flexible adaptors", "Flexible conduit adaptor 20 mm", "PCS", "2", 0.02, StageNames.Third, Price: "1.5"),
            new C("sub_3rd_fix", "Subcontract labour", "Flexible conduit drop (subcontract rate)", "PT", "1", 0, StageNames.Third, ComponentKinds.Labour, SubFlex, LabourBases.Subcontract, "SELF"),
            new C("hr_3rd_fix", "Labour hours", "Electrician + helper", "HR", "hr_3rd", 0, StageNames.Third, ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate Homerun() => T("HOMERUN", "Homerun circuit wiring on tray", ItemTypes.Homerun, "PT",
        "Circuit home run from the DB to the first point on tray / trunking: L + N + E wires (or loop cable), ties, labels.",
        new[] { P("homerun_len", 25, "m", "Average home run length", "contract: one extra point per 30 m horizontal"), P("wire_cores", 3, "cores", "Cores", ""), P("wire_size", 2.5, "mm²", "Wire size", ""), P("hr_run", 0.6, "h", "Hours per home run", "") },
        new[]
        {
            new C("wire_ln", "Wire (live / neutral)", "1C x {wire_size}mm2 CU/LSOH wire", "M", "(homerun_len + 2*term_allow) * max(wire_cores - 1, 0)", 0.05, StageNames.Second, Price: WirePrice),
            new C("wire_e", "Wire (earth)", "1C x {wire_size}mm2 CU/LSOH G/Y earth wire", "M", "homerun_len + 2*term_allow", 0.05, StageNames.Second, Price: WirePrice),
            new C("ties", "Cable ties", "Cable tie 200 mm", "PCS", "ceil(homerun_len / tie_spacing)", 0.1, StageNames.Second, Price: "0.1"),
            new C("label", "Circuit labels", "Circuit label / tag", "PCS", "2", 0, StageNames.Second, Price: "0.5"),
            new C("sub", "Subcontract labour", "Homerun wiring (subcontract rate)", "PT", "1", 0, StageNames.Second, ComponentKinds.Labour, "28", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_run", 0, StageNames.Second, ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate Linear() => T("LINEAR-M", "Linear light / LED strip per metre", ItemTypes.LinearLight, "M",
        "Per metre of linear fixture: aluminium profile, diffuser, clips, end caps / connectors share, driver share; strip and driver usually by the lighting supplier.",
        new[] { P("profile_len", 2, "m", "Profile length", ""), P("driver_every", 5, "m", "One driver per", ""), P("hr_m", 0.35, "h", "Hours per metre", "") },
        new[]
        {
            new C("profile", "Aluminium profile + diffuser", "Aluminium LED profile with diffuser", "M", "1", 0.05, StageNames.Third, Note: "often supplied with the fixture"),
            new C("clips", "Mounting clips", "Profile mounting clip", "PCS", "2", 0.05, StageNames.Third, Price: "1"),
            new C("endcap", "End caps / connectors", "Profile end cap / connector", "PCS", "2 / profile_len", 0.05, StageNames.Third, Price: "3"),
            new C("driver", "Driver share", "LED driver (share)", "PCS", "1 / driver_every", 0, StageNames.Third, Note: "by lighting supplier"),
            new C("fixing", "Fixings", "Screw + plug", "PCS", "2", 0.05, StageNames.Third, Price: "0.15"),
            new C("sub", "Subcontract labour", "Linear fixture installation (subcontract rate)", "M", "1", 0, StageNames.Third, ComponentKinds.Labour, "if(is_high,48,if(facade,59,36))", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_m", 0, StageNames.Third, ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate Accessory() => T("ACCESSORY", "Wiring accessory / device", ItemTypes.Accessory, "NO",
        "Supply / fix a wiring accessory (socket, switch, FCU, data outlet, detector ...): device + screws + labels.",
        new[] { P("hr_dev", 0.3, "h", "Hours per device", "") },
        new[]
        {
            new C("device", "Device", "{system} accessory as described", "PCS", "1", 0.01, StageNames.Third, Note: "price from PO / price list"),
            new C("screws", "Fixing screws", "Accessory fixing screws", "PCS", "2", 0.05, StageNames.Third, Price: "0.1"),
            new C("label", "Label", "Circuit label", "PCS", "1", 0, StageNames.Third, Price: "0.5"),
            new C("sub", "Subcontract labour", "3rd fix device (subcontract rate)", "NO", "1", 0, StageNames.Third, ComponentKinds.Labour, Sub3rd, LabourBases.Subcontract, "3RD FIX"),
            new C("hr", "Labour hours", "Electrician", "HR", "hr_dev", 0, StageNames.Third, ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate Luminaire() => T("LUMINAIRE", "Luminaire (supply / fix)", ItemTypes.Luminaire, "NO",
        "Luminaire as scheduled + connector + fixings + flexible tail; the fitting price comes from the lighting PO / price list.",
        new[] { P("hr_lum", 0.6, "h", "Hours per luminaire", "") },
        new[]
        {
            new C("luminaire", "Luminaire", "Luminaire as described", "PCS", "1", 0.01, StageNames.Third, Note: "price from lighting PO / quotation"),
            new C("connector", "Connector", "Luminaire connector (3-way terminal block)", "PCS", "1", 0.02, StageNames.Third, Price: "1.5"),
            new C("flex", "Flexible tail", "Flexible conduit 20 mm", "M", "flex_len", 0.05, StageNames.Third, Price: "4"),
            new C("fixing", "Fixings", "Screw + plug / hanger", "PCS", "4", 0.05, StageNames.Third, Price: "0.15"),
            new C("sub", "Subcontract labour", "3rd fix luminaire (subcontract rate)", "NO", "1", 0, StageNames.Third, ComponentKinds.Labour, Sub3rd, LabourBases.Subcontract, "3RD FIX"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_lum", 0, StageNames.Third, ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate Isolator() => T("ISOLATOR", "Isolator switch", ItemTypes.Isolator, "NO",
        "Isolator (rating from the text) + internal / external flexible conduit + adaptors + glands + lugs + fixings + label.",
        new[] { P("amps", 20, "A", "Default rating", "the text wins"), P("flex_each", 1, "m", "Flexible conduit each side", ""), P("hr_iso", 1.5, "h", "Hours per isolator", "") },
        new[]
        {
            new C("isolator", "Isolator", "Isolator switch {amps}A {mount_txt}", "PCS", "1", 0, "", Note: "price from PO / price list"),
            new C("flex", "Flexible conduit (in + out)", "Flexible conduit {conduit_size} mm", "M", "2 * flex_each", 0.05, "", Price: "if(conduit_size<=25,5,9)"),
            new C("adaptor", "Flexible adaptors / glands", "Flexible conduit adaptor {conduit_size} mm", "PCS", "4", 0.02, "", Price: "2"),
            new C("lug", "Lugs", "Cable lug for {amps}A", "PCS", "if(amps<=32, 0, 8)", 0.05, "", Price: "if(amps<=63,3,8)"),
            new C("stand", "Stand / bracket", "Isolator stand (unistrut frame)", "SET", "if(mount_stand, 1, 0)", 0, "", Price: "60"),
            new C("fixing", "Fixings", "Anchor / screw", "PCS", "4", 0.05, "", Price: "0.5"),
            new C("label", "Label", "Engraved label", "PCS", "1", 0, "", Price: "3"),
            new C("sub", "Subcontract labour", "Isolator installation (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour,
                "if(mount_stand, if(amps<=20,48,if(amps<=30,60,if(amps<=40,83,if(amps<=60,119,if(amps<=100,166,if(amps<=125,237,if(amps<=150,272,if(amps<=300,473,768)))))))), if(amps<=20,30,if(amps<=30,48,if(amps<=40,60,if(amps<=60,83,if(amps<=100,119,if(amps<=125,119,if(amps<=150,237,if(amps<=280,355,592)))))))))",
                LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_iso", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate FloorBox() => T("FLOOR-BOX", "Floor box", ItemTypes.FloorBox, "NO",
        "Floor box (compartments from the text) + modules per compartment + conduit adaptors + fixings.",
        new[] { P("compartments", 3, "nos", "Default compartments", "the text wins"), P("hr_box", 2, "h", "Hours per box", "") },
        new[]
        {
            new C("box", "Floor box", "Floor box {compartments} compartments", "PCS", "1", 0, "", Note: "price from PO / price list"),
            new C("modules", "Socket / data modules", "Floor box module (socket / data)", "PCS", "compartments", 0.01, StageNames.Third, Note: "price from PO / price list"),
            new C("adaptor", "Conduit adaptors", "PVC adaptor 25 mm", "PCS", "compartments", 0.02, StageNames.First, Price: "0.6"),
            new C("fixing", "Fixings", "Anchor / screw", "PCS", "4", 0.05, "", Price: "0.5"),
            new C("sub", "Subcontract labour", "Floor box installation (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour, "if(compartments<=2,83,if(compartments<=3,119,if(compartments<=4,166,200)))", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_box", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate CableRun() => T("CABLE-RUN", "Power cable run per metre", ItemTypes.CableRun, "M",
        "Per metre of cable: cable + CPC (earth) + ties / cleats + share of glands, lugs and tags at both ends (run length parameter) + labour pull (+ terminations).",
        new[]
        {
            P("run_len", 30, "m", "Average run length", "spreads the two ends over the metres"), P("with_cpc", 1, "0/1", "Separate earth (CPC) cable along the run", "1 = add a G/Y earth cable sized from the phase size"),
            P("tray_share", 0, "m", "Tray metres per cable metre", "0 = tray priced as its own item"), P("hr_pull", 0.15, "h/m", "Man-hours per metre pulled (x (1 + size/100))", ""), P("hr_end", 1.2, "h", "Hours per termination", ""),
        },
        new[]
        {
            new C("cable", "Cable", "{cable}", "M", "1", 0.03, "", Note: "price from PO when the cable is on a PO"),
            new C("cpc", "Earth (CPC) cable", "{cpc}", "M", "if(is_earth || cable_cores == 1, 0, with_cpc)", 0.03, "", Note: "CPC sized from the phase conductor unless the item states it"),
            new C("ties", "Cable ties / cleats", "{tie}", "PCS", "1 / tie_spacing", 0.1, "", Price: "if(cable_size>=95, 6, 0.1)"),
            new C("tray", "Cable tray share", "Cable tray (share)", "M", "tray_share", 0, ""),
            new C("gland", "Cable glands", "Cable gland CW for {cable_core_size}", "PCS", "with_term * 2 / run_len", 0.02, "", Price: GlandPrice),
            new C("lug", "Cable lugs", "Copper cable lug {cable_size}mm2", "PCS", "with_term * (cable_cores + with_cpc * (1 - is_earth)) * 2 / run_len", 0.05, "", Price: LugPrice),
            new C("tag", "Cable tags / markers", "Cable tag", "PCS", "2 / run_len + 1 / 10", 0, "", Price: "1"),
            new C("sub_pull", "Subcontract labour - pulling", "Cable pulling per metre (subcontract rate)", "M", "1", 0, "", ComponentKinds.Labour, SubPull, LabourBases.Subcontract, "SELF"),
            new C("sub_term", "Subcontract labour - terminations", "Cable termination per end (subcontract rate)", "END", "with_term * 2 / run_len", 0, "", ComponentKinds.Labour, SubTerm, LabourBases.Subcontract, "TERMINATION"),
            new C("hr_pull", "Labour hours - pulling", "Cable gang", "HR", "hr_pull * (1 + cable_size / 100)", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
            new C("hr_term", "Labour hours - terminations", "Electrician", "HR", "with_term * hr_end * (1 + cable_size / 150) * 2 / run_len", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate Termination() => T("CABLE-TERM", "Cable termination per end", ItemTypes.CableTermination, "END",
        "One cable end: gland, lugs per core, shroud / heat shrink, core markers, tag, test.",
        new[] { P("hr_end", 1.2, "h", "Hours per end", "") },
        new[]
        {
            new C("gland", "Cable gland", "Cable gland CW for {cable_core_size}", "PCS", "if(is_armoured || cable_cores > 1, 1, 0)", 0.02, "", Price: GlandPrice),
            new C("shroud", "Gland shroud", "PVC shroud for gland", "PCS", "if(is_armoured || cable_cores > 1, 1, 0)", 0.02, "", Price: "3"),
            new C("lug", "Cable lugs", "Copper cable lug {cable_size}mm2", "PCS", "cable_cores", 0.05, "", Price: LugPrice),
            new C("heatshrink", "Heat shrink / tape", "Heat shrink sleeve (phase colours)", "PCS", "cable_cores", 0.1, "", Price: "1"),
            new C("marker", "Core markers", "Core marker / ferrule", "PCS", "cable_cores", 0.05, "", Price: "0.1"),
            new C("tag", "Cable tag", "Cable tag", "PCS", "1", 0, "", Price: "1"),
            new C("sub", "Subcontract labour", "Termination (subcontract rate)", "END", "1", 0, "", ComponentKinds.Labour, SubTerm, LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician", "HR", "hr_end * (1 + cable_size / 150)", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate ElvCable() => T("ELV-CABLE", "ELV cable pulling per metre", ItemTypes.ElvCableRun, "M",
        "Per metre of fire alarm / fibre / low-current cable on tray or in conduit: cable, ties, labels.",
        new[] { P("hr_m", 0.04, "h/m", "Hours per metre", "") },
        new[]
        {
            new C("cable", "Cable", "ELV cable as per system - confirm", "M", "1", 0.05, "", Note: "free issue / system supplier"),
            new C("ties", "Cable ties", "Cable tie 200 mm", "PCS", "1 / tie_spacing", 0.1, "", Price: "0.1"),
            new C("label", "Labels", "Cable label", "PCS", "0.1", 0, "", Price: "0.5"),
            new C("sub", "Subcontract labour", "ELV cable pulling (subcontract rate)", "M", "1", 0, "", ComponentKinds.Labour, "10", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician", "HR", "hr_m", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate ConduitRun() => T("CONDUIT-RUN", "Conduit run per metre", ItemTypes.ConduitRun, "M",
        "Per metre of conduit: conduit, couplings, saddles, bends share, boxes share, glue (PVC), fixings.",
        new[] { P("conduit_size", 25, "mm", "Default size", ""), P("bends_per_m", 0.2, "pcs/m", "Bends per metre", ""), P("hr_m", 0.25, "h/m", "Hours per metre", "") },
        new[]
        {
            new C("conduit", "Conduit", "{conduit} conduit {conduit_size} mm", "M", "1", 0.05, "", Price: ConduitPrice),
            new C("coupling", "Couplings", "{conduit} coupling {conduit_size} mm", "PCS", "1 / stick_len", 0.02, "", Price: CouplingPrice),
            new C("bend", "Bends", "{conduit} bend {conduit_size} mm", "PCS", "if(is_emt, 0, bends_per_m)", 0.02, "", Price: BendPrice),
            new C("saddle", "Saddles / clips", "{conduit} saddle / spacer clip {conduit_size} mm", "PCS", "1 / saddle_spacing", 0.05, "", Price: SaddlePrice),
            new C("jbox", "Junction box share", "{conduit} junction box {conduit_size} mm", "PCS", "1 / jb_spacing", 0, "", Price: JbPrice),
            new C("glue", "Solvent cement (glue)", "PVC solvent cement", "L", "if(is_pvc, (2*coupling + 2*bend + 2*jbox) * glue_per_joint, 0)", 0.1, "", Price: "40"),
            new C("fixing", "Fixings", "Wall plug + screw", "PCS", "saddle + 2*jbox", 0.05, "", Price: "0.15"),
            new C("sub", "Subcontract labour", "Conduit installation per metre (subcontract rate)", "M", "1", 0, "", ComponentKinds.Labour, "if(is_high,28,20)", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_m", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate Tray() => T("TRAY-M", "Cable tray / trunking / ladder per metre", ItemTypes.Tray, "M",
        "Per metre: tray, couplers + bolts per joint, fittings share (bends / tees), supports (rod + channel + anchors) every x m, earth continuity.",
        new[]
        {
            P("tray_width", 300, "mm", "Default tray width", "the text / size band wins"), P("tray_len", 3, "m", "Tray length", ""), P("fittings_per_m", 0.1, "pcs/m", "Bends / tees per metre", ""),
            P("support_spacing", 1.5, "m", "Support spacing", ""), P("rod_len", 1, "m", "Threaded rod per hanger", ""), P("earth_along", 1, "0/1", "Earth cable along the tray", ""),
            P("hr_m", 0.3, "h/m", "Hours per metre", ""),
        },
        new[]
        {
            new C("tray", "Cable tray", "HDG perforated cable tray {tray_width} mm", "M", "1", 0.03, "", Price: "25 + tray_width * 0.14"),
            new C("coupler", "Couplers (splice plates)", "Tray coupler set {tray_width} mm", "SET", "1 / tray_len", 0.02, "", Price: "6"),
            new C("bolts", "Coupler bolts + nuts", "M6 mushroom bolt + nut", "PCS", "8 / tray_len", 0.05, "", Price: "0.5"),
            new C("fitting", "Bends / tees share", "Tray bend / tee {tray_width} mm", "PCS", "fittings_per_m", 0, "", Price: "45 + tray_width * 0.1"),
            new C("rod", "Threaded rod", "Threaded rod M10", "M", "2 * rod_len / support_spacing", 0.05, "", Price: "8"),
            new C("channel", "Support channel", "Unistrut channel 41x41", "M", "(tray_width / 1000 + 0.15) / support_spacing", 0.05, "", Price: "25"),
            new C("anchor", "Anchors", "Anchor bolt M10", "PCS", "2 / support_spacing", 0.05, "", Price: "2.5"),
            new C("nuts", "Nuts / washers", "M10 nut + washer", "PCS", "8 / support_spacing", 0.05, "", Price: "0.3"),
            new C("earth_link", "Earth continuity links", "Tray earth bonding link", "PCS", "1 / tray_len", 0.02, "", Price: "4"),
            new C("earth", "Earth cable along tray", "1C x 16mm2 CU/LSOH G/Y earth cable", "M", "earth_along", 0.03, ""),
            new C("sub", "Subcontract labour", "Tray installation per metre (subcontract rate)", "M", "1", 0, "", ComponentKinds.Labour,
                "if(tray_width>800, if(is_high,100,67), if(tray_width>=400, if(is_high,65,45), if(is_high,45,35)))", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_m * (1 + tray_width / 1000)", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate TrayCover() => T("TRAY-COVER", "Tray cover per metre", ItemTypes.TrayCover, "M",
        "Per metre: cover, cover clips.",
        new[] { P("tray_width", 300, "mm", "Default width", ""), P("tray_len", 3, "m", "Cover length", ""), P("hr_m", 0.08, "h/m", "Hours per metre", "") },
        new[]
        {
            new C("cover", "Tray cover", "HDG tray cover {tray_width} mm", "M", "1", 0.03, "", Price: "10 + tray_width * 0.07"),
            new C("clips", "Cover clips", "Tray cover clip", "PCS", "4 / tray_len", 0.05, "", Price: "1"),
            new C("sub", "Subcontract labour", "Cover installation per metre (subcontract rate)", "M", "1", 0, "", ComponentKinds.Labour, "if(tray_width>800,20,if(tray_width>=400,15,10))", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician", "HR", "hr_m", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate Db() => T("DB", "Distribution board", ItemTypes.Db, "NO",
        "Panel (ways from the text) + glands / adaptors + lugs + circuit markers + circuit chart + fixings.",
        new[] { P("ways", 24, "ways", "Default ways", "the text wins"), P("hr_base", 3, "h", "Hours per board", ""), P("hr_way", 0.15, "h", "Hours per way", "") },
        new[]
        {
            new C("panel", "Distribution board", "Distribution board {ways} ways", "PCS", "1", 0, "", Note: "price from PO / price list"),
            new C("gland", "Incoming glands", "Cable gland (incoming)", "PCS", "2", 0, "", Price: "35"),
            new C("adaptor", "Conduit adaptors (outgoing)", "PVC adaptor 25 mm", "PCS", "ceil(ways / 2)", 0.05, "", Price: "0.6"),
            new C("lug", "Incoming lugs", "Copper lug (incoming)", "PCS", "5", 0.05, "", Price: "6"),
            new C("ferrule", "Circuit markers", "Core marker / ferrule", "PCS", "ways * 3", 0.05, "", Price: "0.05"),
            new C("chart", "Circuit chart + labels", "Circuit chart / engraved label", "SET", "1", 0, "", Price: "25"),
            new C("fixing", "Fixings", "Anchor / screw", "PCS", "4", 0.05, "", Price: "0.5"),
            new C("sub", "Subcontract labour", "Panel installation (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour,
                "if(ways<=12,461,if(ways<=18,615,if(ways<=24,770,if(ways<=30,845,if(ways<=36,921,if(ways<=42,1076,if(ways<=48,1228,if(ways<=54,1383,1536))))))))", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_base + hr_way * ways", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate MainPanel() => T("MDB", "MDB / SMDB / MCC / capacitor bank", ItemTypes.MainPanel, "NO",
        "Floor-standing panel: panel, base channel, copper glands, lugs, fixings, labels, testing.",
        new[] { P("hr_panel", 24, "h", "Hours per panel", "") },
        new[]
        {
            new C("panel", "Panel", "Main / sub-main panel as described", "PCS", "1", 0, "", Note: "price from PO / price list"),
            new C("base", "Base channel", "Base frame / channel", "SET", "1", 0, "", Price: "350"),
            new C("gland", "Copper glands", "Brass cable gland (large)", "PCS", "6", 0, "", Price: "130"),
            new C("lug", "Lugs", "Copper lug (large)", "PCS", "24", 0.05, "", Price: "20"),
            new C("fixing", "Fixings", "Anchor bolt M12", "PCS", "8", 0.05, "", Price: "4"),
            new C("label", "Labels", "Engraved label set", "SET", "1", 0, "", Price: "60"),
            new C("sub", "Subcontract labour", "Panel installation (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour, "600", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Panel gang", "HR", "hr_panel", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate SystemPanel() => T("SYS-PANEL", "System panel (GRMS, DALI, FA, access ...)", ItemTypes.SystemPanel, "NO",
        "Wall-mounted system panel by the system supplier: fixings, glands, labels, connection.",
        new[] { P("hr_panel", 6, "h", "Hours per panel", "") },
        new[]
        {
            new C("panel", "Panel", "{system} panel - by system supplier", "PCS", "1", 0, "", Note: "free issue / system supplier"),
            new C("gland", "Glands / adaptors", "Cable gland / conduit adaptor", "PCS", "6", 0.05, "", Price: "8"),
            new C("fixing", "Fixings", "Anchor / screw", "PCS", "4", 0.05, "", Price: "0.5"),
            new C("label", "Labels", "Engraved label", "PCS", "1", 0, "", Price: "10"),
            new C("sub", "Subcontract labour", "Panel installation (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour, "300", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_panel", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate EarthPit() => T("EARTH-PIT", "Earth pit", ItemTypes.EarthPit, "NO",
        "Earth pit: copper rod 3.5 m, pit chamber + cover, rod clamp, earth enhancing compound, test.",
        new[] { P("hr_pit", 6, "h", "Hours per pit", "") },
        new[]
        {
            new C("rod", "Earth rod", "Copper-bonded earth rod 3.5 m", "PCS", "1", 0, "", Price: "250"),
            new C("pit", "Pit chamber + cover", "Earth pit chamber with cover", "PCS", "1", 0, "", Price: "150"),
            new C("clamp", "Rod clamp", "Rod to cable clamp", "PCS", "1", 0, "", Price: "25"),
            new C("compound", "Earth enhancing compound", "Earth enhancing compound 25 kg", "BAG", "1", 0, "", Price: "60"),
            new C("sub", "Subcontract labour", "Earth pit (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour, "200", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_pit", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate EarthBar() => T("EARTH-BAR", "Earth test busbar", ItemTypes.EarthBar, "NO",
        "Copper earth bar with test link: bar (holes from the text), insulators, fixings, label.",
        new[] { P("ways", 6, "holes", "Default holes", ""), P("hr_bar", 2, "h", "Hours per bar", "") },
        new[]
        {
            new C("bar", "Copper busbar", "Copper earth bar {ways} holes", "PCS", "1", 0, "", Price: "80 + 12 * ways"),
            new C("insulator", "Insulators", "Busbar insulator", "PCS", "2", 0, "", Price: "8"),
            new C("fixing", "Fixings", "Anchor / screw", "PCS", "4", 0.05, "", Price: "0.5"),
            new C("label", "Label", "Engraved label", "PCS", "1", 0, "", Price: "5"),
            new C("sub", "Subcontract labour", "Busbar installation (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour, "60", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician", "HR", "hr_bar", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate EarthTape() => T("EARTH-TAPE", "Copper tape per metre", ItemTypes.EarthTape, "M",
        "Per metre: copper tape 25 x 3 mm, clips, fixings.",
        new[] { P("clip_spacing", 0.5, "m", "Clip spacing", ""), P("hr_m", 0.15, "h/m", "Hours per metre", "") },
        new[]
        {
            new C("tape", "Copper tape", "Copper tape 25 x 3 mm", "M", "1", 0.03, "", Price: "45"),
            new C("clip", "Tape clips", "Copper tape clip", "PCS", "1 / clip_spacing", 0.05, "", Price: "3"),
            new C("fixing", "Fixings", "Screw + plug", "PCS", "1 / clip_spacing", 0.05, "", Price: "0.15"),
            new C("sub", "Subcontract labour", "Copper tape (subcontract rate)", "M", "1", 0, "", ComponentKinds.Labour, "18", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician", "HR", "hr_m", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate Lightning() => T("LPS", "Lightning protection component", ItemTypes.Lightning, "NO",
        "Air terminal / strike pad on base with fixings.",
        new[] { P("hr_lps", 1, "h", "Hours per component", "") },
        new[]
        {
            new C("terminal", "Air terminal / strike pad", "Air terminal / strike pad", "PCS", "1", 0, "", Note: "price from PO / price list"),
            new C("base", "Base", "Air terminal base", "PCS", "1", 0, "", Price: "40"),
            new C("fixing", "Fixings", "Anchor / screw", "PCS", "4", 0.05, "", Price: "0.5"),
            new C("sub", "Subcontract labour", "LPS component (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour, "50", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician", "HR", "hr_lps", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate EvCharger() => T("EV-CHARGER", "EV charger installation", ItemTypes.EvCharger, "NO",
        "EV charging unit (by others) fixed, connected, tested: fixings, glands, lugs, labels.",
        new[] { P("hr_ev", 4, "h", "Hours per unit", "") },
        new[]
        {
            new C("unit", "EV charging unit", "EV charging unit - by others", "PCS", "1", 0, "", Note: "free issue"),
            new C("gland", "Glands", "Cable gland", "PCS", "2", 0, "", Price: "22"),
            new C("lug", "Lugs", "Copper lug", "PCS", "5", 0.05, "", Price: "1.5"),
            new C("fixing", "Fixings", "Anchor bolt", "PCS", "4", 0.05, "", Price: "2.5"),
            new C("label", "Labels", "Engraved label", "PCS", "1", 0, "", Price: "5"),
            new C("sub", "Subcontract labour", "EV charger (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour, "100", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician + helper", "HR", "hr_ev", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });

    private static AssemblyTemplate FinalConnection() => T("FINAL-CONN", "Final connection to equipment", ItemTypes.FinalConnection, "NO",
        "Interface unit to equipment: flexible conduit, adaptors, short cable tail, glands, labels.",
        new[] { P("tail_len", 2, "m", "Cable tail", ""), P("hr_conn", 1, "h", "Hours per connection", "") },
        new[]
        {
            new C("flex", "Flexible conduit", "Flexible conduit 20 mm", "M", "1.5", 0.05, "", Price: "4"),
            new C("adaptor", "Adaptors", "Flexible conduit adaptor 20 mm", "PCS", "2", 0.02, "", Price: "1.5"),
            new C("cable", "Cable tail", "ELV / control cable tail - confirm", "M", "tail_len", 0.05, ""),
            new C("label", "Labels", "Cable label", "PCS", "2", 0, "", Price: "0.5"),
            new C("sub", "Subcontract labour", "Final connection (subcontract rate)", "NO", "1", 0, "", ComponentKinds.Labour, "25", LabourBases.Subcontract, "SELF"),
            new C("hr", "Labour hours", "Electrician", "HR", "hr_conn", 0, "", ComponentKinds.Labour, "lab_hr", LabourBases.Hours),
        });
}
