// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GeoBimRhino
{
    /// <summary>
    /// swisstopo's public geo.admin.ch API (Switzerland and Liechtenstein, no key): place and
    /// address search (SearchServer, type=locations) and the terrain height at an LV95 point
    /// (height service, swissALTI3D). Only called when the user searches or picks a point on the
    /// map. No Rhino types: tests/geo_check checks the parsing.
    /// </summary>
    public static class GeoAdmin
    {
        const string Api = "https://api3.geo.admin.ch/rest/services";

        /// <summary>The plugin's User-Agent for swisstopo and Nominatim: name, version, web site (nothing about the user)</summary>
        public static readonly string UserAgent =
            "geoBIM.app-Rhino/" + (typeof(GeoAdmin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0") + " (+https://geobim.app)";

        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return c;
        }

        /// <summary>LV95 range of Switzerland and Liechtenstein (with a margin)</summary>
        public static bool InSwitzerland(double e, double n) => e >= 2470000 && e <= 2850000 && n >= 1060000 && n <= 1310000;

        /// <summary>Places, addresses, parcels … for the text; empty when nothing matches</summary>
        public static async Task<List<Place>> Search(string text, int limit = 8)
        {
            var url = $"{Api}/api/SearchServer?searchText={Uri.EscapeDataString(text)}&type=locations&sr=2056&limit={limit}";
            return ParseSearch(await Http.GetStringAsync(url).ConfigureAwait(false));
        }

        /// <summary>SearchServer's answer (sr=2056: y = easting, x = northing) → hits with LV95 and WGS84 positions</summary>
        public static List<Place> ParseSearch(string json)
        {
            var hits = new List<Place>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("results", out var results)) return hits;
                foreach (var r in results.EnumerateArray())
                {
                    if (!r.TryGetProperty("attrs", out var a)) continue;
                    if (!a.TryGetProperty("y", out var y) || !a.TryGetProperty("x", out var x)) continue;
                    string label = a.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                    label = Regex.Replace(label, "<[^>]+>", "").Trim();
                    double e = y.GetDouble(), n = x.GetDouble();
                    GeoProj.ToLatLon(2056, e, n, out double lat, out double lon);
                    hits.Add(new Place { Label = label, Source = Places.Swisstopo, E = e, N = n, Lat = lat, Lon = lon });
                }
            }
            return hits;
        }

        /// <summary>Terrain height (m above sea level, LN02) at an LV95 point, or null outside Switzerland or on errors</summary>
        public static async Task<double?> Height(double e, double n)
        {
            if (!InSwitzerland(e, n)) return null;
            try
            {
                var url = string.Format(CultureInfo.InvariantCulture, "{0}/height?easting={1:0.##}&northing={2:0.##}&sr=2056", Api, e, n);
                using (var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false)))
                {
                    if (!doc.RootElement.TryGetProperty("height", out var h)) return null;
                    var s = h.ValueKind == JsonValueKind.String ? h.GetString() : h.GetRawText();
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
