# geoBIM.app for Rhino

Rhino 8 plugin (Windows) that puts Rhino models in their place on the map:

- **Upload to [geoBIM.app](https://viewer.geobim.app)**: the selection becomes an **IFC 4** file (or a GLB with PBR
  materials), the geoBIM.app server converts it into 3D Tiles, and the browser opens the model in place, with colours,
  layers and user text as properties.
- **Upload to [Cesium ion](https://ion.cesium.com)** into your own account, as a 3D Tiles asset.
- **Georeferencing in one step** (`GeoBIMLocation`): pick a point of the model and give its position as map
  coordinates (LV95, UTM, …), latitude/longitude, a place or address, or a click on a map. IFC, GLB and Cesium ion
  then land in the same place, with the right height and north.
- **Export** IFC 4 and GLB files without uploading.

```
Rhino objects ──► IFC 4 / GLB (plugin) ──► geoBIM.app (3D Tiles) or Cesium ion ──► viewer, georeferenced
```

> **Beta.** Version 0.3.0 is a public beta. Please report problems and wishes as
> [GitHub issues](https://github.com/geobim-app/geobim-rhino/issues) or to info@geobim.app.
> **Uploads to geoBIM.app need a geoBIM.app account** with a plugin token; ask for one at info@geobim.app.
> Export, georeferencing and Cesium ion (your own ion account) work without it.

## Install

### Package Manager (recommended)

For Rhino 8 on Windows, from version 0.3.0:

1. In Rhino, run `PackageManager` (or *Tools → Package Manager*).
2. Search for **geobim** and click *Install*.
3. Restart Rhino. The plugin then loads at every start and opens the **geoBIM.app toolbar**;
   `GeoBIMToolbar` shows it again.

The Package Manager also offers new versions, and removes the plugin again: select it among the installed
packages and click *Uninstall*.

### By hand

For a build of your own:

1. Build the plugin (see *Build*): `src/GeoBimRhino/bin/Release/net7.0/GeoBimRhino.rhp` with `GeoBimRhino.rui`
   next to it.
2. Drag `GeoBimRhino.rhp` into the Rhino window (or *Tools → Options → Plug-ins → Install…*).
   It loads at startup from then on and opens the **geoBIM.app toolbar** (`GeoBimRhino.rui` next to
   the `.rhp`); `GeoBIMToolbar` shows it again.

Do not install both ways at once: remove a Package Manager install before loading your own build, and the other
way round.

## Set up (once)

1. In geoBIM.app (owner account): **Account & layout → Rhino plugin → Create** — copy the token
   (`gbp_…`, shown only once). A token can only upload and convert models; revoke it there.
2. In Rhino: `GeoBIMSettings` → **Token** → paste → **Test**.
   - **Live** = https://viewer.geobim.app (default), **Beta** = https://beta.geobim.app, **Server** = custom.
   - A token works only on the site that created it. The plugin keeps one token per server, so switching between
     **Live** and **Beta** needs no new input; choose the server first, then **Token**.
   - **EPSG**: `auto` (default) or a fixed code for models drawn in map coordinates.
   - **DefaultUTM**: EPSG used for UTM coordinates without zone prefix (default 25832).

## Commands

| Command | What it does |
|---|---|
| `GeoBIMUpload` | Select objects (Enter = all visible) → name → IFC → upload with progress → follows the conversion in the command line → opens the model in the browser. The document remembers the model: the next upload **replaces** it (same link); choose *Model = New* for a separate one. *Format = GLB* uploads a GLB instead (see below); the document remembers the format, IFC and GLB are separate models. A selection smaller than 25 cm or larger than 50 km (almost always wrong document units, e.g. a 5 m cube drawn as 5 in millimetres) gets a warning and the question whether to upload it anyway. |
| `GeoBIMExportIfc` | Writes the IFC only (`-GeoBIMExportIfc` takes the file name on the command line, for scripts). |
| `GeoBIMExportGlb` | Writes the GLB only (`-GeoBIMExportGlb` likewise) and prints the placement it would send. |
| `GeoBIMOpen` | Opens the document's model (or the server) in the browser. |
| `GeoBIMLocation` | Where the model lies, in one step (see *Where the model lies*): a point of the model and its position — map coordinates, latitude/longitude, a place or address, or a click on the map — then height and map north. Sets the map origin and Rhino's Location together. `Show` displays the model's outline on the map, `Clear` removes the map origin. Scripted: `-GeoBIMLocation _Enter 2755000,1206000,0,0`. |
| `GeoBIMUploadIon` | Uploads the selection to **Cesium ion** as a new 3D Tiles asset (see below). |
| `GeoBIMSettings` | Server, token, EPSG, connection test; `IonToken` / `IonTest` for Cesium ion. |
| `GeoBIMToolbar` | Shows the geoBIM.app toolbar. Left / right click: **geoBIM.app** (upload / open in the browser), **Cesium ion** (upload / ion token), **Export** (IFC / GLB), **Location** (set the position / show it on the map), **Settings** (settings / test the connections). |

## Where the model lies (once per model)

`GeoBIMLocation` (toolbar: **Location**) asks for:

1. **A point of the model whose position you know.** Enter takes the model origin 0,0,0; otherwise click a survey
   point or a building corner.
2. **Its position**, in whatever form you have it:
   - map coordinates `2755000, 1206000` (LV95; also `2'755'000.00, 1'206'000.00` as map.geo.admin.ch copies them),
     UTM with or without zone prefix, Gauss-Krüger, …;
   - latitude, longitude `46.98695, 9.47632` (Google Maps: right-click → copy) or `46°59'13"N 9°28'35"E`;
   - a place or address (`Valens`, `Marienplatz München`): swisstopo's search in Switzerland, OpenStreetMap's
     Nominatim elsewhere; pick from the list;
   - **Map**: click the point on a map (swisstopo's map and aerial images, BKG TopPlusOpen elsewhere, search box);
     the model's outline follows the marker.
3. **The height** of that point above sea level. After a search or a map click Enter puts the point on the terrain
   (swisstopo in Switzerland, Mapterhorn elsewhere); otherwise Enter keeps the current height while the point stays
   within 1 km. `ModelHeights` keeps the model's z values as heights above sea level. Without a known height there
   is no default.
4. **Map north** in degrees clockwise from +Y (Enter: +Y is north); `TwoPoints` picks a line pointing north.

The plugin stores the map origin (the IFC gets `IfcMapConversion`) and sets Rhino's **Location** to the same place,
with true north from the meridian convergence (about 1.5° in eastern Switzerland). IFC, GLB and Cesium ion then
land in the same place; exports warn if the map origin and Rhino's Location were changed apart. Right-click the
**Location** button (or `GeoBIMLocation` → `Show`) to see the model's outline on the map.

The plugin converts between latitude/longitude and map coordinates itself for **LV95 and UTM** (ETRS89/WGS84,
northern hemisphere), to well below a millimetre against pyproj. Other systems (Gauss-Krüger, LV03, Austria) still
work for the IFC via the map origin; GLB and Cesium ion then need Rhino's Location set separately
(`EarthAnchorPoint`). Models drawn in map coordinates need none of this: their position is known, and GLB and
Cesium ion are placed from it too (LV95, UTM).

Network: the address search asks swisstopo's geo.admin.ch API and OpenStreetMap's Nominatim, `Terrain` asks
swisstopo; the map window loads Leaflet from unpkg, map tiles from swisstopo and BKG, and after a click the
Mapterhorn terrain tile of the point. Typed coordinates are converted offline.

**Full reference:** [docs/GeoBIMLocation.md](docs/GeoBIMLocation.md): all prompts and input formats, how the
coordinate system, height and map north are chosen, what is stored, scripting, messages and accuracy.

## GLB instead of IFC

For sculptures, design studies and anything where the look matters more than IFC classes and
properties: `GeoBIMUpload` → *Format = GLB* (needs a geoBIM.app with GLB upload, beta since 2026-11-14).

- **Stored as is** on the server (`model/<name>.glb`, no conversion), so it is ready right away.
- **Materials**: the Rhino render material in its physically based form — base colour, metallic,
  roughness, opacity (e.g. polished stainless steel: metallic 1, roughness 0.05 → a mirror in
  geoBIM.app). Objects without a render material get their display colour, matt.
- **Geometry**: render meshes with vertex normals, one glTF node per Rhino object (blocks resolved),
  name, layer and user text as node `extras`.
- **Placement** from the model's position (see *Where the model lies*): lon/lat of the model centre, its
  height above sea level (the server adds the geoid: GCG2016 in Germany, EGM2008 elsewhere) and true north
  as heading. Without a position geoBIM.app places it at the centre of the view.

## Cesium ion

`GeoBIMUploadIon` sends the selection to your own [Cesium ion](https://ion.cesium.com) account, where
ion's BIM/CAD tiler turns it into a 3D Tiles asset.

- **Token**: ion.cesium.com → *Access Tokens* → *Create token* with the scopes `assets:list`,
  `assets:read` and `assets:write`; then **right-click the Cesium ion button** in the toolbar (or
  `GeoBIMSettings` → `IonToken`) and paste it into the dialog (hidden field) — the connection and
  the write permission are tested right away. Without `assets:write` ion refuses uploads with a
  misleading `404 ResourceNotFound`; the plugin says what is missing. A Cesium ion token pasted
  into `Token` (the geoBIM.app plugin token) is recognised and stored as ion token.
- **Placement** from the model's position (see *Where the model lies*), which is required: the IFC is
  written in east/north/up metres around the model centre (true north turned to +Y), and ion puts its
  origin at that point's longitude/latitude.
- **Height**: ion works with heights above the ellipsoid, the model with heights above sea level. The
  plugin adds the geoid undulation N at the model itself, from the models the geobim-tiler uses:
  GCG2016 in Germany, EGM2008 elsewhere (embedded grids, within a few centimetres in Europe; outside
  Europe EGM2008 every 1°, up to about 2 m off). The command prints N and the model. The option
  `GeoidN` sets a document's own value (kept in the document); `GeoidN=0` goes back to the model.
- **Every upload is a new asset** (ion has no replace); the command prints the asset id, follows
  the tiling and opens the asset page. Delete old assets in ion yourself.
- Scripted: `_-GeoBIMUploadIon _Enter "Asset name"` (N from the geoid model), or with the document's own
  value `_-GeoBIMUploadIon _Enter GeoidN=48.7 "Asset name"`.

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

0. **Map origin** (`GeoBIMLocation`): the model is drawn in small local coordinates
   (x = E − E0, y = N − N0, z = H − H0) and the document remembers E0/N0/H0 and an optional
   rotation (angle of the local x axis counter-clockwise from map east). The IFC export writes
   `IfcMapConversion` from them and leaves the geometry as it is, so there is no need for a second
   copy of the model moved to real map coordinates. The EPSG code is found as for map coordinates
   below (LV95 → 2056). If the geometry already lies in map coordinates the map origin is ignored
   (with a note). GLB and Cesium ion use the same position (see *Where the model lies*).
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

Package for the Rhino Package Manager (Yak, Windows): `python tools/make_yak.py` builds and writes
`dist/geobim-<version>-rh8_0-win.yak` (manifest and icon in `tools/yak/`). It does not push; try a
package on `https://test.yak.rhino3d.com` first, because a version pushed to the public server
cannot be replaced. To install from the test server, add it as a source in Rhino (*Tools → Options →
Package Manager*) and search for **geobim** as above.

## Test

`tests/make_test_models.py` builds the same small model in seven cases (UTM map coordinates,
EarthAnchorPoint, no georeferencing, LV95 map coordinates, local with a map origin, local with a
rotated map origin, local placed with `-GeoBIMLocation` from latitude/longitude) inside Rhino and
exports each with `-GeoBIMExportIfc`, plus Cesium ion IFCs (`-GeoBIMUploadIon … Upload=No`, nothing
is sent) and the GLB placement:

```
"C:\Program Files\Rhino 8\System\Rhino.exe" /nosplash /runscript="_-RunPythonScript ""<repo>\tests\make_test_models.py"""
python tests/check_exports.py
```

Output in `tests/out/` (not in git). `check_exports.py` validates every IFC with IfcOpenShell,
checks that the map-origin cases give the same absolute coordinates as the model in real LV95
coordinates, and checks the positions `GeoBIMLocation` sets, the GLB placement and the ion frames
against pyproj. `dotnet run` in `tests/geo_check` checks the plugin's own LV95/UTM conversion
against pyproj. The geobim-tiler can tile them as well
(`geobim_tile.py tests/out/utm.ifc --out tests/out/tiles_utm`).

## License

[MPL 2.0](LICENSE) — © 2026 Christof Lorenz, geoBIM.app.

Embedded data (`src/GeoBimRhino/Resources/geoid/`, built by `tools/make_geoid.py` from PROJ-data):
EGM2008 (NGA, public domain); GCG2016 — Derived from work by BKG,
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
