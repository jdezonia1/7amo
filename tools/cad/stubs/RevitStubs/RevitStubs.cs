// COMPILE-ONLY STUBS of the Revit API surface used by Raffaello.CadExport.Revit (RevitAPI.dll / RevitAPIUI.dll).
// They let the add-in build without Revit installed; they do nothing at run time.
#pragma warning disable CS1591
using System;
using System.Collections;
using System.Collections.Generic;

namespace Autodesk.Revit.Attributes
{
    public enum TransactionMode { Manual, ReadOnly }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TransactionAttribute : Attribute { public TransactionAttribute(TransactionMode mode) { } }
}

namespace Autodesk.Revit.DB
{
    public sealed class XYZ { public double X { get; } public double Y { get; } public double Z { get; } public XYZ(double x, double y, double z) { X = x; Y = y; Z = z; } }
    public abstract class Curve { public IList<XYZ> Tessellate() => new List<XYZ>(); }
    public sealed class BoundarySegment { public Curve GetCurve() => null!; }
    public sealed class SpatialElementBoundaryOptions { }
    public enum BuiltInCategory { OST_Rooms, OST_ElectricalFixtures, OST_LightingFixtures, OST_LightingDevices, OST_DataDevices, OST_CommunicationDevices, OST_FireAlarmDevices, OST_SecurityDevices, OST_ElectricalEquipment, OST_NurseCallDevices, OST_TelephoneDevices }
    public sealed class Category { public string Name { get; set; } = ""; }
    public class Element { public string Name { get; set; } = ""; public Category? Category { get; set; } public Location? Location { get; set; } }
    public class Location { }
    public sealed class LocationPoint : Location { public XYZ Point { get; set; } = new XYZ(0, 0, 0); }
    public sealed class Level : Element { public double Elevation { get; set; } }
    public class SpatialElement : Element { public IList<IList<BoundarySegment>> GetBoundarySegments(SpatialElementBoundaryOptions o) => new List<IList<BoundarySegment>>(); }
    public sealed class FamilySymbol : Element { public string FamilyName { get; set; } = ""; }
    public sealed class FamilyInstance : Element { public FamilySymbol Symbol { get; set; } = new FamilySymbol(); public Architecture.Room? Room { get; set; } }
    public sealed class Document { public string Title { get; set; } = ""; public string PathName { get; set; } = ""; }
    public sealed class FilteredElementCollector : IEnumerable<Element>
    {
        public FilteredElementCollector(Document doc) { }
        public FilteredElementCollector OfCategory(BuiltInCategory c) => this;
        public FilteredElementCollector WhereElementIsNotElementType() => this;
        public IEnumerator<Element> GetEnumerator() { yield break; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public sealed class ElementSet { }
}

namespace Autodesk.Revit.DB.Architecture
{
    public sealed class Room : Autodesk.Revit.DB.SpatialElement
    {
        public string Number { get; set; } = "";
        public Autodesk.Revit.DB.Level? Level { get; set; }
        public double Area { get; set; }
        public bool IsPointInRoom(Autodesk.Revit.DB.XYZ p) => false;
    }
}

namespace Autodesk.Revit.UI
{
    public enum Result { Succeeded, Failed, Cancelled }
    public sealed class UIDocument { public Autodesk.Revit.DB.Document Document { get; } = new Autodesk.Revit.DB.Document(); }
    public sealed class UIApplication { public UIDocument ActiveUIDocument { get; } = new UIDocument(); }
    public sealed class ExternalCommandData { public UIApplication Application { get; } = new UIApplication(); }
    public interface IExternalCommand { Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements); }
    public static class TaskDialog { public static void Show(string title, string text) { } }
}
