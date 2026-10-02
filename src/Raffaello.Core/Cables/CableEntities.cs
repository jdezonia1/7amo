using Raffaello.Core.Domain;

namespace Raffaello.Core.Cables;

// =====================================================================================================
//  [cables] Panel & cable register (the source of truth), cable claims, flag decisions, import memory.
//  Plain POCOs (public get/set = columns, table = type name + "s") so SQLite and the server map them by reflection.
// =====================================================================================================

/// <summary>Cable payment stages (labour contracts: 70 % installation &amp; pulling, 20 % termination &amp; test, 10 % initial handover).</summary>
public static class CableStages
{
    public const string Pulling = "PULLING";
    public const string Termination = "TERMINATION & TEST";
    public const string Handover = "HANDOVER";
    public static readonly string[] All = { Pulling, Termination, Handover };

    /// <summary>Contract payment share of each stage.</summary>
    public static double Pct(string stage) => Normalize(stage) switch { Pulling => 0.7, Termination => 0.2, Handover => 0.1, _ => 0 };

    /// <summary>Order (pulling first): a later stage claimed before an earlier one is flagged.</summary>
    public static int Order(string stage) => Normalize(stage) switch { Pulling => 1, Termination => 2, Handover => 3, _ => 9 };

    /// <summary>"CABLE PULLING", "PULLING", "INSTALLATION" -> PULLING; "TERMINATION", "TEST", "TERM&amp;TEST" -> TERMINATION &amp; TEST; "HANDOVER" -> HANDOVER.</summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().ToUpperInvariant();
        if (s.Length == 0) return Pulling;
        if (s.Contains("HAND")) return Handover;
        if (s.Contains("TERM") || s.Contains("TEST")) return Termination;
        if (s.Contains("PULL") || s.Contains("INSTAL") || s.Contains("LAY")) return Pulling;
        return s;
    }

    /// <summary>The ledger stage the tracker uses for cable pulling lines.</summary>
    public const string LedgerPulling = "CABLE PULLING";
    public static bool IsLedgerCableStage(string? stage) => string.Equals((stage ?? "").Trim(), LedgerPulling, StringComparison.OrdinalIgnoreCase);
}

public static class CableStatus
{
    public const string Provisional = "PROVISIONAL";   // created from statements only - no design length
    public const string Proposed = "PROPOSED";         // read from a drawing / schedule, not yet confirmed
    public const string Confirmed = "CONFIRMED";
    public const string Rejected = "REJECTED";
    public static readonly string[] All = { Provisional, Proposed, Confirmed, Rejected };
}

public static class CableSources
{
    public const string Schedule = "SCHEDULE";
    public const string SldPdf = "SLD-PDF";
    public const string SldScan = "SLD-SCAN";
    public const string Drawing = "DWG/DXF";
    public const string Statement = "STATEMENT";
    public const string Tracker = "TRACKER";
    public const string Manual = "MANUAL";
}

