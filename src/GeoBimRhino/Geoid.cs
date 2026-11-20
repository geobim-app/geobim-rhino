// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.IO.Compression;

namespace GeoBimRhino
{
    /// <summary>
    /// Geoid undulation N (ellipsoidal height − height above sea level) at a WGS84 position, from the
    /// models the geobim-tiler uses: GCG2016 in Germany (BKG, CC BY 4.0: "Derived from work by BKG"),
    /// EGM2008 elsewhere (NGA, public domain) — every 5' over Europe (within 8 cm of the full grid),
    /// every 1° outside it (up to about 2 m off in mountains). The grids are embedded resources built by
    /// tools/make_geoid.py. No Rhino types: tests/geo_check checks it against pyproj.
    /// </summary>
    public static class Geoid
    {
        public class Result
        {
            public double N;          // metres
            public string Model;      // "GCG2016", "EGM2008" or "EGM2008 (1° grid)"
            public bool Coarse;       // the 1° grid: only roughly right
        }

        sealed class Grid
        {
            public int Rows, Cols;
            public double Lat0, Lon0, DLat, DLon, Scale;
            public short NoData;
            public short[] V;

            /// <summary>Bilinear value, or NaN outside the grid or next to a node without data</summary>
            public double At(double lat, double lon)
            {
                double r = (Lat0 - lat) / DLat, c = (lon - Lon0) / DLon;
                int r0 = (int)Math.Floor(r), c0 = (int)Math.Floor(c);
                if (r0 < 0 || c0 < 0 || r0 > Rows - 1 || c0 > Cols - 1) return double.NaN;
                if (r0 == Rows - 1) r0--;   // on the last row or column
                if (c0 == Cols - 1) c0--;
                int i = r0 * Cols + c0;
                short q00 = V[i], q01 = V[i + 1], q10 = V[i + Cols], q11 = V[i + Cols + 1];
                if (q00 == NoData || q01 == NoData || q10 == NoData || q11 == NoData) return double.NaN;
                double fr = r - r0, fc = c - c0;
                return Scale * (q00 * (1 - fc) * (1 - fr) + q01 * fc * (1 - fr) + q10 * (1 - fc) * fr + q11 * fc * fr);
            }
        }

        static readonly Lazy<Grid> Gcg = new Lazy<Grid>(() => Load("gcg2016_de.bin"));
        static readonly Lazy<Grid> EgmEurope = new Lazy<Grid>(() => Load("egm2008_eu_5m.bin"));
        static readonly Lazy<Grid> EgmWorld = new Lazy<Grid>(() => Load("egm2008_1d.bin"));

        /// <summary>
        /// N at a position. GCG2016 where it has data, except for models in LV95 (the tiler takes
        /// GCG2016 only for German coordinate systems), else EGM2008. Null only if a grid is missing.
        /// </summary>
        public static Result At(double lat, double lon, int? epsg = null)
        {
            if (epsg != 2056)
            {
                double g = Gcg.Value?.At(lat, lon) ?? double.NaN;
                if (!double.IsNaN(g)) return new Result { N = g, Model = "GCG2016" };
            }
            double e = EgmEurope.Value?.At(lat, lon) ?? double.NaN;
            if (!double.IsNaN(e)) return new Result { N = e, Model = "EGM2008" };
            double lonW = lon > 180 ? lon - 360 : lon < -180 ? lon + 360 : lon;
            double w = EgmWorld.Value?.At(Math.Max(-90, Math.Min(90, lat)), lonW) ?? double.NaN;
            if (!double.IsNaN(w)) return new Result { N = w, Model = "EGM2008 (1° grid)", Coarse = true };
            return null;
        }

        static Grid Load(string name)
        {
            using (var s = typeof(Geoid).Assembly.GetManifestResourceStream("GeoBimRhino.geoid." + name))
            {
                if (s == null) return null;
                using (var br = new BinaryReader(s))
                {
                    // bytes, not chars: the zlib stream must start exactly after the header
                    if (System.Text.Encoding.ASCII.GetString(br.ReadBytes(8)) != "GBGRID1\0")
                        throw new InvalidDataException("geoid grid " + name + ": unknown format");
                    var g = new Grid
                    {
                        Rows = br.ReadInt32(), Cols = br.ReadInt32(),
                        Lat0 = br.ReadDouble(), Lon0 = br.ReadDouble(), DLat = br.ReadDouble(), DLon = br.ReadDouble(),
                        Scale = br.ReadDouble(), NoData = (short)br.ReadInt32()
                    };
                    g.V = new short[g.Rows * g.Cols];
                    using (var z = new ZLibStream(s, CompressionMode.Decompress))
                    using (var zr = new BinaryReader(z))
                    {
                        for (int r = 0; r < g.Rows; r++)
                        {
                            short prev = 0;
                            for (int c = 0; c < g.Cols; c++)
                            {
                                short d = zr.ReadInt16();
                                prev = c == 0 ? d : unchecked((short)(prev + d));
                                g.V[r * g.Cols + c] = prev;
                            }
                        }
                    }
                    return g;
                }
            }
        }
    }
}
