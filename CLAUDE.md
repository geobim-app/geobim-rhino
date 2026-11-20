# CLAUDE.md

Guidance for Claude Code (and other assistants) working on **geoBIM.app for Rhino**, the Rhino 8 plugin that
uploads Rhino models to geoBIM.app (IFC 4 or GLB) and Cesium ion. Read `README.md` first; `CHANGELOG.md` says
what changed recently.

## Build and test

- Build: `dotnet build src/GeoBimRhino/GeoBimRhino.csproj -c Release` writes
  `src/GeoBimRhino/bin/Release/net7.0/GeoBimRhino.rhp` (and copies `GeoBimRhino.rui` next to it).
  A running Rhino that has the plugin loaded locks the `.rhp`. To check that the code compiles anyway, build into
  another folder (`-p:OutDir=<folder>/`). To test a new build, Rhino has to be closed and started again. Ask before
  closing someone's Rhino.
- Test models: `"C:\Program Files\Rhino 8\System\Rhino.exe" /nosplash /runscript="_-RunPythonScript ""<repo>\tests\make_test_models.py"""`
  builds the same small model in every georeferencing case (UTM, EarthAnchorPoint, none, LV95, map origin, map
  origin rotated, placed with `GeoBIMLocation`), exports `tests/out/<case>.ifc` plus Cesium ion IFCs (`ion.ifc`,
  `location_ion.ifc`, `lv95_ion.ifc`; nothing is sent), the GLB placement and `location.json`, and closes Rhino.
  Then run `python tests/check_exports.py` (system Python with ifcopenshell 0.9 and pyproj). It validates the
  schema, compares absolute coordinates between the cases and checks positions, true north and the ion frames
  against pyproj. Exit code 0 means everything passed.
- Projection maths: `dotnet run` in `tests/geo_check` checks `GeoProj.cs` (LV95, UTM, convergence, parsing of
  typed positions) against pyproj values; all must pass (well below a millimetre).
- Toolbar: `python tools/make_icons.py && python tools/make_rui.py`. Buttons and their left/right macros are in
  `BUTTONS` in `make_rui.py`; the icons are drawn in `make_icons.py` (SVG for light and dark mode in
  `tools/icons/`, PNG strips as fallback). The SVG goes into the `.rui` as real XML inside `<icon><light>`/`<dark>`:
  Rhino 8 shows text-only buttons for escaped text or CDATA. The `.rui` is copied next to the `.rhp` on build.
- Uploads: `examples/upload_knot_glb.py` shows how to run `_-GeoBIMUpload` from a script and wait for "ready —"
  in the command history.
- Report exactly which checks ran and which did not. A build alone is not a test.

## Source map (`src/GeoBimRhino/`)

| File | Role |
|---|---|
| `Commands.cs` | All commands; `Shared` holds selection, EPSG resolution and the export entry point |
| `ModelExport.cs` | Rhino objects → `IfcModelData`: blocks resolved, meshing, georeferencing decision |
| `MapOrigin.cs` | Map origin of local coordinates, stored in the document |
| `GeoLocation.cs` | One position for all exports: placement for GLB/ion, setting map origin and Rhino's Location together, mismatch check |
| `GeoProj.cs` | LV95 and UTM ↔ WGS84, meridian convergence, parsing typed positions (no Rhino types) |
| `GeoAdmin.cs` | swisstopo search and terrain height (geo.admin.ch API, Switzerland) |
| `MapPicker.cs` | Map window (Eto WebView, Leaflet, swisstopo and BKG tiles) to pick or show the position |
| `CrsDetect.cs` | Which EPSG code map coordinates belong to (no Rhino types) |
| `IfcWriter.cs` | IFC 4 STEP writer without external libraries; also defines `IfcModelData` |
| `GlbWriter.cs` | Binary glTF 2.0 with PBR materials |
| `GeoBimClient.cs` | geoBIM.app HTTP API, `ProgressStreamContent` for upload progress |
| `IonClient.cs` | Cesium ion REST API and the S3 upload (own SigV4, no AWS SDK) |
| `GeoBimPlugin.cs` | Plugin class, global settings, toolbar |

Georeferencing order in `ModelExport.Build`:
1. The document's map origin, if the geometry is local. This writes `IfcMapConversion` from it.
2. Map coordinates (more than 100 km from 0). This writes `IfcMapConversion` with an origin rounded to the km.
3. The EarthAnchorPoint. This writes `IfcSite` lat/lon and TrueNorth.
4. Nothing.

