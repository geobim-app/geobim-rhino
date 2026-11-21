# GeoBIMLocation: where the model lies

`GeoBIMLocation` (toolbar: **Location**; right click: **Show on map**) tells the plugin where a model drawn in
local coordinates lies on Earth. You give one position; the plugin then sets two things that used to be set
separately, and keeps them in agreement:

- the **map origin** of the document, which the IFC export writes as `IfcMapConversion`;
- **Rhino's Location** (EarthAnchorPoint), which places GLB and Cesium ion uploads and Rhino's own sun and
  geolocation features.

IFC, GLB and Cesium ion then land in the same place. You run the command once per model, and again only if the
position was wrong.

You do **not** need it when the model is drawn in real map coordinates (LV95, UTM, …). The plugin knows that
position already and uses it for every format.

- [Quick start](#quick-start)
- [The prompts](#the-prompts)
- [Position formats](#position-formats)
- [Coordinate system](#coordinate-system)
- [Height](#height)
- [Map north](#map-north)
- [Search](#search)
- [The map window](#the-map-window)
- [What the command stores](#what-the-command-stores)
- [How the exports use it](#how-the-exports-use-it)
- [Scripting](#scripting)
- [Messages](#messages)
- [Accuracy](#accuracy)
- [Network and privacy](#network-and-privacy)

## Quick start

**A survey point is known.** The plan gives a building corner at LV95 E 2 683 110.25, N 1 247 930.10, 408.50 m.

```
Command: GeoBIMLocation
Point whose position you know (Enter = model origin 0,0,0) ( Map  Show  Clear ):   ← click the corner
Its position: …: 2683110.25, 1247930.10, 408.5
Map north in the model, degrees clockwise from +Y <0>:                              ← Enter: +Y is north
```

**Only the place is known.** Enter takes the model origin; then type a place or an address (`Valens`,
`Marienplatz München`) and pick the hit, or choose `Map` and click the spot. If the model origin is at ground level,
Enter takes the terrain height (swisstopo in Switzerland; after a map click also elsewhere, from Mapterhorn);
otherwise type the height.

**Latitude and longitude from Google Maps.** Right-click the spot in Google Maps, click the coordinates to copy
them, and paste them at the position prompt (`46.98695, 9.47632`). The plugin converts them to LV95 or UTM.

**Taminabrücke** (model drawn as x = E − 2 755 000, y = N − 1 206 000, z = height above sea level):

```
_-GeoBIMLocation _Enter 2755000,1206000,0,0
```

Afterwards, right-click **Location** to check the model's outline on the map.

## The prompts

| # | Prompt | Default (Enter) | Options |
|---|---|---|---|
| 1 | *Point whose position you know* | the model origin 0,0,0 | `Map`: go straight to the map window. `Show`: show the current position on the map and end. `Clear`: remove the map origin and end. |
| 2 | *Its position: map coordinates E,N[,H], latitude, longitude[, H], or a place or address* | if the document already has a map origin: the point's current map coordinates | `Map`: pick the point on the map. A place or address lists the hits (see [Search](#search)); type the number of the right one. |
| 3 | *Height of this point above sea level in metres* (only when the position had no height) | see [Height](#height); none when no height is known, then type it | `Terrain`: the terrain height there (swisstopo after a search or map click in Switzerland, Mapterhorn after a map click elsewhere). `ModelHeights`: the point's z, so the model's z values stay heights above sea level. |
| 4 | *Map north in the model, degrees clockwise from +Y* (only when no rotation was given) | the current rotation, else 0 | `TwoPoints`: pick a line that points to map north. |

The command first prints what the document has now, for example:

```
geoBIM.app: model origin at LV95 E 2755000 N 1206000 H 0 = 46.986952° N, 9.476321° E, Rhino's Location matches.
```

and at the end what it set:

```
geoBIM.app: LV95 (EPSG:2056) E 2755000 N 1206000.
geoBIM.app: model origin at LV95 E 2755000 N 1206000 H 0, map north = +Y.
geoBIM.app: Rhino's Location set to the same place (46.986952° N, 9.476321° E, 0 m; true north 1.488° from map north). …
```

## Position formats

| You type | Read as |
|---|---|
| `2755000, 1206000` or `2755000,1206000` | map coordinates E, N |
| `2'755'000.00, 1'206'000.00` | the same, as map.geo.admin.ch copies them (apostrophes are thousands separators) |
| `1206000, 2755000` | LV95 with northing first: swapped automatically (only for LV95, where the ranges are unambiguous) |
| `2755000,1206000,450` | E, N and the height of the point |
| `2755000,1206000,450,12.5` | E, N, height and the map north rotation (as stored in the map origin); nothing else is asked |
| `32373064, 5612345` | UTM with zone prefix (zone 32) |
| `46.98695, 9.47632` | latitude, longitude in degrees (Google Maps order) |
| `46.98695, 9.47632, 830` | latitude, longitude and height |
| `46°59'13"N 9°28'35"E` | degrees, minutes, seconds; `S`/`W` for south/west, `O` (Ost) is accepted for east; longitude first works too |
| `Valens`, `Bahnhofstrasse 1 Zürich`, `Marienplatz München` | anything with letters: a [search](#search) for places and addresses |

Map coordinates must lie more than 100 km from 0 (that is how the plugin tells them from latitude/longitude).
Decimal commas (`46,9123 9,4877`) are refused rather than guessed: use a decimal point.

## Coordinate system

**From map coordinates** the system is recognised by its range, as on export (see *Georeferencing* in the
README): LV95 → EPSG 2056; UTM with zone prefix → 5649/4647/5650; UTM without prefix → the *DefaultUTM* setting
(25832); Gauss-Krüger with zone prefix → 31466–31469. Swiss LV03 and the Austrian systems overlap; the plugin then
asks once. A fixed EPSG in `GeoBIMSettings → EPSG` always wins.

**From latitude/longitude, a search or the map** the plugin picks:

| Where | System |
|---|---|
| Switzerland's box: 45.8–47.85° N, 5.9–10.55° E | LV95 (EPSG 2056) |
| Europe (34–72° N, 16° W – 45° E) | ETRS89 / UTM of the zone (25828–25838; Norway's 32V included) |
| elsewhere, northern hemisphere | WGS84 / UTM of the zone (32601–32660) |
| southern hemisphere | not supported: enter map coordinates |

The box around Switzerland also covers places just across the border (Konstanz, Lörrach, Como, Bregenz, …). For a
project there that should be in UTM, enter the position as UTM coordinates (from a German or Austrian map service)
instead of latitude/longitude. A fixed `GeoBIMSettings → EPSG` (e.g. `25832`) would also do, but it applies to all
documents.

The plugin converts between latitude/longitude and map coordinates itself for **LV95 (2056)** and **UTM north**
(25828–25838, 32601–32660, 4647, 5649, 5650). For other systems (Gauss-Krüger, LV03, Austria) the command still
stores the map origin, so the IFC is exact, but it leaves Rhino's Location alone: set it with `EarthAnchorPoint`
for GLB and Cesium ion.

## Height

`H` is the height of the picked point **above sea level** (orthometric: LN02 in Switzerland, DHHN2016 in Germany).
The map origin stores `H0 = H − z` (z of the picked point, in metres), so a model with z = 0 at ground floor gets
`H0` = ground height, and a model whose z values are already heights above sea level gets `H0 = 0`.

Default at the height prompt:

1. the lowest visible object lies more than 100 m above 0 → the point's z (the model's z values are taken as heights
   above sea level, as in the Taminabrücke);
2. a terrain height is known → that height: a point picked on the map or found by a search is taken to be on the
   ground (swisstopo's height model in Switzerland, Mapterhorn's after a map click elsewhere). If the point had
   another height before, the command says so: *Enter puts the point on the terrain (401.5 m, Mapterhorn); it was
   at 668.2 m — type that to keep it*;
3. the document already has a map origin and the point stays within 1 km of where it was → its current height
   (nothing changes vertically). Moved further, the old height is not proposed, and the command says so;
4. otherwise **no default**: type the height (or take `ModelHeights`). A script without a height stops with
   *no height known for this point — give it as the third value*, and the map origin stays as it was.

Check the default before pressing Enter: a wrong height moves the whole model up or down. A point that is not on the
ground (a bridge deck, an upper floor) needs its height typed.

The geoBIM.app server adds the geoid to these heights (GCG2016 in Germany, EGM2008 elsewhere). Cesium ion works
with ellipsoidal heights, so `GeoBIMUploadIon` adds the geoid undulation N itself, from the same models embedded in
the plugin: GCG2016 in Germany (not for models in LV95, as the tiler does), EGM2008 elsewhere. In Europe this is
within a few centimetres of the full grids; outside Europe the plugin has EGM2008 only every 1° (up to about 2 m
off in mountains). The command prints N and the model, e.g. `geoid undulation N = 48.72 m (EGM2008 at the model)`;
its `GeoidN` option sets a document's own value instead, and `GeoidN=0` goes back to the model.

## Map north

*Map north* is the grid north of the coordinate system (LV95 or UTM), the direction the plan's grid lines point.
Give it in degrees **clockwise from +Y**: 0 when +Y points to map north, 90 when +X does. `TwoPoints` lets you pick a
line in the model that points to map north. The value is the rotation stored in the map origin
(`IfcMapConversion` XAxisAbscissa/Ordinate).

True north differs from map north by the meridian convergence: about +1.5° in eastern Switzerland, 0 at Bern, up to
±3° at the edges of a UTM zone (positive east of the zone's central meridian). The plugin computes it and turns
Rhino's Location accordingly; the command prints it. If your model's +Y points to **true** north instead (for
example because it was aligned with a web map), enter that convergence as map north angle, e.g. `1.49` at Pfäfers.

## Search

A place or address (at the position prompt or in the map window's search box) is looked up in two places at once:

| Service | Covers | Hits |
|---|---|---|
| swisstopo (api3.geo.admin.ch) | Switzerland and Liechtenstein: places, addresses, parcels, field names, stops, … | listed first, with LV95 coordinates |
| OpenStreetMap's Nominatim (nominatim.openstreetmap.org) | the rest of the world | listed after them; hits in Switzerland and Liechtenstein are dropped (swisstopo has them) |

At the command line each hit shows its source; type the number of the right one (a script takes the first):

```
  1  Lindau (ZH)   (swisstopo)
  …
  9  Lindau (Bodensee), Landkreis Lindau, Bayern, 88131, Deutschland   (OpenStreetMap)
  Places outside Switzerland: © OpenStreetMap contributors (Nominatim).
Which one <1>:
```

A swisstopo hit gives LV95 coordinates. An OpenStreetMap hit gives latitude/longitude, which the plugin converts like
a typed one (UTM outside Switzerland's box). The plugin uses Nominatim as its usage policy asks:
- it sends its own User-Agent (`geoBIM.app-Rhino/<version> (+https://geobim.app)`, nothing about you);
- it makes at most one request per second;
- it searches only when you press Enter or *Search*, never while you type;
- it caches the answers for the session;
- it credits OpenStreetMap with the hits;
- the service can be switched without a plugin update: the plugin reads `https://viewer.geobim.app/plugin/rhino.json`
  at most once an hour. `"nominatim"` there names the search URL; `null` switches the OpenStreetMap search off, and the
  command then says *the OpenStreetMap search is switched off for now*. If the file cannot be read, the built-in
  URL is used.

If one service does not answer, the other's hits are shown with a note.

## The map window

`Map` (pick) and `Show` open the same window:

- **Layers** (top right): swisstopo's national map and aerial images (Switzerland), USGS aerial images (USA) and
  BKG's TopPlusOpen (Europe and world). Outside Switzerland, swisstopo's tiles are empty, so the map switches to
  USGS in the USA and to TopPlusOpen elsewhere when you leave Switzerland. A model without a position starts on
  TopPlusOpen over central Europe.
- **Detail** depends on the place. The window opens at most two zoom levels deeper than its map has data there:

  | Where | Map | Detail down to |
  |---|---|---|
  | Switzerland | swisstopo map and aerial images | buildings, 10 cm aerial images |
  | Germany, Austria | TopPlusOpen | buildings and house numbers (zoom 18) |
  | rest of Europe | TopPlusOpen | streets and parks (zoom 16) |
  | USA | USGS aerial images (The National Map); from zoom 17 USDA NAIP | about 1.8 m per pixel (zoom 16); NAIP 0.3–0.6 m |
  | elsewhere | TopPlusOpen | main streets (zoom 13, about 15 m per pixel) |

  In the USA, from zoom 17 on, the window also asks the USGS image server for one NAIP image of the view
  (USDA, public domain); it is rendered on request and appears over the coarser tiles after 2–10 s.
  Zoomed in deeper, the map is enlarged, not empty. Elsewhere a click is good to some 10–20 m; for more, type
  latitude, longitude instead (Google Maps: right-click → copy).
- **Search box** (top left, Enter or *Search*): places and addresses, swisstopo in Switzerland and OpenStreetMap
  elsewhere (see [Search](#search)). The plugin runs the search, not the page. A hit moves the map there, on
  swisstopo's map in Switzerland and on TopPlusOpen elsewhere. When picking, the hit also sets the marker.
- **Pick**: click the point. The marker moves there, and the model's outline (its bounding box) follows it, turned by
  the current map north. The window then reads the terrain height there from Mapterhorn (open terrain models,
  the same as geoBIM.app's viewer; heights above sea level) and shows it next to the position. The command offers it
  as `Terrain`. **OK** takes the position, **Cancel** ends the command.
- **Show**: the marker at the model origin (for models in map coordinates: the model centre) and the outline of the
  visible objects at their place. Close with **Close**.

## What the command stores

| Where | Content |
|---|---|
| `doc.Strings` `geobim` / `mapOrigin` | `E0,N0,H0,rotation` of the model origin, in metres and degrees |
| `doc.Strings` `geobim` / `epsg` | the coordinate system, so the IFC export does not ask again (`GeoBIMSettings → EPSG` clears it) |
| Rhino's Location (EarthAnchorPoint) | latitude/longitude of the model origin, elevation `H0` above mean sea level, model base point 0,0,0, model north and east turned by the convergence, name *geoBIM.app* (only for LV95 and UTM) |

With the picked point (x, y, z) in metres, its position (E, N, H) and the rotation θ:

```
E0 = E − (x·cos θ − y·sin θ)
N0 = N − (x·sin θ + y·cos θ)
H0 = H − z
```

`Clear` removes only the map origin. Rhino's Location stays as it is (change it with `EarthAnchorPoint`).

## How the exports use it

| Export | Position |
|---|---|
| IFC (`GeoBIMUpload`, `GeoBIMExportIfc`) | `IfcMapConversion` (E0, N0, H0, rotation) and `IfcProjectedCRS EPSG:<code>`; the geometry stays in local coordinates |
| GLB (`GeoBIMUpload` *Format = GLB*, `GeoBIMExportGlb`) | latitude/longitude of the model centre (rounded to metres), its height above sea level, true north as heading |
| Cesium ion (`GeoBIMUploadIon`) | the IFC in east/north/up metres around the same base point (true north turned to +Y); ion places its origin at that point's latitude/longitude, height + geoid undulation N (see [Height](#height)) |

GLB and Cesium ion take their position from the map origin (or from the map coordinates of a model drawn in LV95 or
UTM) and use Rhino's Location only when the coordinate system does not convert. Geometry that already lies in map
coordinates ignores the map origin (with a note). When the map origin and Rhino's Location differ by more than
0.5 m or 0.05°, for example after the Location was changed in Rhino's own dialog, every export prints a note;
run `GeoBIMLocation` again to bring them together.

## Scripting

```
_-GeoBIMLocation _Enter 2755000,1206000,0,0          model origin at E,N, height 0, rotation 0: nothing else is asked
_-GeoBIMLocation _Enter 46.98695,9.47632,0 _Enter    latitude, longitude, height; Enter: map north is +Y
_-GeoBIMLocation 12,5,0 2683110.25,1247930.1,408.5,0  model point 12,5,0 lies at E,N,H; rotation 0
_-GeoBIMLocation _Show                               map with the model's outline
_-GeoBIMLocation _Clear                              remove the map origin
```

- Write positions without spaces in macros (spaces end an input).
- A place or address in a script takes the first hit.
- `Map` needs a person and cancels in scripts.
- Python can read the result from `doc.Strings.GetValue("geobim", "mapOrigin")` and `doc.EarthAnchorPoint`.
  Setting the `mapOrigin` string directly still works for the IFC, but it does not set Rhino's Location.
  Prefer the command.

## Messages

| Message | Meaning |
|---|---|
| `"…" is not a position — e.g. 2755000,1206000 (LV95), 46.98695, 9.47632 …` | The text is neither coordinates nor an address (decimal commas, a single number, …). |
| `nothing found for "…" (swisstopo in Switzerland, OpenStreetMap elsewhere)` | Try another spelling (with the town), latitude, longitude or `Map`. |
| `swisstopo did not answer (…), only OpenStreetMap was searched` (or the other way round) | One of the two services is unreachable; the hits come from the other. |
| `search failed — …` | Neither service answered: no connection? |
| `positions south of the equator need map coordinates` | The plugin converts northern UTM zones only. |
| `EPSG:… cannot be converted to latitude/longitude by the plugin …, so Rhino's Location is unchanged` | Gauss-Krüger, LV03 or Austria: the IFC uses the map origin; set the Location with `EarthAnchorPoint` for GLB and Cesium ion. |
| `the visible objects already lie in map coordinates …` | The model is drawn in map coordinates; it needs no map origin. `Show` displays its position. |
| `note — Rhino's Location is … m and …° away from the map origin; GLB and Cesium ion follow the map origin` | The two were changed apart. Run `GeoBIMLocation` again. |
| `GLB is placed by Rhino's Location: the plugin cannot convert EPSG:…` (or *the coordinate system … is ambiguous*) | GLB and Cesium ion fall back to Rhino's Location. |
| `Cesium ion: this model has no position yet — set it with GeoBIMLocation.` | No map origin, no map coordinates, no Location. |
| `Enter puts the point on the terrain (… m, …); it was at … m — type that to keep it` | The point had another height before. Enter takes the terrain; type the old height if the point is not on the ground. |
| `the point moves … km from where it was, so its old height (… m) is not proposed` | The model moves to another place: confirm the terrain height or type the height. |
| `no height known for this point — give it as the third value (E,N,H or latitude, longitude, H)` | A script moved the model without a height, and there is no default for it. Nothing was changed. |

## Accuracy

- **IFC**: exact. The map origin is written as given.
- **Conversion** latitude/longitude ↔ LV95/UTM: below 0.2 mm, and the meridian convergence to 0.00001°,
  compared with pyproj (`tests/geo_check`). The plugin uses the same datum step as pyproj and the geobim-tiler:
  CH1903+ → WGS 84 (EPSG:1676) and ETRS89 = WGS 84. IFC, GLB and Cesium ion therefore agree with each other to
  millimetres. EPSG gives these datum steps an accuracy of about 1 m relative to WGS 84 itself.
- **Search hits** from swisstopo are rounded to about 0.25 m. OpenStreetMap gives a point of the place: a building's
  centre, or a town's centre or town hall. A place name gives the position of its label, not a particular building.
  Check it on the map.
- **Terrain** is the ground, not a roof or a bridge deck: swisstopo's swissALTI3D in Switzerland; elsewhere
  Mapterhorn's best tile at the point (national terrain models where Mapterhorn has them, Copernicus GLO-30
  elsewhere).

## Network and privacy

Typed coordinates are converted offline. The plugin goes online only when you ask it to:

- **Search** reads the plugin's switch file from viewer.geobim.app (at most once an hour, nothing is sent) and sends the
  search text to swisstopo (api3.geo.admin.ch, SearchServer) and to OpenStreetMap's Nominatim
  (nominatim.openstreetmap.org, run by the OpenStreetMap Foundation), with the plugin's User-Agent; Nominatim also
  gets the interface language, so names come in your language where OpenStreetMap has them.
- **Terrain** sends the LV95 coordinates of the point to swisstopo's height service (Switzerland).
- **A map click** loads the Mapterhorn terrain tile of the point (tiles.mapterhorn.com), which says roughly where
  you clicked.
- **The map window** loads Leaflet 1.9.4 from unpkg.com, pinned by its SRI hash. It loads map tiles of the area
  shown from wmts.geo.admin.ch (swisstopo), sgx.geodatenzentrum.de (BKG) and, in the USA,
  basemap.nationalmap.gov and imagery.nationalmap.gov (USGS; the latter gets the view's map extent for the NAIP
  image). Its search box goes through the plugin, like the command line's.

The map data are © swisstopo and © BKG (dl-de/by-2-0), as credited in the map window; the USGS aerial images are
public domain (USGS The National Map). Places outside Switzerland
come from OpenStreetMap, © OpenStreetMap contributors (ODbL). Terrain heights after a map click: © Mapterhorn and
its sources.
