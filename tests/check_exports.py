# geoBIM.app for Rhino: checks the IFCs written by tests/make_test_models.py
#
#   python tests/check_exports.py            (system Python with ifcopenshell 0.9)
#
# For every tests/out/<case>.ifc: schema validation, element count and the
# georeferencing. Then the map-origin cases against each other:
#   lv95       model in real LV95 map coordinates (E 2 600 250, N 1 199 960, z + 400)
#   origin     the same model in local coordinates + map origin 2600000,1200000,0,0
#   origin_rot local + map origin 2600000,1200000,400 rotated 30° (and an EarthAnchorPoint,
#              which the map origin must win over)
# lv95 and origin must give the same absolute coordinates; origin_rot must be
# origin's local geometry turned 30° about the map origin. Exit code 1 on failure.

import math
import os
import sys

import ifcopenshell
import ifcopenshell.util.placement
import ifcopenshell.validate
import numpy as np

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
CASES = ["utm", "anchor", "local", "lv95", "origin", "origin_rot"]
TOL = 2e-5   # the writer rounds vertices to 5 decimals

failures = []


def check(cond, msg):
    print(("  ok   " if cond else "  FAIL ") + msg)
    if not cond:
        failures.append(msg)


def georef(f):
    mc = f.by_type("IfcMapConversion")
    if not mc:
        return None
    m = mc[0]
    return {"crs": m.TargetCRS.Name, "e": m.Eastings, "n": m.Northings, "h": m.OrthogonalHeight,
            "a": m.XAxisAbscissa if m.XAxisAbscissa is not None else 1.0,
            "b": m.XAxisOrdinate if m.XAxisOrdinate is not None else 0.0,
            "s": m.Scale if m.Scale is not None else 1.0}


def local_vertices(f):
    """{element name + id order: Nx3 array of vertices in project coordinates}"""
    out = []
    for el in f.by_type("IfcProduct"):
        if el.is_a("IfcSite") or el.is_a("IfcBuilding") or el.is_a("IfcBuildingStorey") or not el.Representation:
            continue
        mat = ifcopenshell.util.placement.get_local_placement(el.ObjectPlacement) if el.ObjectPlacement else np.eye(4)
        pts = []
        for rep in el.Representation.Representations:
            for item in rep.Items:
                if item.is_a("IfcTriangulatedFaceSet"):
                    pts.extend(item.Coordinates.CoordList)
        if not pts:
            continue
        p = np.array(pts, dtype=float)
        p = (mat[:3, :3] @ p.T).T + mat[:3, 3]
        out.append((el.Name or "", el.is_a(), p))
    out.sort(key=lambda t: (t[0], t[1], tuple(np.round(t[2].min(axis=0), 3))))
    return out


def to_map(p, g):
    a, b = g["a"], g["b"]
    r = math.hypot(a, b)
    a, b = a / r, b / r
    s = g["s"]
    e = g["e"] + s * (a * p[:, 0] - b * p[:, 1])
    n = g["n"] + s * (b * p[:, 0] + a * p[:, 1])
    h = g["h"] + s * p[:, 2]
    return np.column_stack([e, n, h])


files = {}
for case in CASES:
    path = os.path.join(OUT, case + ".ifc")
    print(case)
    if not os.path.exists(path):
        check(False, f"{case}.ifc exists (run make_test_models.py in Rhino first)")
        continue
    f = ifcopenshell.open(path)
    files[case] = f
    logger = ifcopenshell.validate.json_logger()
    ifcopenshell.validate.validate(f, logger)
    check(len(logger.statements) == 0, f"schema valid ({len(logger.statements)} issues)")
    n = len([e for e in local_vertices(f)])
    check(n == 9, f"9 elements with geometry (found {n})")
    g = georef(f)
    site = f.by_type("IfcSite")[0]
    print(f"       georef: {g}, IfcSite lat {site.RefLatitude}")

# --- expectations per case ---
if "lv95" in files:
    g = georef(files["lv95"])
    check(g is not None and g["crs"] == "EPSG:2056", "lv95: EPSG:2056 detected")
if "origin" in files:
    g = georef(files["origin"])
    check(g is not None and g["crs"] == "EPSG:2056" and (g["e"], g["n"], g["h"]) == (2600000.0, 1200000.0, 0.0)
          and abs(g["a"] - 1) < 1e-12 and abs(g["b"]) < 1e-12, "origin: MapConversion = map origin, no rotation")
if "origin_rot" in files:
    g = georef(files["origin_rot"])
    ok = g is not None and (g["e"], g["n"], g["h"]) == (2600000.0, 1200000.0, 400.0) \
        and abs(g["a"] - math.cos(math.radians(30))) < 1e-7 and abs(g["b"] - math.sin(math.radians(30))) < 1e-7   # 8 decimals in the file
    check(ok, "origin_rot: MapConversion with H 400 and 30° rotation (map origin wins over the EarthAnchorPoint)")
    check(files["origin_rot"].by_type("IfcSite")[0].RefLatitude is None, "origin_rot: no IfcSite lat/lon")

if "lv95" in files and "origin" in files:
    A = local_vertices(files["lv95"])
    B = local_vertices(files["origin"])
    ga, gb = georef(files["lv95"]), georef(files["origin"])
    same = len(A) == len(B)
    worst, remeshed = 0.0, []
    for (na, ca, pa), (nb, cb, pb) in zip(A, B):
        same = same and na == nb and ca == cb
        ma, mb = to_map(pa, ga), to_map(pb, gb)
        if pa.shape == pb.shape:
            d = np.abs(np.sort(ma, axis=0) - np.sort(mb, axis=0)).max()
        else:
            # Rhino meshes (and booleans) a Brep differently 2.6 Mio m from the origin: compare extents
            remeshed.append(na)
            d = max(np.abs(ma.min(axis=0) - mb.min(axis=0)).max(), np.abs(ma.max(axis=0) - mb.max(axis=0)).max())
        worst = max(worst, d)
    check(same and worst < TOL, f"lv95 == origin in absolute coordinates (max difference {worst:.2e} m"
          + (f"; meshed differently in map coordinates, extents compared: {', '.join(remeshed)}" if remeshed else "") + ")")

if "origin" in files and "origin_rot" in files:
    A = local_vertices(files["origin"])
    B = local_vertices(files["origin_rot"])
    gb = georef(files["origin_rot"])
    c, s = math.cos(math.radians(30)), math.sin(math.radians(30))
    worst = 0.0
    same = len(A) == len(B)
    for (na, _, pa), (nb, _, pb) in zip(sorted(A, key=lambda t: t[0]), sorted(B, key=lambda t: t[0])):
        if na != nb or pa.shape != pb.shape:
            same = False
            continue
        exp = np.column_stack([2600000 + c * pa[:, 0] - s * pa[:, 1], 1200000 + s * pa[:, 0] + c * pa[:, 1], pa[:, 2]])
        got = to_map(pb, gb)
        worst = max(worst, np.abs(np.sort(exp, axis=0) - np.sort(got, axis=0)).max())
    # both files are rounded to 5 decimals before the rotation: allow two roundings
    check(same and worst < 2.5 * TOL, f"origin_rot = origin turned 30° about the map origin (max difference {worst:.2e} m)")

print()
print("all checks passed" if not failures else f"{len(failures)} check(s) failed")
sys.exit(1 if failures else 0)
