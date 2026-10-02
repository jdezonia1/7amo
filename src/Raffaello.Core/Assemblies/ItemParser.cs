using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Coding;
using Raffaello.Core.Contracts;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Assemblies;

/// <summary>Where the description comes from - changes only the supply-scope default (subcontract items are labour only).</summary>
public enum ItemSourceKind { Text, Boq, Contract }

/// <summary>
/// [assemblies] Reads a BOQ / contract item description (English, Arabic or mixed) into an <see cref="ItemSpec"/>: item type,
/// conduit type and size, wiring, cable cores x size, mounting, height band, stages, supply scope, boxes and accessories.
/// Rule based and offline; every guess is listed in <see cref="ItemSpec.Notes"/> so the user can correct it.
/// </summary>
public static class ItemParser
{
    private static readonly int[] ConduitSizes = { 16, 20, 25, 32, 40, 50 };

    public static ItemSpec Parse(string? description, string? unit = null, ItemSourceKind source = ItemSourceKind.Text)
    {
        var raw = description ?? "";
        var s = new ItemSpec { Unit = Units.Normalize(unit) };
        var d = Norm(raw);
        bool Has(params string[] keys) => keys.Any(k => d.Contains(k, StringComparison.Ordinal));
        bool Rx(string pattern) => Regex.IsMatch(d, pattern);

        if (d.Trim().Length == 0) { s.Notes.Add("empty description"); return s; }

        var attrs = ContractAttributeParser.Parse(raw, unit ?? "");

        // ---- stages
        if (Rx(@"\b(1st|first)[\s-]*fix") || Has("مرحلة أولى", "المرحلة الأولى", "المرحلة الاولى")) s.Stages.Add(StageNames.First);
        if (Rx(@"\b(2nd|second)[\s-]*fix") || Has("مرحلة ثانية", "المرحلة الثانية")) s.Stages.Add(StageNames.Second);
        if (Rx(@"\b(3rd|third|final)[\s-]*fix") || Has("مرحلة ثالثة", "المرحلة الثالثة")) s.Stages.Add(StageNames.Third);
        if (s.Stages.Count == 0 && (Rx(@"\bwire pulling\b") || Has("سحب سلك")) ) { s.Stages.Add(StageNames.Second); s.Notes.Add("wire pulling = 2nd fix"); }

        // ---- height, mount, conduit (shared rules with the contract parser)
        s.Height = attrs.Height;
        s.Mount = attrs.Mount;
        s.Conduit = attrs.Conduit == Conduits.None ? "" : attrs.Conduit;
        if (Rx(@"\bgi\b|galvani[sz]ed") && s.Conduit.Length == 0 && Has("conduit")) s.Conduit = Conduits.Emt;
        if (Rx(@"\bupvc\b") && s.Conduit.Length == 0) s.Conduit = Conduits.Pvc;
        if (Has("stand-mounted", "stand mounted", "على الإستاند", "علي الإستاند", "على الاستاند", "علي الاستاند")) s.Mount = "STAND";
        if (Has("concealed", "recessed", "flush", "مدفون", "chasing", "التكسير")) s.Installation = "CONCEALED";
        else if (Has("surface", "exposed", "ظاهر")) s.Installation = "SURFACE";

        // ---- numbers
        s.ConduitSizeMm = ConduitSize(d);
        s.Ways = Ways(d);
        s.Amps = Amps(d);
        s.Compartments = Int(d, @"(\d+)\s*compartments?");
        s.Gangs = Gangs(d);
        s.Watts = Num(d, @"(\d+(?:\.\d+)?)\s*w\b");
        s.Facade = Has("façade", "facade", "واجهات", "الوجهات", "خارجية");
        s.Homerun = Has("homerun", "home run");
        s.FireRated = Rx(@"\bmica\b|fire[\s-]*rated|fire resistant|\bfr\b|\bfp200|\bcwz\b|مقاوم للحريق");
        s.Sheath = Rx(@"\b(lsoh|lszh|ls0h|lshz|hffr|halogen free|low smoke)\b") ? "LSOH" : Rx(@"\bpvc\b") && Has("cable", "wire") ? "PVC" : "";
        s.Armoured = Rx(@"\b(swa|awa|armou?red)\b");
        if (Rx(@"\b(al|alu|aluminium|aluminum)\b") && Has("cable")) s.Conductor = "AL";
        ReadWiring(d, s);
        ReadAccessories(d, s);

        // ---- type
        Classify(d, raw, s, Has, Rx);

        // ---- supply scope
        s.Supply = Scope(d, source, Has, Rx);

        // ---- per-type tidy up
        Finish(d, s, Has);
        return s;
    }

