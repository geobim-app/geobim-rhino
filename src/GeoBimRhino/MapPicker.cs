// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GeoBimRhino
{
    /// <summary>
    /// A map window (Eto WebView with Leaflet 1.9.4 from unpkg, pinned by its SRI hash): swisstopo's
    /// national map and aerial images (Switzerland) and BKG's TopPlusOpen (Europe, world; taken
    /// automatically outside Switzerland), a search box, the model outline. Pick mode: a click
    /// places the marker, the outline follows it, OK returns latitude/longitude. Show mode: just
    /// the map. After a pick the page reads the terrain height there from Mapterhorn's Terrarium
    /// tiles (tiles.mapterhorn.com, heights above sea level; the browser decodes the WebP).
    /// The page talks to the plugin through document.title: "geobim:lat,lon" reports a
    /// click, "geobim:lat,lon,height" adds the terrain, "geobim-search:n:text" asks for a search, which the plugin runs (Places: swisstopo
    /// and Nominatim, with the plugin's User-Agent and Nominatim's rate limit) and hands back with
    /// showHits(…).
    /// </summary>
    public static class MapPicker
    {
        public class Options
        {
            public string Title = "geoBIM.app — position";
            public bool Pick = true;
            public double? Lat, Lon;                               // marker at the start
            public List<(double East, double North)> Outline;      // pick: outline in metres east/north of the marker
            public List<(double Lat, double Lon)> OutlineLatLon;   // show: outline at its place
            public bool Swiss;                                     // start on swisstopo's map (else BKG's)
        }

        /// <summary>The picked position and, when Mapterhorn answered, the terrain height there</summary>
        public class Pick
        {
            public double Lat, Lon;
            public double? Terrain;   // metres above sea level (Mapterhorn)
        }

        /// <summary>Shows the map; in pick mode the clicked position, else null</summary>
        public static Pick Show(Options o)
        {
            Pick picked = null;
            var web = new Eto.Forms.WebView();
            var info = new Eto.Forms.Label { Text = o.Pick ? "Click the point on the map, or search a place or address." : "Orange: the model's outline." };
            var ok = new Eto.Forms.Button { Text = o.Pick ? "OK" : "Close", Enabled = !o.Pick };
            var cancel = new Eto.Forms.Button { Text = "Cancel", Visible = o.Pick };
            var dlg = new Eto.Forms.Dialog<bool> { Title = o.Title, Resizable = true, ClientSize = new Eto.Drawing.Size(1000, 700), Padding = 0 };
            web.DocumentTitleChanged += (s, e) =>
            {
                var t = e.Title ?? "";
                if (t.StartsWith("geobim-search:", StringComparison.Ordinal))
                {
                    var rest = t.Substring(14);
                    int k = rest.IndexOf(':');
                    RunSearch(web, k >= 0 ? rest.Substring(k + 1) : rest);
                    return;
                }
                if (!t.StartsWith("geobim:", StringComparison.Ordinal)) return;
                var parts = t.Substring(7).Split(',');
                if ((parts.Length == 2 || parts.Length == 3) &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                {
                    double? h = parts.Length == 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double hv)
                        ? hv : (double?)null;
                    // the height arrives after the click; it only counts for the position it was read for
                    if (h != null && (picked == null || picked.Lat != lat || picked.Lon != lon)) return;
                    if (h == null) picked = new Pick { Lat = lat, Lon = lon };
                    else picked.Terrain = h;
                    info.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.000000}° N, {1:0.000000}° E", lat, lon)
                                + (picked.Terrain != null ? string.Format(CultureInfo.InvariantCulture, " — terrain {0:0.0} m (Mapterhorn)", picked.Terrain) : "");
                    ok.Enabled = true;
                }
            };
            ok.Click += (s, e) => dlg.Close(true);
            cancel.Click += (s, e) => dlg.Close(false);
            dlg.DefaultButton = ok;
            dlg.AbortButton = o.Pick ? cancel : ok;

            var buttons = new Eto.Forms.StackLayout { Orientation = Eto.Forms.Orientation.Horizontal, Spacing = 6, Padding = new Eto.Drawing.Padding(10, 8) };
            buttons.Items.Add(new Eto.Forms.StackLayoutItem(info, Eto.Forms.VerticalAlignment.Center, true));
            buttons.Items.Add(ok);
            buttons.Items.Add(cancel);
            var layout = new Eto.Forms.DynamicLayout();
            layout.Add(web, yscale: true);
            layout.Add(buttons);
            dlg.Content = layout;
            web.LoadHtml(Html(o));
            bool result = dlg.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);
            return o.Pick && result ? picked : null;
        }

        /// <summary>Search for the page off the UI thread; the hits go back as showHits({hits, note, osm}) or showHits({error})</summary>
        static void RunSearch(Eto.Forms.WebView web, string text)
        {
            Task.Run(async () =>
            {
                string js;
                try
                {
                    var res = await Places.Search(text).ConfigureAwait(false);
                    var hits = res.Hits.Select(h => new { label = h.Label, source = h.Source, lat = h.Lat, lon = h.Lon }).ToList();
                    js = "showHits(" + JsonSerializer.Serialize(new { hits, note = res.Note, osm = Nominatim.Attribution }) + ")";
                }
                catch (Exception ex)
                {
                    js = "showHits(" + JsonSerializer.Serialize(new { error = "search failed: " + ((ex as AggregateException)?.InnerException?.Message ?? ex.Message) }) + ")";
                }
                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    try { web.ExecuteScriptAsync(js); } catch { /* window closed meanwhile */ }
                });
            });
        }

        static string Num(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);

        static string Html(Options o)
        {
            var cfg = new StringBuilder("{");
            cfg.Append("pick:").Append(o.Pick ? "true" : "false");
            cfg.Append(",swiss:").Append(o.Swiss ? "true" : "false");
            cfg.Append(",lat:").Append(o.Lat.HasValue ? Num(o.Lat.Value) : "null");
            cfg.Append(",lon:").Append(o.Lon.HasValue ? Num(o.Lon.Value) : "null");
            cfg.Append(",outline:").Append(o.Outline == null ? "null" : "[" + string.Join(",", o.Outline.Select(p => $"[{Num(p.East)},{Num(p.North)}]")) + "]");
            cfg.Append(",outlineLatLon:").Append(o.OutlineLatLon == null ? "null" : "[" + string.Join(",", o.OutlineLatLon.Select(p => $"[{Num(p.Lat)},{Num(p.Lon)}]")) + "]");
            cfg.Append("}");
            return Page.Replace("/*CFG*/null", cfg.ToString());
        }

        const string Page = @"<!doctype html>