/// <summary>A panel / board / piece of equipment that cables run between. <see cref="Key"/> is the normalised name (see <see cref="PanelNames"/>).</summary>
public sealed class CablePanel : Entity
{
    /// <summary>Display name as first read (or as confirmed by the user).</summary>
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>MDB / EMDB / SMDB / ESMDB / EMCC / MCC / DB / LDB / EDB / FACP / JF / MAF / EAF / FFP ... or EQUIPMENT.</summary>
    public string Type { get; set; } = "";
    public string Building { get; set; } = "";
    public string Zone { get; set; } = "";
    public string Level { get; set; } = "";
    public string Room { get; set; } = "";
    public string Suffix { get; set; } = "";
    /// <summary>Feeder (parent) panel key.</summary>
    public string ParentKey { get; set; } = "";
    public bool IsEquipment { get; set; }
    public string Status { get; set; } = CableStatus.Proposed;
    public string SourceKind { get; set; } = "";
    public string SourceDoc { get; set; } = "";
    public int SourcePage { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>A spelling of a panel name that resolves to <see cref="PanelKey"/>. Learned when the user confirms a merge.</summary>
public sealed class CablePanelAlias : Entity
{
    public string PanelKey { get; set; } = "";
    public string Alias { get; set; } = "";
    /// <summary>Normalised key of the alias text.</summary>
    public string AliasKey { get; set; } = "";
    /// <summary>AUTO (same normalised key) / USER (confirmed merge) / REJECTED (user said "not the same").</summary>
    public string Kind { get; set; } = "USER";
    public string ConfirmedBy { get; set; } = "";
    public DateTime? ConfirmedAt { get; set; }
}

/// <summary>One cable route FROM -> TO (phase conductors; the earth conductor is a companion on the same route, not a separate run).</summary>
public sealed class CableRun : Entity
{
    /// <summary>Short reference shown to the user (C-0001), or the circuit / cable tag of the schedule.</summary>
    public string Ref { get; set; } = "";
    public string Building { get; set; } = "";
    public string Level { get; set; } = "";
    public string FromKey { get; set; } = "";
    public string FromName { get; set; } = "";
    public string ToKey { get; set; } = "";
    public string ToName { get; set; } = "";
    public int Cores { get; set; }
    public double SizeMm2 { get; set; }
    /// <summary>Normalised size "4X16" (see <see cref="CableSize"/>).</summary>
    public string SizeKey { get; set; } = "";
    /// <summary>CU / AL.</summary>
    public string Conductor { get; set; } = "CU";
    /// <summary>XLPE / LSOH / MICA (fire rated) / PVC.</summary>
    public string Insulation { get; set; } = "";
    /// <summary>Companion earth conductor, e.g. "1X16" with a 4X16 run (blank when none / unknown).</summary>
    public string EarthSizeKey { get; set; } = "";
    /// <summary>Length from the cable schedule / SLD / drawing (m).</summary>
    public double? DesignLength { get; set; }
    /// <summary>Route length measured on the drawings (Drawings module) (m).</summary>
    public double? MeasuredLength { get; set; }
    public string CircuitRef { get; set; } = "";
    public string Breaker { get; set; } = "";
    public string Status { get; set; } = CableStatus.Proposed;
    public string SourceKind { get; set; } = "";
    public string SourceDoc { get; set; } = "";
    public int SourcePage { get; set; }
    /// <summary>0..1 how sure the reader was (SLD geometry); 1 for schedules and confirmed runs.</summary>
    public double Confidence { get; set; } = 1;
    public string Notes { get; set; } = "";

    /// <summary>Route key (direction-independent): the same pair of panels whichever way round it is written.</summary>
    public string RouteKey => CableKeys.Route(FromKey, ToKey);
    public string Title => $"{FromName} -> {ToName} {SizeKey}";
    /// <summary>Measured route length when known, else the design length.</summary>
    public double? ReferenceLength => MeasuredLength is > 0 ? MeasuredLength : DesignLength is > 0 ? DesignLength : null;
}

/// <summary>A cable quantity claimed by a subcontractor (site statement CABLES sheet, tracker CABLES sheets, ledger CABLE PULLING lines).</summary>
public sealed class CableClaim : Entity
{
    public long? RunId { get; set; }
    public string Building { get; set; } = "";
    public string Subcontractor { get; set; } = "";
    public int InvoiceNo { get; set; }
    public string StatementNo { get; set; } = "";
    /// <summary>PULLING / TERMINATION &amp; TEST / HANDOVER.</summary>
    public string Stage { get; set; } = CableStages.Pulling;
    public string RawStage { get; set; } = "";
    public string Location { get; set; } = "";
    public string Level { get; set; } = "";
    public string FromRaw { get; set; } = "";
    public string ToRaw { get; set; } = "";
    public string FromKey { get; set; } = "";
    public string ToKey { get; set; } = "";
    public string SizeRaw { get; set; } = "";
    public string SizeKey { get; set; } = "";
    /// <summary>Single-core earth conductor claimed on the same route as a multi-core cable (companion).</summary>
    public bool IsEarth { get; set; }
    /// <summary>Metres claimed.</summary>
    public double Qty { get; set; }
    public double SitePct { get; set; } = 1;
    public double WirPct { get; set; } = 1;
    public string WirNo { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>TRACKER / LEDGER / STATEMENT / MANUAL.</summary>
    public string Source { get; set; } = CableSources.Manual;
    /// <summary>Stable key of the source row (import dedupe).</summary>
    public string SourceKey { get; set; } = "";
    /// <summary><see cref="ClaimLine.SourceKey"/> of the ledger line that carries the same claim (no double counting).</summary>
    public string LedgerSourceKey { get; set; } = "";
    /// <summary>MATCHED (design run) / PROVISIONAL (run from statements only) / UNKNOWN.</summary>
    public string MatchStatus { get; set; } = "";
    public double MatchScore { get; set; }
    public DateTime EnteredAt { get; set; }

    public double QtyAfterSite => Qty * SitePct;
    public double QtyAfterWir => Qty * SitePct * WirPct;
    public string Route => CableKeys.Route(FromKey, ToKey);
}

/// <summary>A cable flag the user let through (warnings never block; the bypass and its reason are audited).</summary>
public sealed class CableFlagDecision : Entity
{
    /// <summary>Stable flag key (code + claim + related claims).</summary>
    public string FlagKey { get; set; } = "";
    public string Code { get; set; } = "";
    public long ClaimId { get; set; }
    /// <summary>BYPASSED (accepted with a reason).</summary>
    public string Decision { get; set; } = "BYPASSED";
    public string Reason { get; set; } = "";
    public string By { get; set; } = "";
    public DateTime At { get; set; }
}

/// <summary>Remembered column mapping of a cable schedule layout (by header signature).</summary>
public sealed class CableImportProfile : Entity
{
    public string Signature { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"FIELD=Header;FIELD=Header" (see <see cref="CableScheduleImporter.Fields"/>).</summary>
    public string Mapping { get; set; } = "";
    public DateTime LastUsed { get; set; }
}

public static class CableKeys
{
    public static string Route(string a, string b)
    {
        a ??= ""; b ??= "";
        return string.CompareOrdinal(a, b) <= 0 ? a + "<>" + b : b + "<>" + a;
    }
}