    // ------------------------------------------------------------------ classification

    private static void Classify(string d, string raw, ItemSpec s, Func<string[], bool> has, Func<string, bool> rx)
    {
        bool Has(params string[] k) => has(k);
        void Set(string type, double conf, string? note = null)
        {
            s.ItemType = type; s.Confidence = conf;
            if (note != null) s.Notes.Add(note);
        }

        var pointWords = Has("outlet", "point", "مخرج", "نقطة") || s.Stages.Count > 0;
        var pulling = Rx(@"\bpull") || Has("سحب");
        var trayWords = Rx(@"cable (supports?|tray|trunking|ladder)|\btrays?\b|\btrunking\b|cable ladder") || Has("حوامل كابلات", "كيبل تراي", "كيبل ترنك", "سلالم كابلات");
        var onTray = Has("on cable tray", "on the cable tray", "on cable trays", "علي الكابل تراي", "على الكابل تراي");
        bool Rx(string p) => rx(p);

        if (s.Homerun)
        {
            var elv = Has("loop") && !Has("lighting", "power socket", "socket");
            Set(ItemTypes.Homerun, 0.9, elv ? "homerun loop cable (ELV) on tray" : "homerun circuit wiring on tray / trunking");
            if (elv) s.System = SystemOf(d, "ELV");
            return;
        }
        if (pulling && (Has("fire alarm cable", "fiber optic", "fibre optic", "low current", "التيار الخفيف", "فايبر", "انذار حريق او", "إنذار حريق او") || Rx(@"\belv cable")))
        { Set(ItemTypes.ElvCableRun, 0.9); s.System = "ELV"; return; }
        if (trayWords && !onTray && !pulling)
        {
            if (Has("cover", "غطاء")) Set(ItemTypes.TrayCover, 0.9); else Set(ItemTypes.Tray, 0.9);
            s.TrayWidthMm = TrayWidth(d, s);
            if (Has("ladder", "سلالم")) s.Accessories.Add("LADDER");
            if (Has("trunking", "ترنك")) s.Accessories.Add("TRUNKING");
            return;
        }
        if (Has("earth pit", "earthing pit", "earthing manhole", "earth rod", "earth electrode", "حفرة", "بئر"))
        { Set(ItemTypes.EarthPit, 0.9); return; }
        if (Has("busbar", "bus bar", "بارات نحاس", "earth bar", "earth terminal bar", "bar نحاس", "test link"))
        { Set(ItemTypes.EarthBar, 0.9); s.Ways = Int(d, @"(\d+)\s*(?:holes?|terminals?|فتحة|فتحات)"); return; }
        if (Has("copper tape", "شريط نحاس", "copper strip")) { Set(ItemTypes.EarthTape, 0.9); return; }
        if (Has("lightning protection", "air terminal", "strike pad", "صواعق")) { Set(ItemTypes.Lightning, 0.85); return; }
        if (Has("isolator", "disconnect switch", "isolating switch", "مفتاح فصل")) { Set(ItemTypes.Isolator, 0.9); return; }
        if (Rx(@"main distribution board|\b[a-z]{0,2}s?mdb\b|sub main distribution|capacitor|power factor|harmonic|\bahf\b|\bmcc\b|\bemcc\b|\bats\b|\bacb\b|transfer switch|transformer|\btx\b.*kva|اللوحة الرئيسية|لوحة رئيسية"))
        { Set(ItemTypes.MainPanel, 0.85, Rx(@"transformer|\btx\b|\bats\b|\bacb\b|\bahf\b|harmonic") ? "large LV / MV equipment priced with the main-panel template" : null); return; }
        if (Rx(@"\bpanel\b|cabinet|لوحة|\bups\b|power supply unit|booster") && Rx(@"dali driver|grms|access control|fire alarm|lighting control|central battery|intercom|zigbee|repeater|security|\bups\b|booster|bms|cctv|data|network|evacuation|battery")
            || Rx(@"\b(e?lcp|facp|idf|mdf)\b|\bracks?\b|control panel|\bcp\d"))
        { Set(ItemTypes.SystemPanel, 0.85); s.System = SystemOf(d, Rx(@"\bidf\b|\bmdf\b|\brack") ? "DATA" : Rx(@"lcp|control panel|\bcp\d") ? "LIGHTING CONTROL" : ""); return; }
        if (Rx(@"distribution board|electrical panel|panel ?board|consumer unit|\b[a-z]{0,2}db\b|لوحة كهرباء|لوحة توزيع") || Rx(@"\bpanel\b") && s.Ways > 0 || Has("لوحة") && s.Ways > 0)
        { Set(ItemTypes.Db, 0.9); return; }
        if (Has("ev charging unit", "ev charger", "charging station", "charging unit") || Has("ev charging") && Has("unit", "commissioning of ev"))
        { Set(ItemTypes.EvCharger, 0.85); s.System = "EV"; return; }
        if (Has("floor box", "media hub", "floorbox")) { Set(ItemTypes.FloorBox, 0.9, Has("media hub") ? "media hub treated as a floor box" : null); s.Mount = "FLOOR"; return; }
        if (Has("final connection", "توصيل من", "connection from interface")) { Set(ItemTypes.FinalConnection, 0.8); s.System = SystemOf(d, ""); return; }
        if (Has("linear lighting", "led strip", "aluminum profile", "aluminium profile", "strip light", "ليد ستريب", "profile light"))
        { Set(ItemTypes.LinearLight, 0.9); s.System = "LIGHT"; return; }
        if (Has("flexible conduit", "flexable conduit", "flexible conduit for", "فليكس") && (s.Stages.Contains(StageNames.Third) || !Has("1st fix", "2nd fix")) && !Has("isolator"))
        { Set(ItemTypes.FlexDrop, 0.9, "flexible conduit drop to the device (3rd fix)"); s.System = Rx(@"lighting point|lighting switch|light fixture|إنارة|انارة") ? "LIGHT" : SystemOf(d, "LIGHT"); s.Conduit = Conduits.Flex; return; }

        // ---- cables (pull / terminate / supply) before points: their text never says "outlet"
        var cable = s.CableSizeMm2 > 0 && (Has("cable", "كابل", "wire") || Rx(@"\b\d+\s*[x×]\s*\d|\b\d\s*c\s*\d+(\.\d+)?\s*mm2"));
        var earthCable = Has("earth cable", "earthing cable", "أرضي", "ارضي", "earth conductor") || Has("g/y", "y/g") && (s.CpcSizeMm2 <= 0 || s.CableCores == 1);
        if (cable && !(pointWords && Has("outlet", "مخرج", "نقطة")))
        {
            var terminate = Rx(@"terminat|\bglands?\b|install, test,? and commission") || Has("تركيب وتسليم واختبار", "تشطيب");
            if (earthCable) s.EarthCable = true;
            if (terminate && !pulling) { Set(ItemTypes.CableTermination, 0.9, "termination per cable end (glands, lugs, tags)"); return; }
            Set(ItemTypes.CableRun, 0.9);
            if (terminate && pulling) s.Accessories.Add("TERMINATIONS");
            return;
        }
        if (earthCable && !pointWords) { Set(ItemTypes.CableRun, 0.6, "earthing cable without a size - set the size"); s.EarthCable = true; return; }

        // ---- conduit priced per metre
        var perMetre = Units.Family(s.Unit) == "LENGTH";
        if ((Rx(@"conduit|\bduct\b") || Has("ماسورة", "مواسير")) && !pointWords || perMetre && s.Conduit is Conduits.Rs or Conduits.Emt or Conduits.Pvc && s.Stages.Contains(StageNames.First))
        {
            Set(ItemTypes.ConduitRun, perMetre ? 0.85 : 0.7, perMetre ? "unit is metres: priced as a conduit run per metre" : null);
            s.System = SystemOf(d, "");
            if (s.ConduitSizeMm == 0 && Regex.Match(d, @"(\d{2})\s*mm\s*dia") is { Success: true } dia && int.TryParse(dia.Groups[1].Value, out var dv)) s.ConduitSizeMm = dv;
            return;
        }
        // containment given only by its size ("150x50mm", "2x tier 450x50mm") - an owner BOQ row under a tray / trunking heading
        if (Regex.Match(d, @"^\s*(?:\d\s*x\s*tier\s*)?(\d{2,4})\s*x\s*(\d{2,3})\s*mm\s*$") is { Success: true } cont)
        {
            Set(ItemTypes.Tray, 0.5, "only a size in the text - containment (tray / trunking) assumed");
            s.TrayWidthMm = int.Parse(cont.Groups[1].Value, CultureInfo.InvariantCulture);
            return;
        }

        // ---- devices supplied as such (owner BOQ "13A switched socket outlet", "recessed downlight")
        var stageWords = s.Stages.Count > 0;
        var isPoint = Rx(@"\bpoints?\b") && !Rx(@"call points?\b") || Has("نقطة") || Has("outlet for", "outlets for") || stageWords;
        if (!isPoint && Rx(@"downlight|luminaire|light fitting|\buplight|spotlight|pendant|wall (mounted )?light|wall-mounted light|floodlight|flood light|bollard|step light|chandelier|exit sign|decorative lighting|\bcove\b|\bspot\b|track light|batten|high bay|street light|garden light|in-ground|inground|luminary|lantern|\blamp\b|reading light|emergency luminaire|\bem\d|\bref\.? [a-z]{0,3}-?[a-z]?\d")
            || !isPoint && Rx(@"\d+(\.\d+)?\s*w\b") && Rx(@"ceiling|surface|recessed|\bip\s?\d{2}\b|mounted"))
        { Set(ItemTypes.Luminaire, 0.8); s.System = "LIGHT"; return; }
        if (!isPoint && Rx(@"socket outlet|switched socket|fused connection unit|\bfcu\b|fused spur|shaver|cooker control|\b(one|two|1|2)[\s-]way\b|\bgang\b|dp switch|double pole switch|\bswitch\b|dimmer|key ?card|data outlet|\brj45\b|faceplate|call point|smoke detector|heat detector|\bsounder|beacon|thermostat|doorbell|door bell|card holder|\bspeaker\b|motion sensor|occupancy sensor|pir sensor|push button|emergency stop|detector|loudspeaker|strike lock|control plate|controller|control unit|interface (unit|module)|projector|sound ?bar|subwoofer|amplifier|selector|\bkvm\b|charger|card reader|exit button|break glass|keypad|\bcamera\b|input plate"))
        {
            Set(ItemTypes.Accessory, 0.8);
            s.System = Rx(@"call point|smoke|heat detector|sounder|beacon|beam detector|fire") ? "FIRE"
                     : Rx(@"data|rj45") ? "DATA" : Rx(@"speaker") ? "EVACUATION" : Rx(@"thermostat|key ?card|card holder|doorbell|door bell") ? "GRMS"
                     : Rx(@"socket|fused|fcu|shaver|cooker") ? "POWER" : "LIGHT";
            return;
        }

        // ---- points by system
        if (Rx(@"\bdali\b")) { Set(ItemTypes.DaliPoint, 0.9); s.System = "DALI"; return; }
        if (Has("grms", "guest room management", "room management")) { Set(ItemTypes.GrmsPoint, 0.9); s.System = "GRMS"; return; }
        if (Rx(@"\bdata\b|telephone|\btv\b|\bcctv\b|camera|\bhdmi\b|\bnetwork\b|\bwap\b|wireless access|wifi|wi-fi") || Has("داتا", "تليفون", "تليفزيون", "تلفزيون", "كاميرا"))
        {
            Set(ItemTypes.DataPoint, 0.9);
            s.System = Rx(@"\bhdmi\b") ? "HDMI" : Rx(@"\bdata\b|\bnetwork\b|\bwap\b|wifi|wi-fi") || Has("داتا") ? "DATA" : Rx(@"\btv\b") || Has("تليفزيون", "تلفزيون") ? "TV" : Rx(@"cctv|camera") || Has("كاميرا") ? "CCTV" : "DATA";
            if (Rx(@"\bdata\b") && Rx(@"\btv\b|cctv|telephone")) s.Notes.Add("covers data / telephone / TV / CCTV - priced as a data point (Cat6)");
            return;
        }
        if (Rx(@"fire alarm|\bfdas\b|\bspeaker|\ba/v\b|\bbgm\b|voice evac|public address|\bpa\b system") || Has("إنذار حريق", "انذار حريق", "سماعة"))
        { Set(ItemTypes.FireAlarmPoint, 0.9); s.System = Rx(@"fire alarm") || Has("إنذار حريق", "انذار حريق") ? "FIRE" : "EVACUATION"; return; }
        if (Rx(@"ev charging|\bev\b")) { Set(ItemTypes.SocketPoint, 0.75, "EV charging point priced as a radial power point (6 mm², 32 mm conduit)"); s.System = "EV"; return; }
        if (Rx(@"\bbms\b|access control|intercom|parking|gate barrier|metering|\bmeter\b|disabled|lighting control|nurse call|security|\bpanic\b|alarm"))
        { Set(ItemTypes.ElvPoint, 0.85); s.System = SystemOf(d, "ELV"); return; }

        var light = Rx(@"lighting points?|light points?|lighting outlets?|luminaire points?|light fixture|façade lighting|facade lighting|lighting point") || Has("مخرج إنارة", "مخرج انارة", "إنارة خارجية", "انارة خارجية") || Rx(@"\blighting\b") && !Rx(@"lighting switch");
        var socket = Rx(@"power sockets?|\bsockets?\b|power points?|small power|power outlets?") || Has("بريزة", "مخرج كهرباء");
        var sw = Rx(@"lighting switch|switch points?|\bswitch(es)?\b") || Has("مفتاح انارة", "مفتاح إنارة", "مفاتيح");
        var found = new List<string>();
        if (light) found.Add(ItemTypes.LightingPoint);
        if (socket) found.Add(ItemTypes.SocketPoint);
        if (sw) found.Add(ItemTypes.SwitchPoint);
        if (found.Count > 0)
        {
            Set(found[0], found.Count == 1 ? 0.9 : 0.75);
            s.AlsoCovers = found.Skip(1).ToList();
            s.System = found[0] == ItemTypes.SocketPoint ? "POWER" : "LIGHT";
            if (s.Facade && found[0] == ItemTypes.LightingPoint) s.System = "FACADE LIGHT";
            if (found.Count > 1) s.Notes.Add($"text covers {string.Join(" / ", found)} - priced as {found[0]}; switch the type if needed");
            return;
        }
        if (Rx(@"final sub-?circuit|\bsub-?circuit"))
        { Set(ItemTypes.SocketPoint, 0.5, "final sub-circuit to a device - priced as a power point, check"); s.System = "POWER"; return; }
        if (pointWords && (Rx(@"\boutlet") || Has("مخرج")))
        { Set(ItemTypes.LightingPoint, 0.4, "generic outlet without a system - priced as a lighting point, check"); s.System = "LIGHT"; return; }
        if (Has("wiring", "wires", "تمديد")) { Set(ItemTypes.CableRun, 0.4, "wiring without a cable size - set the size"); return; }
        Set(ItemTypes.Unknown, 0, "no item type recognised");
    }

