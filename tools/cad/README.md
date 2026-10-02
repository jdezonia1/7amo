# CAD exchange add-ins (AutoCAD, Revit, Dynamo)

They export room boundaries and electrical point counts per room into one **raffaello-cad-exchange v1** JSON file
(schema: `src/Raffaello.Core/Integrations/CadExchange/cad-exchange.schema.json`, description: `docs/CAD_EXCHANGE.md`),
which Raffaello imports in TRUST & INTEGRATIONS > CAD EXCHANGE (or `raffaello-cli cad-import FILE --db DB [--commit]`).
They replace the QSEXPORT / QSARCH LISP routines and EXPORT_ROOM_BOUNDARIES.dyn.

| Folder | What | Target |
|---|---|---|
| `Raffaello.CadExport.AutoCad` | command `RAFFEXPORT` | AutoCAD 2025+ (.NET 8). AutoCAD 2024 and older: `-p:TargetFramework=net48` |
| `Raffaello.CadExport.Revit` | External Command "Raffaello export" | Revit 2025+ (.NET 8) |
| `dynamo/raffaello_rooms_export.py` | Python node | Revit 2024 (work PC) with Dynamo 2.x |
| `blockmap.json` | block / family name -> system, item, points per block | edit for the real block names |
| `stubs/` | compile-only fakes of the Autodesk APIs | CI / Linux only, never deployed |

The add-ins are **not in Raffaello.sln** (they need the Autodesk SDK DLLs). Geometry, block rules, exploded-symbol
heuristics and the file model are linked from Raffaello.Core (`Integrations/CadExchange/CadGeometry.cs`,
`CadExchangeModel.cs`), so the CAD side and the import side use the same code (unit tested in
`tests/Raffaello.Core.Tests/TrustCadExchangeTests.cs`).

## Build

```
# real build on the work PC (Windows, AutoCAD / Revit installed)
dotnet build tools/cad/Raffaello.CadExport.AutoCad -c Release -p:AutoCADDir="C:\Program Files\Autodesk\AutoCAD 2025"
dotnet build tools/cad/Raffaello.CadExport.Revit   -c Release -p:RevitDir="C:\Program Files\Autodesk\Revit 2025"

# compile check anywhere (verified in the Linux build): stub references
dotnet build tools/cad/Raffaello.CadExport.AutoCad -c Release -p:UseStubs=true
dotnet build tools/cad/Raffaello.CadExport.Revit   -c Release -p:UseStubs=true
```

AutoCAD 2024 / .NET Framework 4.8: add `-p:TargetFramework=net48` (System.Text.Json comes from NuGet); the shared files avoid
APIs newer than .NET Framework 4.8.

## Use

**AutoCAD**: `NETLOAD` the built `Raffaello.CadExport.AutoCad.dll` (or put it in an autoloader bundle), open the level drawing
(XREF-bound, model space), run `RAFFEXPORT`, answer level id (guessed from the file name), building and output file.
- Room boundaries: closed polylines on layers `*ROOM*;A-AREA*;*BOUNDARY*`; the room number is the text (DBText / MText on
  `*ROOM*;*RM-NO*;A-AREA-IDEN*;*TAG*`) inside the boundary that looks like `P2-106` / `L2-201`. Unnamed boundaries get
  `<level>-ROOMnnn` (fix them in the drawing - the import lists them as unknown rooms).
- Symbols: block references mapped by `blockmap.json` (drawing folder first, then next to the DLL; defaults built in).
  Dynamic blocks use their effective name. `qtyAttribute` multiplies by an attribute value.
- **Exploded symbols**: loose circles / arcs / lines on `E-*;*ELEC*;*LIGHT*;*POWER*` are clustered and compared with the
  shapes of the intact blocks of the same drawing (primitive counts, size, rotation-independent); matches count with
  method `EXPLODED` so the QS sees them separately. Exploded symbols with no intact copy in the drawing cannot be recognised.
- Points outside every boundary are listed under `unassigned` (warning on import).

**Revit 2025+**: copy `Raffaello.CadExport.addin` + DLLs to `%APPDATA%\Autodesk\Revit\Addins\2025\`, run the command;
placed rooms (number, level, boundary loops incl. holes) and electrical family instances (room from the instance, else
point-in-room) are written next to the model. **Revit 2024**: Dynamo > Python Script node > paste `dynamo/raffaello_rooms_export.py`.

Units: the add-ins write millimetres; Revit internal feet are converted (x 304.8).

## Not verified here

Nothing was run inside AutoCAD or Revit (no Autodesk software on the build machine): the projects compile against the stubs
and the shared geometry is unit tested. First real run: check layer names / block names in `blockmap.json`, compare the
room count and a few rooms' points with the drawing, then import with the preview.
