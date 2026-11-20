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
#   ion        the anchor model as GeoBIMUploadIon writes it (east/north/up, model north
#              turned onto +Y; written with Upload=No, nothing is sent)
#   location   local model placed by GeoBIMLocation from latitude/longitude (Pfaefers);
#              with location.json (what Rhino stored and printed), location_ion.ifc
#   lv95_ion   the lv95 model (map coordinates, no Location) as Cesium ion IFC
#   location_auto  the location model's ion origin with GeoidN=0: the height must be EGM2008's
#              undulation at the origin (pyproj with the full grid from cdn.proj.org)
# lv95 and origin must give the same absolute coordinates; origin_rot must be
# origin's local geometry turned 30° about the map origin; ion must be anchor's
# geometry turned 30° onto true north. The GeoBIMLocation cases are checked against
# pyproj: map origin, Rhino's Location and true north (meridian convergence), the GLB
# placement and the ion IFCs (east/north/up around a base point). Exit code 1 on failure.

import json
import math
import os
import sys

import ifcopenshell
import ifcopenshell.util.placement
import ifcopenshell.validate
import numpy as np
from pyproj import CRS, Proj, Transformer

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
CASES = ["utm", "anchor", "ion", "local", "lv95", "origin", "origin_rot", "location", "location_ion", "lv95_ion"]
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

if "anchor" in files and "ion" in files:
    # ion: the anchor model in east/north/up around the base point; model north was
    # 30° clockwise from +Y, so the geometry is turned 30° counter-clockwise
    fi = files["ion"]
    north = fi.by_type("IfcGeometricRepresentationContext")[0].TrueNorth
    check(georef(fi) is None and fi.by_type("IfcSite")[0].RefLatitude is None,
          "ion: no MapConversion and no IfcSite lat/lon (the position travels with the upload)")
    check(north is None or tuple(round(v, 9) for v in north.DirectionRatios) == (0.0, 1.0), "ion: TrueNorth (0,1)")
    A = sorted(local_vertices(files["anchor"]), key=lambda t: t[0])
    B = sorted(local_vertices(fi), key=lambda t: t[0])
    c, s = math.cos(math.radians(30)), math.sin(math.radians(30))
    worst, same = 0.0, len(A) == len(B)
    for (na, _, pa), (nb, _, pb) in zip(A, B):
        if na != nb or pa.shape != pb.shape:
            same = False
            continue
        exp = np.column_stack([c * pa[:, 0] - s * pa[:, 1], s * pa[:, 0] + c * pa[:, 1], pa[:, 2]])
        worst = max(worst, np.abs(np.sort(exp, axis=0) - np.sort(pb, axis=0)).max())
    check(same and worst < TOL, f"ion = anchor model turned onto true north (max difference {worst:.2e} m)")

# --- GeoBIMLocation and placement from the map position (pyproj as reference) ---
TO_LV95 = Transformer.from_crs(4326, 2056, always_xy=True)
TO_WGS = Transformer.from_crs(2056, 4326, always_xy=True)
LV95 = Proj(CRS.from_epsg(2056))


def convergence(lon, lat):
    """true north, degrees counter-clockwise from grid north"""
    return LV95.get_factors(lon, lat).meridian_convergence


