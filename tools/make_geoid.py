# geoBIM.app for Rhino — builds the geoid grids embedded in the plugin (src/GeoBimRhino/Resources/geoid/)
#
# GeoBIMUploadIon needs the geoid undulation N at the model (Cesium ion works with heights above the
# ellipsoid, the model with heights above sea level). The plugin looks it up in the same models the
# geobim-tiler uses: GCG2016 in Germany, EGM2008 elsewhere.
#
#   gcg2016_de.bin     GCG2016 (BKG), every 4th node of PROJ-data de_bkg_gcg2016.tif: 0.05° × 1/30°,
#                      238 × 263 nodes from 3.25625° E / 55.979166667° N; no data outside Germany.
#                      The same subset as geoBIM.app's viewer (geodesy/de_bkg_gcg2016_x4.u16), within
#                      4.4 cm of the full grid. "Derived from work by BKG. CC-BY-4.0"
#   egm2008_eu_5m.bin  EGM2008 (NGA, public domain) every 5' over Europe (34–72° N, 16° W – 45° E),
#                      sampled from PROJ-data us_nga_egm08_25.tif; within 8 cm of the full grid.
#   egm2008_1d.bin     EGM2008 every 1° worldwide, for elsewhere (errors up to about 2 m in mountains).
#
# Format of each file: "GBGRID1\0", int32 rows, int32 cols, float64 lat0 (first row, north), lon0
# (first column, west), dLat (> 0, rows go south), dLon, scale (metres per unit), int32 nodata, then a
# zlib stream of int16 little-endian values per row: the first absolute, the others differences to the
# left neighbour (int16 arithmetic wraps). Values are centimetres; nodata = -32768.
#
# The grids come from cdn.proj.org through pyproj (network access is switched on here):
#   python tools/make_geoid.py            (system Python with numpy and pyproj)

import os
import struct
import zlib

import numpy as np
import pyproj
from pyproj import Transformer

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "GeoBimRhino", "Resources", "geoid")
NODATA = -32768

pyproj.network.set_network_enabled(True)
EGM = Transformer.from_crs("EPSG:4326+3855", "EPSG:4979", always_xy=True)      # EGM2008 height → ellipsoidal
GCG = Transformer.from_crs("EPSG:4258+7837", "EPSG:4937", always_xy=True)      # DHHN2016 → ETRS89 (GCG2016)


def sample(tr, lat0, lon0, dlat, dlon, rows, cols):
    lats = lat0 - dlat * np.arange(rows)
    lons = lon0 + dlon * np.arange(cols)
    LON, LAT = np.meshgrid(lons, lats)
    _, _, h = tr.transform(LON.ravel(), LAT.ravel(), np.zeros(LON.size), errcheck=False)
    return np.asarray(h, dtype=float).reshape(rows, cols)


def encode(n, lat0, lon0, dlat, dlon):
    """undulations in metres (NaN = no data) → file bytes"""
    rows, cols = n.shape
    v = np.where(np.isfinite(n), np.round(n * 100), NODATA).astype("<i2")
    d = v.copy()
    d[:, 1:] = (v[:, 1:].astype(np.int32) - v[:, :-1].astype(np.int32)).astype(np.int16)   # wraps like int16
    head = b"GBGRID1\0" + struct.pack("<ii5di", rows, cols, lat0, lon0, dlat, dlon, 0.01, NODATA)
    return head + zlib.compress(d.astype("<i2").tobytes(), 9), v


def decode_check(data, v):
    rows, cols = struct.unpack_from("<ii", data, 8)
    d = np.frombuffer(zlib.decompress(data[8 + 8 + 5 * 8 + 4:]), dtype="<i2").reshape(rows, cols)
    back = np.cumsum(d.astype(np.int64), axis=1)
    back = ((back + 32768) % 65536 - 32768).astype(np.int16)
    assert np.array_equal(back, v), "round trip failed"


def write(name, n, lat0, lon0, dlat, dlon):
    data, v = encode(n, lat0, lon0, dlat, dlon)
    decode_check(data, v)
    path = os.path.join(OUT, name)
    open(path, "wb").write(data)
    ok = np.isfinite(n)
    print(f"{name}: {n.shape[0]} x {n.shape[1]}, {ok.sum()} values, N {np.nanmin(n):.2f} … {np.nanmax(n):.2f} m, {len(data) / 1024:.0f} KB")


def main():
    os.makedirs(OUT, exist_ok=True)

    # GCG2016: every 4th node of de_bkg_gcg2016.tif (0.0125° × 1/120°); outside Germany PROJ gives no
    # geoid (inf, or the height unchanged)
    lat0, lon0, dlat, dlon, rows, cols = 55.979166667, 3.25625, 1 / 30, 0.05, 263, 238
    n = sample(GCG, lat0, lon0, dlat, dlon, rows, cols)
    n[~np.isfinite(n) | (np.abs(n) < 1.0)] = np.nan
    write("gcg2016_de.bin", n, lat0, lon0, dlat, dlon)

    # EGM2008 over Europe every 5'
    d = 5 / 60
    n = sample(EGM, 72.0, -16.0, d, d, int(round(38 / d)) + 1, int(round(61 / d)) + 1)
    write("egm2008_eu_5m.bin", n, 72.0, -16.0, d, d)

    # EGM2008 worldwide every 1°
    n = sample(EGM, 90.0, -180.0, 1.0, 1.0, 181, 361)
    write("egm2008_1d.bin", n, 90.0, -180.0, 1.0, 1.0)


if __name__ == "__main__":
    main()
