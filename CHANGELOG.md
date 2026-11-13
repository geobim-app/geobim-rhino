# Changelog

All notable changes to geoBIM.app for Rhino are documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