    private static string SystemOf(string d, string fallback) =>
        Regex.IsMatch(d, @"\bbms\b") ? "BMS" : d.Contains("access control") ? "ACCESS" : d.Contains("intercom") ? "INTERCOM"
        : Regex.IsMatch(d, @"parking|gate barrier") ? "PARKING" : Regex.IsMatch(d, @"metering|\bmeter\b") ? "METERING" : d.Contains("disabled") ? "DISABLED"
        : d.Contains("lighting control") ? "LIGHTING CONTROL" : d.Contains("nurse call") ? "NURSE CALL" : Regex.IsMatch(d, @"dali") ? "DALI"
        : d.Contains("grms") ? "GRMS" : Regex.IsMatch(d, @"fire alarm|انذار|إنذار") ? "FIRE" : Regex.IsMatch(d, @"speaker|bgm|evac|a/v") ? "EVACUATION"
        : Regex.IsMatch(d, @"\bdata\b|network") ? "DATA" : Regex.IsMatch(d, @"cctv|camera|security") ? "CCTV" : Regex.IsMatch(d, @"\bups\b|battery|booster|power supply") ? "POWER"
        : d.Contains("zigbee") ? "ACCESS" : Regex.IsMatch(d, @"lighting|انارة|إنارة") ? "LIGHT" : fallback;

