using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Raffaello.Core.Integrations.CadExchange;

[assembly: CommandClass(typeof(Raffaello.CadExport.RaffExportCommand))]

namespace Raffaello.CadExport;

/// <summary>
/// RAFFEXPORT: room boundaries (closed polylines on the room layers, named by the room-number text inside them) and the
/// electrical symbols per room (intact blocks mapped by blockmap.json, plus exploded symbols recognised from loose circles /
/// lines on the electrical layers) -> one raffaello-cad-exchange JSON file for Raffaello (TRUST > CAD EXCHANGE).
/// Replaces the QSEXPORT / QSARCH LISP routines.
/// </summary>
public sealed class RaffExportCommand
{
    /// <summary>Layer wildcards (';' separated) of the room boundary polylines.</summary>
    public static string RoomLayers = "*ROOM*;A-AREA*;*BOUNDARY*";
    /// <summary>Layer wildcards of the room-number texts.</summary>
    public static string RoomTextLayers = "*ROOM*;*RM-NO*;A-AREA-IDEN*;*TAG*";
    /// <summary>Room numbers look like P2-106, L2-201, B1-05 ...</summary>
    public static Regex RoomNo = new Regex(@"^[A-Z]{0,3}\d{0,2}-?\d{2,4}[A-Z]?$", RegexOptions.IgnoreCase);
    /// <summary>Layers whose loose geometry is searched for exploded symbols.</summary>
    public static string SymbolLayers = "E-*;*ELEC*;*LIGHT*;*POWER*";

    [CommandMethod("RAFFEXPORT")]
    public void Export()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        var db = doc.Database;
        var drawing = Path.GetFileName(db.Filename);
        var level = Ask(ed, "Level id (e.g. L02)", Guess(drawing));
        var building = Ask(ed, "Building (BRANDED / HOTEL)", "BRANDED").ToUpperInvariant();
        var output = Ask(ed, "Output file", Path.Combine(Path.GetDirectoryName(db.Filename) ?? ".", Path.GetFileNameWithoutExtension(drawing) + ".raffaello.json"));
        var mapping = LoadMapping(db.Filename);

