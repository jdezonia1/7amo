// [trust] CAD exchange file model (raffaello-cad-exchange v1). Linked into the AutoCAD / Revit add-ins - keep it plain C#.
// Schema: docs/cad-exchange.schema.json, description: docs/CAD_EXCHANGE.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raffaello.Core.Integrations.CadExchange
{
    /// <summary>
    /// What the AutoCAD add-in / Revit add-in / Dynamo script writes and Raffaello imports: room boundary polygons per level and
    /// the number of points per room x system x item. Coordinates in drawing units (see <see cref="CadSource.Units"/>),
    /// Y up (CAD convention). JSON with camelCase names.
    /// </summary>
    public sealed class CadExchangeFile
    {
        public const string FormatName = "raffaello-cad-exchange";
        public const int CurrentVersion = 1;

        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = CurrentVersion;
        public CadSource Source { get; set; } = new CadSource();
        /// <summary>BRANDED / HOTEL (optional - the import screen asks when empty).</summary>
        public string Building { get; set; } = "";
        public List<CadLevel> Levels { get; set; } = new List<CadLevel>();
        public List<CadRoom> Rooms { get; set; } = new List<CadRoom>();
        public List<CadCount> Counts { get; set; } = new List<CadCount>();
        /// <summary>Symbols found outside every room (to be fixed in the drawing or assigned by hand).</summary>
        public List<CadUnassigned> Unassigned { get; set; } = new List<CadUnassigned>();
        /// <summary>The block rules that were used (traceability).</summary>
        public List<CadBlockRuleDto> BlockMap { get; set; } = new List<CadBlockRuleDto>();
    }

    public sealed class CadSource
    {
        /// <summary>"AutoCAD 2025", "Revit 2024", "Dynamo 2.x".</summary>
        public string Application { get; set; } = "";
        public string Addin { get; set; } = "";
        public string Drawing { get; set; } = "";
        /// <summary>ISO 8601 UTC.</summary>
        public string ExportedAt { get; set; } = "";
        public string User { get; set; } = "";
        /// <summary>mm (default) / m / ft.</summary>
        public string Units { get; set; } = "mm";
    }

    public sealed class CadLevel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public double? Elevation { get; set; }
        /// <summary>Plan sheet code used by the plan view (L00, L01 ...); default = Id.</summary>
        public string Plan { get; set; } = "";
        /// <summary>Drawing extents the plan image covers (to place room shapes on the PDF plan); default = extents of the rooms.</summary>
        public CadFrame? Frame { get; set; }
    }

    public sealed class CadFrame
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }

    public sealed class CadRoom
    {
        /// <summary>The room code used in the ledger (P2-106, L2-201 ...). Required.</summary>
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Level id (matches <see cref="CadLevel.Id"/>). Required.</summary>
        public string Level { get; set; } = "";
        /// <summary>Unit / room type (1BR-A, KING ...).</summary>
        public string Type { get; set; } = "";
        public string AreaType { get; set; } = "";
        /// <summary>Outer boundary [[x,y], ...] - at least 3 points, closing point optional. Required.</summary>
        public List<double[]> Polygon { get; set; } = new List<double[]>();
        public List<List<double[]>> Holes { get; set; } = new List<List<double[]>>();
        /// <summary>Area in square metres as reported by the CAD tool (checked against the polygon).</summary>
        public double? Area { get; set; }
    }

    public sealed class CadCount
    {
        /// <summary>Room id. Required.</summary>
        public string Room { get; set; } = "";
        /// <summary>Ledger system / item (POWER, LIGHT, DATA, GRMS ...). Required.</summary>
        public string System { get; set; } = "";
        /// <summary>Symbol / device (TWIN SOCKET, DOWNLIGHT ...). Required.</summary>
        public string Item { get; set; } = "";
        /// <summary>Points (after the per-block multiplier). Required.</summary>
        public double Qty { get; set; }
        /// <summary>Optional stage; empty = applies to every stage chosen at import.</summary>
        public string Stage { get; set; } = "";
        public string Block { get; set; } = "";
        public string Layer { get; set; } = "";
        /// <summary>BLOCK (intact block reference), ATTRIBUTE (multiplied by an attribute), EXPLODED (recognised from loose geometry), FAMILY (Revit).</summary>
        public string Method { get; set; } = "BLOCK";
    }

    public sealed class CadUnassigned
    {
        public string System { get; set; } = "";
        public string Item { get; set; } = "";
        public double Qty { get; set; }
        public string Level { get; set; } = "";
        public string Reason { get; set; } = "";
        public List<double[]> Points { get; set; } = new List<double[]>();
    }

    public sealed class CadBlockRuleDto
    {
        public string Block { get; set; } = "";
        public string Layer { get; set; } = "";
        public string System { get; set; } = "";
        public string Item { get; set; } = "";
        public double Qty { get; set; } = 1;
        public string Stage { get; set; } = "";
        public string QtyAttribute { get; set; } = "";
        public bool Ignore { get; set; }

        public BlockRule ToRule() => new BlockRule { Block = Block, Layer = Layer, System = System, Item = Item, Qty = Qty, Stage = Stage, QtyAttribute = QtyAttribute, Ignore = Ignore };
        public static CadBlockRuleDto From(BlockRule r) => new CadBlockRuleDto { Block = r.Block, Layer = r.Layer, System = r.System, Item = r.Item, Qty = r.Qty, Stage = r.Stage, QtyAttribute = r.QtyAttribute, Ignore = r.Ignore };
    }

    /// <summary>Read / write the exchange file and the block map (camelCase JSON, UTF-8).</summary>
    public static class CadExchangeJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Serialize(CadExchangeFile f) => JsonSerializer.Serialize(f, Options);
        public static CadExchangeFile Deserialize(string json) => JsonSerializer.Deserialize<CadExchangeFile>(json, Options) ?? new CadExchangeFile();
        public static void Write(string path, CadExchangeFile f) => File.WriteAllText(path, Serialize(f), new UTF8Encoding(false));
        public static CadExchangeFile Read(string path) => Deserialize(File.ReadAllText(path));

        public static BlockMapping ReadBlockMap(string path)
        {
            var rules = JsonSerializer.Deserialize<List<CadBlockRuleDto>>(File.ReadAllText(path), Options) ?? new List<CadBlockRuleDto>();
            return new BlockMapping { Rules = rules.Select(r => r.ToRule()).ToList() };
        }

        public static void WriteBlockMap(string path, BlockMapping m) =>
            File.WriteAllText(path, JsonSerializer.Serialize(m.Rules.Select(CadBlockRuleDto.From).ToList(), Options), new UTF8Encoding(false));

        public static List<P2> Ring(IEnumerable<double[]>? pts) =>
            CadGeometry.Clean((pts ?? Enumerable.Empty<double[]>()).Where(p => p != null && p.Length >= 2).Select(p => new P2(p[0], p[1])));

        public static double[] Pt(P2 p) => new[] { Math.Round(p.X, 3), Math.Round(p.Y, 3) };
    }
}
