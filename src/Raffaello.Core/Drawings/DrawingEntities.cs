using System.Globalization;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Drawings;

// =====================================================================================================
//  [drawings] Drawings module: takeoff (counts + lengths), statement verification, revision compare.
//  Coordinates of hits / runs / rooms are "sheet units": pixels at the sheet's DPI for PDF / image sheets,
//  model units for CAD (DXF / DWG) and IFC / Revit JSON sheets. DwgSheet.MetresPerUnit turns them into metres.
// =====================================================================================================

public static class DwgSourceKinds
{
    public const string Pdf = "PDF", Image = "IMAGE", Dxf = "DXF", Dwg = "DWG", Ifc = "IFC", RevitJson = "REVIT JSON";
    public static readonly string[] All = { Pdf, Image, Dxf, Dwg, Ifc, RevitJson };
    public static bool IsRaster(string k) => k is Pdf or Image;
    public static bool IsModel(string k) => k is Dxf or Dwg or Ifc or RevitJson;

    public static string FromPath(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => Pdf,
        ".dxf" => Dxf,
        ".dwg" => Dwg,
        ".ifc" => Ifc,
        ".json" => RevitJson,
        _ => Image,
    };
}

public static class DwgHitStatus
{
    /// <summary>Found by the matcher / reader, not reviewed.</summary>
    public const string Auto = "AUTO";
    public const string Confirmed = "CONFIRMED";
    /// <summary>Removed by the user (false positive). Never counted.</summary>
    public const string Removed = "REMOVED";
    /// <summary>Added by the user (missed symbol).</summary>
    public const string Added = "ADDED";
    /// <summary>Claude vision said "not this symbol" - not counted, the user can restore it.</summary>
    public const string AiRejected = "AI REJECTED";
    public const string AiConfirmed = "AI CONFIRMED";
    public static bool Counts(string s) => s is not (Removed or AiRejected);
}

public static class DwgOrigins
{
    public const string Template = "TEMPLATE", Block = "BLOCK", Geometry = "GEOMETRY", Ifc = "IFC", Json = "JSON", Manual = "MANUAL",
        Vector = "VECTOR", Raster = "RASTER", Cad = "CAD", Highlight = "HIGHLIGHT", Route = "ROUTE";
}

/// <summary>A drawing sheet (one PDF page, image, CAD file or model) per building / level, with its revision and scale.</summary>
public sealed class DwgSheet : Entity
{
    public string Building { get; set; } = Buildings.Branded;
    public string Level { get; set; } = "";
    public string SheetNo { get; set; } = "";
    public string Title { get; set; } = "";
    public string Revision { get; set; } = "0";
    /// <summary>PDF / IMAGE / DXF / DWG / IFC / REVIT JSON.</summary>
    public string SourceKind { get; set; } = DwgSourceKinds.Pdf;
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>1-based PDF page.</summary>
    public int Page { get; set; } = 1;
    /// <summary>Raster resolution the sheet units refer to (PDF / image sheets).</summary>
    public int Dpi { get; set; } = 150;
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    /// <summary>Drawing scale 1:N (0 = unknown).</summary>
    public double ScaleDenominator { get; set; }
    /// <summary>Metres per sheet unit (0 = unknown - lengths are then reported in sheet units only).</summary>
    public double MetresPerUnit { get; set; }
    /// <summary>TITLE BLOCK / CALIBRATED / INSUNITS / MANUAL / IFC.</summary>
    public string ScaleSource { get; set; } = "";
    /// <summary>Tracker plan code (PlanImage.Plan) whose room shapes are aligned to this sheet.</summary>
    public string PlanCode { get; set; } = "";
    /// <summary>DRAWING (clean) or STATEMENT (subcontractor markup).</summary>
    public string Role { get; set; } = "DRAWING";
    public DateTime ImportedAt { get; set; }
    public string Notes { get; set; } = "";

    public string Label => $"{Building} {Level} {SheetNo} rev {Revision}".Trim();

    /// <summary>Metres per pixel from DPI + scale (paper mm x N / 1000).</summary>
    public static double MetresPerPixel(int dpi, double scaleDenominator) => dpi <= 0 || scaleDenominator <= 0 ? 0 : 25.4 / dpi * scaleDenominator / 1000.0;
}

