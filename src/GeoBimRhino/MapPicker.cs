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

namespace GeoBimRhino
{
    /// <summary>
    /// A map window (Eto WebView with Leaflet 1.9.4 from unpkg, pinned by its SRI hash): swisstopo's
    /// national map and aerial images (Switzerland) and BKG's TopPlusOpen (Europe, world), a
    /// search box (swisstopo, Switzerland), the model outline. Pick mode: a click places the
    /// marker, the outline follows it, OK returns latitude/longitude. Show mode: just the map.
    /// The page reports a click by setting document.title to "geobim:lat,lon".
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
            public bool Swiss;                                     // start on swisstopo's map
        }

        /// <summary>Shows the map; in pick mode the clicked position, else null</summary>
        public static (double Lat, double Lon)? Show(Options o)
        {
            (double, double)? picked = null;
            var web = new Eto.Forms.WebView();
            var info = new Eto.Forms.Label { Text = o.Pick ? "Click the point on the map (search: places and addresses in Switzerland)." : "Orange: the model's outline." };
            var ok = new Eto.Forms.Button { Text = o.Pick ? "OK" : "Close", Enabled = !o.Pick };
            var cancel = new Eto.Forms.Button { Text = "Cancel", Visible = o.Pick };
            var dlg = new Eto.Forms.Dialog<bool> { Title = o.Title, Resizable = true, ClientSize = new Eto.Drawing.Size(1000, 700), Padding = 0 };
            web.DocumentTitleChanged += (s, e) =>
            {
                var t = e.Title ?? "";
                if (!t.StartsWith("geobim:", StringComparison.Ordinal)) return;
                var parts = t.Substring(7).Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                {
                    picked = (lat, lon);
                    info.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.000000}° N, {1:0.000000}° E", lat, lon);
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
#bar{position:absolute;top:10px;left:56px;z-index:1000;width:340px}
#q{width:100%;box-sizing:border-box;padding:7px 9px;border:1px solid #aaa;border-radius:4px;font:inherit}
#hits{background:#fff;border-radius:4px;box-shadow:0 2px 6px rgba(0,0,0,.3);max-height:300px;overflow:auto}
#hits div{padding:6px 9px;cursor:pointer;border-top:1px solid #eee}
#hits div:hover{background:#eef6ff}
.leaflet-container{cursor:crosshair}
</style></head><body>
<div id=""map""></div>
<div id=""bar""><input id=""q"" placeholder=""Search a place or address in Switzerland, Enter""><div id=""hits""></div></div>
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
function place(lat, lon, report) {
  if (marker) marker.setLatLng([lat, lon]); else marker = L.marker([lat, lon], {interactive: false}).addTo(map);
  if (cfg.outline) {
    var k = Math.cos(lat * Math.PI / 180);
    drawOutline(cfg.outline.map(function (p) { return [lat + p[1] / 111320, lon + p[0] / (111320 * k)]; }));
  }
  if (report) document.title = 'geobim:' + lat.toFixed(8) + ',' + lon.toFixed(8);
}
if (cfg.outlineLatLon) drawOutline(cfg.outlineLatLon);
if (cfg.lat !== null) place(cfg.lat, cfg.lon, false);
// show the outline and the marker together
var view = outline ? outline.getBounds() : null;
if (view && marker) view.extend(marker.getLatLng());
if (view) map.fitBounds(view, {maxZoom: 19, padding: [60, 60]});
else if (marker) map.setView(marker.getLatLng(), 17);
else map.setView([cfg.swiss ? 46.8 : 50.5, cfg.swiss ? 8.2 : 10.0], cfg.swiss ? 8 : 5);
if (cfg.pick) map.on('click', function (e) { place(e.latlng.lat, e.latlng.lng, true); });
var q = document.getElementById('q'), hits = document.getElementById('hits');
q.addEventListener('keydown', function (ev) {
  if (ev.key !== 'Enter' || !q.value.trim()) return;
  fetch('https://api3.geo.admin.ch/rest/services/api/SearchServer?type=locations&sr=4326&limit=8&searchText=' + encodeURIComponent(q.value))
    .then(function (r) { return r.json(); })
    .then(function (j) {
      hits.innerHTML = '';
      (j.results || []).forEach(function (h) {
        var a = h.attrs, d = document.createElement('div');
        d.textContent = String(a.label || '').replace(/<[^>]+>/g, '');
        d.onclick = function () {
          hits.innerHTML = '';
          if (map.hasLayer(topo)) { map.removeLayer(topo); map.addLayer(lk); }
          map.setView([a.lat, a.lon], 18);
          if (cfg.pick) place(a.lat, a.lon, true);
        };
        hits.appendChild(d);
      });
      if (!hits.firstChild) hits.innerHTML = '<div>nothing found (swisstopo searches Switzerland only)</div>';
    })
    .catch(function () { hits.innerHTML = '<div>search failed (no connection?)</div>'; });
});
</script></body></html>";
    }
}
