#! python3
# Knotted Möbius band (out/knot.txt from knot_sdf.py) at Cloud Gate → geoBIM.app as GLB
#   Rhino.exe /nosplash /runscript="_-RunPythonScript ""<repo>\examples\upload_knot_glb.py"""
# Polished stainless steel as a Rhino PBR material; the plugin writes it into the
# GLB (metallic 1, roughness 0.05), so geoBIM.app shows it mirrored. Writes
# out/knot_plugin.glb (_-GeoBIMExportGlb) and uploads with _-GeoBIMUpload Format=GLB;
# Rhino stays open while the upload runs. Log: out/log_glb.txt

import os
import math
import traceback
import System
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")
LAT, LON, ELEV, ROT_DEG = 41.882684, -87.623328, 180.0, 90.0
NAME = "Knoten_Moebius_CloudGate"
LOG = open(os.path.join(OUT, "log_glb.txt"), "w", encoding="utf-8")


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
    m.Faces.CullDegenerateFaces()
    m.Vertices.CombineIdentical(True, True)
    m.Vertices.CullUnused()
    m.Normals.ComputeNormals()
    m.Compact()
    return m


try:
    doc = sc.doc
    doc.AdjustModelUnitSystem(Rhino.UnitSystem.Meters, False)
    mesh = read_mesh(os.path.join(OUT, "knot.txt"))
    mesh.Transform(rg.Transform.Rotation(math.radians(ROT_DEG), rg.Vector3d.ZAxis, rg.Point3d.Origin))

    mat = Rhino.DocObjects.Material()
    mat.Name = "Edelstahl spiegelpoliert"
    mat.ToPhysicallyBased()
    mat.PhysicallyBased.BaseColor = Rhino.Display.Color4f(0.93, 0.94, 0.96, 1.0)
    mat.PhysicallyBased.Metallic = 1.0
    mat.PhysicallyBased.Roughness = 0.05
    rm = Rhino.Render.RenderMaterial.FromMaterial(mat, doc)   # a real "Physically Based" render material
    doc.RenderMaterials.Add(rm)

    C = System.Drawing.Color
    a = Rhino.DocObjects.ObjectAttributes()
    a.Name = "Knoten-Möbiusband (anstelle Cloud Gate)"
    a.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
    a.ObjectColor = C.FromArgb(200, 203, 208)
    a.RenderMaterial = rm
    a.SetUserString("Material", "Edelstahl 1.4404, spiegelpoliert")
    a.SetUserString("Ersetzt", "Cloud Gate, Anish Kapoor (2006)")
    doc.Objects.AddMesh(mesh, a)

    ep = Rhino.DocObjects.EarthAnchorPoint()
    ep.EarthBasepointLatitude, ep.EarthBasepointLongitude, ep.EarthBasepointElevation = LAT, LON, ELEV
    ep.ModelBasePoint = rg.Point3d.Origin
    ep.ModelNorth, ep.ModelEast = rg.Vector3d.YAxis, rg.Vector3d.XAxis
    doc.EarthAnchorPoint = ep

    obj = list(doc.Objects)[0]
    pm = obj.GetRenderMaterial(True).ConvertToPhysicallyBased(Rhino.Render.RenderTexture.TextureGeneration.Skip)
    log("render material as PBR: metallic %.2f, roughness %.2f" % (pm.Metallic, pm.Roughness))

    glb = os.path.join(OUT, "knot_plugin.glb")
    if os.path.exists(glb):
        os.remove(glb)
    ok = Rhino.RhinoApp.RunScript('_SelNone _-GeoBIMExportGlb _Enter "%s"' % glb, True)
    log("export: %s, exists=%s, size=%s" % (ok, os.path.exists(glb), os.path.getsize(glb) if os.path.exists(glb) else 0))

    ok = Rhino.RhinoApp.RunScript('_SelNone _-GeoBIMUpload _Enter Format=GLB "%s"' % NAME, True)
    log("upload started: %s" % ok)
except Exception:
    log(traceback.format_exc())
finally:
    LOG.flush()


def on_idle(sender, e):
    """Log the plugin's last lines once the upload is done or has failed."""
    lines = [l for l in Rhino.RhinoApp.CommandHistoryWindowText.splitlines() if l.startswith("geoBIM.app:")]
    if any(("ready —" in l) or ("failed" in l) for l in lines):
        Rhino.RhinoApp.Idle -= on_idle
        log("result:\n" + "\n".join(lines[-6:]))


Rhino.RhinoApp.Idle += on_idle