/// <summary>
/// A symbol of the library: one example cut from a sheet (template), a CAD block name, a geometry signature or an IFC class,
/// mapped to the PROJECT QTY keys it feeds (stage | item | qty per hit).
/// </summary>
public sealed class DwgSymbol : Entity
{
    public string Name { get; set; } = "";
    /// <summary>POWER / LIGHT / DATA / GRMS ... (for colours, filters and statement comparison).</summary>
    public string System { get; set; } = "";
    /// <summary>PROJECT QTY item the stage rules count it under (POWER, LIGHT, DATA, GRMS, AV ...). Defaults to <see cref="System"/>.</summary>
    public string Item { get; set; } = "";
    /// <summary>Text in the ITEM / TAG column of the takeoff sheet ("SOCKET/SPUR", "FCU/HIGH-LEVEL SPUR").</summary>
    public string Tag { get; set; } = "";
    /// <summary>W = wall item, C = ceiling item (H &gt;= 3000 -> ceiling).</summary>
    public string Mount { get; set; } = "W";
    /// <summary>Ceiling light fitting (DALI column).</summary>
    public bool IsLightFitting { get; set; }
    /// <summary>Quantity per hit at 2ND FIX (twin data outlet = 2).</summary>
    public double SecondFixFactor { get; set; } = 1;
    /// <summary>Not counted (terrace / WP sockets when excluded); marked grey "EXCL" on the takeoff sheet.</summary>
    public bool Excluded { get; set; }
    /// <summary>Default mounting height (m) for the vertical part of cable lengths (0 = the settings default for the item).</summary>
    public double MountingHeightM { get; set; }
    /// <summary>Explicit "STAGE|ITEM|QTY;..." targets - when set they replace the stage rules (e.g. "1ST FIX|POWER|1;2ND FIX|POWER|1").</summary>
    public string Targets { get; set; } = "";
    /// <summary>PNG of the example (greyscale) at <see cref="TemplateDpi"/>.</summary>
    public byte[]? TemplatePng { get; set; }
    public int TemplateDpi { get; set; } = 150;
    /// <summary>Per-symbol NCC threshold (0 = use the run's).</summary>
    public double Threshold { get; set; }
    public bool AllowRotation { get; set; } = true;
    public bool AllowMirror { get; set; } = true;
    /// <summary>CAD block names (wildcards *, ?) separated by ';'.</summary>
    public string BlockNames { get; set; } = "";
    /// <summary>Geometry signature of the symbol for exploded CAD blocks (see <see cref="GeometrySignature"/>).</summary>
    public string GeometrySignature { get; set; } = "";
    /// <summary>IFC classes ("IfcOutlet;IfcLightFixture") and optional predefined type / name filter "IfcOutlet:POWEROUTLET".</summary>
    public string IfcClasses { get; set; } = "";
    public string ColourHex { get; set; } = "";
    public long? SourceSheetId { get; set; }
    /// <summary>"x,y,w,h" on the source sheet.</summary>
    public string SourceBox { get; set; } = "";
    public string Building { get; set; } = "";
    public bool Active { get; set; } = true;
    public string Notes { get; set; } = "";
}

/// <summary>
/// A class of linear element measured for lengths (tray 300 mm, PVC conduit 25, cable 4C 16 ...): how to recognise it
/// (PDF vector style, raster colour, CAD layers, IFC classes) and the PROJECT QTY keys it feeds (metres).
/// </summary>
public sealed class DwgLinearClass : Entity
{
    public string Name { get; set; } = "";
    /// <summary>TRAY / TRUNKING / LADDER / CONDUIT / CABLE ...</summary>
    public string System { get; set; } = "";
    /// <summary>Size text ("300", "25 PVC", "4C 16").</summary>
    public string Size { get; set; } = "";
    /// <summary>"1ST FIX|CABLE TRAY 300|1" - factor per metre.</summary>
    public string Targets { get; set; } = "";
    /// <summary>Vector style key from a picked legend sample (see <see cref="VectorStyle.Key"/>); several separated by ';'.</summary>
    public string StyleKeys { get; set; } = "";
    /// <summary>CAD layer patterns (wildcards) separated by ';'.</summary>
    public string Layers { get; set; } = "";
    public string IfcClasses { get; set; } = "";
    /// <summary>Raster fallback: line colour to trace on scans.</summary>
    public string ColourHex { get; set; } = "";
    public double ColourTolerance { get; set; } = 60;
    public string Building { get; set; } = "";
    public bool Active { get; set; } = true;
}

