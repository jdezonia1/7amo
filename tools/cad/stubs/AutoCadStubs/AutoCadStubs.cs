// COMPILE-ONLY STUBS of the AutoCAD .NET API surface used by Raffaello.CadExport.AutoCad.
// They let the add-in build on a machine without the AutoCAD SDK (CI / Linux). They do nothing at run time.
// The real build references AcCoreMgd.dll, AcDbMgd.dll and AcMgd.dll from the AutoCAD install (see tools/cad/README.md).
#pragma warning disable CS1591
using System;
using System.Collections;
using System.Collections.Generic;

namespace Autodesk.AutoCAD.Runtime
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class CommandMethodAttribute : Attribute { public CommandMethodAttribute(string globalName) { } }
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class CommandClassAttribute : Attribute { public CommandClassAttribute(Type t) { } }
}

namespace Autodesk.AutoCAD.Geometry
{
    public struct Point3d { public double X { get; set; } public double Y { get; set; } public double Z { get; set; } public Point3d(double x, double y, double z) { X = x; Y = y; Z = z; } }
    public struct Point2d { public double X { get; set; } public double Y { get; set; } public Point2d(double x, double y) { X = x; Y = y; } }
    public struct Extents3d { public Point3d MinPoint { get; set; } public Point3d MaxPoint { get; set; } }
}

namespace Autodesk.AutoCAD.EditorInput
{
    public enum PromptStatus { OK, Cancel, None, Error }
    public class PromptResult { public PromptStatus Status { get; set; } public string StringResult { get; set; } = ""; }
    public sealed class PromptStringOptions
    {
        public PromptStringOptions(string message) { }
        public bool AllowSpaces { get; set; }
        public string DefaultValue { get; set; } = "";
        public bool UseDefaultValue { get; set; }
    }
    public sealed class Editor
    {
        public void WriteMessage(string message, params object[] parameter) { }
        public PromptResult GetString(PromptStringOptions options) => new PromptResult();
    }
}

namespace Autodesk.AutoCAD.DatabaseServices
{
    using Autodesk.AutoCAD.Geometry;
    public enum OpenMode { ForRead, ForWrite, ForNotify }
    public struct ObjectId { public bool IsNull => true; }
    public class DBObject : IDisposable { public void Dispose() { } }
    public class Entity : DBObject { public string Layer { get; set; } = ""; public Extents3d GeometricExtents { get; set; } }
    public class Curve : Entity { }
    public sealed class Polyline : Curve
    {
        public bool Closed { get; set; }
        public int NumberOfVertices { get; set; }
        public Point2d GetPoint2dAt(int index) => new Point2d();
    }
    public sealed class Circle : Curve { public Point3d Center { get; set; } public double Radius { get; set; } }
    public sealed class Arc : Curve { public Point3d Center { get; set; } public double Radius { get; set; } }
    public sealed class Line : Curve { public Point3d StartPoint { get; set; } public Point3d EndPoint { get; set; } public double Length { get; set; } }
    public sealed class DBText : Entity { public string TextString { get; set; } = ""; public Point3d Position { get; set; } }
    public sealed class MText : Entity { public string Text { get; set; } = ""; public Point3d Location { get; set; } }
    public sealed class AttributeReference : Entity { public string Tag { get; set; } = ""; public string TextString { get; set; } = ""; }
    public sealed class AttributeCollection : IEnumerable { public IEnumerator GetEnumerator() { yield break; } }
    public sealed class BlockReference : Entity
    {
        public string Name { get; set; } = "";
        public Point3d Position { get; set; }
        public ObjectId BlockTableRecord { get; set; }
        public ObjectId DynamicBlockTableRecord { get; set; }
        public bool IsDynamicBlock { get; set; }
        public AttributeCollection AttributeCollection { get; } = new AttributeCollection();
    }
    public class SymbolTableRecord : DBObject { public string Name { get; set; } = ""; }
    public sealed class BlockTableRecord : SymbolTableRecord, IEnumerable
    {
        public const string ModelSpace = "*Model_Space";
        public bool IsLayout { get; set; }
        public bool IsFromExternalReference { get; set; }
        public IEnumerator GetEnumerator() { yield break; }
    }
    public sealed class BlockTable : DBObject, IEnumerable
    {
        public ObjectId this[string key] => new ObjectId();
        public IEnumerator GetEnumerator() { yield break; }
    }
    public sealed class Transaction : IDisposable
    {
        public DBObject GetObject(ObjectId id, OpenMode mode) => new DBObject();
        public void Commit() { }
        public void Dispose() { }
    }
    public sealed class TransactionManager { public Transaction StartTransaction() => new Transaction(); }
    public sealed class Database
    {
        public TransactionManager TransactionManager { get; } = new TransactionManager();
        public ObjectId BlockTableId { get; set; }
        public string Filename { get; set; } = "";
    }
}

namespace Autodesk.AutoCAD.ApplicationServices
{
    using Autodesk.AutoCAD.DatabaseServices;
    using Autodesk.AutoCAD.EditorInput;
    public sealed class Document { public Editor Editor { get; } = new Editor(); public Database Database { get; } = new Database(); public string Name { get; set; } = ""; }
    public sealed class DocumentCollection { public Document MdiActiveDocument { get; set; } = new Document(); }
    public static class Application { public static DocumentCollection DocumentManager { get; } = new DocumentCollection(); }
}
