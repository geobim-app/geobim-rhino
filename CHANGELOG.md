# Changelog

All notable changes to geoBIM.app for Rhino are documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.3.0] — 2026-11-20

### Added

- **Map origin for local coordinates** (`MapOrigin.cs`, `ModelExport.cs`, `Commands.cs`): the document
  stores E0, N0, H0 and an optional rotation (`doc.Strings` `geobim/mapOrigin`, set with `GeoBIMLocation`). The IFC export then writes `IfcMapConversion` from them (Eastings/Northings/
  OrthogonalHeight, XAxisAbscissa/Ordinate) and leaves the local geometry as it is. A model no longer
  needs a second copy shifted to real map coordinates. Geometry that already lies in map coordinates
  ignores the map origin; GLB is still placed by the EarthAnchorPoint. `GeoBIMSettings` shows the
  document's map origin. Idea from EarthLink (github.com/J1mmywzh/EarthLink, MIT): the placement is
  data kept with the document, the geometry never moves.
- **One position for every export: `GeoBIMLocation`** (`GeoLocation.cs`, `GeoProj.cs`, `GeoAdmin.cs`,
  `MapPicker.cs`, `Commands.cs`): one command says where the model lies. Pick a point of the model (Enter = origin)
  and give its position as map coordinates, latitude/longitude (as Google Maps or map.geo.admin.ch copy them, also
  degrees/minutes/seconds), a place or address (swisstopo search in Switzerland, OpenStreetMap's Nominatim
  elsewhere, asked in parallel; hits show their source; Nominatim used as its usage policy asks: the plugin's
  User-Agent, one request per second, no search while typing, cached, credited) or a click on a map window
  (swisstopo map and aerial images, BKG TopPlusOpen, taken automatically outside Switzerland and at the start when
  the model has no position yet; the model's outline follows the marker; its search box runs through the
  plugin); then the height
  (`Terrain`: swisstopo's terrain height after a search or map click) and map north (`TwoPoints`). It stores the
  map origin and the coordinate system and sets Rhino's Location to the same place, with true north from the
  meridian convergence. `Show` displays the model's outline on the map. Replaces `GeoBIMMapOrigin` (never
  released). The plugin converts LV95 (Swiss oblique Mercator, CH1903+ → WGS84 as EPSG:1676) and UTM (ETRS89/WGS84,
  Krüger series) itself; `tests/geo_check` checks it against pyproj (below 0.2 mm, convergence to 0.00001°)
  and the parsing of swisstopo's and Nominatim's answers (`dotnet run online` also asks both services once).
  Reference: `docs/GeoBIMLocation.md` (prompts, input formats, coordinate system, height, north, stored values,
  scripting, messages, accuracy, network).
  - **GLB and Cesium ion follow the map position**: they take latitude/longitude and true north from the map
    origin, or from the map coordinates of models drawn in LV95/UTM (base point: the model centre rounded to
    metres), and fall back to Rhino's Location only for other systems. Models in map coordinates can now be
    uploaded as GLB and to Cesium ion without setting a Location. Exports warn when Rhino's Location and the map
    origin are more than 0.5 m or 0.05° apart.
- **Test cases** `lv95`, `origin`, `origin_rot` in `tests/make_test_models.py` and
  `tests/check_exports.py`, which validates all test IFCs with IfcOpenShell and compares the absolute
  coordinates of the map-origin cases with the model in real LV95 coordinates.
