#! python3
# geoBIM.app for Rhino — test models + IFC export (run inside Rhino 8)
#
#   Rhino.exe /nosplash /runscript="_-RunPythonScript \"<repo>\tests\make_test_models.py\" _-Exit _No"
#
# Loads the built plugin (src/GeoBimRhino/bin/Release/net7.0/GeoBimRhino.rhp),
# builds the same small model three times — in UTM map coordinates, with an
# EarthAnchorPoint, without georeferencing — saves each as tests/out/<case>.3dm
# and exports tests/out/<case>.ifc with _-GeoBIMExportIfc. Log: tests/out/log.txt

import os
import math
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


def run_case(name, offset, anchor):
    doc = sc.doc
    doc.Objects.Clear()
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
    run_case("local", rg.Vector3d(250, -40, 0), False)
    log("done")
except Exception:
    log(traceback.format_exc())
finally:
    LOG.close()
    # Close Rhino without the "save changes?" question
    sc.doc.Modified = False
    Rhino.RhinoApp.Exit(True)
