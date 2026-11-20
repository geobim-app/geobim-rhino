#! python3
# geoBIM.app for Rhino — test models + IFC export (run inside Rhino 8)
#
#   Rhino.exe /nosplash /runscript="_-RunPythonScript \"<repo>\tests\make_test_models.py\" _-Exit _No"
#
# Loads the built plugin (src/GeoBimRhino/bin/Release/net7.0/GeoBimRhino.rhp),
# builds the same small model several times — in UTM map coordinates, with an
# EarthAnchorPoint, without georeferencing, in LV95 map coordinates, and local
# with a map origin (with and without rotation) — saves each as
# tests/out/<case>.3dm and exports tests/out/<case>.ifc with _-GeoBIMExportIfc.
# GeoBIMLocation: a local model placed by latitude/longitude ("location"), its GLB
# placement and Cesium ion IFC, and the ion IFC of the LV95 model ("lv95_ion"); what
# Rhino stored and printed goes to tests/out/location.json.
# Log: tests/out/log.txt. Then check the IFCs: python tests/check_exports.py

import os
import json
import math
import re
import traceback
import System
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
OUT = os.path.join(HERE, "out")
RHP = os.path.join(REPO, "src", "GeoBimRhino", "bin", "Release", "net7.0", "GeoBimRhino.rhp")
os.makedirs(OUT, exist_ok=True)
LOG = open(os.path.join(OUT, "log.txt"), "w", encoding="utf-8")


def log(msg):
    LOG.write(str(msg) + "\n")
    LOG.flush()


def layer(doc, path, color):
    idx = doc.Layers.FindByFullPath(path, -1)
    if idx >= 0:
        return idx
    parent_id = System.Guid.Empty
    parts = path.split("::")
    if len(parts) > 1:
        parent_id = doc.Layers[layer(doc, "::".join(parts[:-1]), color)].Id
    l = Rhino.DocObjects.Layer()
    l.Name = parts[-1]
    l.Color = color
    l.ParentLayerId = parent_id
    return doc.Layers.Add(l)


def attrs(doc, layer_path, color, name=None, user=None, obj_color=None):
    a = Rhino.DocObjects.ObjectAttributes()
    a.LayerIndex = layer(doc, layer_path, color)
    if name:
        a.Name = name
    if obj_color is not None:
        a.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
        a.ObjectColor = obj_color
    for k, v in (user or {}).items():
        a.SetUserString(k, v)
    return a


def box(x0, y0, z0, x1, y1, z1, offset):
    bb = rg.BoundingBox(rg.Point3d(x0, y0, z0) + offset, rg.Point3d(x1, y1, z1) + offset)
    return rg.Brep.CreateFromBox(bb)


def build(doc, offset, case):
    C = System.Drawing.Color
    tol = doc.ModelAbsoluteTolerance
    doc.Objects.AddBrep(box(0, 0, 0, 12, 8, 0.3, offset),
                        attrs(doc, "Bauteile::Platten", C.LightGray, "Bodenplatte",
                              {"IfcClass": "IfcSlab", "Material": "C30/37", "Bauteil": "Bodenplatte"}))
    doc.Objects.AddBrep(box(0, 7.7, 0.3, 12, 8, 3.3, offset),
                        attrs(doc, "Bauteile::IfcWall", C.Wheat, "Wand Nord"))
    # Wall with a round opening (boolean difference)
    wall = box(0, 0, 0.3, 0.3, 7.7, 3.3, offset)
    cyl = rg.Cylinder(rg.Circle(rg.Plane(rg.Point3d(-1, 4, 1.8) + offset, rg.Vector3d.XAxis), 0.6), 3).ToBrep(True, True)
    diff = rg.Brep.CreateBooleanDifference(wall, cyl, tol)
    doc.Objects.AddBrep(diff[0] if diff else wall,
                        attrs(doc, "Bauteile::IfcWall", C.Wheat, "Wand West mit Öffnung", {"Brandschutz": "F90"}))
    # SubD "Brücke Süd" (umlauts in names) and a mesh terrain patch
    sphere = rg.Mesh.CreateFromSphere(rg.Sphere(rg.Point3d(6, -6, 2) + offset, 2), 8, 8)
    subd = rg.SubD.CreateFromMesh(sphere)
    doc.Objects.AddSubD(subd, attrs(doc, "Gestaltung", C.SteelBlue, "Brücke Süd"))
    patch = rg.Mesh.CreateFromPlane(rg.Plane(rg.Point3d(6, 4, -0.05) + offset, rg.Vector3d.ZAxis),
                                    rg.Interval(-10, 10), rg.Interval(-10, 10), 10, 10)
    doc.Objects.AddMesh(patch, attrs(doc, "Gelände", C.OliveDrab, None, None))
    # Block with two components, inserted twice (one instance as column)
    comp = [box(0, 0, 0, 0.4, 0.4, 3, rg.Vector3d.Zero), box(-0.1, -0.1, 3, 0.5, 0.5, 3.2, rg.Vector3d.Zero)]
    ca = [attrs(doc, "Bauteile::Stützen", C.Gray, "Schaft"), attrs(doc, "Bauteile::Stützen", C.Gray, "Kopf", None, C.Red)]
    idx = doc.InstanceDefinitions.Add("Stütze 40x40 " + case, "", rg.Point3d.Origin, comp, ca)
    for i, x in enumerate((4, 8)):
        xf = rg.Transform.Translation(rg.Vector3d(x, 4, 0.3) + offset)
        doc.Objects.AddInstanceObject(idx, xf, attrs(doc, "Bauteile::Stützen", C.Gray, "Stütze %d" % (i + 1),
                                                      {"IfcClass": "IfcColumn"} if i == 0 else None))
    # A curve: must be skipped
    doc.Objects.AddCurve(rg.LineCurve(rg.Point3d(0, 0, 0) + offset, rg.Point3d(12, 0, 0) + offset),
                         attrs(doc, "Achsen", C.Black))


