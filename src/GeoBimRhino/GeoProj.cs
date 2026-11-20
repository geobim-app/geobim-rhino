// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GeoBimRhino
{
    /// <summary>
    /// Map coordinates ↔ WGS84 latitude/longitude for the coordinate systems the plugin
    /// converts itself, so one position (map coordinates or latitude/longitude) sets both
    /// the map origin and Rhino's Location:
    ///  - Swiss LV95 (EPSG 2056): Swiss oblique Mercator on Bessel 1841 (swisstopo's rigorous
    ///    formulas), then CH1903+ → WGS84 by the geocentric translation EPSG calls
    ///    "CH1903+ to WGS 84 (1)" — the same operation pyproj uses, so GLB and Cesium ion
    ///    land where the geobim-tiler puts the IFC;
    ///  - UTM north zones on ETRS89/GRS80 (258zz, with zone prefix 5649/4647/5650) or WGS84
    ///    (326zz): Krüger series to n⁶ (well below a millimetre), ETRS89 taken as WGS84 like pyproj.
    /// No Rhino types: checked against pyproj by tests/geo_check.
    /// </summary>
    public static class GeoProj
    {
        const double Deg = Math.PI / 180.0;

        public static bool Supports(int epsg) => epsg == 2056 || Utm(epsg, out _, out _, out _);

        /// <summary>Short name for messages, e.g. "LV95" or "UTM 32N (ETRS89)"</summary>
        public static string Name(int epsg)
        {
            if (epsg == 2056) return "LV95";
            if (Utm(epsg, out int zone, out bool grs80, out double prefix))
                return $"UTM {zone}N ({(grs80 ? "ETRS89" : "WGS84")}{(prefix != 0 ? ", zone prefix" : "")})";
            return "EPSG:" + epsg;
        }

        static bool Utm(int epsg, out int zone, out bool grs80, out double prefix)
        {
            zone = 0; grs80 = true; prefix = 0;
            if (epsg >= 25828 && epsg <= 25838) { zone = epsg - 25800; return true; }
            if (epsg >= 32601 && epsg <= 32660) { zone = epsg - 32600; grs80 = false; return true; }
            if (epsg == 5649) { zone = 31; prefix = 31e6; return true; }
            if (epsg == 4647) { zone = 32; prefix = 32e6; return true; }
            if (epsg == 5650) { zone = 33; prefix = 33e6; return true; }
            return false;
        }

        /// <summary>Map coordinates (metres) → WGS84 latitude/longitude (degrees); false for unsupported systems</summary>
        public static bool ToLatLon(int epsg, double e, double n, out double lat, out double lon)
        {
            lat = lon = double.NaN;
            if (epsg == 2056) { Lv95ToWgs84(e, n, out lat, out lon); return true; }
            if (Utm(epsg, out int zone, out bool grs80, out double prefix))
            {
                UtmInverse(grs80, zone, e - prefix, n, out lat, out lon);
                return true;
            }
            return false;
        }

        /// <summary>WGS84 latitude/longitude (degrees) → map coordinates (metres); false for unsupported systems</summary>
        public static bool ToMap(int epsg, double lat, double lon, out double e, out double n)
        {
            e = n = double.NaN;
            if (epsg == 2056) { Wgs84ToLv95(lat, lon, out e, out n); return true; }
            if (Utm(epsg, out int zone, out bool grs80, out double prefix))
            {
                UtmForward(grs80, zone, lat, lon, out e, out n);
                e += prefix;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Meridian convergence: angle of true north from grid north, counter-clockwise, in degrees
        /// (positive east of the central meridian; about 1.5° at Pfäfers in LV95, up to 3° at UTM zone edges)
        /// </summary>
        public static double Convergence(int epsg, double lat, double lon)
        {
            const double d = 1e-4;
            if (!ToMap(epsg, lat - d, lon, out double e0, out double n0) || !ToMap(epsg, lat + d, lon, out double e1, out double n1)) return 0;
            return Math.Atan2(-(e1 - e0), n1 - n0) / Deg;
        }

        /// <summary>
        /// Coordinate system for a WGS84 position: LV95 within Switzerland's box, else the UTM
        /// zone (ETRS89 in Europe, WGS84 elsewhere, with Norway's zone 32V); null south of the equator
        /// </summary>
        public static int? DefaultEpsg(double lat, double lon)
        {
            if (lat >= 45.8 && lat <= 47.85 && lon >= 5.9 && lon <= 10.55) return 2056;
            if (lat < 0 || lat > 84 || lon < -180 || lon > 180) return null;
            int zone = (int)Math.Floor((lon + 180) / 6) + 1;
            if (zone > 60) zone = 60;
            if (lat >= 56 && lat < 64 && lon >= 3 && lon < 12) zone = 32;
            bool europe = lat >= 34 && lat <= 72 && lon >= -16 && lon <= 45 && zone >= 28 && zone <= 38;
            return europe ? 25800 + zone : 32600 + zone;
        }

        // ---------------------------------------------------------------- LV95

        const double BesselA = 6377397.155, BesselInvF = 299.1528128;
        const double Grs80A = 6378137.0, Grs80InvF = 298.257222101, Wgs84InvF = 298.257223563;
        // CH1903+ → WGS84 (EPSG:1676, geocentric translation)
        const double ChDx = 674.374, ChDy = 15.056, ChDz = 405.346;

        static readonly double BE2 = 2 / BesselInvF - 1 / (BesselInvF * BesselInvF);
        static readonly double BE = Math.Sqrt(BE2);
        static readonly double Phi0 = (46 + 57 / 60.0 + 8.66 / 3600) * Deg;
        static readonly double Lam0 = (7 + 26 / 60.0 + 22.5 / 3600) * Deg;
        static readonly double SR = BesselA * Math.Sqrt(1 - BE2) / (1 - BE2 * Math.Sin(Phi0) * Math.Sin(Phi0));
        static readonly double Alpha = Math.Sqrt(1 + BE2 / (1 - BE2) * Math.Pow(Math.Cos(Phi0), 4));
        static readonly double B0 = Math.Asin(Math.Sin(Phi0) / Alpha);
        static readonly double SK = Math.Log(Math.Tan(Math.PI / 4 + B0 / 2))
                                    - Alpha * Math.Log(Math.Tan(Math.PI / 4 + Phi0 / 2))
                                    + Alpha * BE / 2 * Math.Log((1 + BE * Math.Sin(Phi0)) / (1 - BE * Math.Sin(Phi0)));

        static void Lv95ToWgs84(double e, double n, out double lat, out double lon)
        {
            double y = e - 2600000, x = n - 1200000;
            double lb = y / SR;
            double bb = 2 * (Math.Atan(Math.Exp(x / SR)) - Math.PI / 4);
            double b = Math.Asin(Math.Cos(B0) * Math.Sin(bb) + Math.Sin(B0) * Math.Cos(bb) * Math.Cos(lb));
            double l = Math.Atan(Math.Sin(lb) / (Math.Cos(B0) * Math.Cos(lb) - Math.Sin(B0) * Math.Tan(bb)));
            double lam = Lam0 + l / Alpha;
            double phi = b;
            for (int i = 0; i < 20; i++)
            {
                double s = (Math.Log(Math.Tan(Math.PI / 4 + b / 2)) - SK) / Alpha
                           + BE * Math.Log(Math.Tan(Math.PI / 4 + Math.Asin(BE * Math.Sin(phi)) / 2));
                double next = 2 * Math.Atan(Math.Exp(s)) - Math.PI / 2;
                bool done = Math.Abs(next - phi) < 1e-13;
                phi = next;
                if (done) break;
            }
            Shift(phi, lam, BesselA, BE2, ChDx, ChDy, ChDz, Grs80A, E2Of(Wgs84InvF), out lat, out lon);
        }

        static void Wgs84ToLv95(double lat, double lon, out double e, out double n)
        {
            Shift(lat * Deg, lon * Deg, Grs80A, E2Of(Wgs84InvF), -ChDx, -ChDy, -ChDz, BesselA, BE2, out double phiDeg, out double lamDeg);
            double phi = phiDeg * Deg, lam = lamDeg * Deg;
            double s = Alpha * Math.Log(Math.Tan(Math.PI / 4 + phi / 2))
                       - Alpha * BE / 2 * Math.Log((1 + BE * Math.Sin(phi)) / (1 - BE * Math.Sin(phi))) + SK;
            double b = 2 * (Math.Atan(Math.Exp(s)) - Math.PI / 4);
            double l = Alpha * (lam - Lam0);
            double lb = Math.Atan(Math.Sin(l) / (Math.Sin(B0) * Math.Tan(b) + Math.Cos(B0) * Math.Cos(l)));
            double bb = Math.Asin(Math.Cos(B0) * Math.Sin(b) - Math.Sin(B0) * Math.Cos(b) * Math.Cos(l));
            e = 2600000 + SR * lb;
            n = 1200000 + SR / 2 * Math.Log((1 + Math.Sin(bb)) / (1 - Math.Sin(bb)));
        }

        static double E2Of(double invF) => 2 / invF - 1 / (invF * invF);

        /// <summary>Geodetic (radians, height 0 as pyproj's 2D transform) → ECEF + translation → geodetic on the other ellipsoid (degrees)</summary>
        static void Shift(double phi, double lam, double a1, double e21, double dx, double dy, double dz,
                          double a2, double e22, out double latDeg, out double lonDeg)
        {
            double nu = a1 / Math.Sqrt(1 - e21 * Math.Sin(phi) * Math.Sin(phi));
            double x = nu * Math.Cos(phi) * Math.Cos(lam) + dx;
            double y = nu * Math.Cos(phi) * Math.Sin(lam) + dy;
            double z = nu * (1 - e21) * Math.Sin(phi) + dz;
            double p = Math.Sqrt(x * x + y * y);
            double lat = Math.Atan2(z, p * (1 - e22));
            for (int i = 0; i < 10; i++)
            {
                double nu2 = a2 / Math.Sqrt(1 - e22 * Math.Sin(lat) * Math.Sin(lat));
                double h = p / Math.Cos(lat) - nu2;
                lat = Math.Atan2(z, p * (1 - e22 * nu2 / (nu2 + h)));
            }
            latDeg = lat / Deg;
            lonDeg = Math.Atan2(y, x) / Deg;
        }

        // ---------------------------------------------------------------- UTM (Krüger series)

        const double K0 = 0.9996, FalseE = 500000;

        sealed class Tm
        {
            public double A, N, C;   // rectifying radius, third flattening, 2√n/(1+n)
            public double[] Alpha = new double[7], Beta = new double[7], Delta = new double[7];
        }

        static readonly Tm TmGrs80 = MakeTm(Grs80A, Grs80InvF), TmWgs84 = MakeTm(Grs80A, Wgs84InvF);

        static Tm MakeTm(double a, double invF)
        {
            double f = 1 / invF, n = f / (2 - f);
            double n2 = n * n, n3 = n2 * n, n4 = n3 * n, n5 = n4 * n, n6 = n5 * n;
            var t = new Tm { N = n, A = a / (1 + n) * (1 + n2 / 4 + n4 / 64 + n6 / 256), C = 2 * Math.Sqrt(n) / (1 + n) };
            t.Alpha[1] = n / 2 - 2 * n2 / 3 + 5 * n3 / 16 + 41 * n4 / 180 - 127 * n5 / 288 + 7891 * n6 / 37800;
            t.Alpha[2] = 13 * n2 / 48 - 3 * n3 / 5 + 557 * n4 / 1440 + 281 * n5 / 630 - 1983433 * n6 / 1935360;
            t.Alpha[3] = 61 * n3 / 240 - 103 * n4 / 140 + 15061 * n5 / 26880 + 167603 * n6 / 181440;
            t.Alpha[4] = 49561 * n4 / 161280 - 179 * n5 / 168 + 6601661 * n6 / 7257600;
            t.Alpha[5] = 34729 * n5 / 80640 - 3418889 * n6 / 1995840;
            t.Alpha[6] = 212378941 * n6 / 319334400;
            t.Beta[1] = n / 2 - 2 * n2 / 3 + 37 * n3 / 96 - n4 / 360 - 81 * n5 / 512 + 96199 * n6 / 604800;
            t.Beta[2] = n2 / 48 + n3 / 15 - 437 * n4 / 1440 + 46 * n5 / 105 - 1118711 * n6 / 3870720;
            t.Beta[3] = 17 * n3 / 480 - 37 * n4 / 840 - 209 * n5 / 4480 + 5569 * n6 / 90720;
            t.Beta[4] = 4397 * n4 / 161280 - 11 * n5 / 504 - 830251 * n6 / 7257600;
            t.Beta[5] = 4583 * n5 / 161280 - 108847 * n6 / 3991680;
            t.Beta[6] = 20648693 * n6 / 638668800;
            t.Delta[1] = 2 * n - 2 * n2 / 3 - 2 * n3 + 116 * n4 / 45 + 26 * n5 / 45 - 2854 * n6 / 675;
            t.Delta[2] = 7 * n2 / 3 - 8 * n3 / 5 - 227 * n4 / 45 + 2704 * n5 / 315 + 2323 * n6 / 945;
            t.Delta[3] = 56 * n3 / 15 - 136 * n4 / 35 - 1262 * n5 / 105 + 73814 * n6 / 2835;
            t.Delta[4] = 4279 * n4 / 630 - 332 * n5 / 35 - 399572 * n6 / 14175;
            t.Delta[5] = 4174 * n5 / 315 - 144838 * n6 / 6237;
            t.Delta[6] = 601676 * n6 / 22275;
            return t;
        }

        static double CentralMeridian(int zone) => (zone * 6 - 183) * Deg;

        static void UtmForward(bool grs80, int zone, double lat, double lon, out double e, out double n)
        {
            var t = grs80 ? TmGrs80 : TmWgs84;
            double phi = lat * Deg, dl = lon * Deg - CentralMeridian(zone);
            double s = Math.Sin(phi);
            double tau = Math.Sinh(Atanh(s) - t.C * Atanh(t.C * s));
            double xi = Math.Atan2(tau, Math.Cos(dl));
            double eta = Atanh(Math.Sin(dl) / Math.Sqrt(1 + tau * tau));
            double x = xi, y = eta;
            for (int j = 1; j <= 6; j++)
            {
                x += t.Alpha[j] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
                y += t.Alpha[j] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            }
            e = FalseE + K0 * t.A * y;
            n = K0 * t.A * x;
        }

        static void UtmInverse(bool grs80, int zone, double e, double n, out double lat, out double lon)
        {
            var t = grs80 ? TmGrs80 : TmWgs84;
            double xi = n / (K0 * t.A), eta = (e - FalseE) / (K0 * t.A);
            double xp = xi, yp = eta;
            for (int j = 1; j <= 6; j++)
            {
                xp -= t.Beta[j] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
                yp -= t.Beta[j] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            }
            double chi = Math.Asin(Math.Sin(xp) / Math.Cosh(yp));
            double phi = chi;
            for (int j = 1; j <= 6; j++) phi += t.Delta[j] * Math.Sin(2 * j * chi);
            lat = phi / Deg;
            lon = (CentralMeridian(zone) + Math.Atan2(Math.Sinh(yp), Math.Cos(xp))) / Deg;
        }

        static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));
    }

    /// <summary>What the user typed for a position: map coordinates, latitude/longitude, or an address</summary>
    public class PositionText
    {
        public enum Kinds { Map, LatLon, Address }
        public Kinds Kind;
        public double E, N, Lat, Lon;
        public double? H;            // height above sea level, when given
        public double? RotationDeg;  // map coordinates only: fourth value (as in the stored map origin)
        public string Address;

        static readonly Regex Dms = new Regex(
            @"(?<d>\d+(?:[.,]\d+)?)\s*°\s*(?:(?<m>\d+(?:[.,]\d+)?)\s*['′]\s*)?(?:(?<s>\d+(?:[.,]\d+)?)\s*(?:""|″|''))?\s*(?<h>[NSEWO])?",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// "2'755'000.00, 1'206'000.00[, H[, rotation]]" (map coordinates, apostrophes as thousands
        /// separators as map.geo.admin.ch copies them), "46.9123, 9.4877[, H]" (latitude, longitude
        /// as Google Maps copies them), 46°54'44"N 9°29'16"E, or anything with letters (an address).
        /// Null when it is none of these.
        /// </summary>
        public static PositionText Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim().Trim('"');
            if (text.Contains("°"))
            {
                var ms = Dms.Matches(text);
                if (ms.Count == 2)
                {
                    double a = DmsValue(ms[0]), b = DmsValue(ms[1]);
                    string ha = ms[0].Groups["h"].Value.ToUpperInvariant(), hb = ms[1].Groups["h"].Value.ToUpperInvariant();
                    if (ha == "S" || ha == "W") a = -a;
                    if (hb == "S" || hb == "W") b = -b;
                    bool swapped = ha == "E" || ha == "O" || ha == "W";
                    double lat = swapped ? b : a, lon = swapped ? a : b;
                    if (Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180) return new PositionText { Kind = Kinds.LatLon, Lat = lat, Lon = lon };
                }
                return null;
            }
            var clean = text.Replace("'", "").Replace("’", "").Replace(" ", "").Replace(" ", "");
            var parts = clean.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var v = new List<double>();
            foreach (var p in parts)
            {
                if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsNaN(d) || double.IsInfinity(d))
                {
                    v = null;
                    break;
                }
                v.Add(d);
            }
            if (v == null)
            {
                foreach (char ch in text) if (char.IsLetter(ch)) return new PositionText { Kind = Kinds.Address, Address = text };
                return null;
            }
            if (v.Count < 2 || v.Count > 4) return null;
            if (CrsDetect.LooksLikeMap(v[0], v[1]))
            {
                double e = v[0], n = v[1];
                // LV95 typed northing first (1 2xx xxx, 2 7xx xxx): swap
                if (e >= 1.0e6 && e < 1.4e6 && n >= 2.4e6 && n < 2.9e6) { e = v[1]; n = v[0]; }
                return new PositionText
                {
                    Kind = Kinds.Map, E = e, N = n,
                    H = v.Count > 2 ? v[2] : (double?)null,
                    RotationDeg = v.Count > 3 ? v[3] : (double?)null
                };
            }
            if (v.Count <= 3 && Math.Abs(v[0]) <= 90 && Math.Abs(v[1]) <= 180)
                return new PositionText { Kind = Kinds.LatLon, Lat = v[0], Lon = v[1], H = v.Count > 2 ? v[2] : (double?)null };
            return null;
        }

        static double DmsValue(Match m)
        {
            double P(string g) => m.Groups[g].Success && m.Groups[g].Value.Length > 0
                ? double.Parse(m.Groups[g].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : 0;
            return P("d") + P("m") / 60 + P("s") / 3600;
        }
    }
}
