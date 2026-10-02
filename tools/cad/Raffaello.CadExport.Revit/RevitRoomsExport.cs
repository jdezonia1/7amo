using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Raffaello.Core.Integrations.CadExchange;

namespace Raffaello.CadExport;

/// <summary>
/// Revit equivalent of RAFFEXPORT: every placed room (number, level, boundary from GetBoundarySegments) and the electrical
/// family instances per room (room from FamilyInstance.Room, else point-in-room) -> raffaello-cad-exchange JSON next to the
/// model. Families are mapped to system / item by blockmap.json on the family name (same rules as AutoCAD blocks). Read-only.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class RevitRoomsExport : IExternalCommand
{
    private const double FeetToMm = 304.8;

    private static readonly BuiltInCategory[] DeviceCategories =
    {
        BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_DataDevices,
        BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_SecurityDevices, BuiltInCategory.OST_NurseCallDevices,
        BuiltInCategory.OST_TelephoneDevices,
    };

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument.Document;
        var folder = string.IsNullOrEmpty(doc.PathName) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(doc.PathName)!;
        var mapPath = Path.Combine(folder, "blockmap.json");
        if (!File.Exists(mapPath)) mapPath = Path.Combine(Path.GetDirectoryName(typeof(RevitRoomsExport).Assembly.Location) ?? ".", "blockmap.json");
        var mapping = File.Exists(mapPath) ? CadExchangeJson.ReadBlockMap(mapPath) : BlockMapping.Default();

        var file = new CadExchangeFile
        {
            Source = new CadSource { Application = "Revit", Addin = "Raffaello.CadExport.Revit 1.0", Drawing = doc.Title, ExportedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), User = Environment.UserName, Units = "mm" },
            BlockMap = mapping.Rules.Select(CadBlockRuleDto.From).ToList(),
        };
        var rooms = new List<Room>();
        foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
        {
            if (e is not Room r || r.Area <= 0) continue;   // unplaced / not enclosed rooms have no area
            var loops = r.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (loops.Count == 0) continue;
            List<double[]> Ring(IList<BoundarySegment> loop) =>
                loop.SelectMany(s => s.GetCurve().Tessellate()).Select(p => new[] { Math.Round(p.X * FeetToMm, 1), Math.Round(p.Y * FeetToMm, 1) }).ToList();
            var level = r.Level?.Name ?? "";
            if (!file.Levels.Any(l => l.Id == level)) file.Levels.Add(new CadLevel { Id = level, Name = level, Plan = level, Elevation = r.Level is null ? null : Math.Round(r.Level.Elevation * FeetToMm) });
            file.Rooms.Add(new CadRoom
            {
                Id = r.Number.Trim(), Name = r.Name, Level = level, Polygon = Ring(loops[0]), Holes = loops.Skip(1).Select(Ring).ToList(),
                Area = Math.Round(r.Area * 0.09290304, 3),
            });
            rooms.Add(r);
        }

        var counts = new Dictionary<(string Room, string System, string Item, string Stage, string Block), double>();
        foreach (var cat in DeviceCategories)
            foreach (var e in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType())
            {
                if (e is not FamilyInstance fi) continue;
                var name = fi.Symbol.FamilyName + " " + fi.Name;
                var rule = mapping.Match(name, fi.Category?.Name ?? "");
                if (rule is null || rule.Ignore) continue;
                var room = fi.Room;
                if (room is null && fi.Location is LocationPoint lp) room = rooms.FirstOrDefault(r => r.IsPointInRoom(lp.Point));
                if (room is null)
                {
                    var u = file.Unassigned.FirstOrDefault(x => x.System == rule.System && x.Item == rule.Item);
                    if (u is null) file.Unassigned.Add(u = new CadUnassigned { System = rule.System, Item = rule.Item, Reason = "not inside a placed room" });
                    u.Qty += rule.Qty;
                    continue;
                }
                var key = (room.Number.Trim(), rule.System, rule.Item, rule.Stage, fi.Symbol.FamilyName);
                counts[key] = counts.TryGetValue(key, out var v) ? v + rule.Qty : rule.Qty;
            }
        file.Counts = counts.Select(kv => new CadCount { Room = kv.Key.Room, System = kv.Key.System, Item = kv.Key.Item, Stage = kv.Key.Stage, Block = kv.Key.Block, Method = "FAMILY", Qty = kv.Value }).ToList();

        var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName) + ".raffaello.json");
        CadExchangeJson.Write(output, file);
        TaskDialog.Show("Raffaello", $"{file.Rooms.Count} rooms, {file.Counts.Sum(c => c.Qty)} points in rooms, {file.Unassigned.Sum(u => u.Qty)} outside rooms.\n\n{output}");
        return Result.Succeeded;
    }
}
