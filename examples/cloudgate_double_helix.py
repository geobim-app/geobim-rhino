#! python3
# Double helix in place of Cloud Gate (Millennium Park, Chicago) → geoBIM.app
#
#   1. python double_helix_sdf.py out/cloudgate_double_helix.txt      (numpy/scipy/scikit-image/trimesh)
#   2. Rhino.exe /nosplash /runscript="_-RunPythonScript ""<repo>\examples\cloudgate_double_helix.py"""
#
# Builds the Rhino mesh from step 1, sets the EarthAnchorPoint at Cloud Gate, saves
# out/cloudgate_double_helix.3dm and runs _-GeoBIMUpload (Rhino stays open: the plugin
# follows the conversion and opens the model in the browser). out/slug.txt remembers the
# model, so a second run replaces it under the same link. Log: out/log.txt

import os
import math
import traceback
import System
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")
MESH_TXT = os.path.join(OUT, "cloudgate_double_helix.txt")
PATH_3DM = os.path.join(OUT, "cloudgate_double_helix.3dm")
SLUG_TXT = os.path.join(OUT, "slug.txt")
LOG_PATH = os.path.join(OUT, "log.txt")
MODEL_NAME = "DoubleHelix_CloudGate"

# Cloud Gate centre and plaza level (orthometric, m), checked against the geoBIM.app basemap
LAT, LON, ELEV = 41.882684, -87.623328, 180.0
ROT_DEG = 90.0         # long axis north–south like Cloud Gate (0 = east–west, counter-clockwise positive)

os.makedirs(OUT, exist_ok=True)
LOG = open(LOG_PATH, "a", encoding="utf-8")


def log(msg):
    LOG.write(str(msg) + "\n")
    LOG.flush()


def read_mesh(path):
    m = rg.Mesh()
    with open(path) as fh:
        for line in fh:
            p = line.split()
            if p[0] == "v":
                m.Vertices.Add(float(p[1]), float(p[2]), float(p[3]))
            elif p[0] == "f":
                m.Faces.AddFace(int(p[1]), int(p[2]), int(p[3]))
    m.Vertices.CombineIdentical(True, True)
    m.Normals.ComputeNormals()
    m.Compact()
    return m


def steel_material(doc):
    mat = Rhino.DocObjects.Material()
    mat.Name = "Edelstahl spiegelpoliert"
    mat.ToPhysicallyBased()
    pbr = mat.PhysicallyBased
    pbr.BaseColor = Rhino.Display.Color4f(0.80, 0.81, 0.83, 1.0)
    pbr.Metallic = 1.0
    pbr.Roughness = 0.05
    rm = Rhino.Render.RenderMaterial.CreateBasicMaterial(mat, doc)
    doc.RenderMaterials.Add(rm)
    return rm


def layer(doc, name, color):
    idx = doc.Layers.FindByFullPath(name, -1)
    if idx >= 0:
        return idx
    l = Rhino.DocObjects.Layer()
    l.Name = name
    l.Color = color
    return doc.Layers.Add(l)


PLUGIN_ID = System.Guid("29a087ec-708c-4259-90fe-414d6c4e3880")


def slug_key():
    """Same key as GeoBimRhino Shared.SlugKey: "slug|" + server (lower case)."""
    plug = Rhino.PlugIns.PlugIn.Find(PLUGIN_ID)
    server = str(plug.GetType().GetProperty("Server").GetValue(plug))
    return "slug|" + server.lower()


def on_idle(sender, e):
    """After the upload the plugin stores the slug in the document: save it for the next run."""
    doc = sc.doc
    key = slug_key()
    slug = doc.Strings.GetValue("geobim", key)
    if slug and slug != PREVIOUS.get(key):
        Rhino.RhinoApp.Idle -= on_idle
        with open(SLUG_TXT, "w", encoding="utf-8") as fh:
            fh.write(key + "\n" + slug + "\n")
        doc.WriteFile(PATH_3DM, Rhino.FileIO.FileWriteOptions())
        log("uploaded as %s (%s)" % (slug, key))


PREVIOUS = {}


