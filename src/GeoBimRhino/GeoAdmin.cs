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
    /// swisstopo's public geo.admin.ch API (Switzerland only, no key): place and address search
    /// (SearchServer, type=locations) and the terrain height at an LV95 point (height service,
    /// swissALTI3D). Only called when the user searches for an address or picks a point on the map.
    /// </summary>
    public static class GeoAdmin
    {
        const string Api = "https://api3.geo.admin.ch/rest/services";
        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("geoBIM.app-Rhino/" + GeoBimPlugin.PluginVersion);
            return c;
        }

        public class Hit
        {
            public string Label;
            public double E, N;   // LV95
        }

        /// <summary>LV95 range of Switzerland and Liechtenstein (with a margin)</summary>
        public static bool InSwitzerland(double e, double n) => e >= 2470000 && e <= 2850000 && n >= 1060000 && n <= 1310000;

        /// <summary>Places, addresses, parcels … for the text; empty when nothing matches</summary>
        public static async Task<List<Hit>> Search(string text, int limit = 8)
        {
            var url = $"{Api}/api/SearchServer?searchText={Uri.EscapeDataString(text)}&type=locations&sr=2056&limit={limit}";
            using (var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false)))
            {
                var hits = new List<Hit>();
                if (!doc.RootElement.TryGetProperty("results", out var results)) return hits;
                foreach (var r in results.EnumerateArray())
                {
                    if (!r.TryGetProperty("attrs", out var a)) continue;
                    // SearchServer with sr=2056: y = easting, x = northing
                    if (!a.TryGetProperty("y", out var y) || !a.TryGetProperty("x", out var x)) continue;
                    string label = a.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                    label = Regex.Replace(label, "<[^>]+>", "").Trim();
                    hits.Add(new Hit { Label = label, E = y.GetDouble(), N = x.GetDouble() });
                }
                return hits;
            }
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