    // ------------------------------------------------------------------ supply scope

    private static string Scope(string d, ItemSourceKind source, Func<string[], bool> has, Func<string, bool> rx)
    {
        bool Has(params string[] k) => has(k);
        if (rx(@"labou?r only|installation only|install only|free[\s-]issue|fix only") || Has("مصنعيات", "عمالة فقط", "مصنعية")) return SupplyScopes.LabourOnly;
        if (rx(@"supply only|supply of\b(?!.*install)") || Has("توريد فقط")) return SupplyScopes.SupplyOnly;
        if (rx(@"supply,? (and|&) (install|fix|apply|lay|pull)|supply,? install|furnish and install|supply & apply|supply and apply") || Has("توريد وتركيب", "توريد و تركيب")) return SupplyScopes.SupplyInstall;
        var labourVerbs = rx(@"\binstall|\bpull|hand ?over|terminat|wire pulling|commission|fixing of|connection") || Has("تركيب", "سحب", "تسليم", "تثبيت");
        if (source == ItemSourceKind.Contract && labourVerbs && !rx(@"\bsupply")) return SupplyScopes.LabourOnly;
        if (rx(@"\bsupply\b") && !labourVerbs) return SupplyScopes.SupplyOnly;
        return SupplyScopes.SupplyInstall;
    }