def run_ion_case():
    """Cesium ion IFC without uploading: the anchor model, written by GeoBIMUploadIon Upload=No"""
    doc = sc.doc
    tmp = os.path.join(System.IO.Path.GetTempPath(), "geobim-rhino", "ion_ion.ifc")
    if os.path.exists(tmp):
        os.remove(tmp)
    doc.Strings.Delete("geobim", "mapOrigin")
    ok = Rhino.RhinoApp.RunScript('_SelNone _-GeoBIMUploadIon _Enter GeoidN=47.5 Upload=No "ion"', False)
    dst = os.path.join(OUT, "ion.ifc")
    if os.path.exists(dst):
        os.remove(dst)
    if os.path.exists(tmp):
        os.replace(tmp, dst)
    log("ion: RunScript=%s, ifc exists=%s" % (ok, os.path.exists(dst)))


RESULTS = {}


def history_mark():
    return len(Rhino.RhinoApp.CommandHistoryWindowText)


def history_since(mark):
    return Rhino.RhinoApp.CommandHistoryWindowText[mark:]


def numbers_after(text, key):
    """lon/lat/height printed by the plugin, e.g. 'origin lon 9.47, lat 46.98, 830 m'"""
    m = re.search(key + r" lon ([-0-9.]+), lat ([-0-9.]+), ([-0-9.]+) m", text)
    return [float(v) for v in m.groups()] if m else None


def ion_ifc(name, geoid="48.7"):
    """GeoBIMUploadIon with Upload=No: the ion IFC (nothing is sent) and the printed origin;
    GeoidN=0 takes the undulation from the plugin's geoid model"""
    tmp = os.path.join(System.IO.Path.GetTempPath(), "geobim-rhino", name + "_ion.ifc")
    if os.path.exists(tmp):
        os.remove(tmp)
    mark = history_mark()
    ok = Rhino.RhinoApp.RunScript('_SelNone _-GeoBIMUploadIon _Enter GeoidN=%s Upload=No "%s"' % (geoid, name), False)
    text = history_since(mark)
    dst = os.path.join(OUT, name + ".ifc")
    if os.path.exists(dst):
        os.remove(dst)
    if os.path.exists(tmp):
        os.replace(tmp, dst)
    log("%s: RunScript=%s, ifc exists=%s" % (name, ok, os.path.exists(dst)))
    RESULTS[name] = {"origin": numbers_after(text, "origin")}
    log(text.strip())


