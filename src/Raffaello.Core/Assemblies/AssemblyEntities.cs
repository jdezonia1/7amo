using Raffaello.Core.Domain;

namespace Raffaello.Core.Assemblies;

// =====================================================================================================
//  [assemblies] BOQ ITEM BREAKDOWN (assembly / rate analysis).
//  Own tables (Asm*), created by SqliteAssemblyStore in the shared data file (SideStore conventions) or served by the
//  Raffaello server (AssembliesServerModule). Every quantity rule and price is data the user can edit; the seeded
//  library is a set of DEFAULTS TO BE CONFIRMED BY MOHAMED (flag Confirmed = false until he does).
// =====================================================================================================

public static class ComponentKinds
{
    public const string Material = "MATERIAL";
    public const string Labour = "LABOUR";
    public const string Equipment = "EQUIPMENT";
    public static readonly string[] All = { Material, Labour, Equipment };
}

/// <summary>How a LABOUR component is priced: subcontract rate per stage (contract) or hours x hourly rate.</summary>
public static class LabourBases
{
    public const string Subcontract = "SUBCONTRACT";
    public const string Hours = "HOURS";
    public static readonly string[] All = { Subcontract, Hours };
}

public static class PriceSources
{
    public const string Manual = "MANUAL";
    public const string Po = "PO";
    public const string PriceList = "PRICE LIST";
    public const string Contract = "CONTRACT";
    public const string Default = "DEFAULT";
    public const string Unknown = "UNKNOWN";
    public const string Ai = "AI";
}

/// <summary>An assembly template: the recipe for one item type (lighting point, socket point, cable run ...).</summary>
public sealed class AsmTemplate : Entity
{
    /// <summary>Unique code, e.g. LIGHT-PT.</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>One of <see cref="ItemTypes"/>.</summary>
    public string ItemType { get; set; } = "";
    /// <summary>Unit of the BOQ item the template prices (PT, M, NO, END).</summary>
    public string Unit { get; set; } = "PT";
    public string Description { get; set; } = "";
    /// <summary>SEED / USER / AI.</summary>
    public string Origin { get; set; } = "SEED";
    /// <summary>Mohamed confirmed the recipe (seeded defaults start unconfirmed).</summary>
    public bool Confirmed { get; set; }
    public bool Active { get; set; } = true;
    public string Notes { get; set; } = "";
}

/// <summary>A named number used by the quantity formulas. TemplateId 0 = global (all templates).</summary>
public sealed class AsmParam : Entity
{
    public long TemplateId { get; set; }
    /// <summary>Identifier used in formulas (route_len, stick_len ...).</summary>
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public string Label { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Confirmed { get; set; }
}

/// <summary>One line of a template: what is needed per unit of the item, as a formula.</summary>
public sealed class AsmComponent : Entity
{
    public long TemplateId { get; set; }
    public int Order { get; set; }
    /// <summary>Identifier: later formulas can use this component's quantity per unit (before waste).</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Material specification; {placeholders} are filled from the parsed item (e.g. "{conduit} conduit {conduit_size} mm").</summary>
    public string Spec { get; set; } = "";
    public string Unit { get; set; } = "PCS";
    /// <summary>Quantity per unit of the item (formula over parameters, spec values and earlier component keys).</summary>
    public string QtyFormula { get; set; } = "1";
    /// <summary>0.05 = 5 %.</summary>
    public double WastePct { get; set; }
    /// <summary>1ST FIX / 2ND FIX / 3RD FIX, or "" when the line belongs to every stage.</summary>
    public string Stage { get; set; } = "";
    public string Kind { get; set; } = ComponentKinds.Material;
    /// <summary>LABOUR only: SUBCONTRACT (contract rate per stage) or HOURS (hours x hourly rate).</summary>
    public string LabourBasis { get; set; } = "";
    /// <summary>LABOUR / SUBCONTRACT only: contract item category to look up (OUTLET, WIRING, DEVICE, FLEX CONDUIT, CABLE, CABLE TERMINATION, TRAY, PANEL ...).</summary>
    public string LabourCategory { get; set; } = "";
    /// <summary>Price used when no PO / price-list / manual price is found (formula, e.g. "lab_1st" or "1.2"). Empty = unknown.</summary>
    public string DefaultPrice { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>A component price: manual entry or a supplier price-list row. PO prices are read live from the Materials module.</summary>
public sealed class AsmPrice : Entity
{
    /// <summary>Normalised specification (see <see cref="PriceBook.KeyOf"/>).</summary>
    public string Key { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Price { get; set; }
    public string Currency { get; set; } = "SAR";
    /// <summary>MANUAL / PRICE LIST.</summary>
    public string Source { get; set; } = PriceSources.Manual;
    /// <summary>File name / quotation no / who typed it.</summary>
    public string SourceRef { get; set; } = "";
    public string Supplier { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public DateTime? PriceDate { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>User corrections to the parsed spec of a BOQ / contract item (kept so the next parse uses them).</summary>
public sealed class AsmItemSpec : Entity
{
    /// <summary>CONTRACT / BOQ / BOQLINE / TEXT.</summary>
    public string SourceKind { get; set; } = "";
    /// <summary>CONTRACT: "contractNo|itemNo"; BOQ / BOQLINE: BOQ code; TEXT: normalised description.</summary>
    public string SourceKey { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The edited <see cref="ItemSpec"/> as JSON.</summary>
    public string SpecJson { get; set; } = "";
    /// <summary>Template chosen for the item (empty = by item type).</summary>
    public string TemplateCode { get; set; } = "";
    /// <summary>Per-item parameter overrides as JSON {"route_len": 8}.</summary>
    public string ParamsJson { get; set; } = "";
    public bool Confirmed { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>Assembly settings (overhead %, profit %, labour mode ...), one row per name.</summary>
public sealed class AsmSetting : Entity
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}