        var file = new CadExchangeFile
        {
            Building = building,
            Source = new CadSource { Application = "AutoCAD", Addin = "Raffaello.CadExport.AutoCad 1.0", Drawing = drawing, ExportedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), User = Environment.UserName, Units = "mm" },
            Levels = { new CadLevel { Id = level, Name = level, Plan = level } },
            BlockMap = mapping.Rules.Select(CadBlockRuleDto.From).ToList(),
        };

        var outlines = new List<RoomOutline>();
        var texts = new List<(P2 At, string Text)>();
        var blocks = new List<(string Name, string Layer, P2 At, double Multiplier)>();
        var loose = new List<CadPrimitive>();
        var matcher = new ExplodedSymbolMatcher();

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                switch (ent)
                {
                    case Polyline pl when pl.Closed && Any(RoomLayers, pl.Layer) && pl.NumberOfVertices >= 3:
                        var ring = Enumerable.Range(0, pl.NumberOfVertices).Select(i => { var p = pl.GetPoint2dAt(i); return new P2(p.X, p.Y); }).ToList();
                        outlines.Add(new RoomOutline { Level = level, Polygon = CadGeometry.Clean(ring) });
                        break;
                    case DBText t when Any(RoomTextLayers, t.Layer):
                        texts.Add((new P2(t.Position.X, t.Position.Y), t.TextString.Trim()));
                        break;
                    case MText m when Any(RoomTextLayers, m.Layer):
                        texts.Add((new P2(m.Location.X, m.Location.Y), m.Text.Trim()));
                        break;
                    case BlockReference br:
                    {
                        var name = EffectiveName(tr, br);
                        var rule = mapping.Match(name, br.Layer);
                        if (rule is null || rule.Ignore) break;
                        var multiplier = 1.0;
                        if (rule.QtyAttribute.Length > 0)
                            foreach (ObjectId aid in br.AttributeCollection)
                                if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar && ar.Tag.Equals(rule.QtyAttribute, StringComparison.OrdinalIgnoreCase)
                                    && double.TryParse(ar.TextString, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q)) multiplier = q;
                        blocks.Add((name, br.Layer, new P2(br.Position.X, br.Position.Y), multiplier));
                        // learn the symbol shape from its definition for the exploded-geometry search
                        var def = (BlockTableRecord)tr.GetObject(br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord, OpenMode.ForRead);
                        matcher.Learn(name, rule.System, rule.Item, rule.Qty, Primitives(tr, def));
                        break;
                    }
                    case Circle or Arc or Line when Any(SymbolLayers, ent!.Layer):
                        loose.Add(Primitive(ent));
                        break;
                }
            }
            tr.Commit();
        }

        // name the rooms: the room-number text inside each boundary
        var n = 0;
        foreach (var o in outlines)
        {
            var label = texts.Where(t => RoomNo.IsMatch(t.Text) && CadGeometry.Inside(o.Polygon, t.At)).Select(t => t.Text.ToUpperInvariant()).FirstOrDefault();
            o.Id = label ?? $"{level}-ROOM{++n:000}";
        }
        foreach (var dup in outlines.GroupBy(o => o.Id).Where(g => g.Count() > 1))
        {
            var i = 0;
            foreach (var o in dup.Skip(1)) o.Id = $"{o.Id}#{++i}";
        }
        file.Rooms = outlines.Select(o => new CadRoom { Id = o.Id, Level = level, Polygon = o.Polygon.Select(CadExchangeJson.Pt).ToList(), Area = Math.Round(o.Area / 1e6, 3) }).ToList();

        var locator = new RoomLocator(outlines);
        var counts = new Dictionary<(string Room, string System, string Item, string Stage, string Block, string Method), double>();
        void Count(P2 at, string system, string item, string stage, double qty, string block, string method)
        {
            var room = locator.Find(at);
            if (room is null)
            {
                var u = file.Unassigned.FirstOrDefault(x => x.System == system && x.Item == item);
                if (u is null) file.Unassigned.Add(u = new CadUnassigned { System = system, Item = item, Level = level, Reason = "outside every room boundary" });
                u.Qty += qty;
                if (u.Points.Count < 50) u.Points.Add(CadExchangeJson.Pt(at));
                return;
            }
            var key = (room.Id, system, item, stage, block, method);
            counts[key] = counts.TryGetValue(key, out var v) ? v + qty : qty;
        }
        foreach (var b in blocks)
        {
            var rule = mapping.Match(b.Name, b.Layer)!;
            Count(b.At, rule.System, rule.Item, rule.Stage, rule.Qty * b.Multiplier, b.Name, b.Multiplier != 1 ? "ATTRIBUTE" : "BLOCK");
        }
        var (found, unrecognised) = matcher.Match(loose);
        foreach (var f in found) Count(f.At, f.Template.System, f.Template.Item, "", f.Template.Qty, f.Template.Name, "EXPLODED");
        file.Counts = counts.Select(kv => new CadCount { Room = kv.Key.Room, System = kv.Key.System, Item = kv.Key.Item, Stage = kv.Key.Stage, Block = kv.Key.Block, Method = kv.Key.Method, Qty = kv.Value }).ToList();

        CadExchangeJson.Write(output, file);
        ed.WriteMessage($"\nRaffaello: {file.Rooms.Count} rooms, {blocks.Count} blocks, {found.Count} exploded symbols recognised ({unrecognised} clusters not), " +
                        $"{file.Unassigned.Sum(u => u.Qty)} points outside rooms -> {output}\n");
    }

    private static string Ask(Editor ed, string message, string def)
    {
        var r = ed.GetString(new PromptStringOptions($"\n{message} <{def}>: ") { AllowSpaces = true, DefaultValue = def, UseDefaultValue = true });
        return r.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(r.StringResult) ? r.StringResult.Trim() : def;
    }

    private static string Guess(string drawing)
    {
        var m = Regex.Match(drawing ?? "", @"(L|LEVEL|B|BS|RF)[-_ ]?(\d{1,2})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant()[0] + m.Groups[2].Value.PadLeft(2, '0') : "L00";
    }

    private static bool Any(string patterns, string layer) => patterns.Split(';').Any(p => BlockMapping.Wildcard(p.Trim(), layer ?? ""));

    private static string EffectiveName(Transaction tr, BlockReference br)
    {
        if (!br.IsDynamicBlock) return br.Name;
        return ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name;
    }

    private static BlockMapping LoadMapping(string drawingPath)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(Path.GetDirectoryName(drawingPath) ?? ".", "blockmap.json"),
            Path.Combine(Path.GetDirectoryName(typeof(RaffExportCommand).Assembly.Location) ?? ".", "blockmap.json"),
        })
            if (File.Exists(candidate)) return CadExchangeJson.ReadBlockMap(candidate);
        return BlockMapping.Default();
    }

    private static IEnumerable<CadPrimitive> Primitives(Transaction tr, BlockTableRecord def)
    {
        foreach (ObjectId id in def)
            if (tr.GetObject(id, OpenMode.ForRead) is Entity e && e is Circle or Arc or Line) yield return Primitive(e);
    }

    private static CadPrimitive Primitive(Entity e)
    {
        var ext = e.GeometricExtents;
        var p = new CadPrimitive { Layer = e.Layer, MinX = ext.MinPoint.X, MinY = ext.MinPoint.Y, MaxX = ext.MaxPoint.X, MaxY = ext.MaxPoint.Y };
        switch (e)
        {
            case Circle c: p.Kind = "CIRCLE"; p.Size = c.Radius; break;
            case Arc a: p.Kind = "ARC"; p.Size = a.Radius; break;
            case Line l: p.Kind = "LINE"; p.Size = l.Length; break;
        }
        return p;
    }
}