def run_location_case():
    """A local model placed with GeoBIMLocation from latitude/longitude (LV95 2755000/1206000, Pfäfers)"""
    doc = sc.doc
    doc.Objects.Clear()
    doc.Strings.Delete("geobim", "mapOrigin")
    doc.Strings.Delete("geobim", "epsg")
    doc.EarthAnchorPoint = Rhino.DocObjects.EarthAnchorPoint()
    build(doc, rg.Vector3d(250, -40, 400), "location")
    mark = history_mark()
    ok = Rhino.RhinoApp.RunScript("_-GeoBIMLocation _Enter 46.9869521994,9.4763209037,0 _Enter", False)
    log("location: GeoBIMLocation RunScript=%s" % ok)
    log(history_since(mark).strip())
    ap = doc.EarthAnchorPoint
    RESULTS["location"] = {
        "mapOrigin": doc.Strings.GetValue("geobim", "mapOrigin"),
        "epsg": doc.Strings.GetValue("geobim", "epsg"),
        "lat": ap.EarthBasepointLatitude, "lon": ap.EarthBasepointLongitude, "elevation": ap.EarthBasepointElevation,
        "zero": str(ap.EarthBasepointElevationZero),
        "base": [ap.ModelBasePoint.X, ap.ModelBasePoint.Y, ap.ModelBasePoint.Z],
        "north": [ap.ModelNorth.X, ap.ModelNorth.Y, ap.ModelNorth.Z], "east": [ap.ModelEast.X, ap.ModelEast.Y, ap.ModelEast.Z],
    }
    doc.WriteFile(os.path.join(OUT, "location.3dm"), Rhino.FileIO.FileWriteOptions())
    ifc = os.path.join(OUT, "location.ifc")
    if os.path.exists(ifc):
        os.remove(ifc)
    ok = Rhino.RhinoApp.RunScript('_SelNone _-GeoBIMExportIfc _Enter "%s"' % ifc, False)
    log("location: IFC RunScript=%s, exists=%s" % (ok, os.path.exists(ifc)))
    glb = os.path.join(OUT, "location.glb")
    if os.path.exists(glb):
        os.remove(glb)
    mark = history_mark()
    ok = Rhino.RhinoApp.RunScript('_SelNone _-GeoBIMExportGlb _Enter "%s"' % glb, False)
    text = history_since(mark)
    m = re.search(r"placement lon ([-0-9.]+), lat ([-0-9.]+), ([-0-9.]+) m above sea level, heading ([-0-9.]+)", text)
    RESULTS["location_glb"] = {"placement": [float(v) for v in m.groups()] if m else None}
    log("location: GLB RunScript=%s, exists=%s; %s" % (ok, os.path.exists(glb), text.strip()))
    ion_ifc("location_ion")
    ion_ifc("location_auto", "0")   # geoid undulation from the plugin's EGM2008 grid


def run_case(name, offset, anchor, map_origin=None):
    doc = sc.doc
    doc.Objects.Clear()
    # map origin of local coordinates (GeoBIMLocation), kept in the document
    if map_origin:
        doc.Strings.SetString("geobim", "mapOrigin", map_origin)
    else:
        doc.Strings.Delete("geobim", "mapOrigin")
    ep = Rhino.DocObjects.EarthAnchorPoint()
    if anchor:
        ep.EarthBasepointLatitude = 50.6745
        ep.EarthBasepointLongitude = 7.2035
        ep.EarthBasepointElevation = 120.0
        ep.ModelBasePoint = rg.Point3d(6, 4, 0)
        a = math.radians(30)   # model north turned 30° clockwise from +Y
        ep.ModelNorth = rg.Vector3d(math.sin(a), math.cos(a), 0)
        ep.ModelEast = rg.Vector3d(math.cos(a), -math.sin(a), 0)
    doc.EarthAnchorPoint = ep
    build(doc, offset, name)
    path3dm = os.path.join(OUT, name + ".3dm")
    doc.WriteFile(path3dm, Rhino.FileIO.FileWriteOptions())
    ifc = os.path.join(OUT, name + ".ifc")
    if os.path.exists(ifc):
        os.remove(ifc)
    ok = Rhino.RhinoApp.RunScript('_SelNone _-GeoBIMExportIfc _Enter "%s"' % ifc, False)
    log("%s: RunScript=%s, ifc exists=%s, size=%s" % (name, ok, os.path.exists(ifc),
                                                       os.path.getsize(ifc) if os.path.exists(ifc) else 0))


try:
    log("Rhino " + str(Rhino.RhinoApp.Version))
    res, pid = Rhino.PlugIns.PlugIn.LoadPlugIn(RHP)
    log("LoadPlugIn: %s %s" % (res, pid))
    sc.doc.AdjustModelUnitSystem(Rhino.UnitSystem.Meters, False)
    run_case("utm", rg.Vector3d(373000, 5615000, 60), False)
    run_case("anchor", rg.Vector3d.Zero, True)
    run_ion_case()   # same document as "anchor"
    run_case("local", rg.Vector3d(250, -40, 0), False)
    # Swiss LV95: the same model once in real map coordinates, once local with a
    # map origin (must give the same absolute coordinates), once with a rotation
    run_case("lv95", rg.Vector3d(2600250, 1199960, 400), False)
    ion_ifc("lv95_ion")   # map coordinates: placed without any Location
    run_case("origin", rg.Vector3d(250, -40, 400), False, "2600000,1200000,0,0")
    run_case("origin_rot", rg.Vector3d(250, -40, 0), True, "2600000,1200000,400,30")
    run_location_case()
    with open(os.path.join(OUT, "location.json"), "w", encoding="utf-8") as fj:
        json.dump(RESULTS, fj, indent=1)
    log("done")
except Exception:
    log(traceback.format_exc())
finally:
    LOG.close()
    # Close Rhino without the "save changes?" question
    sc.doc.Modified = False
    Rhino.RhinoApp.Exit(True)
