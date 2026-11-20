// Checks GeoProj against pyproj 3.7.2 / PROJ 9.5.1 (reference values computed with
// Transformer.from_crs(epsg, 4326) and Proj.get_factors(...).meridian_convergence),
// and PositionText against the formats map.geo.admin.ch and Google Maps copy.
using System;
using GeoBimRhino;

static class Program
{
    static int _failed;

    sealed class R
    {
        public int Epsg; public double E, N, Lat, Lon, Conv;
        public R(int epsg, double e, double n, double lat, double lon, double conv) { Epsg = epsg; E = e; N = n; Lat = lat; Lon = lon; Conv = conv; }
    }

    static readonly R[] Refs =
    {
        new R(2056, 2755000.000, 1206000.000, 46.9869521994, 9.4763209037, 1.488275),
        new R(2056, 2600000.000, 1200000.000, 46.9510827719, 7.4386324209, -0.000695),
        new R(2056, 2485000.000, 1110000.000, 46.1316565963, 5.9506123934, -1.088172),
        new R(2056, 2830000.000, 1180000.000, 46.7314892771, 10.4483782210, 2.198326),
        new R(25832, 500000.000, 5500000.000, 49.6525429236, 9.0000000000, -0.000000),
        new R(25832, 373064.000, 5612345.000, 50.6491187936, 7.2044623446, -1.388633),
        new R(25832, 690000.000, 5340000.000, 48.1847976067, 11.5563060652, 1.905780),
        new R(25833, 390000.000, 5820000.000, 52.5189953134, 13.3788117697, -1.286630),
        new R(32632, 373064.000, 5612345.000, 50.6491187925, 7.2044623446, -1.388633),
        new R(4647, 32373064.000, 5612345.000, 50.6491187936, 7.2044623446, -1.388633),
        new R(5650, 33390000.000, 5820000.000, 52.5189953134, 13.3788117697, -1.286630),
    };

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name + "  " + detail);
        if (!ok) _failed++;
    }

    static int Main()
    {
        const double m = 111320.0;   // metres per degree latitude (roughly)
        foreach (var r in Refs)
        {
            GeoProj.ToLatLon(r.Epsg, r.E, r.N, out double lat, out double lon);
            double dn = (lat - r.Lat) * m, de = (lon - r.Lon) * m * Math.Cos(r.Lat * Math.PI / 180);
            double d = Math.Sqrt(dn * dn + de * de);
            Check($"EPSG:{r.Epsg} {r.E:0} {r.N:0} → lat/lon", d < 0.001, $"{d * 1000:0.000} mm from pyproj");

            GeoProj.ToMap(r.Epsg, r.Lat, r.Lon, out double e, out double n);
            // the 2D datum shift (height 0 on either side) is not exactly reversible: pyproj's
            // WGS84 → LV95 of these points gives +0.6 mm E, +1.1 mm N, and so must GeoProj
            double re = r.E + (r.Epsg == 2056 ? 0.0006 : 0), rn = r.N + (r.Epsg == 2056 ? 0.0011 : 0);
            double dm = Math.Sqrt((e - re) * (e - re) + (n - rn) * (n - rn));
            Check($"EPSG:{r.Epsg} lat/lon → {r.E:0} {r.N:0}", dm < 0.001, $"{dm * 1000:0.000} mm");

            double c = GeoProj.Convergence(r.Epsg, r.Lat, r.Lon);
            Check($"EPSG:{r.Epsg} convergence", Math.Abs(c - r.Conv) < 0.0005, $"{c:0.000000}° (pyproj {r.Conv:0.000000}°)");
        }

        Check("DefaultEpsg Pfäfers", GeoProj.DefaultEpsg(46.987, 9.476) == 2056, "");
        Check("DefaultEpsg Bonn", GeoProj.DefaultEpsg(50.73, 7.10) == 25832, "");
        Check("DefaultEpsg Berlin", GeoProj.DefaultEpsg(52.52, 13.40) == 25833, "");
        Check("DefaultEpsg Chicago", GeoProj.DefaultEpsg(41.88, -87.62) == 32616, "");
        Check("DefaultEpsg Bergen (32V)", GeoProj.DefaultEpsg(60.39, 5.32) == 25832, "");
        Check("DefaultEpsg Sydney", GeoProj.DefaultEpsg(-33.87, 151.21) == null, "");

        P("2'755'000.00, 1'206'000.00", PositionText.Kinds.Map, 2755000, 1206000, null);
        P("2755000,1206000,0,12.5", PositionText.Kinds.Map, 2755000, 1206000, 0);
        P("1206000 2755000", PositionText.Kinds.Map, 2755000, 1206000, null);
        P("46.98695, 9.47632", PositionText.Kinds.LatLon, 46.98695, 9.47632, null);
        P("46.98695, 9.47632, 830", PositionText.Kinds.LatLon, 46.98695, 9.47632, 830);
        P("46°59'13.0\"N 9°28'34.8\"E", PositionText.Kinds.LatLon, 46 + 59 / 60.0 + 13.0 / 3600, 9 + 28 / 60.0 + 34.8 / 3600, null);
        P("9°28'34.8\"E 46°59'13.0\"N", PositionText.Kinds.LatLon, 46 + 59 / 60.0 + 13.0 / 3600, 9 + 28 / 60.0 + 34.8 / 3600, null);
        P("Valens Post", PositionText.Kinds.Address, 0, 0, null);
        var bad = PositionText.Parse("46,9123 9,4877");
        Check("decimal commas are refused", bad == null, bad == null ? "" : bad.Kind.ToString());

        Console.WriteLine(_failed == 0 ? "all geo checks passed" : _failed + " geo checks FAILED");
        return _failed == 0 ? 0 : 1;
    }

    static void P(string text, PositionText.Kinds kind, double a, double b, double? h)
    {
        var p = PositionText.Parse(text);
        bool ok = p != null && p.Kind == kind;
        if (ok && kind == PositionText.Kinds.Map) ok = Math.Abs(p.E - a) < 1e-6 && Math.Abs(p.N - b) < 1e-6 && p.H == h;
        if (ok && kind == PositionText.Kinds.LatLon) ok = Math.Abs(p.Lat - a) < 1e-9 && Math.Abs(p.Lon - b) < 1e-9 && p.H == h;
        Check("parse \"" + text + "\"", ok, p == null ? "null" : $"{p.Kind} E {p.E} N {p.N} lat {p.Lat} lon {p.Lon} H {p.H}");
    }
}