- **`CLAUDE.md`**: build and test commands, source map, where settings live, code rules and rules for
  operating Rhino for someone (after EarthLink's agent guide).
- **Cesium ion upload** (`IonClient.cs`, `AwsSigV4.cs`, `Commands.cs`, `ModelExport.cs`): new command
  `GeoBIMUploadIon` creates a 3D Tiles asset (`sourceType` `BIM_CAD`, as lab.geobim.app's
  `uploadToIon`), uploads the IFC to ion's S3 location (own AWS Signature V4, no AWS SDK), completes
  the upload and follows the tiling until the asset is ready. The IFC is written in east/north/up
  metres around the model's position (true north turned to +Y, no heading needed); the origin goes
  to its lon/lat at the height above sea level plus the geoid undulation N.
  `GeoBIMSettings` gains `IonToken` and `IonTest`. `tests/sigv4_check` checks the signature against
  AWS's published S3 examples (`dotnet run`, both pass); `tests/make_test_models.py` writes the ion
  IFCs of the EarthAnchorPoint, `GeoBIMLocation` and LV95 cases (`Upload=No`), one with N from the geoid
  model, and `check_exports.py` checks them.
  - **Write permission**: `IonTest` checks it without creating anything (an empty `POST /v1/assets`).
    Ion answers writes of a token without `assets:write` with `404 ResourceNotFound`
    (`/v1/assets?assetRegion=us-east-1`), not 403 — seen on 2026-11-19 with a read-only token; upload
    errors of that kind say that the token lacks `assets:write`.
  - **S3 region from ion**: ion sends the account's storage region with the upload location
    (`region`, e.g. `eu-west-1` with bucket `cesiumion-assets-production-eu-west-1`) and the upload is
    signed for it. If S3 names another region anyway, the upload is retried once there.
  - **Geoid undulation from the plugin** (`Geoid.cs`, `tools/make_geoid.py`): N comes from the models
    the geobim-tiler uses, GCG2016 in Germany and EGM2008 elsewhere, embedded as grids (GCG2016 every
    4th node of PROJ-data's grid, as geoBIM.app's viewer; EGM2008 every 5' over Europe and every 1°
    worldwide; 330 KB). `tests/geo_check` compares them with PROJ's full grids: within 2.5 cm at the
    test points in Europe (GCG2016 within 4 mm). The question for `GeoidN` is gone; the option sets a
    document's own value, 0 goes back to the model, and the command prints N with its source.
  - Checked end-to-end on 2026-11-19: Taminabrücke uploaded to Cesium ion (EU account), tiled with
    the BIM/CAD tiler in about 4 minutes; tileset transform exactly at the Location, bridge centre
    0.13 m / 0.08 m from the LV95 position computed with pyproj.
- **Toolbar in Rhino 8 style** (`tools/make_icons.py`, `tools/make_rui.py`, `tools/icons/`): five buttons,
  each with a left and a right click as in Rhino's own toolbars — **geoBIM.app** (upload / open in the
  browser), **Cesium ion** (upload / ion token), **Export** (IFC / GLB), **Location**
  (`GeoBIMLocation` / show the position on the map), **Settings** (settings / test both connections). The icons are drawn by
  `make_icons.py` like Rhino's: filled shapes with a dark outline — the geoBIM logo, the Earth, an IFC
  page, a map pin on a construction grid with the red and green axes, a gear — with an orange badge for
  upload and a green one for export. They are embedded in the `.rui` as SVG for light and dark mode, so
  they stay sharp at every size; Rhino 8 reads them only as real XML inside `<icon><light>`/`<dark>`
  (escaped text and CDATA gave text-only buttons, checked in Rhino 8 on 2026-11-20). PNG strips remain
  as fallback; button GUIDs stay derived from fixed names, so Rhino keeps treating it as the same toolbar.
- **Package for the Rhino Package Manager** (`tools/make_yak.py`, `tools/yak/`): builds the plugin and
  writes `dist/geobim-<version>-rh8_0-win.yak` (Windows, Rhino 8) with the `.rhp`, `.rui`, icon,
  licence and manifest. It never pushes.
- **GLB upload** (`GlbWriter.cs`, `Commands.cs`, `GeoBimClient.cs`, `ModelExport.cs`): `GeoBIMUpload`
  has *Format = IFC/GLB* (remembered per document; IFC and GLB are separate models with their own
  replace link). GLB is written by the plugin (binary glTF 2.0, Y-up, one node per object, vertex
  normals, user text as `extras`) with the render material in physically based form — polished steel
  stays a mirror in geoBIM.app. Placement from the EarthAnchorPoint (lon/lat, height above sea level
  with `height_datum=orthometric`, model north as heading); needs geoBIM.app's GLB upload
  (`api/pointcloud-upload.php`, beta). New command `GeoBIMExportGlb`. `GeoBIMOpen` opens the model of
  the format used last. Tested in Rhino 8 with `examples/upload_knot_glb.py`: GLB checked with
  trimesh (PBR metallic 1 / roughness 0.05, normals, bounds), uploaded to beta, placed at Cloud Gate
  (41.882684, −87.623328, 180 m above sea level → 145.99 m ellipsoidal).

