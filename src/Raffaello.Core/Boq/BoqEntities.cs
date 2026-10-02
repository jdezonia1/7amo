using Raffaello.Core.Domain;

namespace Raffaello.Core.Boq;

/// <summary>One row of an imported owner BOQ workbook, categorised by system and category.</summary>
public sealed class BoqLine : Entity
{
    public string Source { get; set; } = "";
    public string Sheet { get; set; } = "";
    public int RowNo { get; set; }
    public string Bill { get; set; } = "";
    public string ItemNo { get; set; } = "";
    public string BoqCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Qty { get; set; }
    public double Rate { get; set; }
    public double Amount { get; set; }
    /// <summary>True for heading / note rows (no qty / rate).</summary>
    public bool IsHeading { get; set; }
    public string System { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>RULE / LEARNED / AI / MANUAL / HEADING.</summary>
    public string CatSource { get; set; } = "";
    public double CatScore { get; set; }
    public bool Confirmed { get; set; }
}

/// <summary>A learned categorisation correction: normalised description (or keyword) -> system + category.</summary>
public sealed class BoqCatRule : Entity
{
    public string Key { get; set; } = "";
    public string System { get; set; } = "";
    public string Category { get; set; } = "";
    public int Uses { get; set; }
}
