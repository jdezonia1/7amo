using System.Globalization;
using System.Text.Json;

namespace Raffaello.Core.Assemblies;

/// <summary>[assemblies] Item types the parser recognises; each maps to a default template.</summary>
public static class ItemTypes
{
    public const string LightingPoint = "LIGHTING POINT";
    public const string SocketPoint = "SOCKET POINT";
    public const string SwitchPoint = "SWITCH POINT";
    public const string DaliPoint = "DALI POINT";
    public const string DataPoint = "DATA POINT";
    public const string GrmsPoint = "GRMS POINT";
    public const string FireAlarmPoint = "FIRE ALARM POINT";
    public const string ElvPoint = "ELV POINT";
    public const string FlexDrop = "FLEX DROP";
    public const string Homerun = "HOMERUN";
    public const string LinearLight = "LINEAR LIGHT";
    public const string Accessory = "ACCESSORY";
    public const string Luminaire = "LUMINAIRE";
    public const string Isolator = "ISOLATOR";
    public const string FloorBox = "FLOOR BOX";
    public const string CableRun = "CABLE RUN";
    public const string CableTermination = "CABLE TERMINATION";
    public const string ElvCableRun = "ELV CABLE RUN";
    public const string ConduitRun = "CONDUIT RUN";
    public const string Tray = "CABLE TRAY";
    public const string TrayCover = "TRAY COVER";
    public const string Db = "DISTRIBUTION BOARD";
    public const string MainPanel = "MDB / SMDB";
    public const string SystemPanel = "SYSTEM PANEL";
    public const string EarthPit = "EARTH PIT";
    public const string EarthBar = "EARTH BAR";
    public const string EarthTape = "EARTH TAPE";
    public const string Lightning = "LIGHTNING PROTECTION";
    public const string EvCharger = "EV CHARGER";
    public const string FinalConnection = "FINAL CONNECTION";
    public const string Unknown = "UNKNOWN";

    public static readonly string[] All =
    {
        LightingPoint, SocketPoint, SwitchPoint, DaliPoint, DataPoint, GrmsPoint, FireAlarmPoint, ElvPoint, FlexDrop, Homerun, LinearLight,
        Accessory, Luminaire, Isolator, FloorBox, CableRun, CableTermination, ElvCableRun, ConduitRun, Tray, TrayCover, Db, MainPanel,
        SystemPanel, EarthPit, EarthBar, EarthTape, Lightning, EvCharger, FinalConnection, Unknown,
    };

    /// <summary>Point types (conduit + box + wire + accessory, priced per point).</summary>
    public static bool IsPoint(string t) => t is LightingPoint or SocketPoint or SwitchPoint or DaliPoint or DataPoint or GrmsPoint or FireAlarmPoint or ElvPoint;
}

public static class SupplyScopes
{
    public const string SupplyInstall = "SUPPLY + INSTALL";
    public const string LabourOnly = "LABOUR ONLY";
    public const string SupplyOnly = "SUPPLY ONLY";
    public static readonly string[] All = { SupplyInstall, LabourOnly, SupplyOnly };
}

public static class StageNames
{
    public const string First = "1ST FIX";
    public const string Second = "2ND FIX";
    public const string Third = "3RD FIX";
    public static readonly string[] All = { First, Second, Third };
}

