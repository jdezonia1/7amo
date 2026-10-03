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
    // contract BOQ (print-style bills, 03-Oct): position on the printed page, used to find the project code
    /// <summary>Section letter from the page footer / banner, e.g. "R" (electrical installations).</summary>
    public string Section { get; set; } = "";
    /// <summary>Page number from the footer "B6.R / Page 3".</summary>
    public int Page { get; set; }
    /// <summary>Item letter on the page (A..Z, AA..).</summary>
    public string Ref { get; set; } = "";
    /// <summary>"Rate Only" / note text in the Total column: priced but no quantity value.</summary>
    public bool RateOnly { get; set; }
    /// <summary>How BoqCode was found: PROJECT CODE (exact page), PROJECT CODE (PAGE MAP: project code pages restart per division), PAGE KEY (no project code).</summary>
    public string CodeSource { get; set; } = "";
    /// <summary>"B6.R / Page 3 / AM" - the printed reference.</summary>
    public string PageRef => Page > 0 ? $"{Bill}.{Section} / Page {Page} / {Ref}" : "";
}

/// <summary>A learned categorisation correction: normalised description (or keyword) -> system + category.</summary>
public sealed class BoqCatRule : Entity
{
    public string Key { get; set; } = "";
    public string System { get; set; } = "";
    public string Category { get; set; } = "";
    public int Uses { get; set; }
}
