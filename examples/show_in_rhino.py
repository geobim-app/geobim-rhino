#! python3
# Sculpture variants side by side in Rhino for viewing — no upload.
#   Rhino.exe /nosplash /runscript="_-RunPythonScript ""<repo>\examples\show_in_rhino.py"""
# Meshes from moebius_sdf.py / knot_sdf.py in out/. The first variant sits at the
# Cloud Gate position (EarthAnchorPoint as in cloudgate_double_helix.py), the others
# next to it along +X (east). Saves out/varianten.3dm.

import os
import math
import System
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")
LAT, LON, ELEV, ROT_DEG = 41.882684, -87.623328, 180.0, 90.0
SPACING = 30.0
VARIANTS = [
    ("Knoten-Möbiusband (3 halbe Drehungen)", "knot.txt"),
    ("Möbiusband", "moebius.txt"),
]


def read_mesh(path):
    m = rg.Mesh()
    with open(path) as fh:
        for line in fh:
            p = line.split()
            if p[0] == "v":
                m.Vertices.Add(float(p[1]), float(p[2]), float(p[3]))
            elif p[0] == "f":
                m.Faces.AddFace(int(p[1]), int(p[2]), int(p[3]))
    m.Faces.CullDegenerateFaces()
    m.Vertices.CombineIdentical(True, True)
    m.Vertices.CullUnused()
    m.Normals.ComputeNormals()
    m.Compact()
    return m


doc = sc.doc
doc.AdjustModelUnitSystem(Rhino.UnitSystem.Meters, False)

mat = Rhino.DocObjects.Material()
mat.Name = "Edelstahl spiegelpoliert"
mat.ToPhysicallyBased()
mat.PhysicallyBased.BaseColor = Rhino.Display.Color4f(0.80, 0.81, 0.83, 1.0)
mat.PhysicallyBased.Metallic = 1.0
mat.PhysicallyBased.Roughness = 0.05
rm = Rhino.Render.RenderMaterial.CreateBasicMaterial(mat, doc)
doc.RenderMaterials.Add(rm)

C = System.Drawing.Color
for k, (name, fname) in enumerate(VARIANTS):
    path = os.path.join(OUT, fname)
    if not os.path.exists(path):
        continue
    mesh = read_mesh(path)
    mesh.Transform(rg.Transform.Rotation(math.radians(ROT_DEG), rg.Vector3d.ZAxis, rg.Point3d.Origin))
    mesh.Translate(rg.Vector3d(k * SPACING, 0, 0))
    layer = Rhino.DocObjects.Layer()
    layer.Name = name
    layer.Color = C.FromArgb(200, 203, 208)
    a = Rhino.DocObjects.ObjectAttributes()
    a.LayerIndex = doc.Layers.Add(layer)
    a.Name = name
    a.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
    a.ObjectColor = C.FromArgb(200, 203, 208)
    a.RenderMaterial = rm
    a.SetUserString("IfcClass", "IfcBuildingElementProxy")
    a.SetUserString("Material", "Edelstahl 1.4404, spiegelpoliert")
    a.SetUserString("Ersetzt", "Cloud Gate, Anish Kapoor (2006)")
    doc.Objects.AddMesh(mesh, a)

ep = Rhino.DocObjects.EarthAnchorPoint()
ep.EarthBasepointLatitude, ep.EarthBasepointLongitude, ep.EarthBasepointElevation = LAT, LON, ELEV
ep.ModelBasePoint = rg.Point3d.Origin
ep.ModelNorth, ep.ModelEast = rg.Vector3d.YAxis, rg.Vector3d.XAxis
doc.EarthAnchorPoint = ep

view = doc.Views.Find("Perspective", False) or doc.Views.ActiveView
doc.Views.ActiveView = view
view.Maximized = True
vp = view.ActiveViewport
vp.DisplayMode = Rhino.Display.DisplayModeDescription.FindByName("Raytraced") or \
    Rhino.Display.DisplayModeDescription.FindByName("Rendered")
mid = (len(VARIANTS) - 1) * SPACING / 2
vp.SetCameraLocations(rg.Point3d(mid, 0, 4.5), rg.Point3d(mid - 30, -45, 14))
doc.WriteFile(os.path.join(OUT, "varianten.3dm"), Rhino.FileIO.FileWriteOptions())
doc.Modified = False

_ticks = [0]


def on_idle(sender, e):
    """Zoom once the main window has its final size; save a preview image."""
    _ticks[0] += 1
    if _ticks[0] < 20:
        return
    Rhino.RhinoApp.Idle -= on_idle
    v = sc.doc.Views.ActiveView
    v.ActiveViewport.ZoomExtents()
    v.ActiveViewport.Magnify(0.9, False)
    v.Redraw()
    bmp = v.CaptureToBitmap(System.Drawing.Size(1800, 1000))
    if bmp:
        bmp.Save(os.path.join(OUT, "varianten.png"))


Rhino.RhinoApp.Idle += on_idle
