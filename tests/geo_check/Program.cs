// Checks GeoProj against pyproj 3.7.2 / PROJ 9.5.1 (reference values computed with
// Transformer.from_crs(epsg, 4326) and Proj.get_factors(...).meridian_convergence),
// PositionText against the formats map.geo.admin.ch and Google Maps copy, and the parsing
// of swisstopo's and Nominatim's search answers (samples in the shape both services send), and
// the embedded geoid grids against PROJ's full grids (us_nga_egm08_25.tif, de_bkg_gcg2016.tif).
// "dotnet run online" also searches both services once for "Lindau".
using System;
using System.Linq;
using GeoBimRhino;

static class Program
{
    static int _failed;

    sealed class R
    {
        public int Epsg; public double E, N, Lat, Lon, Conv;
        public R(int epsg, double e, double n, double lat, double lon, double conv) { Epsg = epsg; E = e; N = n; Lat = lat; Lon = lon; Conv = conv; }
    }

    sealed class G
    {
        public string Name; public double Lat, Lon, N; public int Epsg; public string Model;
        public G(string name, double lat, double lon, int epsg, double n, string model) { Name = name; Lat = lat; Lon = lon; Epsg = epsg; N = n; Model = model; }
    }

    // exact N from pyproj 3.7.2 with the full grids (EPSG:4326+3855 → 4979, EPSG:4258+7837 → 4937)
    static readonly G[] Geoids =
    {
        new G("Pfaefers (LV95)", 46.986950, 9.476320, 2056, 48.715, "EGM"),
        new G("Bern (LV95)", 46.951080, 7.438630, 2056, 49.372, "EGM"),
        new G("Basel (LV95, near Germany)", 47.559600, 7.588600, 2056, 49.316, "EGM"),
        new G("Muenchen", 48.137400, 11.575500, 25832, 45.658, "GCG"),
        new G("Berlin", 52.516300, 13.377700, 25833, 39.318, "GCG"),
        new G("Hamburg", 53.551100, 9.993700, 25832, 39.942, "GCG"),
        new G("Garmisch (Zugspitze area)", 47.421000, 10.985300, 25832, 49.556, "GCG"),
        new G("Wien", 48.208200, 16.373800, 25833, 44.730, "EGM"),
        new G("Paris", 48.856600, 2.352200, 25831, 44.612, "EGM"),
        new G("Chicago", 41.882684, -87.623328, 32616, -33.939, "EGM"),
    };

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

    static int Main(string[] args)
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

        // geoid: GCG2016 subset within 5 cm, EGM2008 5' within 8 cm, the 1° grid within 2.5 m
        foreach (var g in Geoids)
        {
            var r = Geoid.At(g.Lat, g.Lon, g.Epsg);
            bool model = r != null && (g.Model == "GCG" ? r.Model == "GCG2016" : r.Model.StartsWith("EGM2008"));
            double tol = r == null ? 0 : r.Model == "GCG2016" ? 0.05 : r.Coarse ? 2.5 : 0.08;
            Check($"geoid {g.Name}", model && Math.Abs(r.N - g.N) <= tol,
                  r == null ? "no grid" : $"{r.N:0.000} m {r.Model} (PROJ {g.N:0.000}, {r.N - g.N:+0.000;-0.000})");
        }

        // search answers
        var ch = GeoAdmin.ParseSearch(@"{""results"":[
            {""attrs"":{""label"":""<i>Ort</i> <b>Valens</b> (SG) - Pfäfers"",""lat"":46.9693756,""lon"":9.4811401,""x"":1204056.25,""y"":2755417.25},""id"":245836,""weight"":100},
            {""attrs"":{""label"":""without coordinates""},""id"":2,""weight"":1}]}");
        Check("swisstopo answer: one hit, label without tags, LV95 from y/x", ch.Count == 1 && ch[0].Label == "Ort Valens (SG) - Pfäfers"
              && ch[0].E == 2755417.25 && ch[0].N == 1204056.25 && ch[0].Source == Places.Swisstopo, ch.Count > 0 ? ch[0].Label : "none");
        Check("swisstopo answer: WGS84 from LV95", ch.Count == 1 && Math.Abs(ch[0].Lat - 46.9693756) < 1e-5 && Math.Abs(ch[0].Lon - 9.4811401) < 1e-5,
              ch.Count > 0 ? $"{ch[0].Lat:0.0000000}, {ch[0].Lon:0.0000000}" : "");
        var osm = Nominatim.Parse(@"[
            {""lat"":""47.5728668"",""lon"":""9.6961919"",""display_name"":""Lindau (Bodensee), Landkreis Lindau, Bayern, 88131, Deutschland"",""address"":{""city"":""Lindau (Bodensee)"",""country_code"":""de""}},
            {""lat"":""47.4426676"",""lon"":""8.6722839"",""display_name"":""Lindau, Bezirk Pfäffikon, Zürich, Schweiz"",""address"":{""country_code"":""ch""}},
            {""lat"":""47.1410"",""lon"":""9.5209"",""display_name"":""Vaduz, Liechtenstein"",""address"":{""country_code"":""li""}},
            {""display_name"":""without position"",""address"":{""country_code"":""de""}}]");
        Check("Nominatim answer: hits in Switzerland and Liechtenstein and without position dropped", osm.Count == 1 && osm[0].Label.StartsWith("Lindau (Bodensee)")
              && osm[0].Lat == 47.5728668 && osm[0].Lon == 9.6961919 && osm[0].E == null && osm[0].Source == Places.Osm, osm.Count + " hits");

        if (args.Contains("online"))
        {
            var res = Places.Search("Lindau").GetAwaiter().GetResult();
            foreach (var h in res.Hits) Console.WriteLine($"     {h.Source,-13} {h.Lat,10:0.00000} {h.Lon,10:0.00000}  {h.Label}");
            if (res.Note != null) Console.WriteLine("     note: " + res.Note);
            Check("online: swisstopo finds Lindau ZH", res.Hits.Any(h => h.Source == Places.Swisstopo), "");
            Check("online: OpenStreetMap finds a Lindau outside Switzerland", res.Hits.Any(h => h.Source == Places.Osm), "");
            var again = Places.Search("Lindau").GetAwaiter().GetResult();
            Check("online: second search answered (Nominatim from the cache)", again.Hits.Count == res.Hits.Count, again.Hits.Count + " hits");
        }

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