<html><head><meta charset=""utf-8""><title>geoBIM.app map</title>
<link rel=""stylesheet"" href=""https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"" integrity=""sha256-p4NxAoJBhIIN+hmNHrzRCf9tD/miZyoHS5obTRR9BMY="" crossorigin="""">
<script src=""https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"" integrity=""sha256-20nQCchB9co0qIjJZRGuk2/Z9VM+kNiyxNV1lvTlZBo="" crossorigin=""""></script>
<style>
html,body,#map{height:100%;margin:0;font:13px system-ui,sans-serif}
#bar{position:absolute;top:10px;left:56px;z-index:1000;width:380px}
#row{display:flex;gap:4px}
#q{flex:1;box-sizing:border-box;padding:7px 9px;border:1px solid #aaa;border-radius:4px;font:inherit}
#go{padding:0 12px;border:1px solid #aaa;border-radius:4px;background:#fff;font:inherit;cursor:pointer}
#hits .src{color:#777;font-size:11px;margin-left:6px}
#hits .note{color:#777;font-size:11px;cursor:default}
#hits{background:#fff;border-radius:4px;box-shadow:0 2px 6px rgba(0,0,0,.3);max-height:300px;overflow:auto}
#hits div{padding:6px 9px;cursor:pointer;border-top:1px solid #eee}
#hits div:hover{background:#eef6ff}
.leaflet-container{cursor:crosshair}
</style></head><body>
<div id=""map""></div>
<div id=""bar""><div id=""row""><input id=""q"" placeholder=""Search a place or address""><button id=""go"" title=""Search"">Search</button></div><div id=""hits""></div></div>
<script>
var cfg = /*CFG*/null;
function swiss(layer, ext, maxNative) {
  return L.tileLayer('https://wmts.geo.admin.ch/1.0.0/' + layer + '/default/current/3857/{z}/{x}/{y}.' + ext,
    {maxZoom: 21, maxNativeZoom: maxNative, attribution: '&copy; swisstopo'});
}
var lk = swiss('ch.swisstopo.pixelkarte-farbe', 'jpeg', 18), img = swiss('ch.swisstopo.swissimage', 'jpeg', 20);
var topo = L.tileLayer('https://sgx.geodatenzentrum.de/wmts_topplus_open/tile/1.0.0/web/default/WEBMERCATOR/{z}/{y}/{x}.png',
  {maxZoom: 21, maxNativeZoom: 18, attribution: '&copy; BKG (dl-de/by-2-0)'});
