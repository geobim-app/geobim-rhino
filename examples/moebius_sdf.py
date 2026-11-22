"""Möbius band in the envelope of Cloud Gate (Chicago) as one seamless mesh.

An upright loop whose lens-shaped band turns by half a turn on the way round: at the
bottom it lies flat on the plaza as the foot, at the top it stands on edge in the
loop plane. Signed distance field + marching cubes, then scaled in plan to the
20 m length.

    python moebius_sdf.py <out.txt>

Output format and axes as double_helix_sdf.py (x = long axis, z = up, metres).
"""
import sys
import numpy as np
from scipy.spatial import cKDTree
from skimage import measure

L, W, H = 20.0, 13.0, 10.0
BAND = 5.0           # band width (m)
THICK = 1.2          # band thickness in the middle (m)
A = 8.6              # half span of the upright loop along the long axis
LEAN = 5.0           # sideways lean of the loop's top (uses the depth of the envelope)
STEP = 0.07
N = 4000


def centre():
    """Upright loop in the x-z plane. Bottom: band lies flat on the plaza (foot);
    half a twist on the way round, so at the top it stands upright in the loop plane."""
    phi = np.linspace(-np.pi / 2, 1.5 * np.pi, N, endpoint=False)
    zb, zt = THICK / 2, H - BAND / 2
    zc, cz = (zb + zt) / 2, (zt - zb) / 2
    c = np.c_[A * np.cos(phi), LEAN * (1 + np.sin(phi)) / 2 - LEAN / 2, zc + cz * np.sin(phi)]
    t = np.gradient(c, axis=0)
    t /= np.linalg.norm(t, axis=1)[:, None]
    inward = np.array([0.0, 0.0, zc]) - c
    inward -= (inward * t).sum(1)[:, None] * t
    inward /= np.linalg.norm(inward, axis=1)[:, None]
    side = np.cross(t, inward)
    psi = (phi + np.pi / 2) / 2          # 0 at the foot, 90° at the top, 180° back at the foot → Möbius
    d = np.cos(psi)[:, None] * side + np.sin(psi)[:, None] * inward
    e = np.cross(t, d)
    return c, d, e


def sdf(pts):
    c, d, e = centre()
    _, i = cKDTree(c).query(pts, workers=-1)
    q = pts - c[i]
    u = (q * d[i]).sum(1) / (BAND / 2)
    v = (q * e[i]).sum(1) / (THICK / 2)
    # Lens: thicker in the middle, thinning to rounded edges (superellipse-ish)
    r = (np.abs(u) ** 2.4 + np.abs(v) ** 2) ** (1 / 2.2)
    f = (r - 1) * min(BAND, THICK) / 2
    return np.maximum(f, -pts[:, 2])     # flat at plaza level


def main(out):
    pad = 1.0
    xs = np.arange(-A - BAND / 2 - pad, A + BAND / 2 + pad, STEP)
    ys = np.arange(-LEAN - BAND / 2 - pad, LEAN + BAND / 2 + pad, STEP)
    zs = np.arange(-0.3, H + pad, STEP)
    g = np.stack(np.meshgrid(xs, ys, zs, indexing="ij"), -1).reshape(-1, 3)
    vol = sdf(g).reshape(len(xs), len(ys), len(zs))
    v, faces, _, _ = measure.marching_cubes(vol, 0.0, spacing=(STEP,) * 3, gradient_direction="ascent")
    v += [xs[0], ys[0], zs[0]]
    try:
        import fast_simplification
        v, faces = fast_simplification.simplify(v.astype(np.float32), faces.astype(np.int64), 0.75)
    except ImportError:
        pass
    import trimesh
    m = trimesh.Trimesh(v, faces, process=True)
    m = max(m.split(only_watertight=False), key=lambda b: len(b.faces))
    trimesh.repair.fix_normals(m)
    trimesh.repair.fill_holes(m)
    if m.volume < 0:
        m.invert()
    v, faces = np.asarray(m.vertices, dtype=float), np.asarray(m.faces)
    # Fit the Cloud Gate envelope (centre of footprint at the origin, base at 0)
    v[:, 2] = np.maximum(v[:, 2], 0.0)
    mn, mx = v.min(0), v.max(0)
    v[:, :2] -= (mn[:2] + mx[:2]) / 2
    k = L / (mx - mn)[0]
    v[:, :2] *= k                         # uniform in plan: no distortion of the band
    print("watertight %s, triangles %d" % (m.is_watertight, len(faces)))
    print("bbox %.2f x %.2f x %.2f m, plan scale %s" % (*(v.max(0) - v.min(0)), np.round(k, 2)))
    with open(out, "w") as fh:
        for p in v:
            fh.write("v %.4f %.4f %.4f\n" % tuple(p))
        for t in faces:
            fh.write("f %d %d %d\n" % tuple(t))


if __name__ == "__main__":
    main(sys.argv[1])
