"""Double helix in the envelope of Cloud Gate (Chicago) as one seamless mesh.

Builds a signed distance field — two tapering strands winding about the 20 m long
axis, three "base pair" rungs, two flared feet, all blended with a smooth minimum
(the seamless, liquid look of polished stainless steel) — and extracts the surface
with marching cubes.

    python double_helix_sdf.py <out.txt>

Output: plain text, "v x y z" and "f a b c" (0-based) in metres; x = long axis,
y = depth, z = up, origin = centre of the footprint at plaza level.
Needs numpy, scipy, scikit-image, trimesh (and optionally fast_simplification).
"""
import sys
import math
import numpy as np
from scipy.spatial import cKDTree
from skimage import measure

L, W, H = 20.0, 13.0, 10.0      # Cloud Gate envelope (length, depth, height)
R0, R_END = 1.10, 0.65          # strand radius in the middle / at the tips
GAP = 0.8                       # lowest strand point above the plaza; the feet bridge it
ZC = (H + GAP) / 2              # helix axis height
AZ = H - ZC - R0                # vertical helix radius  → strands reach GAP and 10 m
AY = W / 2 - R0                 # lateral helix radius   → 13 m depth
FOOT_X = 6.0                    # each strand comes down to a foot once, at x = ∓6
HALF_TURN = 2 * FOOT_X          # A at the bottom at -6, B at the bottom at +6
STEP = 0.07                     # voxel size (m)
K_STRAND, K_RUNG, K_FOOT = 0.9, 0.7, 1.6   # smooth-min blend radii


def radius(x):
    """Constant in the middle, smooth (cosine) taper to the tips."""
    t = np.clip((np.abs(x) - 6.5) / (L / 2 - R_END - 6.5), 0, 1)
    return R_END + (R0 - R_END) * 0.5 * (1 + np.cos(np.pi * t))


def strand(phase, n=3000):
    x = np.linspace(-L / 2 + R_END, L / 2 - R_END, n)
    th = -np.pi / 2 + np.pi * (x + FOOT_X) / HALF_TURN + phase
    return np.c_[x, AY * np.cos(th), ZC + AZ * np.sin(th)], radius(x)


def tube_sdf(pts, centre, r):
    d, i = cKDTree(centre).query(pts, workers=-1)
    return d - r[i]


def smin(a, b, k):
    h = np.clip(0.5 + 0.5 * (b - a) / k, 0, 1)
    return b * (1 - h) + a * h - k * h * (1 - h)


def segment(p, q, n=400):
    t = np.linspace(0, 1, n)[:, None]
    return p + (q - p) * t


def build(pts):
    a, ra = strand(0.0)
    b, rb = strand(np.pi)
    f = smin(tube_sdf(pts, a, ra), tube_sdf(pts, b, rb), K_STRAND)
    # Rungs between the strands at the same x (like base pairs), slimmer in the middle
    for x in (-3.0, 0.0, 3.0):
        ia, ib = np.argmin(np.abs(a[:, 0] - x)), np.argmin(np.abs(b[:, 0] - x))
        seg = segment(a[ia], b[ib])
        t = np.linspace(0, 1, len(seg))
        r = 0.42 + 0.25 * (2 * t - 1) ** 2
        f = smin(f, tube_sdf(pts, seg, r), K_RUNG)
    # Feet: flat ellipsoids on the ground under the two contact points
    for x in (-FOOT_X, FOOT_X):
        q = (pts - [x, 0, 0]) / [2.6, 2.2, 1.1]
        e = (np.linalg.norm(q, axis=1) - 1) * 0.6
        f = smin(f, e, K_FOOT)
    # Cut flat at plaza level
    return np.maximum(f, -pts[:, 2])


def main(out):
    xs = np.arange(-L / 2 - 0.5, L / 2 + 0.5, STEP)
    ys = np.arange(-W / 2 - 0.5, W / 2 + 0.5, STEP)
    zs = np.arange(-0.3, H + 0.5, STEP)
    g = np.stack(np.meshgrid(xs, ys, zs, indexing="ij"), -1).reshape(-1, 3)
    vol = build(g).reshape(len(xs), len(ys), len(zs))
    v, faces, _, _ = measure.marching_cubes(vol, 0.0, spacing=(STEP,) * 3, gradient_direction="ascent")
    v += [xs[0], ys[0], zs[0]]
    try:
        import fast_simplification
        v, faces = fast_simplification.simplify(v.astype(np.float32), faces.astype(np.int64), 0.75)
    except ImportError:
        pass
    # Clean up: largest body only, consistent outward normals, close pinholes
    import trimesh
    m = trimesh.Trimesh(v, faces, process=True)
    m = max(m.split(only_watertight=False), key=lambda b: len(b.faces))
    trimesh.repair.fix_normals(m)
    trimesh.repair.fill_holes(m)
    if m.volume < 0:
        m.invert()
    print("watertight %s, volume %.1f m3, area %.1f m2" % (m.is_watertight, m.volume, m.area))
    v, faces = np.asarray(m.vertices), np.asarray(m.faces)
    v[:, 2] = np.maximum(v[:, 2], 0.0)
    mn, mx = v.min(0), v.max(0)
    print("vertices %d, triangles %d" % (len(v), len(faces)))
    print("bbox %.2f x %.2f x %.2f m (z %.2f..%.2f)" % (*(mx - mn), mn[2], mx[2]))
    # Clear height at the centre line (x = 0, y = ±AY): lowest surface point above y = ±AY
    for yy in (AY, -AY):
        col = v[(np.abs(v[:, 0]) < 0.3) & (np.abs(v[:, 1] - yy) < 0.3) & (v[:, 2] < ZC)]
        if len(col):
            print("clear height under strand at x=0, y=%+.1f: %.2f m" % (yy, col[:, 2].min()))
    with open(out, "w") as fh:
        for p in v:
            fh.write("v %.4f %.4f %.4f\n" % tuple(p))
        for t in faces:
            fh.write("f %d %d %d\n" % tuple(t))


if __name__ == "__main__":
    main(sys.argv[1])