/// <summary>One takeoff run on a sheet (template matching, vector / CAD / IFC reading or an imported Revit JSON).</summary>
public sealed class DwgTakeoff : Entity
{
    public long SheetId { get; set; }
    public DateTime RunAt { get; set; }
    public string Source { get; set; } = DwgOrigins.Template;
    public double Threshold { get; set; }
    public string Scales { get; set; } = "";
    /// <summary>DRAFT / REVIEWED / APPLIED (PROJECT QTY diff accepted).</summary>
    public string Status { get; set; } = "DRAFT";
    public int Hits { get; set; }
    public int Runs { get; set; }
    public double Seconds { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>A counted symbol occurrence.</summary>
public sealed class DwgHit : Entity
{
    public long TakeoffId { get; set; }
    public long SymbolId { get; set; }
    public string SymbolName { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double Score { get; set; }
    public int Rotation { get; set; }
    public bool Mirrored { get; set; }
    public string Room { get; set; } = "";
    public string Status { get; set; } = DwgHitStatus.Auto;
    public string Origin { get; set; } = DwgOrigins.Template;
    /// <summary>Block attributes / IFC name, for the review list.</summary>
    public string Attributes { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>MARK NO. on the takeoff sheet (sequential per sheet).</summary>
    public int MarkNo { get; set; }
    /// <summary>Mounting height read next to the symbol ("H=1350mm"), metres; 0 = unknown.</summary>
    public double MountingHeightM { get; set; }

    public PointD Center => new(X + W / 2, Y + H / 2);
}

/// <summary>A measured linear run (tray / conduit / cable / highlighted route) - polyline in sheet units, length in metres.</summary>
public sealed class DwgRun : Entity
{
    public long TakeoffId { get; set; }
    public long ClassId { get; set; }
    public string ClassName { get; set; } = "";
    /// <summary>"x,y x,y ..." in sheet units.</summary>
    public string Points { get; set; } = "";
    public double LengthUnits { get; set; }
    public double LengthM { get; set; }
    /// <summary>Room the run is in ("" = several / none; per-room split is computed on the fly).</summary>
    public string Room { get; set; } = "";
    public string Status { get; set; } = DwgHitStatus.Auto;
    public string Origin { get; set; } = DwgOrigins.Vector;
    public string StyleKey { get; set; } = "";
    public string Circuit { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>Alignment of the tracker plan (room shapes, normalised 0..1) to a sheet: clicked point pairs and the fitted transform.</summary>
public sealed class DwgCalibration : Entity
{
    public long SheetId { get; set; }
    public string Plan { get; set; } = "";
    /// <summary>"sx,sy>px,py;..." sheet point > plan point (normalised).</summary>
    public string Pairs { get; set; } = "";
    /// <summary>Plan (normalised) -> sheet units, <see cref="Affine2D.Serialize"/>.</summary>
    public string Transform { get; set; } = "";
    public double Residual { get; set; }
    public DateTime At { get; set; }
}

/// <summary>A room boundary on a sheet (sheet units) from the tracker shapes, a CSV / JSON import, CAD polylines or IFC spaces.</summary>
public sealed class DwgRoom : Entity
{
    public long SheetId { get; set; }
    public string Room { get; set; } = "";
    public string Level { get; set; } = "";
    /// <summary>"x,y x,y ..." sheet units.</summary>
    public string Polygon { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>Ceiling / route height for vertical drops (m, 0 = use the default).</summary>
    public double CeilingHeightM { get; set; }
}

/// <summary>A decision on a proposed PROJECT QTY change (never applied silently).</summary>
public sealed class DwgQtyDecision : Entity
{
    public string Building { get; set; } = "";
    public string Room { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Item { get; set; } = "";
    public string Unit { get; set; } = "no";
    public double? Current { get; set; }
    public double Proposed { get; set; }
    /// <summary>ACCEPTED / REJECTED.</summary>
    public string Decision { get; set; } = "";
    public long SheetId { get; set; }
    public long TakeoffId { get; set; }
    /// <summary>TAKEOFF / REVISION.</summary>
    public string Origin { get; set; } = "TAKEOFF";
    public string DecidedBy { get; set; } = "";
    public DateTime DecidedAt { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>A marked-up statement verification (one statement PDF, several pages).</summary>
public sealed class DwgStatementCheck : Entity
{
    public string Subcontractor { get; set; } = "";
    public string StatementNo { get; set; } = "";
    public int InvoiceNo { get; set; }
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Pages { get; set; } = "";
    /// <summary>Clean drawing sheet the pages were registered to (0 = none).</summary>
    public long SheetId { get; set; }
    public string Stage { get; set; } = "";
    public string Colours { get; set; } = "";
    public DateTime RunAt { get; set; }
    public int HighlightSpots { get; set; }
    public int HighlightedSymbols { get; set; }
    public int UnmatchedSpots { get; set; }
    public double HighlightedRunsM { get; set; }
    public int Flags { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>Claimed vs highlighted vs drawing total for one room x stage x item.</summary>
public sealed class DwgStatementLine : Entity
{
    public long CheckId { get; set; }
    public int Page { get; set; }
    public string Room { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Item { get; set; } = "";
    public double Claimed { get; set; }
    public double Highlighted { get; set; }
    public double DrawingTotal { get; set; }
    public double Diff { get; set; }
    /// <summary>OK / OVER CLAIM / UNDER CLAIM / NOT CLAIMED / NOT ON DRAWING.</summary>
    public string Flag { get; set; } = "";
    public double RunLengthM { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>A comparison of two revisions of a drawing.</summary>
public sealed class DwgRevisionCompare : Entity
{
    public long OldSheetId { get; set; }
    public long NewSheetId { get; set; }
    public DateTime RunAt { get; set; }
    public int AddedRegions { get; set; }
    public int RemovedRegions { get; set; }
    public int AddedSymbols { get; set; }
    public int RemovedSymbols { get; set; }
    /// <summary>New sheet -> old sheet units.</summary>
    public string Transform { get; set; } = "";
    public double AlignScore { get; set; }
    public long? VariationId { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>Count / length change per room x stage x item between two revisions.</summary>
public sealed class DwgRevisionDelta : Entity
{
    public long CompareId { get; set; }
    public string Room { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Item { get; set; } = "";
    public string Unit { get; set; } = "no";
    public double OldQty { get; set; }
    public double NewQty { get; set; }
    public double Delta { get; set; }
}

/// <summary>Every table of the module (server module, offline cache, migration, reset).</summary>
public static class DrawingEntities
{
    public static readonly Type[] All =
    {
        typeof(DwgSheet), typeof(DwgSymbol), typeof(DwgLinearClass), typeof(DwgTakeoff), typeof(DwgHit), typeof(DwgRun), typeof(DwgCalibration),
        typeof(DwgRoom), typeof(DwgQtyDecision), typeof(DwgStatementCheck), typeof(DwgStatementLine), typeof(DwgRevisionCompare), typeof(DwgRevisionDelta),
    };
}

/// <summary>One PROJECT QTY key a symbol / linear class feeds, with the quantity per hit (or per metre).</summary>
public sealed record QtyTarget(string Stage, string Item, double Factor)
{
    public static List<QtyTarget> Parse(string? s)
    {
        var res = new List<QtyTarget>();
        foreach (var part in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = part.Split('|', StringSplitOptions.TrimEntries);
            if (p.Length < 2 || p[1].Length == 0) continue;
            var f = p.Length > 2 && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1;
            res.Add(new QtyTarget(p[0].ToUpperInvariant(), p[1].ToUpperInvariant(), f));
        }
        return res;
    }

    public static string Format(IEnumerable<QtyTarget> t) =>
        string.Join(";", t.Select(x => $"{x.Stage}|{x.Item}|{x.Factor.ToString("0.###", CultureInfo.InvariantCulture)}"));
}
