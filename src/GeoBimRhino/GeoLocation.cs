// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace GeoBimRhino
{
    /// <summary>Where GLB and Cesium ion put the model: a model point, its WGS84 position and height, true north in the model</summary>
    public class GeoAnchor
    {
        public Point3d BasePoint;            // model units
        public Vector3d North;               // true north in model coordinates (unit vector in the XY plane)
        public double Lat, Lon, Elevation;   // degrees; metres above sea level at BasePoint
        public string Source;                // for messages: "map origin (LV95)", "Rhino's Location", …
        public int? Epsg;                    // coordinate system of the map position, null for Rhino's Location
    }

    /// <summary>
    /// One position for every export. The map origin (or geometry in map coordinates) is the
    /// reference whenever its coordinate system converts (LV95, UTM — see GeoProj); GLB and
    /// Cesium ion then take their latitude/longitude and true north from it, so IFC, GLB and
    /// ion land in the same place. Rhino's Location (EarthAnchorPoint) is used only when there
    /// is no such map position. GeoBIMLocation sets both from one input.
    /// </summary>
    public static class GeoLocation
    {
        static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

        /// <summary>EPSG of a map position without asking: fixed setting, the document's choice, else an unambiguous detection</summary>
        public static int? KnownEpsg(RhinoDoc doc, double e, double n)
        {
            var p = GeoBimPlugin.Instance;
            var setting = (p.Epsg ?? "auto").Replace("EPSG:", "").Trim();
            if (!setting.Equals("auto", StringComparison.OrdinalIgnoreCase) && int.TryParse(setting, out int fixedCode)) return fixedCode;
            if (int.TryParse(doc.Strings.GetValue("geobim", Shared.DocEpsgKey) ?? "", out int docCode)) return docCode;
            return CrsDetect.Guess(e, n, p.DefaultUtmEpsg).Epsg;
        }

        /// <summary>True north in local model coordinates at a WGS84 position, for local axes turned by rotDeg (map origin rotation)</summary>
        public static Vector3d NorthInModel(int epsg, double lat, double lon, double rotDeg)
        {
            double g = GeoProj.Convergence(epsg, lat, lon) * Math.PI / 180;   // true north, counter-clockwise from grid north
            double gx = -Math.Sin(g), gy = Math.Cos(g);                       // in the map grid
            double t = rotDeg * Math.PI / 180;                                // local = R(−t) · grid
            var v = new Vector3d(Math.Cos(t) * gx + Math.Sin(t) * gy, -Math.Sin(t) * gx + Math.Cos(t) * gy, 0);
            v.Unitize();
            return v;
        }

        /// <summary>
        /// Placement for GLB and Cesium ion with the model centre (cx, cy in metres) as base point:
        /// from the map origin or the map coordinates when their coordinate system converts,
        /// else from Rhino's Location; null without any. note: why the map position was not used.
        /// </summary>
        public static GeoAnchor For(RhinoDoc doc, MapOrigin mo, double cx, double cy, double scale, out string note)
        {
            note = null;
            double bx = Math.Round(cx), by = Math.Round(cy);
            bool inMap = CrsDetect.LooksLikeMap(cx, cy);
            if (mo != null && !inMap)
            {
                mo.ToMap(bx, by, out double e, out double n);
                int? epsg = KnownEpsg(doc, e, n);
                if (epsg != null && GeoProj.ToLatLon(epsg.Value, e, n, out double lat, out double lon))
                    return new GeoAnchor
                    {
                        BasePoint = new Point3d(bx / scale, by / scale, 0), Lat = lat, Lon = lon, Elevation = mo.H,
                        North = NorthInModel(epsg.Value, lat, lon, mo.RotationDeg), Source = $"map origin ({GeoProj.Name(epsg.Value)})",
                        Epsg = epsg
                    };
                note = epsg == null
                    ? "the map origin's coordinate system is not known yet (export an IFC once, or set it with GeoBIMSettings → EPSG)"
                    : $"the plugin cannot convert EPSG:{epsg} (only LV95 and UTM)";
            }
            else if (inMap)
            {
                int? epsg = KnownEpsg(doc, bx, by);
                if (epsg != null && GeoProj.ToLatLon(epsg.Value, bx, by, out double lat, out double lon))
                    return new GeoAnchor
                    {
                        BasePoint = new Point3d(bx / scale, by / scale, 0), Lat = lat, Lon = lon, Elevation = 0,
                        North = NorthInModel(epsg.Value, lat, lon, 0), Source = $"map coordinates ({GeoProj.Name(epsg.Value)})",
                        Epsg = epsg
                    };
                note = epsg == null
                    ? "the coordinate system of the map coordinates is ambiguous (set it with GeoBIMSettings → EPSG)"
                    : $"the plugin cannot convert EPSG:{epsg} (only LV95 and UTM)";
            }
            var a = doc.EarthAnchorPoint;
            if (a != null && a.EarthLocationIsSet())
            {
                var north = a.ModelNorth;
                north.Z = 0;
                if (!north.Unitize()) north = Vector3d.YAxis;
                return new GeoAnchor
                {
                    BasePoint = a.ModelBasePoint, Lat = a.EarthBasepointLatitude, Lon = a.EarthBasepointLongitude,
                    Elevation = a.EarthBasepointElevation, North = north, Source = "Rhino's Location"
                };
            }
            return null;
        }

        /// <summary>Map coordinates (metres) of a model point from the map origin or the map coordinates themselves; false if there are none</summary>
        public static bool ModelToMap(RhinoDoc doc, Point3d pt, out double e, out double n, out double h, out int? epsg)
        {
            double scale = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
            double x = pt.X * scale, y = pt.Y * scale, z = pt.Z * scale;
            var mo = MapOrigin.FromDoc(doc);
            e = n = h = 0;
            epsg = null;
            if (CrsDetect.LooksLikeMap(x, y)) { e = x; n = y; h = z; }
            else if (mo != null) { mo.ToMap(x, y, out e, out n); h = mo.H + z; }
            else return false;
            epsg = KnownEpsg(doc, e, n);
            return true;
        }

        /// <summary>WGS84 position of a model point: map position when it converts, else Rhino's Location; false without either</summary>
        public static bool ModelToLatLon(RhinoDoc doc, Point3d pt, out double lat, out double lon)
        {
            lat = lon = 0;
            if (ModelToMap(doc, pt, out double e, out double n, out _, out int? epsg) && epsg != null &&
                GeoProj.ToLatLon(epsg.Value, e, n, out lat, out lon)) return true;
            var a = doc.EarthAnchorPoint;
            if (a == null || !a.EarthLocationIsSet()) return false;
            // small offsets from the Location: east/north metres on the WGS84 ellipsoid
            double scale = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
            var north = a.ModelNorth;
            north.Z = 0;
            if (!north.Unitize()) north = Vector3d.YAxis;
            var east = new Vector3d(north.Y, -north.X, 0);
            var d = (pt - a.ModelBasePoint) * scale;
            double phi = a.EarthBasepointLatitude * Math.PI / 180, e2 = 0.00669437999014;
            double w = Math.Sqrt(1 - e2 * Math.Sin(phi) * Math.Sin(phi));
            double rn = 6378137.0 / w, rm = 6378137.0 * (1 - e2) / (w * w * w);
            lat = a.EarthBasepointLatitude + d * north / rm * 180 / Math.PI;
            lon = a.EarthBasepointLongitude + d * east / (rn * Math.Cos(phi)) * 180 / Math.PI;
            return true;
        }

        /// <summary>
        /// Stores the map origin and its coordinate system (for the IFC export) and sets Rhino's
        /// Location to the same place: model origin as base point, height above sea level, true
        /// north from the meridian convergence. False when the coordinate system does not convert
        /// (then only the map origin is stored).
        /// </summary>
        public static bool Apply(RhinoDoc doc, MapOrigin mo, int epsg)
        {
            mo.Save(doc);
            doc.Strings.SetString("geobim", Shared.DocEpsgKey, epsg.ToString(CultureInfo.InvariantCulture));
            if (!GeoProj.ToLatLon(epsg, mo.E, mo.N, out double lat, out double lon)) return false;
            var north = NorthInModel(epsg, lat, lon, mo.RotationDeg);
            doc.EarthAnchorPoint = new EarthAnchorPoint
            {
                EarthBasepointLatitude = lat,
                EarthBasepointLongitude = lon,
                EarthBasepointElevation = mo.H,
                EarthBasepointElevationZero = BasepointZero.MeanSeaLevel,
                ModelBasePoint = Point3d.Origin,
                ModelNorth = north,
                ModelEast = new Vector3d(north.Y, -north.X, 0),
                Name = "geoBIM.app",
                Description = $"{GeoProj.Name(epsg)} E {F(mo.E, "0.###")} N {F(mo.N, "0.###")} (set by GeoBIMLocation)"
            };
            return true;
        }

        /// <summary>
        /// How far Rhino's Location is from the position the map origin gives the same model
        /// point (metres) and how far its north is turned (degrees); null when there is nothing
        /// to compare (no map origin, no Location, or a coordinate system that does not convert).
        /// </summary>
        public static (double Distance, double NorthDeg)? Mismatch(RhinoDoc doc)
        {
            var a = doc.EarthAnchorPoint;
            if (a == null || !a.EarthLocationIsSet()) return null;
            if (!ModelToMap(doc, a.ModelBasePoint, out double e, out double n, out _, out int? epsg) || epsg == null) return null;
            if (!GeoProj.ToMap(epsg.Value, a.EarthBasepointLatitude, a.EarthBasepointLongitude, out double ae, out double an)) return null;
            double dist = Math.Sqrt((ae - e) * (ae - e) + (an - n) * (an - n));
            // local model with a map origin: its rotation; geometry in map coordinates: none
            double scale = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
            var mo = MapOrigin.FromDoc(doc);
            bool local = !CrsDetect.LooksLikeMap(a.ModelBasePoint.X * scale, a.ModelBasePoint.Y * scale);
            var expected = NorthInModel(epsg.Value, a.EarthBasepointLatitude, a.EarthBasepointLongitude,
                local && mo != null ? mo.RotationDeg : 0);
            var actual = a.ModelNorth;
            actual.Z = 0;
            double angle = actual.Unitize() ? Vector3d.VectorAngle(expected, actual) * 180 / Math.PI : 0;
            return (dist, angle);
        }

        /// <summary>A warning line when Rhino's Location and the map position disagree (more than 0.5 m or 0.05°), else null</summary>
        public static string MismatchWarning(RhinoDoc doc)
        {
            var m = Mismatch(doc);
            if (m == null || (m.Value.Distance < 0.5 && m.Value.NorthDeg < 0.05)) return null;
            return string.Format(CultureInfo.InvariantCulture,
                "geoBIM.app: note — Rhino's Location is {0:0.##} m and {1:0.##}° away from the map origin; GLB and Cesium ion follow the map origin. GeoBIMLocation sets both to the same place.",
                m.Value.Distance, m.Value.NorthDeg);
        }

        /// <summary>One line on where the document is, for the command line</summary>
        public static string Describe(RhinoDoc doc)
        {
            var mo = MapOrigin.FromDoc(doc);
            var a = doc.EarthAnchorPoint;
            bool hasLoc = a != null && a.EarthLocationIsSet();
            if (mo != null)
            {
                int? epsg = KnownEpsg(doc, mo.E, mo.N);
                string crs = epsg != null ? GeoProj.Name(epsg.Value) : "coordinate system not chosen yet";
                string ll = epsg != null && GeoProj.ToLatLon(epsg.Value, mo.E, mo.N, out double lat, out double lon)
                    ? $" = {F(lat, "0.000000")}° N, {F(lon, "0.000000")}° E" : "";
                string sync = MismatchWarning(doc) != null ? " — Rhino's Location differs" : hasLoc ? ", Rhino's Location matches" : ", Rhino's Location not set";
                return $"geoBIM.app: model origin at {crs} {mo.Describe()}{ll}{sync}.";
            }
            if (hasLoc)
                return $"geoBIM.app: no map origin; Rhino's Location {F(a.EarthBasepointLatitude, "0.000000")}° N, {F(a.EarthBasepointLongitude, "0.000000")}° E, {F(a.EarthBasepointElevation, "0.##")} m.";
            return "geoBIM.app: this model has no position yet.";
        }

        /// <summary>Outline of the visible objects (bounding box corners) as model points, or null when there are none</summary>
        public static List<Point3d> Footprint(RhinoDoc doc)
        {
            var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false, ObjectTypeFilter = Shared.Exportable };
            var bbox = BoundingBox.Empty;
            foreach (var o in doc.Objects.GetObjectList(settings)) bbox.Union(o.Geometry.GetBoundingBox(true));
            if (!bbox.IsValid) return null;
            double z = bbox.Min.Z;
            return new List<Point3d>
            {
                new Point3d(bbox.Min.X, bbox.Min.Y, z), new Point3d(bbox.Max.X, bbox.Min.Y, z),
                new Point3d(bbox.Max.X, bbox.Max.Y, z), new Point3d(bbox.Min.X, bbox.Max.Y, z)
            };
        }
    }
}