    // ------------------------------------------------------------------ finishing

    private static void Finish(string d, ItemSpec s, Func<string[], bool> has)
    {
        bool Has(params string[] k) => has(k);
        if (s.Unit.Length == 0)
            s.Unit = s.ItemType switch
            {
                ItemTypes.CableRun or ItemTypes.ElvCableRun or ItemTypes.ConduitRun or ItemTypes.Tray or ItemTypes.TrayCover or ItemTypes.LinearLight or ItemTypes.EarthTape => Units.M,
                ItemTypes.CableTermination => "END",
                _ => "PT",
            };
        if (s.ItemType == ItemTypes.CableTermination && s.Unit is "PCS" or "") s.Unit = "END";
        if (ItemTypes.IsPoint(s.ItemType) || s.ItemType is ItemTypes.FlexDrop)
        {
            if (s.Stages.Count == 0 && s.ItemType != ItemTypes.FlexDrop) s.Notes.Add("no stage in the text - complete point (1st + 2nd + 3rd fix)");
            if (s.Conduit.Length == 0 && s.ItemType != ItemTypes.FlexDrop && (s.Stages.Count == 0 || s.Stages.Contains(StageNames.First)))
                s.Notes.Add("conduit type not stated - template default (PVC)");
            if (s.Installation.Length == 0) s.Installation = s.Conduit is Conduits.Emt or Conduits.Rs ? "SURFACE" : "CONCEALED";
        }
        if (s.ItemType == ItemTypes.SocketPoint && s.System == "EV")
        {
            if (s.WireSizeMm2 <= 0) { s.WireCores = 3; s.WireSizeMm2 = 6; }
            if (s.ConduitSizeMm <= 0) s.ConduitSizeMm = 32;
        }
        if (s.ItemType == ItemTypes.SocketPoint && Has("twin", "double", "duplex")) s.Gangs = Math.Max(s.Gangs, 2);
        if (s.ItemType == ItemTypes.Isolator && s.Amps <= 0) s.Notes.Add("rating not found - 20 A assumed by the template");
        if (s.ItemType == ItemTypes.Db && s.Ways <= 0) s.Notes.Add("ways not found - template default");
        if (s.ItemType is ItemTypes.CableRun or ItemTypes.CableTermination && s.CableSizeMm2 > 0 && s.CableCores == 0)
        {
            s.CableCores = s.EarthCable ? 1 : 4;
            s.Notes.Add(s.EarthCable ? "earth cable: 1 core" : "cores not stated - 4 cores assumed");
        }
        if (s.ItemType is ItemTypes.CableRun && s.EarthCable && s.Sheath.Length == 0) s.Sheath = "LSOH";
        if (s.Height == HeightBands.Any && (ItemTypes.IsPoint(s.ItemType) || s.ItemType is ItemTypes.Tray or ItemTypes.FlexDrop)) s.Notes.Add("height band not stated (< 4.5 m used)");
        if (s.ItemType == ItemTypes.Unknown) s.Confidence = 0;
    }

