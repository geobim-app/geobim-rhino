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
   It loads at startup from then on and opens the **geoBIM.app toolbar** (`GeoBimRhino.rui` next to
   the `.rhp`); `GeoBIMToolbar` shows it again.

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
| `GeoBIMUpload` | Select objects (Enter = all visible) → name → IFC → upload with progress → follows the conversion in the command line → opens the model in the browser. The document remembers the model: the next upload **replaces** it (same link); choose *Model = New* for a separate one. *Format = GLB* uploads a GLB instead (see below); the document remembers the format, IFC and GLB are separate models. |
| `GeoBIMExportIfc` | Writes the IFC only (`-GeoBIMExportIfc` takes the file name on the command line, for scripts). |
| `GeoBIMExportGlb` | Writes the GLB only (`-GeoBIMExportGlb` likewise) and prints the placement it would send. |
| `GeoBIMOpen` | Opens the document's model (or the server) in the browser. |
| `GeoBIMSettings` | Server, token, EPSG, connection test. |
| `GeoBIMToolbar` | Shows the geoBIM.app toolbar (Upload, Open, Export IFC, Settings). |

## GLB instead of IFC

For sculptures, design studies and anything where the look matters more than IFC classes and
properties: `GeoBIMUpload` → *Format = GLB* (needs a geoBIM.app with GLB upload, beta since 2026-11-14).

- **Stored as is** on the server (`model/<name>.glb`, no conversion), so it is ready right away.
- **Materials**: the Rhino render material in its physically based form — base colour, metallic,
  roughness, opacity (e.g. polished stainless steel: metallic 1, roughness 0.05 → a mirror in
  geoBIM.app). Objects without a render material get their display colour, matt.
- **Geometry**: render meshes with vertex normals, one glTF node per Rhino object (blocks resolved),
  name, layer and user text as node `extras`.
- **Placement** from the *Location* (EarthAnchorPoint): lon/lat of the model base point, its height
  above sea level (the server adds the geoid: GCG2016 in Germany, EGM2008 elsewhere) and model north
  as heading. Without a Location geoBIM.app places it at the centre of the view; models in map
  coordinates need *Format = IFC*.

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

1. **Map coordinates** — model centre more than 100 km from the origin: geometry relative to a
   rounded origin (keeps millimetres), `IfcMapConversion` + `IfcProjectedCRS "EPSG:<code>"`.
   With EPSG `auto` the plugin recognises:
   - UTM with zone prefix (8-digit eastings, e.g. 32 373 064) → 4647 / 5650 / 5649
   - Swiss LV95 (E 2.4–2.9 Mio, N 1.0–1.4 Mio) → 2056
   - German Gauß-Krüger with zone prefix (E 2–5 Mio, N 5–6 Mio) → 31466–31469
   - UTM without zone prefix → *DefaultUTM* (25832; set 25833 for eastern Germany / Austria)

   Swiss LV03 and the Austrian systems (MGI Gauß-Krüger West/Central/East, BMN M28/M31/M34,
   Lambert MGI / ETRS89) overlap in their coordinate ranges: the plugin then asks once, offering
   only the plausible ones, and remembers the choice in the document (reset it with
   `GeoBIMSettings → EPSG`). Heights are taken as orthometric; the tiler lifts them with GCG2016
   (Germany) or EGM2008 (elsewhere).
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