GLB and Cesium ion are placed by latitude/longitude from `GeoLocation.For`: the map origin or the map coordinates
when their coordinate system converts (LV95, UTM; `GeoProj`), else Rhino's Location (EarthAnchorPoint). The base
point is the model centre rounded to metres; true north includes the meridian convergence. `GeoBIMLocation` sets
the map origin and Rhino's Location from one input, so all formats agree; exports warn when they differ.

## Where settings live

- **Per document:** `doc.Strings`, section `"geobim"`, saved in the .3dm.

  | Key | Content |
  |---|---|
  | `slug\|<server>` | IFC model on that geoBIM.app server |
  | `glbslug\|<server>` | GLB model on that server |
  | `format` | `ifc` or `glb` |
  | `epsg` | EPSG chosen for this document (also set by `GeoBIMLocation`) |
  | `mapOrigin` | `E,N,H,rotationDeg` |
  | `geoidN` | geoid undulation for Cesium ion |
  | `ionasset` | last Cesium ion asset id |

- **Per user:** `PlugIn.Settings` (`GeoBimPlugin.cs`). Keys: `server`, `token`, `epsg`, `defaultUtmEpsg`, `ionToken`,
  `toolbarShown`.

## Code rules

- **Never block Rhino's UI thread with network calls.**
  - Export (meshing) runs on the UI thread. Upload and polling run in `Task.Run`.
  - Everything that touches Rhino goes back through `Shared.Ui` (`RhinoApp.InvokeOnUiThread`). `GeoBIMUpload` is
    the pattern: a busy flag, a progress meter keyed by the document serial, and a document lookup by serial
    because the document may close meanwhile.
- **Tokens:**
  - Never print or log a token. Use `GeoBimPlugin.MaskedToken`.
  - Never put tokens into files, test output or commit messages.
- **Dependencies:** no NuGet packages besides RhinoCommon without asking. The plugin ships as one `.rhp` (plus
  `.rui`).
- **External services besides the upload targets** are called only on the user's action: swisstopo's search and
  height service (`GeoAdmin.cs`) when they search an address or pick on the map; the map window loads Leaflet from
  unpkg (version and SRI hash pinned) and tiles from swisstopo and BKG.
- **Compatibility:**
  - Keep the plugin GUID and the command GUIDs. Rhino and the Package Manager identify the plugin by them.
  - New commands get a new GUID.
  - Keep `net7.0`, which loads in every Rhino 8.
- **Formatting:** numbers in files and in scriptable input use `CultureInfo.InvariantCulture`.
- **Documentation:** every user-visible change gets a `CHANGELOG.md` entry (Keep a Changelog, `[Unreleased]`) and,
  if it changes usage, a README update. Changes to `GeoBIMLocation` or to how exports are placed also go into
  `docs/GeoBIMLocation.md` (its prompts, defaults and messages are quoted there).

## Operating Rhino for someone (rhino-mcp or scripts)

1. **Check the state first.** Find out the active document, whether it is saved, its units, the EarthAnchorPoint,
   the map origin (`doc.Strings` `geobim`/`mapOrigin`), the visible layers and the selection. Do not assume an
   earlier session's file is still open.
2. **Never invent coordinates.** Use only locations the person gave or that are documented in their project.
3. **Hide what must not be sent.** Enter at the selection prompt means all *visible* objects, so hide layers that
   must not be exported (e.g. terrain).
4. **Every upload publishes.** `GeoBIMUpload` (geoBIM.app) and `GeoBIMUploadIon` (Cesium ion) send the model to an
   external service. Ask for an explicit yes each time. An earlier yes does not cover the next upload.
   `GeoBIMUpload` replaces the document's model by default.
5. **Use the scripted forms for automation:**
   - `_SelNone _-GeoBIMExportIfc _Enter "<path>.ifc" _Enter`
   - `_-GeoBIMLocation _Enter 2755000,1206000,0,0` (E,N,H,rotation of the model origin; nothing else is asked)
   - `_-GeoBIMLocation _Enter 46.98695,9.47632,0 _Enter` (latitude, longitude, height; map north = +Y)
6. **If Rhino stops responding,** stop driving it and ask before restarting it.

## Git and releases

- Work on a feature branch, commit only when asked, never force-push, and never commit `tests/out/`, `dist/` or
  tokens.
- Releases go to the Rhino Package Manager (Yak, `tools/make_yak.py`, Windows only for now). A `yak push` to the
  public server is permanent for that version and needs an explicit yes. Try the test server first.
