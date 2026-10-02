# CAD exchange file - raffaello-cad-exchange v1

JSON written by the AutoCAD add-in (`RAFFEXPORT`), the Revit add-in or the Dynamo script (tools/cad) and imported by Raffaello
(TRUST & INTEGRATIONS > CAD EXCHANGE, `raffaello-cli cad-import`). Formal JSON Schema (draft 2020-12):
`src/Raffaello.Core/Integrations/CadExchange/cad-exchange.schema.json` (also `raffaello-cli cad-schema`).

```json
{
  "format": "raffaello-cad-exchange",
  "version": 1,
  "source": { "application": "AutoCAD", "addin": "Raffaello.CadExport.AutoCad 1.0", "drawing": "L02-ELEC.dwg",
              "exportedAt": "2026-10-02T07:00:00Z", "user": "mohamed", "units": "mm" },
  "building": "HOTEL",
  "levels": [ { "id": "L02", "name": "LEVEL 02", "plan": "L02", "frame": { "minX": 0, "minY": 0, "maxX": 84000, "maxY": 42000 } } ],
  "rooms":  [ { "id": "L2-201", "level": "L02", "type": "KING", "polygon": [[0,0],[6000,0],[6000,5000],[0,5000]], "holes": [], "area": 30.0 } ],
  "counts": [ { "room": "L2-201", "system": "LIGHT", "item": "DOWNLIGHT", "qty": 12, "block": "E-LT-DL", "method": "BLOCK" } ],
  "unassigned": [ { "system": "LIGHT", "item": "DOWNLIGHT", "qty": 2, "level": "L02", "reason": "outside every room boundary" } ],
  "blockMap": [ { "block": "*DOWNLIGHT*", "system": "LIGHT", "item": "DOWNLIGHT", "qty": 1 } ]
}
```

| Part | Required | Meaning |
|---|---|---|
| `rooms[].id` | yes | room code as used in the ledger (P2-106, L2-201) |
| `rooms[].level` | yes | level id |
| `rooms[].polygon` | yes | outer boundary, >= 3 points, drawing units, Y up; closing point optional |
| `rooms[].holes`, `area` | no | shafts / voids; reported m2 (checked against the polygon, 5 % tolerance) |
| `counts[].room / system / item / qty` | yes | points per room x system (ledger item: POWER, LIGHT, DATA, GRMS ...) x symbol |
| `counts[].stage` | no | empty = the count applies to every stage chosen at import (default 1ST FIX and 2ND FIX) |
| `counts[].method` | no | BLOCK, ATTRIBUTE, EXPLODED (recognised from loose geometry), FAMILY (Revit) |
| `levels[].frame` | no | drawing extents of the plan image, so room shapes line up with the PDF plan in the plan view |

## Import rules

- Validation first: format / version, unique room ids, levels, polygon size and area, counts with room / system / item and qty >= 0.
  Errors block the import; warnings (area mismatch, points outside rooms, counts for unknown rooms) are shown.
- Rooms not yet in the building are created (option). Room shapes (`RM_<room>-<plan>`) replace earlier shapes of the same room
  and plan; coordinates are normalised to the level frame (Y flipped to image coordinates).
- PROJECT QTY = sum of the counts per room x stage x system. Mode **REPLACE** overwrites the keys the file covers; **ADD_MISSING**
  only fills keys that have no PROJECT QTY yet. Every change is previewed (old -> new, with the symbols that make it up) and
  written in one audited batch (`Source = CAD:<drawing>`).
- Points per block (twin socket = 1, twin data = 2 ...) are decided by `blockmap.json` on the CAD side, so the file already holds points.
