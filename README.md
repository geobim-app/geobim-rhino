# geoBIM.app for Rhino

Rhino 8 plugin that uploads Rhino models straight to [geoBIM.app](https://viewer.geobim.app):
the selected objects become an **IFC 4** file, the geoBIM.app server converts it with the
geobim-tiler into 3D Tiles, and the browser opens the model in place — georeferenced, with
colours, layers and user text as properties.

```
Rhino objects ──► IFC 4 (plugin) ──► api/pointcloud-upload.php ──► geobim-tiler ──► viewer.geobim.app/?model=<slug>
```

## Install

1. Build (see below) or take `GeoBimRhino.rhp` from a release.
2. Drag `GeoBimRhino.rhp` into the Rhino window (or *Tools → Options → Plug-ins → Install…*).
   It loads at startup from then on.

## Set up (once)

1. In geoBIM.app (owner account): **Account & layout → Rhino plugin → Create** — copy the token
   (`gbp_…`, shown only once). A token can only upload and convert models; revoke it there.
2. In Rhino: `GeoBIMSettings` → **Token** → paste → **Test**.
   - **Live** = https://viewer.geobim.app (default), **Beta** = https://beta.geobim.app, **Server** = custom.
   - **EPSG**: `auto` (default) or a fixed code for models drawn in map coordinates.
   - **DefaultUTM**: EPSG used for UTM coordinates without zone prefix (default 25832).

## Commands

| Command | What it does |
|---|---|
| `GeoBIMUpload` | Select objects (Enter = all visible) → name → IFC → upload with progress → follows the conversion in the command line → opens the model in the browser. The document remembers the model: the next upload **replaces** it (same link); choose *Model = New* for a separate one. |
| `GeoBIMExportIfc` | Writes the IFC only (`-GeoBIMExportIfc` takes the file name on the command line, for scripts). |
| `GeoBIMOpen` | Opens the document's model (or the server) in the browser. |
| `GeoBIMSettings` | Server, token, EPSG, connection test. |

## What ends up in the IFC

- **One element per Rhino object** (polysurfaces, extrusions, surfaces, SubD, meshes; blocks are
  resolved — one element per component, with the instance transform). Curves, points and
  annotations are skipped and counted.
- **Geometry**: `IfcTriangulatedFaceSet` from the document's render mesh settings, in metres.
- **IFC class**: user text `IfcClass` (e.g. `IfcWall`, `Slab`), else a layer whose name starts with
  an IFC class (`IfcWall`, `IfcColumn – Stützen`), else `IfcBuildingElementProxy`.
  Supported: Wall, Slab, Beam, Column, Roof, Footing, Member, Plate, Railing, Ramp, Stair,
  Covering, CurtainWall, Chimney, ShadingDevice, Furniture, GeographicElement, BuildingElementProxy.
- **Colour**: display colour (object / layer / parent), transparency from the material.
- **Properties**: `Pset_Rhino` (Layer path, Name, ObjectId, Type, Block, Material) and
  `Pset_RhinoUserText` (all user text keys) — shown in geoBIM.app's InfoBox and filters.
- **Layers** also as `IfcPresentationLayerAssignment`. GlobalIds come from the Rhino object ids,
  so a re-upload keeps them.

## Georeferencing (automatic)

1. **Map coordinates** — model centre beyond 100 km in x or 1000 km in y: geometry relative to a
   rounded origin (keeps millimetres), `IfcMapConversion` + `IfcProjectedCRS "EPSG:<code>"`.
   EPSG `auto`: zone prefix 32/33 (8-digit eastings) → 4647/5650, 7-digit eastings 2–5 Mio →
   Gauss-Krüger 31466–31469, otherwise *DefaultUTM*. Heights are taken as orthometric (NHN);
   the tiler lifts them with GCG2016 / EGM2008.
2. **EarthAnchorPoint** set (*Location* in Rhino): `IfcSite` RefLatitude/RefLongitude/RefElevation
   at the model base point, model north as `TrueNorth`.
3. **Neither**: geoBIM.app drops the model in the centre of the view; move it with the gizmo and
   *Save position* — a later replace keeps that position.

## Build

Requires the .NET 8 SDK (builds `net7.0`, which loads in every Rhino 8 on Windows and Mac).

```
dotnet build src/GeoBimRhino/GeoBimRhino.csproj -c Release
→ src/GeoBimRhino/bin/Release/net7.0/GeoBimRhino.rhp
```

## Test

`tests/make_test_models.py` builds the same small model three times (UTM map coordinates,
EarthAnchorPoint, no georeferencing) inside Rhino and exports each with `-GeoBIMExportIfc`:

```
"C:\Program Files\Rhino 8\System\Rhino.exe" /nosplash /runscript="_-RunPythonScript ""<repo>\tests\make_test_models.py"""
```

Output in `tests/out/` (not in git). Check the IFCs with IfcOpenShell and the geobim-tiler
(`geobim_tile.py tests/out/utm.ifc --out tests/out/tiles_utm`).

## License

[MPL 2.0](LICENSE) — © 2026 Christof Lorenz, geoBIM.app.
