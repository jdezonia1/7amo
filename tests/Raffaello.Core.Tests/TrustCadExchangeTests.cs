using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Integrations.CadExchange;

namespace Raffaello.Core.Tests;

/// <summary>[trust] CAD exchange: schema validation, geometry heuristics, import into rooms / shapes / PROJECT QTY.</summary>
public sealed class TrustCadExchangeTests
{
    private static List<double[]> Rect(double x0, double y0, double x1, double y1) => new() { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 }, new[] { x0, y0 } };

    internal static CadExchangeFile Sample() => new()
    {
        Source = new CadSource { Application = "AutoCAD 2025", Addin = "test", Drawing = "L02-ELEC.dwg", Units = "mm" },
        Building = Buildings.Hotel,
        Levels = new() { new CadLevel { Id = "L02", Name = "LEVEL 02", Plan = "L02" } },
        Rooms = new()
        {
            new CadRoom { Id = "L2-201", Level = "L02", Type = "KING", Polygon = Rect(0, 0, 6000, 5000), Area = 30 },
            new CadRoom { Id = "L2-202", Level = "L02", Type = "TWIN", Polygon = Rect(6000, 0, 12000, 5000), Area = 30 },
        },
        Counts = new()
        {
            new CadCount { Room = "L2-201", System = "LIGHT", Item = "DOWNLIGHT", Qty = 12 },
            new CadCount { Room = "L2-201", System = "LIGHT", Item = "SWITCH", Qty = 4 },
            new CadCount { Room = "L2-201", System = "POWER", Item = "TWIN SOCKET", Qty = 6 },
            new CadCount { Room = "L2-202", System = "POWER", Item = "TWIN SOCKET", Qty = 5, Method = "EXPLODED" },
            new CadCount { Room = "L2-202", System = "GRMS", Item = "THERMOSTAT (CP-4)", Qty = 1, Stage = "2ND FIX" },
        },
        Unassigned = new() { new CadUnassigned { System = "LIGHT", Item = "DOWNLIGHT", Qty = 2, Level = "L02", Reason = "outside every room" } },
    };

    [Fact]
    public void Geometry_point_in_polygon_with_holes_and_nested_rooms()
    {
        var suite = new RoomOutline { Id = "SUITE", Polygon = CadExchangeJson.Ring(Rect(0, 0, 10, 10)) };
        var bath = new RoomOutline { Id = "BATH", Polygon = CadExchangeJson.Ring(Rect(7, 7, 10, 10)) };
        var shaft = new RoomOutline { Id = "LOBBY", Polygon = CadExchangeJson.Ring(Rect(20, 0, 30, 10)), Holes = { CadExchangeJson.Ring(Rect(24, 4, 26, 6)) } };
        var loc = new RoomLocator(new[] { suite, bath, shaft });
        Assert.Equal("BATH", loc.Find(new P2(8, 8))!.Id);       // smallest containing room
        Assert.Equal("SUITE", loc.Find(new P2(2, 2))!.Id);
        Assert.Equal("SUITE", loc.Find(new P2(0, 5))!.Id);      // on the boundary
        Assert.Null(loc.Find(new P2(25, 5)));                   // inside the hole (shaft)
        Assert.Equal("LOBBY", loc.Find(new P2(21, 5))!.Id);
        Assert.Null(loc.Find(new P2(15, 5)));
        Assert.Equal(100, CadGeometry.Area(suite.Polygon), 6);
        Assert.Equal(5, CadGeometry.Centroid(suite.Polygon).X, 6);
        Assert.Equal(4, suite.Polygon.Count);                   // closing point removed
    }

    [Fact]
    public void Block_rules_use_wildcards_and_layer_fallback()
    {
        var m = BlockMapping.Default();
        Assert.Equal("POWER", m.Match("E-TWIN-SOCKET-13A", "E-POWER")!.System);
        var twinData = m.Match("TWIN_DATA_OUTLET", "E-DATA")!;
        Assert.Equal("DATA", twinData.System);
        Assert.Equal(2, twinData.Qty);   // twin data = 2 points
        Assert.Equal("LIGHT", m.Match("XYZ-123", "E-LIGHTING")!.System);
        Assert.Null(m.Match("TITLE-BLOCK", "0"));
        var path = Path.Combine(TestData.TempDir(), "blockmap.json");
        CadExchangeJson.WriteBlockMap(path, m);
        Assert.Equal(m.Rules.Count, CadExchangeJson.ReadBlockMap(path).Rules.Count);
    }

    [Fact]
    public void Exploded_symbols_are_recognised_from_learned_blocks()
    {
        static CadPrimitive Circle(double x, double y, double r) => new() { Kind = "CIRCLE", MinX = x - r, MinY = y - r, MaxX = x + r, MaxY = y + r, Size = r };
        static CadPrimitive Line(double x0, double y0, double x1, double y1) => new() { Kind = "LINE", MinX = Math.Min(x0, x1), MinY = Math.Min(y0, y1), MaxX = Math.Max(x0, x1), MaxY = Math.Max(y0, y1), Size = 1 };
        var matcher = new ExplodedSymbolMatcher { ClusterGap = 20 };
        // socket symbol: circle r=100 + two lines; downlight: circle r=150 + cross (two lines)
        matcher.Learn("E-SOCKET", "POWER", "SOCKET", 1, new[] { Circle(0, 0, 100), Line(-100, 0, 100, 0), Line(0, 100, 0, 250) });
        matcher.Learn("E-DOWNLIGHT", "LIGHT", "DOWNLIGHT", 1, new[] { Circle(0, 0, 150), Line(-150, 0, 150, 0), Line(0, -150, 0, 150) });
        var loose = new List<CadPrimitive>();
        foreach (var x in new[] { 1000.0, 3000.0 }) loose.AddRange(new[] { Circle(x, 0, 100), Line(x - 100, 0, x + 100, 0), Line(x, 100, x, 250) });
        // a rotated socket (width / height swapped)
        loose.AddRange(new[] { Circle(5000, 0, 100), Line(5000, -100, 5000, 100), Line(5100, 0, 5250, 0) });
        loose.AddRange(new[] { Circle(8000, 0, 150), Line(7850, 0, 8150, 0), Line(8000, -150, 8000, 150) });
        loose.Add(Line(0, 5000, 20000, 5000));   // a wall line - not a symbol
        var (found, left) = matcher.Match(loose);
        Assert.Equal(3, found.Count(f => f.Template.Name == "E-SOCKET"));
        Assert.Single(found, f => f.Template.Name == "E-DOWNLIGHT");
        Assert.Equal(1, left);
        Assert.Equal(8000, found.Single(f => f.Template.Name == "E-DOWNLIGHT").At.X, 3);
    }

    [Fact]
    public void Valid_file_imports_rooms_shapes_and_project_qty()
    {
        var db = TestData.NewDb();
        db.Insert(new Room { Building = Buildings.Hotel, Code = "L2-201", Level = "L02" });
        db.Insert(new RoomQty { Building = Buildings.Hotel, Room = "L2-201", Stage = "1ST FIX", Item = "LIGHT", Qty = 10, Source = "TRACKER" });
        var path = Path.Combine(TestData.TempDir(), "l02.json");
        CadExchangeJson.Write(path, Sample());
        var json = File.ReadAllText(path);
        Assert.Contains("\"format\": \"raffaello-cad-exchange\"", json);   // camelCase on disk

        var preview = CadExchangeImporter.Preview(path, ProjectSnapshot.Load(db), new CadImportOptions());
        Assert.True(preview.CanCommit, string.Join("; ", preview.Errors));
        Assert.Single(preview.NewRooms);
        Assert.Equal("L2-202", preview.NewRooms[0].Code);
        Assert.Equal(2, preview.Shapes.Count);
        var light1 = preview.QtyChanges.Single(q => q.Room == "L2-201" && q.Stage == "1ST FIX" && q.Item == "LIGHT");
        Assert.Equal("UPDATE", light1.Action);
        Assert.Equal(10, light1.OldQty);
        Assert.Equal(16, light1.NewQty);   // 12 downlights + 4 switches
        Assert.Single(preview.QtyChanges, q => q.Item == "GRMS");   // stage given on the count: only 2ND FIX
        Assert.Contains(preview.Warnings, w => w.Contains("outside every room"));
        Assert.Contains("EXPLODED", preview.QtyChanges.Single(q => q.Room == "L2-202" && q.Stage == "1ST FIX").Detail);

        var msg = CadExchangeImporter.Commit(preview, db);
        Assert.Contains("1 rooms added", msg);
        var s = ProjectSnapshot.Load(db);
        Assert.Equal(2, s.Rooms.Count);
        Assert.Equal(16, s.RoomQtys.Single(q => q.Room == "L2-201" && q.Stage == "1ST FIX" && q.Item == "LIGHT").Qty);
        Assert.StartsWith("CAD:", s.RoomQtys.First(q => q.Room == "L2-202").Source);
        var shape = s.RoomShapes.Single(r => r.Room == "L2-201");
        Assert.Equal("RM_L2-201-L02", shape.ShapeName);
        Assert.InRange(shape.Left, 0, 0.05);
        Assert.InRange(shape.Bottom, 0.95, 1);   // y flipped into image coordinates
        // importing the same file again changes nothing (shapes replaced, quantities SAME)
        var again = CadExchangeImporter.Preview(path, s, new CadImportOptions());
        Assert.All(again.QtyChanges, q => Assert.Equal("SAME", q.Action));
        CadExchangeImporter.Commit(again, db);
        Assert.Equal(2, db.All<RoomShape>().Count);
    }

    [Fact]
    public void Add_missing_mode_keeps_existing_project_qty()
    {
        var db = TestData.NewDb();
        db.Insert(new RoomQty { Building = Buildings.Hotel, Room = "L2-201", Stage = "1ST FIX", Item = "LIGHT", Qty = 10 });
        var p = CadExchangeImporter.Preview(Sample(), "x.json", ProjectSnapshot.Load(db), new CadImportOptions { Mode = CadImportModes.AddMissing });
        Assert.Equal("KEEP", p.QtyChanges.Single(q => q.Room == "L2-201" && q.Stage == "1ST FIX" && q.Item == "LIGHT").Action);
        CadExchangeImporter.Commit(p, db);
        Assert.Equal(10, db.All<RoomQty>().Single(q => q.Room == "L2-201" && q.Stage == "1ST FIX" && q.Item == "LIGHT").Qty);
    }

    [Fact]
    public void Invalid_files_are_explained()
    {
        var bad = Sample();
        bad.Format = "something";
        bad.Rooms.Add(new CadRoom { Id = "L2-201", Level = "L02", Polygon = Rect(0, 0, 1, 1) });            // duplicate
        bad.Rooms.Add(new CadRoom { Id = "L2-203", Level = "", Polygon = new() { new[] { 0.0, 0 }, new[] { 1.0, 1 } } });
        bad.Rooms.Add(new CadRoom { Id = "L2-204", Level = "L02", Polygon = Rect(0, 0, 1000, 1000), Area = 25 });   // 1 m2 vs 25 reported
        bad.Counts.Add(new CadCount { Room = "L2-201", System = "", Item = "X", Qty = -1 });
        var p = CadExchangeImporter.Preview(bad, "bad.json", new ProjectSnapshot(), new CadImportOptions());
        Assert.False(p.CanCommit);
        Assert.Contains(p.Errors, e => e.Contains("format"));
        Assert.Contains(p.Errors, e => e.Contains("appears twice"));
        Assert.Contains(p.Errors, e => e.Contains("no level"));
        Assert.Contains(p.Errors, e => e.Contains("at least 3"));
        Assert.Contains(p.Errors, e => e.Contains("no system"));
        Assert.Contains(p.Errors, e => e.Contains("invalid qty"));
        Assert.Contains(p.Warnings, w => w.Contains("L2-204") && w.Contains("differs"));
        Assert.Throws<InvalidOperationException>(() => CadExchangeImporter.Commit(p, TestData.NewDb()));

        var path = Path.Combine(TestData.TempDir(), "broken.json");
        File.WriteAllText(path, "{ not json");
        Assert.Contains("JSON", CadExchangeImporter.Preview(path, new ProjectSnapshot(), new CadImportOptions()).Errors.Single());
    }

    [Fact]
    public void The_schema_is_embedded_and_matches_the_model()
    {
        var schema = CadExchangeImporter.JsonSchema();
        Assert.Contains("raffaello-cad-exchange", schema);
        using var doc = System.Text.Json.JsonDocument.Parse(schema);
        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "format", "version", "rooms", "counts" }, required);
        var roomReq = doc.RootElement.GetProperty("properties").GetProperty("rooms").GetProperty("items").GetProperty("required").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(new[] { "id", "level", "polygon" }, roomReq);
        // every property of the model is described in the schema
        var json = CadExchangeJson.Serialize(Sample());
        using var sample = System.Text.Json.JsonDocument.Parse(json);
        var described = doc.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.All(sample.RootElement.EnumerateObject(), p => Assert.Contains(p.Name, described));
    }
}
