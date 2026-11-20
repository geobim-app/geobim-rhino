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
using System.Threading;
using System.Threading.Tasks;

namespace GeoBimRhino
{
    /// <summary>A search hit: what it is, where it comes from, where it lies</summary>
    public class Place
    {
        public string Label;
        public string Source;       // Places.Swisstopo or Places.Osm
        public double Lat, Lon;     // WGS84
        public double? E, N;        // LV95, for swisstopo hits
    }

    /// <summary>
    /// Place and address search for GeoBIMLocation: swisstopo for Switzerland and Liechtenstein
    /// (GeoAdmin), OpenStreetMap's Nominatim for everywhere else, asked in parallel; swisstopo's
    /// hits come first. No Rhino types: tests/geo_check checks the parsing.
    /// </summary>
    public static class Places
    {
        public const string Swisstopo = "swisstopo";
        public const string Osm = "OpenStreetMap";

        public class Result
        {
            public List<Place> Hits = new List<Place>();
            public string Note;     // one search failed while the other worked, else null
        }

        /// <summary>Both searches in parallel; throws only when both fail</summary>
        public static async Task<Result> Search(string text, int limit = 8)
        {
            var ch = GeoAdmin.Search(text, limit);
            var osm = Nominatim.Search(text, limit);
            try { await Task.WhenAll(ch, osm).ConfigureAwait(false); }
            catch { /* looked at per task below */ }
            if (ch.IsFaulted && osm.IsFaulted) throw ch.Exception.InnerException ?? ch.Exception;
            var res = new Result();
            if (!ch.IsFaulted) res.Hits.AddRange(ch.Result);
            if (!osm.IsFaulted) res.Hits.AddRange(osm.Result);
            if (ch.IsFaulted) res.Note = "swisstopo did not answer (" + Message(ch.Exception) + "), only OpenStreetMap was searched";
            else if (osm.IsFaulted) res.Note = "OpenStreetMap did not answer (" + Message(osm.Exception) + "), only Switzerland was searched";
            return res;
        }

        static string Message(AggregateException ex) => (ex?.InnerException ?? ex)?.Message ?? "error";
    }

    /// <summary>
    /// OpenStreetMap's Nominatim (nominatim.openstreetmap.org), used as its usage policy asks: an
    /// application User-Agent, at most one request per second, no search while typing (only on
    /// Enter), results cached, "© OpenStreetMap contributors" shown with the hits. Hits in
    /// Switzerland and Liechtenstein are dropped (swisstopo has them, with LV95 coordinates).
    /// </summary>
    public static class Nominatim
    {
        public const string Attribution = "© OpenStreetMap contributors (Nominatim)";
        const string Url = "https://nominatim.openstreetmap.org/search";
        static readonly HttpClient Http = CreateClient();
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        static readonly Dictionary<string, List<Place>> Cache = new Dictionary<string, List<Place>>(StringComparer.OrdinalIgnoreCase);
        static DateTime _last = DateTime.MinValue;

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(GeoAdmin.UserAgent);
            return c;
        }

        public static async Task<List<Place>> Search(string text, int limit = 8)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return new List<Place>();
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Cache.TryGetValue(text, out var cached)) return new List<Place>(cached);
                var wait = _last.AddMilliseconds(1100) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
                string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var url = $"{Url}?q={Uri.EscapeDataString(text)}&format=jsonv2&addressdetails=1&limit={limit}" +
                          (lang.Length == 2 ? "&accept-language=" + lang : "");
                string json;
                try { json = await Http.GetStringAsync(url).ConfigureAwait(false); }
                finally { _last = DateTime.UtcNow; }
                var hits = Parse(json);
                if (Cache.Count > 100) Cache.Clear();
                Cache[text] = hits;
                return new List<Place>(hits);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Nominatim's jsonv2 answer (with addressdetails) → hits outside Switzerland and Liechtenstein</summary>
        public static List<Place> Parse(string json)
        {
            var hits = new List<Place>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return hits;
                foreach (var r in doc.RootElement.EnumerateArray())
                {
                    string cc = r.TryGetProperty("address", out var a) && a.TryGetProperty("country_code", out var c) ? c.GetString() ?? "" : "";
                    if (cc.Equals("ch", StringComparison.OrdinalIgnoreCase) || cc.Equals("li", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Num(r, "lat", out double lat) || !Num(r, "lon", out double lon)) continue;
                    string label = r.TryGetProperty("display_name", out var d) ? d.GetString() ?? "" : "";
                    hits.Add(new Place { Label = label, Source = Places.Osm, Lat = lat, Lon = lon });
                }
            }
            return hits;
        }

        static bool Num(JsonElement r, string name, out double v)
        {
            v = 0;
            if (!r.TryGetProperty(name, out var p)) return false;
            if (p.ValueKind == JsonValueKind.Number) return p.TryGetDouble(out v);
            return p.ValueKind == JsonValueKind.String &&
                   double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