var map = L.map('map', {layers: [cfg.swiss ? lk : topo]});
L.control.layers({'swisstopo map': lk, 'swisstopo aerial': img, 'TopPlusOpen (BKG)': topo}).addTo(map);
L.control.scale({imperial: false}).addTo(map);
var marker = null, outline = null;
function drawOutline(pts) {
  if (!pts) return;
  if (outline) outline.setLatLngs(pts);
  else outline = L.polygon(pts, {color: '#FF8A00', weight: 2, fillOpacity: 0.15, interactive: false}).addTo(map);
}
// Terrain height (m above sea level) from Mapterhorn's Terrarium tiles, the deepest zoom that has the
// point (404 = no tile at that zoom); undefined when a newer pick came meanwhile, null without data
var terrainSeq = 0, terrainCredit = false;
function terrainAt(lat, lon) {
  var seq = ++terrainSeq;
  function tryZoom(z) {
    if (z < 0) return Promise.resolve(null);
    var n = Math.pow(2, z), s = Math.sin(lat * Math.PI / 180);
    var x = (lon + 180) / 360 * n, y = (0.5 - Math.log((1 + s) / (1 - s)) / (4 * Math.PI)) * n;
    var tx = Math.floor(x), ty = Math.floor(y);
    return fetch('https://tiles.mapterhorn.com/' + z + '/' + tx + '/' + ty + '.webp')
      .then(function (r) {
        if (r.status === 404) return null;
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.blob();
      })
      .then(function (blob) {
        if (!blob) return tryZoom(z - 1);
        return createImageBitmap(blob, {colorSpaceConversion: 'none', premultiplyAlpha: 'none'}).then(function (img) {
          var c = document.createElement('canvas');
          c.width = img.width; c.height = img.height;
          var ctx = c.getContext('2d', {willReadFrequently: true});
          ctx.drawImage(img, 0, 0);
          // bilinear between pixel centres, Terrarium: R·256 + G + B/256 − 32768
          var px = (x - tx) * img.width - 0.5, py = (y - ty) * img.height - 0.5;
          var x0 = Math.max(0, Math.min(img.width - 2, Math.floor(px))), y0 = Math.max(0, Math.min(img.height - 2, Math.floor(py)));
          var fx = Math.max(0, Math.min(1, px - x0)), fy = Math.max(0, Math.min(1, py - y0));
          var d = ctx.getImageData(x0, y0, 2, 2).data;
          function h(i) { return d[i] * 256 + d[i + 1] + d[i + 2] / 256 - 32768; }
          return h(0) * (1 - fx) * (1 - fy) + h(4) * fx * (1 - fy) + h(8) * (1 - fx) * fy + h(12) * fx * fy;
        });
      });
  }
  return tryZoom(17).then(function (h) { return seq === terrainSeq ? h : undefined; });
}
function place(lat, lon, report) {
  if (marker) marker.setLatLng([lat, lon]); else marker = L.marker([lat, lon], {interactive: false}).addTo(map);
  if (cfg.outline) {
    var k = Math.cos(lat * Math.PI / 180);
    drawOutline(cfg.outline.map(function (p) { return [lat + p[1] / 111320, lon + p[0] / (111320 * k)]; }));
  }
  if (report) {
    var ll = lat.toFixed(8) + ',' + lon.toFixed(8);
    document.title = 'geobim:' + ll;
    terrainAt(lat, lon).then(function (h) {
      if (h === undefined || h === null || !isFinite(h)) return;
      if (!terrainCredit) {
        map.attributionControl.addAttribution('Terrain &copy; <a href=""https://mapterhorn.com/attribution"">Mapterhorn</a>');
        terrainCredit = true;
      }
      document.title = 'geobim:' + ll + ',' + h.toFixed(2);
    }).catch(function () { /* no height: the command asks for it */ });
  }
}
if (cfg.outlineLatLon) drawOutline(cfg.outlineLatLon);
if (cfg.lat !== null) place(cfg.lat, cfg.lon, false);
// show the outline and the marker together
var view = outline ? outline.getBounds() : null;
if (view && marker) view.extend(marker.getLatLng());
if (view) map.fitBounds(view, {maxZoom: 19, padding: [60, 60]});
else if (marker) map.setView(marker.getLatLng(), 17);
else map.setView(cfg.swiss ? [46.8, 8.2] : [48.5, 9.5], cfg.swiss ? 8 : 6);
if (cfg.pick) map.on('click', function (e) { place(e.latlng.lat, e.latlng.lng, true); });
// swisstopo's tiles cover Switzerland only: outside it, take BKG's map
function inSwiss(lat, lon) { return lat >= 45.8 && lat <= 47.85 && lon >= 5.9 && lon <= 10.55; }
function useLayer(layer) {
  [lk, img, topo].forEach(function (l) { if (l !== layer && map.hasLayer(l)) map.removeLayer(l); });
  if (!map.hasLayer(layer)) map.addLayer(layer);
}
map.on('moveend', function () {
  var c = map.getCenter();
  if (!inSwiss(c.lat, c.lng) && (map.hasLayer(lk) || map.hasLayer(img))) useLayer(topo);
});
var q = document.getElementById('q'), hits = document.getElementById('hits'), searches = 0;
function note(text) { var d = document.createElement('div'); d.className = 'note'; d.textContent = text; hits.appendChild(d); }
// the plugin runs the search (swisstopo in Switzerland, OpenStreetMap elsewhere) and calls showHits
function search() {
  var text = q.value.trim();
  if (!text) return;
  hits.innerHTML = '';
  note('searching ...');
  document.title = 'geobim-search:' + (++searches) + ':' + text;
}
function showHits(r) {
  hits.innerHTML = '';
  if (r.error) { note(r.error); return; }
  var osm = false;
  r.hits.forEach(function (h) {
    var d = document.createElement('div'), s = document.createElement('span');
    d.textContent = h.label;
    s.className = 'src';
    s.textContent = h.source;
    d.appendChild(s);
    if (h.source !== 'swisstopo') osm = true;
    d.onclick = function () {
      hits.innerHTML = '';
      var swiss = inSwiss(h.lat, h.lon);
      if (swiss && map.hasLayer(topo)) useLayer(lk);
      if (!swiss) useLayer(topo);
      map.setView([h.lat, h.lon], 18);
      if (cfg.pick) place(h.lat, h.lon, true);
    };
    hits.appendChild(d);
  });
  if (!r.hits.length) note('nothing found (swisstopo in Switzerland, OpenStreetMap elsewhere)');
  if (r.note) note(r.note);
  if (osm) note('Places outside Switzerland: ' + r.osm);
}
q.addEventListener('keydown', function (ev) { if (ev.key === 'Enter') search(); });
document.getElementById('go').addEventListener('click', search);
</script></body></html>";
    }
}
