// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Globalization;
using Rhino;

namespace GeoBimRhino
{
    /// <summary>
    /// Map coordinates of the model origin, kept in the document so the geometry
    /// can stay in small local coordinates (x = E − E0, y = N − N0, z = H − H0).
    /// The export writes them into IfcMapConversion; nothing in the model moves.
    /// Rotation: angle of the local x axis counter-clockwise from map east, in
    /// degrees (0 when the local axes are parallel to the map grid).
    /// Stored as "E0,N0,H0,rotation" (invariant culture) in doc.Strings
    /// section "geobim", key "mapOrigin".
    /// </summary>
    public class MapOrigin
    {
        public const string DocKey = "mapOrigin";

        public double E, N, H, RotationDeg;

        public double XAxisAbscissa => Math.Cos(RotationDeg * Math.PI / 180.0);
        public double XAxisOrdinate => Math.Sin(RotationDeg * Math.PI / 180.0);

        /// <summary>Map coordinates of a local point (metres)</summary>
        public void ToMap(double x, double y, out double e, out double n)
        {
            double c = XAxisAbscissa, s = XAxisOrdinate;
            e = E + c * x - s * y;
            n = N + s * x + c * y;
        }

        /// <summary>"E,N[,H[,rotation]]", separated by commas, semicolons or spaces; null when not parseable</summary>
        public static MapOrigin Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Length > 4) return null;
            var v = new double[4];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) ||
                    double.IsNaN(v[i]) || double.IsInfinity(v[i])) return null;
            }
            return new MapOrigin { E = v[0], N = v[1], H = v[2], RotationDeg = v[3] };
        }

        public static MapOrigin FromDoc(RhinoDoc doc) => doc == null ? null : Parse(doc.Strings.GetValue("geobim", DocKey));

        public void Save(RhinoDoc doc) => doc.Strings.SetString("geobim", DocKey, ToString());

        public static void Clear(RhinoDoc doc) => doc.Strings.Delete("geobim", DocKey);

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###},{3:0.######}", E, N, H, RotationDeg);

        public string Describe() =>
            string.Format(CultureInfo.InvariantCulture, "E {0:0.###} N {1:0.###} H {2:0.###}{3}", E, N, H,
                RotationDeg != 0 ? $", rotation {RotationDeg.ToString("0.######", CultureInfo.InvariantCulture)}°" : "");
    }
}