/// <summary>
/// [assemblies] Structured reading of a BOQ / contract item description. Every field is editable in the UI; <see cref="Notes"/>
/// says what was found and what was assumed.
/// </summary>
public sealed class ItemSpec
{
    public string ItemType { get; set; } = ItemTypes.Unknown;
    /// <summary>LIGHT / POWER / DATA / TV / CCTV / GRMS / DALI / FIRE / BMS / ACCESS ... (first system found).</summary>
    public string System { get; set; } = "";
    /// <summary>Other item types the text also covers ("lighting switch, power socket or lighting point").</summary>
    public List<string> AlsoCovers { get; set; } = new();
    /// <summary>PVC / EMT / RS / FLEX / NONE ("" = template default).</summary>
    public string Conduit { get; set; } = "";
    /// <summary>mm; 0 = default for the type.</summary>
    public int ConduitSizeMm { get; set; }
    /// <summary>Point wiring: cores (L + N + E = 3) and size (mm²). 0 = default for the type.</summary>
    public int WireCores { get; set; }
    public double WireSizeMm2 { get; set; }
    /// <summary>Cable run / termination: cores x size.</summary>
    public int CableCores { get; set; }
    public double CableSizeMm2 { get; set; }
    public bool EarthCable { get; set; }
    /// <summary>Separate earth (CPC) cable stated with the cable ("+ 1C 16mm2 G/Y"), mm²; 0 = none stated.</summary>
    public double CpcSizeMm2 { get; set; }
    public bool FireRated { get; set; }
    /// <summary>LSOH / PVC / "".</summary>
    public string Sheath { get; set; } = "";
    public bool Armoured { get; set; }
    /// <summary>CU / AL.</summary>
    public string Conductor { get; set; } = "CU";
    /// <summary>WALL / CEILING / BOTH / FLOOR / STAND.</summary>
    public string Mount { get; set; } = "";
    /// <summary>CONCEALED / SURFACE / "".</summary>
    public string Installation { get; set; } = "";
    /// <summary>LOW (&lt; 4.5 m) / HIGH (&gt; 4.5 m) / ANY.</summary>
    public string Height { get; set; } = "ANY";
    /// <summary>Stages covered by the item; empty = all stages (complete point).</summary>
    public List<string> Stages { get; set; } = new();
    public string Supply { get; set; } = SupplyScopes.SupplyInstall;
    public string Unit { get; set; } = "";
    public string BoxType { get; set; } = "";
    public List<string> Accessories { get; set; } = new();
    public int Ways { get; set; }
    public double Amps { get; set; }
    public int TrayWidthMm { get; set; }
    public int Compartments { get; set; }
    public int Gangs { get; set; }
    public double Watts { get; set; }
    /// <summary>Route length per point stated in the text (m), 0 = none.</summary>
    public double RouteLengthM { get; set; }
    public bool Facade { get; set; }
    public bool Homerun { get; set; }
    /// <summary>0..1 - how sure the parser is about the item type.</summary>
    public double Confidence { get; set; }
    public List<string> Notes { get; set; } = new();

    public bool Recognised => ItemType != ItemTypes.Unknown;

    /// <summary>True when the item covers the stage (no stage stated = all stages).</summary>
    public bool Covers(string stage) => string.IsNullOrEmpty(stage) || Stages.Count == 0 || Stages.Contains(stage, StringComparer.OrdinalIgnoreCase);

    public string StagesText => Stages.Count == 0 ? "ALL STAGES" : string.Join(" + ", Stages);

    public string Summary
    {
        get
        {
            var parts = new List<string> { ItemType };
            if (System.Length > 0) parts.Add(System);
            if (Conduit.Length > 0 && Conduit != "NONE") parts.Add(ConduitSizeMm > 0 ? $"{Conduit} {ConduitSizeMm} mm" : Conduit);
            if (WireSizeMm2 > 0) parts.Add($"{WireCores}x{F(WireSizeMm2)} mm²");
            if (CableSizeMm2 > 0) parts.Add($"{(CableCores > 0 ? CableCores + "C " : "")}{F(CableSizeMm2)} mm²{(FireRated ? " FR" : "")}{(EarthCable ? " EARTH" : "")}");
            if (Mount.Length > 0) parts.Add(Mount);
            if (Height is "LOW" or "HIGH") parts.Add(Height == "LOW" ? "< 4.5 m" : "> 4.5 m");
            parts.Add(StagesText);
            parts.Add(Supply);
            return string.Join(" | ", parts);
        }
    }

    public ItemSpec Clone() => FromJson(ToJson()) ?? new ItemSpec();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static ItemSpec? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ItemSpec>(json, Json); } catch (JsonException) { return null; }
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
