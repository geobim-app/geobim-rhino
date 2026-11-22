"""Sculpture mesh (out/*.txt, x = long axis, z = up, metres) → GLB with a polished
stainless steel PBR material (metallic 1, roughness 0.05).

    python to_glb.py <in.txt> <out.glb> [heading_deg]

glTF is Y-up: (x, y, z) → (x, z, -y). heading_deg turns the model about the
vertical axis first (counter-clockwise, like ROT_DEG in the Rhino scripts).
"""
import sys
import math
import numpy as np
import trimesh

src, dst = sys.argv[1], sys.argv[2]
rot = math.radians(float(sys.argv[3])) if len(sys.argv) > 3 else 0.0
v, f = [], []
for line in open(src):
    p = line.split()
    (v if p[0] == "v" else f).append([float(x) for x in p[1:]])
v = np.array(v)
f = np.array(f, dtype=np.int64)
c, s = math.cos(rot), math.sin(rot)
v[:, :2] = v[:, :2] @ np.array([[c, s], [-s, c]])
v = np.c_[v[:, 0], v[:, 2], -v[:, 1]]
m = trimesh.Trimesh(v, f, process=True)
m.visual = trimesh.visual.TextureVisuals(material=trimesh.visual.material.PBRMaterial(
    name="Edelstahl spiegelpoliert", baseColorFactor=[0.93, 0.94, 0.96, 1.0],
    metallicFactor=1.0, roughnessFactor=0.05, doubleSided=False))
m.export(dst, include_normals=True)   # without normals Cesium draws the model unlit
print("%s: %d triangles, %.1f MB" % (dst, len(m.faces), len(open(dst, "rb").read()) / 1e6))
