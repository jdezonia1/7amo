# Raffaello - Dynamo Python node (Revit 2024, CPython3 or IronPython2 engine).
# Writes the raffaello-cad-exchange v1 JSON (rooms + electrical devices per room) next to the model.
# Replaces EXPORT_ROOM_BOUNDARIES.dyn. Inputs (optional): IN[0] = output path, IN[1] = building (BRANDED / HOTEL).
# Usage: Dynamo > new graph > Python Script node > paste this file > connect a String node to IN[0] if wanted > Run.
import clr, json, os, datetime, fnmatch
clr.AddReference('RevitAPI')
clr.AddReference('RevitServices')
from Autodesk.Revit.DB import FilteredElementCollector, BuiltInCategory, BuiltInParameter, SpatialElementBoundaryOptions, FamilyInstance, LocationPoint
from RevitServices.Persistence import DocumentManager

FT_TO_MM = 304.8
doc = DocumentManager.Instance.CurrentDBDocument
inputs = IN if 'IN' in dir() else []
out_path = inputs[0] if len(inputs) > 0 and inputs[0] else os.path.join(os.path.dirname(doc.PathName) or os.path.expanduser('~'), (doc.Title or 'model') + '.raffaello.json')
building = (inputs[1] if len(inputs) > 1 and inputs[1] else 'BRANDED').upper()

# Same rules as tools/cad/blockmap.json (first match wins, matched on "family name + type name")
RULES = [
    ('*TWIN*DATA*', 'DATA', 'TWIN DATA OUTLET', 2), ('*TWIN*SOCKET*', 'POWER', 'TWIN SOCKET', 1), ('*SOCKET*', 'POWER', 'SOCKET OUTLET', 1),
    ('*ISOLATOR*', 'POWER', 'ISOLATOR', 1), ('*SWITCH*', 'LIGHT', 'SWITCH', 1), ('*DOWNLIGHT*', 'LIGHT', 'DOWNLIGHT', 1),
    ('*EMERG*', 'EMERGENCY LIGHT', 'EMERGENCY LUMINAIRE', 1), ('*EXIT*', 'EMERGENCY LIGHT', 'EXIT SIGN', 1), ('*DATA*', 'DATA', 'DATA OUTLET', 1),
    ('*TV*', 'AV', 'TV OUTLET', 1), ('*THERMOSTAT*', 'GRMS', 'THERMOSTAT (CP-4)', 1), ('*GRMS*', 'GRMS', 'GRMS DEVICE', 1),
]
CATEGORIES = [BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices,
              BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_FireAlarmDevices,
              BuiltInCategory.OST_SecurityDevices, BuiltInCategory.OST_NurseCallDevices, BuiltInCategory.OST_TelephoneDevices]

def match(name):
    for pattern, system, item, qty in RULES:
        if fnmatch.fnmatch(name.upper(), pattern):
            return system, item, qty
    return None

def ring(loop):
    pts = []
    for seg in loop:
        for p in seg.GetCurve().Tessellate():
            pts.append([round(p.X * FT_TO_MM, 1), round(p.Y * FT_TO_MM, 1)])
    return pts

result = {'format': 'raffaello-cad-exchange', 'version': 1, 'building': building,
          'source': {'application': 'Revit / Dynamo', 'addin': 'raffaello_rooms_export.py 1.0', 'drawing': doc.Title,
                     'exportedAt': datetime.datetime.utcnow().strftime('%Y-%m-%dT%H:%M:%SZ'), 'user': os.environ.get('USERNAME', ''), 'units': 'mm'},
          'levels': [], 'rooms': [], 'counts': [], 'unassigned': []}
rooms = []
opts = SpatialElementBoundaryOptions()
for r in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType():
    if r.Area <= 0:
        continue
    loops = r.GetBoundarySegments(opts)
    if not loops or len(loops) == 0:
        continue
    level = r.Level.Name if r.Level else ''
    if level not in [l['id'] for l in result['levels']]:
        result['levels'].append({'id': level, 'name': level, 'plan': level})
    name_param = r.get_Parameter(BuiltInParameter.ROOM_NAME)
    result['rooms'].append({'id': r.Number.strip(), 'name': name_param.AsString() if name_param else '',
                            'level': level, 'polygon': ring(loops[0]), 'holes': [ring(l) for l in list(loops)[1:]], 'area': round(r.Area * 0.09290304, 3)})
    rooms.append(r)

counts = {}
unassigned = {}
for cat in CATEGORIES:
    for fi in FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType():
        if not isinstance(fi, FamilyInstance):
            continue
        rule = match(fi.Symbol.FamilyName + ' ' + fi.Name)
        if rule is None:
            continue
        system, item, qty = rule
        room = fi.Room
        if room is None and isinstance(fi.Location, LocationPoint):
            for r in rooms:
                if r.IsPointInRoom(fi.Location.Point):
                    room = r
                    break
        if room is None:
            unassigned[(system, item)] = unassigned.get((system, item), 0) + qty
            continue
        key = (room.Number.strip(), system, item, fi.Symbol.FamilyName)
        counts[key] = counts.get(key, 0) + qty

for (room, system, item, block), qty in sorted(counts.items()):
    result['counts'].append({'room': room, 'system': system, 'item': item, 'qty': qty, 'block': block, 'method': 'FAMILY'})
for (system, item), qty in unassigned.items():
    result['unassigned'].append({'system': system, 'item': item, 'qty': qty, 'reason': 'not inside a placed room'})

with open(out_path, 'w') as f:
    json.dump(result, f, indent=2)
OUT = '%d rooms, %d counts -> %s' % (len(result['rooms']), len(result['counts']), out_path)