    // ------------------------------------------------------------------ readers

    /// <summary>Lower case, single spaces, "²" -> "2", "×" -> "x".</summary>
    public static string Norm(string text) =>
        " " + Regex.Replace(text.ToLowerInvariant().Replace('²', '2').Replace('×', 'x').Replace('–', '-').Replace(' ', ' '), @"\s+", " ").Trim() + " ";

    private static int ConduitSize(string d)
    {
        foreach (var p in new[] { @"(\d{2})\s*mm\s*(?:dia\.?|diameter|ø|ø)?\s*(?:pvc|upvc|emt|rs|gi|rigid|flexible|flex)?\s*conduit", @"conduits?[^.;]{0,30}?(\d{2})\s*mm", @"ø\s*(\d{2})", @"(\d{2})\s*mm\s*(?:dia|diameter)" })
        {
            var m = Regex.Match(d, p);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v) && ConduitSizes.Contains(v)) return v;
        }
        return 0;
    }

    private static int Ways(string d)
    {
        var m = Regex.Match(d, @"(\d+)\s*(?:-|to)?\s*(\d+)?\s*ways?\b");
        if (m.Success) return int.Parse(m.Groups[m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? 2 : 1].Value, CultureInfo.InvariantCulture);
        m = Regex.Match(d, @"(\d+)\s*خط");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static double Amps(string d)
    {
        var m = Regex.Match(d, @"(\d+)\s*a\b(?!\s*/)");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static int Gangs(string d)
    {
        var m = Regex.Match(d, @"\b(\d|one|two|three|four|six)[\s-]*gang");
        if (!m.Success) return Regex.IsMatch(d, @"\btwin\b") ? 2 : 0;
        return m.Groups[1].Value switch { "one" => 1, "two" => 2, "three" => 3, "four" => 4, "six" => 6, var x => int.Parse(x, CultureInfo.InvariantCulture) };
    }

    private static int TrayWidth(string d, ItemSpec s)
    {
        var mm = Regex.Match(d, @"(\d{2,4})\s*mm\s*(?:wide|w\b|x\s*\d{2,3}\s*mm)?");
        if (mm.Success && int.TryParse(mm.Groups[1].Value, out var w) && w is >= 50 and <= 1200) return w;
        if (Regex.IsMatch(d, @"above 80\s*cm|over 80\s*cm|فوق 80|اكبر من 80|أكبر من 80")) { s.Notes.Add("size band above 80 cm: 900 mm tray assumed"); return 900; }
        if (Regex.IsMatch(d, @"40\s*cm\s*to\s*80|40\s*-\s*80\s*cm|من 40")) { s.Notes.Add("size band 40-80 cm: 600 mm tray assumed"); return 600; }
        if (Regex.IsMatch(d, @"5\s*cm\s*to\s*(28|30)|5\s*-\s*30\s*cm|من 5")) { s.Notes.Add("size band 5-30 cm: 200 mm tray assumed"); return 200; }
        s.Notes.Add("tray width not stated: 300 mm assumed");
        return 300;
    }

    private static void ReadWiring(string d, ItemSpec s)
    {
        // power cable written any way: "4X16mm2 CU/XLPE/SWA/LSOH", "cable 16 mm2 (4C or 3x16)", "16*4C", "70*1"
        var spec = Fingerprints.Cable(d);
        var m = Regex.Match(d, @"cable[^.;]{0,25}?(\d+(?:\.\d+)?)\s*mm2?\s*\((\d)\s*c\b");
        if (m.Success)
        {
            s.CableSizeMm2 = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            s.CableCores = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (Regex.Match(d, @"(\d+(?:\.\d+)?)\s*\*\s*(\d)\s*c?\b") is { Success: true } ar && (d.Contains("كابل") || d.Contains("cable")))
        {
            s.CableSizeMm2 = double.Parse(ar.Groups[1].Value, CultureInfo.InvariantCulture);
            s.CableCores = int.Parse(ar.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (spec != null && (d.Contains("cable") || d.Contains("كابل") || Regex.IsMatch(d, @"xlpe|swa|lsoh|lszh|lshz|g/y|\b\d\s*c\s*\d+(\.\d+)?\s*mm2")))
        {
            s.CableSizeMm2 = spec.Size; s.CableCores = spec.Cores;
            if (spec.FireRated) s.FireRated = true;
            if (spec.Earth) s.EarthCable = true;
            if (spec.Conductor == "AL") s.Conductor = "AL";
        }
        else if (Regex.Match(d, @"cable[^.;]{0,15}?(\d+(?:\.\d+)?)\s*mm2") is { Success: true } c1)
            s.CableSizeMm2 = double.Parse(c1.Groups[1].Value, CultureInfo.InvariantCulture);
        else if (Regex.Match(d, @"(\d+(?:\.\d+)?)\s*mm2[^.;]{0,25}cable") is { Success: true } c2)
            s.CableSizeMm2 = double.Parse(c2.Groups[1].Value, CultureInfo.InvariantCulture);
        if (s.CableSizeMm2 > 0 && s.CableCores == 0)
        {
            var cores = Regex.Match(d, @"\((\d)\s*c\b|\b(\d)\s*c\b|(\d)\s*/\s*c\b|(\d)\s*cores?\b");
            if (cores.Success) s.CableCores = int.Parse(cores.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value, CultureInfo.InvariantCulture);
        }

        // separate earth with the cable: "4C 16mm2 Cu/XLPE/SWA/LSZH + 1C 16mm2 Cu/LSF G/Y"
        var cpc = Regex.Match(d, @"\+\s*(?:1\s*c|1\s*x|1\s*core)\s*x?\s*(\d+(?:\.\d+)?)\s*mm");
        if (cpc.Success && s.CableSizeMm2 > 0)
        {
            s.CpcSizeMm2 = double.Parse(cpc.Groups[1].Value, CultureInfo.InvariantCulture);
            s.Notes.Add($"earth (CPC) cable {s.CpcSizeMm2:0.##} mm² stated with the cable");
        }

        // point wiring: "3x2.5 mm2 wires", "wiring 1.5mm2", "cat6"
        var w = Regex.Match(d, @"(\d)\s*[x]\s*(1\.5|2\.5|4|6|10)\s*mm2?\s*(?:lsoh|lszh|pvc|cu|single core|wires?|cables?)");
        if (w.Success && s.CableSizeMm2 is 0 or 1.5 or 2.5 or 4 or 6 or 10)
        {
            s.WireCores = int.Parse(w.Groups[1].Value, CultureInfo.InvariantCulture);
            s.WireSizeMm2 = double.Parse(w.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (Regex.Match(d, @"(?:wire|wiring)[^.;]{0,20}?(1\.5|2\.5|4|6)\s*mm2") is { Success: true } w2)
            s.WireSizeMm2 = double.Parse(w2.Groups[1].Value, CultureInfo.InvariantCulture);
        if (Regex.IsMatch(d, @"\bcat\s*6a\b")) s.Accessories.Add("CAT6A");
        else if (Regex.IsMatch(d, @"\bcat\s*6\b")) s.Accessories.Add("CAT6");
    }

    private static void ReadAccessories(string d, ItemSpec s)
    {
        void A(string name, params string[] keys) { if (keys.Any(k => Regex.IsMatch(d, k)) && !s.Accessories.Contains(name)) s.Accessories.Add(name); }
        A("GLANDS", @"\bglands?\b", "جلاند");
        A("LUGS", @"\blugs?\b", "كوس");
        A("LABELS", @"label", @"\btagging\b", @"numbering", "ترقيم");
        A("CABLE TIES", @"cable ties", @"cable tie", "تاي");
        A("FLEXIBLE CONDUIT", @"flexible", "فليكس");
        A("EARTHING", @"earthing|grounding", "تأريض");
        A("SUPPORTS", @"supports|hangers", "تعليق");
        A("COUPLERS", @"couplers?|joints");
        A("JUNCTION BOX", @"junction box|draw box|pull box");
        A("BACK BOX", @"back ?box|pattress");
        A("CIRCULAR BOX", @"circular box|round box");
        A("CLEANING", @"cleaning", "تنظيف");
        A("CHASING", @"chasing", "التكسير");
        A("ALUMINIUM PROFILE", @"alumin(i)?um profile", "بروفايل");
        if (s.Accessories.Contains("CIRCULAR BOX")) s.BoxType = "CIRCULAR BOX";
        else if (s.Accessories.Contains("BACK BOX")) s.BoxType = "BACK BOX";
        else if (s.Accessories.Contains("JUNCTION BOX")) s.BoxType = "JUNCTION BOX";
    }

    private static int Int(string d, string pattern)
    {
        var m = Regex.Match(d, pattern);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static double Num(string d, string pattern)
    {
        var m = Regex.Match(d, pattern);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>Parse coverage of a list of descriptions: share with a recognised item type.</summary>
    public static (int Total, int Recognised, Dictionary<string, int> ByType) Coverage(IEnumerable<ItemSpec> specs)
    {
        var list = specs.ToList();
        return (list.Count, list.Count(x => x.Recognised), list.GroupBy(x => x.ItemType).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()));
    }
}