### Changed

- **Tokens can no longer be mixed up**: a Cesium ion token pasted into `GeoBIMSettings → Token` is
  recognised (JWT, `eyJ…`) and stored as the ion token; a `gbp_…` token pasted into `IonToken` is
  refused with a hint. Saving the ion token runs `IonTest` at once.
- **Tokens are entered in a dialog with a hidden field** (interactive `GeoBIMSettings`): Rhino's
  command line echoed pasted tokens into the command history. Scripted `-GeoBIMSettings` still reads
  them from the command line.

### Fixed

- **`GeoBIMToolbar` in Rhino 8** (`GeoBimPlugin.cs`): Rhino 8 turns the toolbar group into a container,
  so `ToolbarFile.GetGroup` returns nothing (`GroupCount` 0) and the command reported "toolbar file not
  found" although the file was open. RhinoCommon has no call to show a container, and Rhino 8's
  `_-ShowToolbar "geoBIM.app"` accepted the name but left a hidden container hidden (8.37). The plugin
  now calls Rhino 8's own internal `ToolBarFileController.ShowToolBar` (Rhino.UI, by reflection), which shows the container or a new floating one; `_-ShowToolbar` stays as
  fallback, Rhino 7's group path as before. Checked in Rhino 8.37 on 2026-11-20 with the container
  hidden.

## [0.2.0] — 2026-11-13

### Added

- **Toolbar** `GeoBimRhino.rui` next to the `.rhp` (Rhino opens it on the plugin's first load; the
  plugin opens it once itself for installs that predate it): Upload, Open, Export IFC, Settings with
  Lucide icons in the brand colour, built by `tools/make_rui.py` in Rhino 8's RUI layout. New command
  `GeoBIMToolbar`.
- **Coordinate systems for Switzerland and Austria** (`CrsDetect.cs`): LV95 is detected automatically
  (EPSG 2056); for Swiss LV03 and the Austrian systems (MGI GK West/Central/East, BMN M28/M31/M34,
  Lambert MGI/ETRS89), whose ranges overlap, the plugin asks once with the plausible candidates and
  remembers the choice in the document. Map coordinates now start at 100 km from the origin in x or y
  (Austrian GK eastings are small). Checked with the geobim-tiler: EPSG 2056 and 31255 placed exactly
  as pyproj computes; Taminabrücke (LV95) uploaded and checked on beta against Mapterhorn terrain (deck ends within 1.2 m).

### Changed

- **Clear messages** when a server does not accept plugin tokens yet (e.g. live before the server
  part is released) or the token belongs to another site.

## [0.1.0] — 2026-11-13

### Added

- **Plugin for Rhino 8** (`net7.0`, RhinoCommon 8.0): commands `GeoBIMUpload`, `GeoBIMExportIfc`,
  `GeoBIMOpen`, `GeoBIMSettings`.
- **IFC 4 writer** without external libraries: one element per Rhino object (blocks resolved per
  component), `IfcTriangulatedFaceSet` from render meshes in metres, colours, `Pset_Rhino`,
  `Pset_RhinoUserText`, layer assignments, IFC class from user text `IfcClass` or the layer name,
  stable GlobalIds from Rhino object ids, non-ASCII names as `\X2\…\X0\`.
- **Automatic georeferencing**: map coordinates → `IfcMapConversion` (EPSG auto-detected:
  UTM with/without zone prefix, Gauss-Krüger), EarthAnchorPoint → `IfcSite` RefLatitude/
  RefLongitude/RefElevation + TrueNorth, otherwise none (placed in geoBIM.app).
- **Upload** with a geoBIM.app plugin token, progress in the status bar, conversion followed in
  the command line, browser opens `?model=<slug>`; re-upload replaces the document's model
  (falls back to a new one if it is gone).
- Tested with Rhino 8.35: the three test models (UTM, EarthAnchorPoint, none) validate with
  IfcOpenShell 0.9 without issues; geobim-tiler 1.9.4 places them via IfcMapConversion
  (50.672970 / 7.202647, matches pyproj), IfcSite (50.6745 / 7.2035, heading 330°) and none;
  upload against a local geoBIM.app server.