try:
    log("---- " + str(System.DateTime.Now))
    doc = sc.doc
    doc.AdjustModelUnitSystem(Rhino.UnitSystem.Meters, False)
    doc.Objects.Clear()

    mesh = read_mesh(MESH_TXT)
    if ROT_DEG:
        mesh.Transform(rg.Transform.Rotation(math.radians(ROT_DEG), rg.Vector3d.ZAxis, rg.Point3d.Origin))
    bb = mesh.GetBoundingBox(True)
    log("mesh: %d vertices, %d faces, closed=%s, bbox %.2f x %.2f x %.2f m" %
        (mesh.Vertices.Count, mesh.Faces.Count, mesh.IsClosed, bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y, bb.Max.Z - bb.Min.Z))

    C = System.Drawing.Color
    a = Rhino.DocObjects.ObjectAttributes()
    a.LayerIndex = layer(doc, "Kunstwerk", C.FromArgb(200, 203, 208))
    a.Name = "Doppel-Helix (anstelle Cloud Gate)"
    a.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
    a.ObjectColor = C.FromArgb(200, 203, 208)
    a.RenderMaterial = steel_material(doc)
    for k, v in {
        "IfcClass": "IfcBuildingElementProxy",
        "Material": "Edelstahl 1.4404, spiegelpoliert",
        "Kunstwerk": "Doppel-Helix (Entwurfsstudie)",
        "Ersetzt": "Cloud Gate, Anish Kapoor (2006)",
        "Abmessungen": "20 x 13 x 10 m, lichte Höhe 3,7 m",
        "Standort": "AT&T Plaza, Millennium Park, Chicago",
    }.items():
        a.SetUserString(k, v)
    ok_valid, why = mesh.IsValidWithLog()
    if not ok_valid:
        log("mesh invalid: " + str(why)[:500])
        mesh.Vertices.UseDoublePrecisionVertices = False
        mesh.Faces.CullDegenerateFaces()
        mesh.Vertices.CullUnused()
        mesh.Normals.ComputeNormals()
        log("after repair valid=%s" % mesh.IsValid)
    oid = doc.Objects.AddMesh(mesh, a)
    log("AddMesh: %s" % oid)

    ep = Rhino.DocObjects.EarthAnchorPoint()
    ep.EarthBasepointLatitude = LAT
    ep.EarthBasepointLongitude = LON
    ep.EarthBasepointElevation = ELEV
    ep.ModelBasePoint = rg.Point3d.Origin
    ep.ModelNorth = rg.Vector3d.YAxis
    ep.ModelEast = rg.Vector3d.XAxis
    doc.EarthAnchorPoint = ep

    # Replace the earlier upload, if any
    if os.path.exists(SLUG_TXT):
        key, slug = [s.strip() for s in open(SLUG_TXT, encoding="utf-8").read().splitlines()[:2]]
        if key == slug_key():
            doc.Strings.SetString("geobim", key, slug)
            PREVIOUS[key] = slug
            log("replacing " + slug)

    view = doc.Views.ActiveView
    view.ActiveViewport.DisplayMode = Rhino.Display.DisplayModeDescription.FindByName("Rendered")
    view.ActiveViewport.SetProjection(Rhino.Display.DefinedViewportProjection.Perspective, None, False)
    view.ActiveViewport.ZoomExtents()
    view.Redraw()
    bmp = view.CaptureToBitmap(System.Drawing.Size(1600, 1000))
    if bmp:
        bmp.Save(os.path.join(OUT, "preview.png"))

    doc.WriteFile(PATH_3DM, Rhino.FileIO.FileWriteOptions())
    Rhino.RhinoApp.Idle += on_idle
    ok = Rhino.RhinoApp.RunScript('_SelNone _-GeoBIMUpload _Enter "%s"' % MODEL_NAME, True)
    log("GeoBIMUpload started: %s" % ok)
    log("history:\n" + "\n".join(Rhino.RhinoApp.CommandHistoryWindowText.splitlines()[-15:]))
except Exception:
    log(traceback.format_exc())
finally:
    LOG.flush()