def rot_z(p, deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return np.column_stack([c * p[:, 0] - s * p[:, 1], s * p[:, 0] + c * p[:, 1], p[:, 2]])


def compare(A, B, fn, tol):
    """max difference between fn(A) and B per element (sorted vertices; extents when meshed differently)"""
    worst, same, remeshed = 0.0, len(A) == len(B), []
    for (na, _, pa), (nb, _, pb) in zip(sorted(A, key=lambda t: t[0]), sorted(B, key=lambda t: t[0])):
        if na != nb:
            same = False
            continue
        ea = fn(pa)
        if pa.shape == pb.shape:
            d = np.abs(np.sort(ea, axis=0) - np.sort(pb, axis=0)).max()
        else:
            remeshed.append(na)
            d = max(np.abs(ea.min(axis=0) - pb.min(axis=0)).max(), np.abs(ea.max(axis=0) - pb.max(axis=0)).max())
        worst = max(worst, d)
    return same and worst < tol, worst, remeshed


jpath = os.path.join(OUT, "location.json")
LOC = json.load(open(jpath, encoding="utf-8")) if os.path.exists(jpath) else None
print("location.json")
check(LOC is not None, "location.json exists")
if LOC and "location" in LOC:
    L = LOC["location"]
    lat_in, lon_in = 46.9869521994, 9.4763209037
    e_ref, n_ref = TO_LV95.transform(lon_in, lat_in)
    try:
        E0, N0, H0, ROT = (float(v) for v in L["mapOrigin"].split(","))
    except Exception:
        E0 = N0 = H0 = ROT = float("nan")
    check(abs(E0 - e_ref) < 0.002 and abs(N0 - n_ref) < 0.002 and H0 == 0 and ROT == 0,
          f"location: map origin {L['mapOrigin']} = pyproj LV95 of the latitude/longitude ({e_ref:.4f}, {n_ref:.4f})")
    check(L["epsg"] == "2056", "location: EPSG 2056 stored for the IFC export")
    lon0, lat0 = TO_WGS.transform(E0, N0)
    d = math.hypot((L["lat"] - lat0) * 111320, (L["lon"] - lon0) * 111320 * math.cos(math.radians(lat0)))
    check(d < 0.002 and L["elevation"] == 0 and L["base"] == [0, 0, 0] and L["zero"] == "MeanSeaLevel",
          f"location: Rhino's Location at the map origin ({d * 1000:.2f} mm from pyproj), base point 0,0,0, mean sea level")
    g0 = math.radians(convergence(lon0, lat0))
    nx, ny, _ = L["north"]
    ex, ey, _ = L["east"]
    check(abs(nx + math.sin(g0)) < 1e-6 and abs(ny - math.cos(g0)) < 1e-6 and abs(ex - math.cos(g0)) < 1e-6 and abs(ey - math.sin(g0)) < 1e-6,
          f"location: model north turned by the meridian convergence {math.degrees(g0):.4f}° (north {nx:.6f}, {ny:.6f})")
    if "location" in files:
        g = georef(files["location"])
        check(g is not None and g["crs"] == "EPSG:2056" and abs(g["e"] - E0) < 1e-6 and abs(g["n"] - N0) < 1e-6 and g["h"] == 0,
              "location: IFC MapConversion = the map origin GeoBIMLocation set")

    # GLB placement and the ion IFC share the base point: the model centre rounded to metres
    pl = (LOC.get("location_glb") or {}).get("placement")
    io = (LOC.get("location_ion") or {}).get("origin")
    check(pl is not None and io is not None, "location: GLB placement and ion origin printed")
    if pl and io and "location" in files and "location_ion" in files:
        A = local_vertices(files["location"])
        allp = np.vstack([t[2] for t in A])
        centre = (allp.min(axis=0) + allp.max(axis=0)) / 2
        eb, nb = TO_LV95.transform(io[0], io[1])
        bx, by = eb - E0, nb - N0
        check(abs(bx - round(bx)) < 0.02 and abs(by - round(by)) < 0.02 and abs(bx - centre[0]) < 1.5 and abs(by - centre[1]) < 1.5,
              f"location_ion: origin at the model centre rounded to metres (local {bx:.3f}, {by:.3f}; centre {centre[0]:.2f}, {centre[1]:.2f})")
        check(abs(io[2] - 48.7) < 1e-3, f"location_ion: height = H0 + GeoidN = 48.7 m ({io[2]})")
        check(abs(pl[0] - io[0]) < 2e-6 and abs(pl[1] - io[1]) < 2e-6 and abs(pl[2]) < 0.01,
              "location_glb: GLB placed at the same base point, 0 m above sea level")
        gb = convergence(io[0], io[1])
        check(abs(pl[3] - gb) < 0.06, f"location_glb: heading {pl[3]}° = convergence {gb:.3f}° (model +Y is map north)")
        b = np.array([round(bx), round(by), 0.0])
        ok, worst, rem = compare(A, local_vertices(files["location_ion"]), lambda p: rot_z(p - b, -gb), 3 * TOL)
        check(ok, f"location_ion: geometry in east/north/up around the base point, turned by -{gb:.3f}° (max difference {worst:.2e} m)"
              + (f"; extents for {', '.join(rem)}" if rem else ""))

if LOC and (LOC.get("location_auto") or {}).get("origin"):
    au = LOC["location_auto"]["origin"]
    io = (LOC.get("location_ion") or {}).get("origin")
    check(io is not None and abs(au[0] - io[0]) < 1e-7 and abs(au[1] - io[1]) < 1e-7,
          "location_auto: same origin as location_ion (only the geoid differs)")
    try:
        import pyproj
        pyproj.network.set_network_enabled(True)
        egm = Transformer.from_crs("EPSG:4326+3855", "EPSG:4979", always_xy=True).transform(au[0], au[1], 0.0)[2]
    except Exception as ex:
        egm = None
        print("  skip location_auto: no EGM2008 grid from pyproj (%s)" % ex)
    if egm is not None and math.isfinite(egm) and abs(egm) > 1:
        check(abs(au[2] - egm) < 0.08, f"location_auto: height {au[2]} m = H0 0 + EGM2008 N {egm:.3f} m (full grid)")
elif LOC:
    check(False, "location_auto: origin printed")

hr = (LOC or {}).get("height_rule")
check(hr is not None, "height_rule: recorded")
if hr:
    check(hr["far_refused"] and hr["after_far"] == hr["before"],
          f"height_rule: a scripted move to Bavaria without a height stops, map origin unchanged ({hr['after_far']})")
    try:
        nb = [float(v) for v in hr["after_near"].split(",")]
        b0 = [float(v) for v in hr["before"].split(",")]
        moved = math.hypot(nb[0] - b0[0], nb[1] - b0[1])
        check(hr["near_kept"] and nb[2] == b0[2] and 80 < moved < 110,
              f"height_rule: a 95 m move without a height keeps H {nb[2]} (moved {moved:.1f} m)")
    except Exception as ex:
        check(False, f"height_rule: near move ({ex})")

sw = (LOC or {}).get("size_warning")
check(sw is not None and sw["exported"] and sw["warned"] and sw["only_5_mm"],
      f"size_warning: a 5 mm model in a millimetre document is exported with a units warning ({sw})")

if LOC and "lv95_ion" in files and "lv95" in files:
    io = (LOC.get("lv95_ion") or {}).get("origin")
    check(io is not None, "lv95_ion: origin printed (map coordinates placed without a Location)")
    if io:
        ga = georef(files["lv95"])
        A = [(n, c, to_map(p, ga)) for n, c, p in local_vertices(files["lv95"])]
        allp = np.vstack([t[2] for t in A])
        centre = (allp.min(axis=0) + allp.max(axis=0)) / 2
        eb, nb = TO_LV95.transform(io[0], io[1])
        check(abs(eb - round(eb)) < 0.02 and abs(nb - round(nb)) < 0.02 and abs(eb - centre[0]) < 1.5 and abs(nb - centre[1]) < 1.5,
              f"lv95_ion: origin = model centre rounded to metres in LV95 ({eb:.3f}, {nb:.3f})")
        check(abs(io[2] - 48.7) < 1e-3, f"lv95_ion: height 0 + GeoidN = 48.7 m ({io[2]})")
        gb = convergence(io[0], io[1])
        b = np.array([round(eb), round(nb), 0.0])
        ok, worst, rem = compare(A, local_vertices(files["lv95_ion"]), lambda p: rot_z(p - b, -gb), 3 * TOL)
        check(ok, f"lv95_ion: LV95 geometry in east/north/up around the base point, turned by -{gb:.4f}° (max difference {worst:.2e} m)"
              + (f"; extents for {', '.join(rem)}" if rem else ""))

print()
print("all checks passed" if not failures else f"{len(failures)} check(s) failed")
sys.exit(1 if failures else 0)
