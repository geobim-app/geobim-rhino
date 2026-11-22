"""Knotted Möbius band in the envelope of Cloud Gate (Chicago) as one seamless mesh.

The band runs along a trefoil knot standing in the x-z plane (three interlaced
loops) and turns by TWIST half turns on the way round — an odd number keeps it a
one-sided Möbius surface. The lowest loop lies flat on the plaza as the foot.
Signed distance field + marching cubes.

    python knot_sdf.py <out.txt> [half_twists]

Output format and axes as double_helix_sdf.py (x = long axis, z = up, metres).
"""
import sys
import numpy as np
from scipy.spatial import cKDTree
from skimage import measure

L, W, H = 20.0, 13.0, 10.0
BAND = 2.5           # band width (m)
THICK = 0.8          # band thickness in the middle (m)
SX, SZ, SY = 3.1, 1.7, 9.5     # trefoil scale: long axis, height, depth (m per unit) → strands ≥ 3 m apart
TWIST = int(sys.argv[2]) if len(sys.argv) > 2 else 3
STEP = 0.07
N = 8000


def centre():
    t = np.linspace(0, 2 * np.pi, N, endpoint=False)
    # Trefoil: plan of the knot drawn in x-z, crossings separated in y
    c = np.c_[SX * (np.sin(t) + 2 * np.sin(2 * t)),
              SY * -np.sin(3 * t) / 2,
              SZ * (np.cos(t) - 2 * np.cos(2 * t))]
    c[:, 2] += THICK / 2 - c[:, 2].min() - 0.15      # lowest loop sinks 15 cm: flat foot
    tan = np.roll(c, -1, 0) - np.roll(c, 1, 0)
    tan /= np.linalg.norm(tan, axis=1)[:, None]
    # Rotation-minimising frame (parallel transport), then close it with the twist
    n = np.zeros_like(c)
    n0 = np.cross(tan[0], [0.0, 0.0, 1.0])
    n[0] = n0 / np.linalg.norm(n0)
    for i in range(1, N):
        v = n[i - 1] - (n[i - 1] @ tan[i]) * tan[i]
        n[i] = v / np.linalg.norm(v)
    b = np.cross(tan, n)
    # Holonomy: angle the transported frame is off after one round
    v = n[-1] - (n[-1] @ tan[0]) * tan[0]
    v /= np.linalg.norm(v)
    hol = np.arctan2(v @ b[0], v @ n[0])
    s = np.arange(N) / N
    psi = -hol * s + TWIST * np.pi * s
    # Phase: band flat (horizontal) where the curve is lowest
    k = np.argmin(c[:, 2])
    ang = np.arctan2(-n[k, 2], b[k, 2])    # direction in (n,b) plane ⟂ to z
    psi += ang - psi[k]
    d = np.cos(psi)[:, None] * n + np.sin(psi)[:, None] * b
    e = np.cross(tan, d)
    return c, d, e


def sdf(pts, c, d, e, tree=None, k=24):
    """Minimum over the k nearest centre-line samples, so that tight bends and
    neighbouring strands cannot pick the wrong section."""
    tree = tree or cKDTree(c)
    _, idx = tree.query(pts, k=k, workers=-1)
    f = np.full(len(pts), np.inf)
    for j in range(k):
        i = idx[:, j]
        q = pts - c[i]
        u = (q * d[i]).sum(1) / (BAND / 2)
        v = (q * e[i]).sum(1) / (THICK / 2)
        w = (q * np.cross(d[i], e[i])).sum(1)        # along the curve: only the local section counts
        r = (np.abs(u) ** 2.4 + np.abs(v) ** 2) ** (1 / 2.2)
        fj = np.maximum((r - 1) * min(BAND, THICK) / 2, np.abs(w) - 0.12)
        f = np.minimum(f, fj)
    return np.maximum(f, -pts[:, 2])


def main(out):
    c, d, e = centre()
    pad = BAND / 2 + 0.5
    lo, hi = c.min(0) - pad, c.max(0) + pad
    lo[2] = -0.3
    xs, ys, zs = (np.arange(lo[k], hi[k], STEP) for k in range(3))
    g = np.stack(np.meshgrid(xs, ys, zs, indexing="ij"), -1).reshape(-1, 3)
    tree = cKDTree(c)
    vol = np.concatenate([sdf(g[j:j + 1_000_000], c, d, e, tree) for j in range(0, len(g), 1_000_000)])
    vol = vol.reshape(len(xs), len(ys), len(zs))
    v, faces, _, _ = measure.marching_cubes(vol, 0.0, spacing=(STEP,) * 3, gradient_direction="ascent")
    v += [xs[0], ys[0], zs[0]]
    import trimesh
    parts = trimesh.Trimesh(v, faces, process=True).split(only_watertight=False)   # before decimation
    try:
        import fast_simplification
        v, faces = fast_simplification.simplify(v.astype(np.float32), faces.astype(np.int64), 0.6)
    except ImportError:
        pass
    m = trimesh.Trimesh(v, faces, process=True)
    pieces = m.split(only_watertight=False)
    m = max(pieces, key=lambda p: len(p.faces))
    trimesh.repair.fix_normals(m)
    trimesh.repair.fill_holes(m)
    if m.volume < 0:
        m.invert()
    v, faces = np.asarray(m.vertices, dtype=float), np.asarray(m.faces)
    v[:, 2] = np.maximum(v[:, 2], 0.0)
    mn, mx = v.min(0), v.max(0)
    v[:, :2] -= (mn[:2] + mx[:2]) / 2
    # Self-clearance: closest approach of centre-line points far apart along the band
    tree = cKDTree(c)
    pairs = tree.query_pairs(BAND + 0.3, output_type="ndarray")
    gap = np.abs(pairs[:, 0] - pairs[:, 1])
    gap = np.minimum(gap, N - gap)
    clash = (gap > N * 0.05).sum()
    print("half twists %d, bodies %d (after decimation %d), watertight %s, triangles %d" % (TWIST, len(parts), len(pieces), m.is_watertight, len(faces)))
    print("bbox %.2f x %.2f x %.2f m, centre-line clashes < %.1f m: %d" % (*(mx - mn), BAND + 0.3, clash))
    with open(out, "w") as fh:
        for p in v:
            fh.write("v %.4f %.4f %.4f\n" % tuple(p))
        for t in faces:
            fh.write("f %d %d %d\n" % tuple(t))


if __name__ == "__main__":
    main(sys.argv[1])
