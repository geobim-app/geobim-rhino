// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace GeoBimRhino
{
    static class Shared
    {
        public const ObjectType Exportable = ObjectType.Brep | ObjectType.Extrusion | ObjectType.SubD |
                                             ObjectType.Mesh | ObjectType.Surface | ObjectType.InstanceReference;

        /// <summary>Selected objects, or all visible ones on Enter</summary>
        public static Result SelectObjects(RhinoDoc doc, out List<RhinoObject> objects)
        {
            objects = new List<RhinoObject>();
            var go = new GetObject();
            go.SetCommandPrompt("Select objects for geoBIM.app (Enter = all visible objects)");
            go.GeometryFilter = Exportable;
            go.AcceptNothing(true);
            go.EnablePreSelect(true, true);
            go.GroupSelect = true;
            go.SubObjectSelect = false;
            var r = go.GetMultiple(1, 0);
            if (r == GetResult.Nothing)
            {
                var settings = new ObjectEnumeratorSettings
                {
                    VisibleFilter = true, HiddenObjects = false, LockedObjects = true, NormalObjects = true,
                    ReferenceObjects = true, IncludeLights = false, IncludeGrips = false, ObjectTypeFilter = Exportable
                };
                objects.AddRange(doc.Objects.GetObjectList(settings));
            }
            else if (r == GetResult.Object)
            {
                for (int i = 0; i < go.ObjectCount; i++) objects.Add(go.Object(i).Object());
            }
            else return go.CommandResult();
            if (objects.Count == 0)
            {
                RhinoApp.WriteLine("geoBIM.app: nothing to export (surfaces, polysurfaces, SubD, meshes or blocks).");
                return Result.Nothing;
            }
            return Result.Success;
        }

        public static string DocName(RhinoDoc doc)
        {
            var n = doc.Name;
            if (string.IsNullOrWhiteSpace(n)) return "Rhino model";
            return Path.GetFileNameWithoutExtension(n);
        }

        public static string SlugKey(string server) => "slug|" + server.ToLowerInvariant();
        /// <summary>The document's GLB model on that server (a separate model from the IFC one)</summary>
        public static string GlbSlugKey(string server) => "glbslug|" + server.ToLowerInvariant();
        public const string DocFormatKey = "format";

        /// <summary>
        /// Placement for a GLB upload from the model's position (GeoLocation.For: map origin,
        /// map coordinates or Rhino's Location): lon/lat of the base point, height above sea
        /// level (the server adds the geoid), heading of the model's +Y axis clockwise from
        /// true north. Null without a position.
        /// </summary>
        public static GeoBimClient.Placement GlbPlacement(IfcModelData m)
        {
            if (m.GeoRef != GeoRefKind.SiteLatLon) return null;
            double heading = -Math.Atan2(m.TrueNorthX, m.TrueNorthY) * 180.0 / Math.PI;
            return new GeoBimClient.Placement { Lon = m.Longitude, Lat = m.Latitude, Height = m.Elevation, Heading = heading, Orthometric = true };
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { RhinoApp.WriteLine("geoBIM.app: could not open the browser ({0}) — {1}", ex.Message, url); }
        }

        public static void Ui(Action a) => RhinoApp.InvokeOnUiThread(a);

        public const string DocEpsgKey = "epsg";

        /// <summary>
        /// EPSG for a model in map coordinates: the fixed setting, else this document's
        /// earlier choice, else CrsDetect; when that is ambiguous (Swiss LV03, Austrian
        /// systems) the user picks once among the plausible candidates (scripts: the first).
        /// </summary>
        public static int? ResolveEpsg(RhinoDoc doc, double e, double n, RunMode mode)
        {
            var p = GeoBimPlugin.Instance;
            var setting = (p.Epsg ?? "auto").Replace("EPSG:", "").Trim();
            if (!setting.Equals("auto", StringComparison.OrdinalIgnoreCase) && int.TryParse(setting, out int fixedCode)) return fixedCode;
            if (int.TryParse(doc.Strings.GetValue("geobim", DocEpsgKey) ?? "", out int docCode)) return docCode;
            var g = CrsDetect.Guess(e, n, p.DefaultUtmEpsg);
            if (g.Epsg != null) return g.Epsg;
            if (mode != RunMode.Interactive) return g.Candidates[0].Epsg;

            RhinoApp.WriteLine(string.Format("geoBIM.app: map coordinates E {0:0} N {1:0} — {2}. Which coordinate system?", e, n, g.Region));
            foreach (var c in g.Candidates) RhinoApp.WriteLine(string.Format("  {0} = EPSG:{1} {2}", c.Option, c.Epsg, c.Label));
            var go = new GetOption();
            go.SetCommandPrompt("Coordinate system of this model");
            var idx = new Dictionary<int, CrsCandidate>();
            foreach (var c in g.Candidates) idx[go.AddOption(c.Option)] = c;
            int other = go.AddOption("OtherEPSG");
            if (go.Get() != GetResult.Option) return null;
            int code;
            int chosenIndex = go.Option().Index;
            if (chosenIndex == other)
            {
                code = g.Candidates[0].Epsg;
                if (RhinoGet.GetInteger("EPSG code", false, ref code, 1000, 999999) != Result.Success) return null;
            }
            else code = idx[chosenIndex].Epsg;
            doc.Strings.SetString("geobim", DocEpsgKey, code.ToString());
            RhinoApp.WriteLine(string.Format("geoBIM.app: EPSG:{0} remembered for this document (change it with GeoBIMSettings → EPSG).", code));
            return code;
        }

        public static ExportResult Export(RhinoDoc doc, List<RhinoObject> objects, string name, string path, RunMode mode, bool glb = false)
        {
            // GLB is placed by latitude/longitude (GeoLocation: the map origin or map coordinates
            // when they convert, else Rhino's Location), so it never asks for an EPSG code
            var mapOrigin = MapOrigin.FromDoc(doc);
            var res = ModelExport.Build(doc, objects, name, (e, n) => glb ? 0 : ResolveEpsg(doc, e, n, mode), mapOrigin,
                                        glb ? ExportTarget.Glb : ExportTarget.Ifc);
            if (res.Cancelled) return res;
            if (glb)
            {
                if (res.PlacementNote != null) RhinoApp.WriteLine("geoBIM.app: GLB is placed by Rhino's Location: " + res.PlacementNote + ".");
                if (res.Elements > 0) GlbWriter.Write(res.Model, path);
            }
            else IfcWriter.Write(res.Model, path);
            RhinoApp.WriteLine(string.Format("geoBIM.app: {0} elements, {1:N0} triangles{2}; {3}",
                res.Elements, res.Triangles, res.Skipped > 0 ? $", {res.Skipped} objects without surfaces skipped" : "", res.GeoRefInfo));
            var warn = GeoLocation.MismatchWarning(doc);
            if (warn != null) RhinoApp.WriteLine(warn);
            return res;
        }

        /// <summary>IFC for Cesium ion: east/north/up around the model's position, which goes to IonLon/IonLat/IonElevation</summary>
        public static ExportResult ExportIon(RhinoDoc doc, List<RhinoObject> objects, string name, string path)
        {
            var res = ModelExport.Build(doc, objects, name, (e, n) => 0, MapOrigin.FromDoc(doc), ExportTarget.Ion);
            if (res.PlacementNote != null) RhinoApp.WriteLine("Cesium ion: placed by Rhino's Location: " + res.PlacementNote + ".");
            if (res.Elements > 0) IfcWriter.Write(res.Model, path);
            RhinoApp.WriteLine(string.Format("Cesium ion: {0} elements, {1:N0} triangles{2}; {3}",
                res.Elements, res.Triangles, res.Skipped > 0 ? $", {res.Skipped} objects without surfaces skipped" : "", res.GeoRefInfo));
            var warn = GeoLocation.MismatchWarning(doc);
            if (warn != null) RhinoApp.WriteLine(warn);
            return res;
        }

        public const string DocGeoidKey = "geoidN";
        public const string DocIonAssetKey = "ionasset";

        public static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "model";
        }
    }

    // ------------------------------------------------------------------
    [Guid("9de6f27d-8e14-4d72-9a3b-e0f2fd8646aa")]
    public class GeoBIMSettingsCommand : Command
    {
        public override string EnglishName => "GeoBIMSettings";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var p = GeoBimPlugin.Instance;
            while (true)
            {
                var go = new GetOption();
                var origin = MapOrigin.FromDoc(doc);
                go.SetCommandPrompt($"geoBIM.app — server {p.Server}, token {GeoBimPlugin.MaskedToken(p.Token)}, EPSG {p.Epsg} (default UTM {p.DefaultUtmEpsg})"
                                    + (origin != null ? $", map origin {origin.Describe()}" : ""));
                int oServer = go.AddOption("Server");
                int oLive = go.AddOption("Live");
                int oBeta = go.AddOption("Beta");
                int oToken = go.AddOption("Token");
                int oEpsg = go.AddOption("EPSG");
                int oUtm = go.AddOption("DefaultUTM");
                int oTest = go.AddOption("Test");
                int oIonToken = go.AddOption("IonToken");
                int oIonTest = go.AddOption("IonTest");
                go.AcceptNothing(true);
                var r = go.Get();
                if (r == GetResult.Nothing) return Result.Success;
                if (r != GetResult.Option) return Result.Cancel;
                int idx = go.Option().Index;

                if (idx == oServer)
                {
                    string s = p.Server;
                    if (RhinoGet.GetString("Server URL", false, ref s) != Result.Success) continue;
                    if (!Uri.TryCreate(s.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Host != "localhost"))
                    {
                        RhinoApp.WriteLine("geoBIM.app: use an https:// address, e.g. https://viewer.geobim.app");
                        continue;
                    }
                    p.Server = s;
                }
                else if (idx == oLive) p.Server = GeoBimPlugin.DefaultServer;
                else if (idx == oBeta) p.Server = "https://beta.geobim.app";
                else if (idx == oToken)
                {
                    string t = AskToken(mode, "geoBIM.app plugin token", "Plugin token (geoBIM.app → Account → Rhino plugin → Create)");
                    if (t == null) continue;
                    t = t.Trim();
                    if (LooksLikeIonToken(t))
                    {
                        // pasted into the wrong option: a Cesium ion token is a JWT (eyJ…, two dots)
                        p.IonToken = t;
                        RhinoApp.WriteLine("geoBIM.app: that is a Cesium ion token, not a geoBIM.app plugin token — saved as the Cesium ion token instead.");
                        IonTest(p);
                        continue;
                    }
                    if (!t.StartsWith("gbp_") || t.Length != 47)
                    {
                        RhinoApp.WriteLine("geoBIM.app: that is not a plugin token (gbp_…, 47 characters).");
                        continue;
                    }
                    p.Token = t;
                    RhinoApp.WriteLine("geoBIM.app: token saved — use Test to check it.");
                }
                else if (idx == oEpsg)
                {
                    string e = p.Epsg;
                    if (RhinoGet.GetString("EPSG for models in map coordinates (auto or a code, e.g. 25832)", false, ref e) != Result.Success) continue;
                    e = e.Trim().Replace("EPSG:", "");
                    if (!e.Equals("auto", StringComparison.OrdinalIgnoreCase) && !int.TryParse(e, out _))
                    {
                        RhinoApp.WriteLine("geoBIM.app: EPSG must be auto or a number.");
                        continue;
                    }
                    p.Epsg = e.ToLowerInvariant() == "auto" ? "auto" : e;
                    // a new setting replaces the coordinate system chosen for this document
                    if (doc.Strings.GetValue("geobim", Shared.DocEpsgKey) != null)
                    {
                        doc.Strings.Delete("geobim", Shared.DocEpsgKey);
                        RhinoApp.WriteLine("geoBIM.app: the coordinate system chosen for this document was reset.");
                    }
                }
                else if (idx == oUtm)
                {
                    int u = p.DefaultUtmEpsg;
                    if (RhinoGet.GetInteger("EPSG for UTM coordinates without zone prefix", false, ref u, 1000, 999999) != Result.Success) continue;
                    p.DefaultUtmEpsg = u;
                }
                else if (idx == oTest)
                {
                    Test(p);
                }
                else if (idx == oIonToken)
                {
                    string t = AskToken(mode, "Cesium ion access token",
                        "Cesium ion access token (ion.cesium.com → Access Tokens). It needs the scopes assets:list, assets:read and assets:write.");
                    if (t == null) continue;
                    t = t.Trim();
                    if (t.StartsWith("gbp_"))
                    {
                        RhinoApp.WriteLine("Cesium ion: that is a geoBIM.app plugin token — choose Token for it. The Cesium ion token comes from ion.cesium.com → Access Tokens.");
                        continue;
                    }
                    if (!LooksLikeIonToken(t))
                    {
                        RhinoApp.WriteLine("Cesium ion: that does not look like an ion access token (a long eyJ… string with two dots).");
                        continue;
                    }
                    p.IonToken = t;
                    RhinoApp.WriteLine("Cesium ion: token saved.");
                    IonTest(p);
                }
                else if (idx == oIonTest)
                {
                    IonTest(p);
                }
            }
        }

        static bool LooksLikeIonToken(string t) => t.StartsWith("eyJ") && t.Length >= 40 && t.Count(ch => ch == '.') == 2;

        /// <summary>
        /// A token: in a small dialog with a hidden field when interactive (Rhino's command line
        /// would echo it in the history), on the command line when scripted (-GeoBIMSettings).
        /// Null when cancelled.
        /// </summary>
        static string AskToken(RunMode mode, string title, string prompt)
        {
            if (mode != RunMode.Interactive)
            {
                string s = "";
                return RhinoGet.GetString(prompt, false, ref s) == Result.Success ? s : null;
            }
            var box = new Eto.Forms.PasswordBox { Width = 460 };
            var dlg = new Eto.Forms.Dialog<bool> { Title = title, Padding = new Eto.Drawing.Padding(12), Resizable = false };
            var ok = new Eto.Forms.Button { Text = "OK" };
            var cancel = new Eto.Forms.Button { Text = "Cancel" };
            ok.Click += (s, e) => dlg.Close(true);
            cancel.Click += (s, e) => dlg.Close(false);
            dlg.DefaultButton = ok;
            dlg.AbortButton = cancel;
            var buttons = new Eto.Forms.StackLayout { Orientation = Eto.Forms.Orientation.Horizontal, Spacing = 6 };
            buttons.Items.Add(new Eto.Forms.StackLayoutItem(null, true));
            buttons.Items.Add(ok);
            buttons.Items.Add(cancel);
            var layout = new Eto.Forms.StackLayout { Spacing = 10, HorizontalContentAlignment = Eto.Forms.HorizontalAlignment.Stretch };
            layout.Items.Add(new Eto.Forms.Label { Text = prompt, Wrap = Eto.Forms.WrapMode.Word, Width = 460 });
            layout.Items.Add(box);
            layout.Items.Add(buttons);
            dlg.Content = layout;
            return dlg.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow) ? box.Text : null;
        }

        static void IonTest(GeoBimPlugin p)
        {
            if (string.IsNullOrEmpty(p.IonToken)) { RhinoApp.WriteLine("Cesium ion: no token yet — choose IonToken first."); return; }
            try
            {
                var client = new IonClient(p.IonToken);
                Task.Run(() => client.CountAssets()).GetAwaiter().GetResult();
                bool? write = Task.Run(() => client.CanWrite()).GetAwaiter().GetResult();
                if (write == true)
                    RhinoApp.WriteLine("Cesium ion: connected (token {0}), assets can be created — GeoBIMUploadIon is ready.", GeoBimPlugin.MaskedToken(p.IonToken));
                else if (write == false)
                    RhinoApp.WriteLine("Cesium ion: the token can read but not create assets — it lacks the assets:write scope. " +
                                       "Create a token with assets:list, assets:read and assets:write at ion.cesium.com → Access Tokens.");
                else
                    RhinoApp.WriteLine("Cesium ion: connected (token {0}); whether it may create assets is unclear — it needs the assets:write scope.", GeoBimPlugin.MaskedToken(p.IonToken));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Cesium ion: connection failed — {0}", (ex as AggregateException)?.InnerException?.Message ?? ex.Message);
            }
        }

        static void Test(GeoBimPlugin p)
        {
            if (string.IsNullOrEmpty(p.Token)) { RhinoApp.WriteLine("geoBIM.app: no token yet — choose Token first."); return; }
            try
            {
                var who = Task.Run(() => new GeoBimClient(p.Server, p.Token).WhoAmI()).GetAwaiter().GetResult();
                var label = who.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : "";
                RhinoApp.WriteLine("geoBIM.app: connected to {0} (token \"{1}\") — uploads are ready.", p.Server, label);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("geoBIM.app: connection failed — {0}", (ex as AggregateException)?.InnerException?.Message ?? ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// Where the model lies, from one input: a point of the model (Enter = origin) and its
    /// position — map coordinates, latitude/longitude, a place or address (swisstopo search) or
    /// a click on the map — then its height and map north. Stores the map origin (IFC export:
    /// IfcMapConversion) and, for LV95 and UTM, sets Rhino's Location to the same place, so IFC,
    /// GLB and Cesium ion agree. Options: Map, Show (map with the model's outline), Clear.
    /// Scripted: _-GeoBIMLocation _Enter 2755000,1206000,0,0   (E,N,H,rotation: nothing else is asked)
    ///           _-GeoBIMLocation _Enter 46.98695,9.47632,0 _Enter   (latitude, longitude, height; north = +Y)
    /// </summary>
    [Guid("714cd07f-7895-4bf7-a22c-4a77bc5acd3e")]
    public class GeoBIMLocationCommand : Command
    {
        public override string EnglishName => "GeoBIMLocation";

        static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            double scale = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
            var current = MapOrigin.FromDoc(doc);
            RhinoApp.WriteLine(GeoLocation.Describe(doc));
            var warn = GeoLocation.MismatchWarning(doc);
            if (warn != null) RhinoApp.WriteLine(warn);
            var footprint = GeoLocation.Footprint(doc);
            if (footprint != null)
            {
                var c = (footprint[0] + footprint[2]) / 2;
                if (CrsDetect.LooksLikeMap(c.X * scale, c.Y * scale))
                    RhinoApp.WriteLine("geoBIM.app: the visible objects already lie in map coordinates — their position is known and GLB and Cesium ion use it. " +
                                       "GeoBIMLocation is for models in local coordinates (Show displays the position).");
            }

            // 1. a point of the model whose position is known
            var gp = new GetPoint();
            gp.SetCommandPrompt("Point whose position you know (Enter = model origin 0,0,0)");
            gp.AcceptNothing(true);
            int oMap = gp.AddOption("Map");
            int oShow = gp.AddOption("Show");
            int oClear = gp.AddOption("Clear");
            var point = Point3d.Origin;
            bool openMap = false;
            var r = gp.Get();
            if (r == GetResult.Option)
            {
                int i = gp.OptionIndex();
                if (i == oShow) return ShowMap(doc);
                if (i == oClear)
                {
                    MapOrigin.Clear(doc);
                    RhinoApp.WriteLine("geoBIM.app: map origin removed. Rhino's Location stays as it is (change it with EarthAnchorPoint).");
                    return Result.Success;
                }
                openMap = i == oMap;
            }
            else if (r == GetResult.Point) point = gp.Point();
            else if (r != GetResult.Nothing) return Result.Cancel;
            double x = point.X * scale, y = point.Y * scale, z = point.Z * scale;

            // 2. its position
            PositionText pos = null;
            bool online = false;   // position from swisstopo's search or the map: terrain height may be looked up
            while (pos == null)
            {
                if (openMap)
                {
                    if (mode != RunMode.Interactive) return Result.Cancel;
                    var ll = PickOnMap(doc, point, current, scale);
                    if (ll == null) return Result.Cancel;
                    pos = new PositionText { Kind = PositionText.Kinds.LatLon, Lat = ll.Value.Lat, Lon = ll.Value.Lon };
                    online = true;
                    break;
                }
                var gs = new GetString();
                gs.SetCommandPrompt("Its position: map coordinates E,N[,H], latitude, longitude[, H], or a place or address in Switzerland");
                string def = null;
                if (GeoLocation.ModelToMap(doc, point, out double ce, out double cn, out double ch, out _) && current != null)
                    def = F(ce, "0.###") + "," + F(cn, "0.###") + "," + F(ch, "0.###");
                if (def != null) gs.SetDefaultString(def);
                gs.AcceptNothing(true);
                int oMap2 = gs.AddOption("Map");
                var gr = gs.GetLiteralString();
                if (gr == GetResult.Option && gs.OptionIndex() == oMap2) { openMap = true; continue; }
                string text;
                if (gr == GetResult.String) text = gs.StringResult().Trim();
                else if (gr == GetResult.Nothing && def != null) text = def;
                else return Result.Cancel;
                if (text.Length == 0 && def != null) text = def;

                var parsed = PositionText.Parse(text);
                if (parsed == null)
                {
                    RhinoApp.WriteLine("geoBIM.app: \"{0}\" is not a position — e.g. 2755000,1206000 (LV95), 46.98695, 9.47632 (latitude, longitude) or Valens.", text);
                    if (mode != RunMode.Interactive) return Result.Failure;
                    continue;
                }
                if (parsed.Kind == PositionText.Kinds.Address)
                {
                    var hit = SearchAddress(parsed.Address, mode);
                    if (hit == null) { if (mode != RunMode.Interactive) return Result.Failure; continue; }
                    parsed = new PositionText { Kind = PositionText.Kinds.Map, E = hit.E, N = hit.N };
                    online = true;
                }
                pos = parsed;
            }

            // 3. coordinate system and map coordinates of the point
            int epsg;
            double e, n;
            int? fixedEpsg = FixedEpsg();
            if (pos.Kind == PositionText.Kinds.Map)
            {
                e = pos.E;
                n = pos.N;
                var guess = CrsDetect.Guess(e, n, GeoBimPlugin.Instance.DefaultUtmEpsg);
                int? chosen = fixedEpsg ?? guess.Epsg ?? Shared.ResolveEpsg(doc, e, n, mode);
                if (chosen == null) return Result.Cancel;
                epsg = chosen.Value;
            }
            else
            {
                int? chosen = fixedEpsg != null && GeoProj.Supports(fixedEpsg.Value) ? fixedEpsg : GeoProj.DefaultEpsg(pos.Lat, pos.Lon);
                if (chosen == null)
                {
                    RhinoApp.WriteLine("geoBIM.app: positions south of the equator need map coordinates (the plugin converts LV95 and northern UTM zones).");
                    return Result.Failure;
                }
                epsg = chosen.Value;
                GeoProj.ToMap(epsg, pos.Lat, pos.Lon, out e, out n);
            }
            RhinoApp.WriteLine(string.Format("geoBIM.app: {0} (EPSG:{1}) E {2} N {3}.", GeoProj.Name(epsg), epsg, F(e, "0.###"), F(n, "0.###")));

            // 4. height of the point above sea level
            double h;
            if (pos.H != null) h = pos.H.Value;
            else
            {
                double? terrain = null;
                if (online && epsg == 2056 && GeoAdmin.InSwitzerland(e, n))
                {
                    try { terrain = Task.Run(() => GeoAdmin.Height(e, n)).GetAwaiter().GetResult(); } catch { terrain = null; }
                    if (terrain != null) RhinoApp.WriteLine("geoBIM.app: terrain there: {0} m above sea level (swisstopo).", F(terrain.Value, "0.0"));
                }
                // keep the heights the model already has, else start from the terrain
                double minZ = footprint != null ? footprint[0].Z * scale : 0;
                double defH = current != null ? current.H + z : (minZ > 100 || terrain == null ? z : terrain.Value);
                var gn = new GetNumber();
                gn.SetCommandPrompt(string.Format(CultureInfo.InvariantCulture,
                    "Height of this point above sea level in metres (its z is {0:0.###} m)", z));
                gn.SetDefaultNumber(defH);
                gn.AcceptNothing(true);
                int oTerrain = terrain != null ? gn.AddOption("Terrain") : -1;
                int oModel = gn.AddOption("ModelHeights");
                while (true)
                {
                    var hr = gn.Get();
                    if (hr == GetResult.Number) { h = gn.Number(); break; }
                    if (hr == GetResult.Nothing) { h = defH; break; }
                    if (hr == GetResult.Option && gn.OptionIndex() == oTerrain) { h = terrain.Value; break; }
                    if (hr == GetResult.Option && gn.OptionIndex() == oModel) { h = z; break; }   // z values are heights above sea level
                    return Result.Cancel;
                }
            }

            // 5. map north
            double rot;
            if (pos.RotationDeg != null) rot = pos.RotationDeg.Value;
            else
            {
                double defRot = current?.RotationDeg ?? 0;
                var gn = new GetNumber();
                gn.SetCommandPrompt("Map north in the model, degrees clockwise from +Y");
                gn.SetDefaultNumber(defRot);
                gn.AcceptNothing(true);
                int oTwo = gn.AddOption("TwoPoints");
                while (true)
                {
                    var nr = gn.Get();
                    if (nr == GetResult.Number) { rot = gn.Number(); break; }
                    if (nr == GetResult.Nothing) { rot = defRot; break; }
                    if (nr == GetResult.Option && gn.OptionIndex() == oTwo)
                    {
                        var a = new GetPoint();
                        a.SetCommandPrompt("Start of a line pointing to map north");
                        if (a.Get() != GetResult.Point) return Result.Cancel;
                        var b = new GetPoint();
                        b.SetCommandPrompt("Point towards map north");
                        b.SetBasePoint(a.Point(), true);
                        b.DrawLineFromPoint(a.Point(), true);
                        if (b.Get() != GetResult.Point) return Result.Cancel;
                        var d = b.Point() - a.Point();
                        if (Math.Abs(d.X) < 1e-12 && Math.Abs(d.Y) < 1e-12) return Result.Cancel;
                        rot = Math.Atan2(d.X, d.Y) * 180 / Math.PI;
                        break;
                    }
                    return Result.Cancel;
                }
            }
            rot = Math.IEEERemainder(rot, 360);

            // 6. map origin from the point, Rhino's Location from the map origin
            double t = rot * Math.PI / 180, cr = Math.Cos(t), sr = Math.Sin(t);
            var mo = new MapOrigin { E = e - (cr * x - sr * y), N = n - (sr * x + cr * y), H = h - z, RotationDeg = rot };
            bool located = GeoLocation.Apply(doc, mo, epsg);
            RhinoApp.WriteLine("geoBIM.app: model origin at {0} {1}{2}.", GeoProj.Name(epsg), mo.Describe(),
                rot != 0 ? "" : ", map north = +Y");
            if (located)
            {
                var ap = doc.EarthAnchorPoint;
                RhinoApp.WriteLine(string.Format("geoBIM.app: Rhino's Location set to the same place ({0}° N, {1}° E, {2} m; true north {3}° from map north). " +
                                   "IFC, GLB and Cesium ion now agree — check it with GeoBIMLocation → Show.",
                    F(ap.EarthBasepointLatitude, "0.000000"), F(ap.EarthBasepointLongitude, "0.000000"), F(ap.EarthBasepointElevation, "0.##"),
                    F(GeoProj.Convergence(epsg, ap.EarthBasepointLatitude, ap.EarthBasepointLongitude), "0.###")));
            }
            else
                RhinoApp.WriteLine("geoBIM.app: EPSG:{0} cannot be converted to latitude/longitude by the plugin (it does LV95 and UTM), so Rhino's Location is unchanged; " +
                                   "the IFC export uses the map origin. For GLB and Cesium ion set the Location with EarthAnchorPoint.", epsg);
            return Result.Success;
        }

        static int? FixedEpsg()
        {
            var setting = (GeoBimPlugin.Instance.Epsg ?? "auto").Replace("EPSG:", "").Trim();
            return !setting.Equals("auto", StringComparison.OrdinalIgnoreCase) && int.TryParse(setting, out int code) ? code : (int?)null;
        }

        /// <summary>swisstopo search; with several hits the user picks one (scripts: the first). Null when nothing was chosen.</summary>
        static GeoAdmin.Hit SearchAddress(string text, RunMode mode)
        {
            List<GeoAdmin.Hit> hits;
            try { hits = Task.Run(() => GeoAdmin.Search(text)).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("geoBIM.app: swisstopo search failed — {0}", (ex as AggregateException)?.InnerException?.Message ?? ex.Message);
                return null;
            }
            if (hits.Count == 0)
            {
                RhinoApp.WriteLine("geoBIM.app: swisstopo found nothing for \"{0}\" (it searches Switzerland and Liechtenstein; elsewhere use latitude, longitude or Map).", text);
                return null;
            }
            for (int i = 0; i < hits.Count; i++)
                RhinoApp.WriteLine(string.Format("  {0}  {1}   (E {2} N {3})", i + 1, hits[i].Label, F(hits[i].E, "0"), F(hits[i].N, "0")));
            int pick = 1;
            if (hits.Count > 1 && mode == RunMode.Interactive &&
                RhinoGet.GetInteger("Which one", true, ref pick, 1, hits.Count) != Result.Success) return null;
            var hit = hits[pick - 1];
            RhinoApp.WriteLine("geoBIM.app: {0}.", hit.Label);
            return hit;
        }

        /// <summary>Map with the marker on the point's current position and the model's outline around it</summary>
        static (double Lat, double Lon)? PickOnMap(RhinoDoc doc, Point3d point, MapOrigin current, double scale)
        {
            var o = new MapPicker.Options { Title = "geoBIM.app — where is this point? Click it on the map", Pick = true };
            if (GeoLocation.ModelToLatLon(doc, point, out double lat, out double lon)) { o.Lat = lat; o.Lon = lon; }
            o.Swiss = o.Lat == null || GeoProj.DefaultEpsg(o.Lat.Value, o.Lon.Value) == 2056;
            var fp = GeoLocation.Footprint(doc);
            if (fp != null)
            {
                double t = (current?.RotationDeg ?? 0) * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
                o.Outline = new List<(double, double)>();
                foreach (var q in fp)
                {
                    double dx = (q.X - point.X) * scale, dy = (q.Y - point.Y) * scale;
                    o.Outline.Add((c * dx - s * dy, s * dx + c * dy));
                }
            }
            return MapPicker.Show(o);
        }

        static Result ShowMap(RhinoDoc doc)
        {
            var fp = GeoLocation.Footprint(doc);
            var o = new MapPicker.Options { Title = "geoBIM.app — position of " + Shared.DocName(doc), Pick = false };
            var marker = Point3d.Origin;
            if (fp != null)
            {
                double scale = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
                var c = (fp[0] + fp[2]) / 2;
                if (CrsDetect.LooksLikeMap(c.X * scale, c.Y * scale)) marker = c;
                o.OutlineLatLon = new List<(double, double)>();
                foreach (var q in fp)
                    if (GeoLocation.ModelToLatLon(doc, q, out double qa, out double qo)) o.OutlineLatLon.Add((qa, qo));
                if (o.OutlineLatLon.Count < 3) o.OutlineLatLon = null;
            }
            if (!GeoLocation.ModelToLatLon(doc, marker, out double lat, out double lon))
            {
                RhinoApp.WriteLine("geoBIM.app: this model has no position yet — run GeoBIMLocation.");
                return Result.Nothing;
            }
            o.Lat = lat;
            o.Lon = lon;
            o.Swiss = GeoProj.DefaultEpsg(lat, lon) == 2056;
            RhinoApp.WriteLine("geoBIM.app: {0} at {1}° N, {2}° E.", marker == Point3d.Origin ? "model origin" : "model centre", F(lat, "0.000000"), F(lon, "0.000000"));
            MapPicker.Show(o);
            return Result.Success;
        }
    }

    // ------------------------------------------------------------------
    [Guid("89e15867-aad7-42b9-b5d4-2306e3f89fa0")]
    public class GeoBIMExportIfcCommand : Command
    {
        public override string EnglishName => "GeoBIMExportIfc";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var r = Shared.SelectObjects(doc, out var objects);
            if (r != Result.Success) return r;
            string name = Shared.DocName(doc);
            string path = Path.Combine(string.IsNullOrEmpty(doc.Path) ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(doc.Path), name + ".ifc");
            if (mode == RunMode.Interactive)
            {
                var dlg = new Rhino.UI.SaveFileDialog { Filter = "IFC 4 (*.ifc)|*.ifc", FileName = path, Title = "Export IFC for geoBIM.app" };
                if (!dlg.ShowSaveDialog()) return Result.Cancel;
                path = dlg.FileName;
            }
            else if (RhinoGet.GetString("IFC file", false, ref path) != Result.Success) return Result.Cancel;
            try
            {
                var res = Shared.Export(doc, objects, name, path, mode);
                if (res.Cancelled) return Result.Cancel;
                RhinoApp.WriteLine("geoBIM.app: written {0}", path);
                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("geoBIM.app: export failed — {0}", ex.Message);
                return Result.Failure;
            }
        }
    }

    // ------------------------------------------------------------------
    [Guid("9495752f-f6f0-47f0-b12b-71bf8777c390")]
    public class GeoBIMExportGlbCommand : Command
    {
        public override string EnglishName => "GeoBIMExportGlb";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var r = Shared.SelectObjects(doc, out var objects);
            if (r != Result.Success) return r;
            string name = Shared.DocName(doc);
            string path = Path.Combine(string.IsNullOrEmpty(doc.Path) ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(doc.Path), name + ".glb");
            if (mode == RunMode.Interactive)
            {
                var dlg = new Rhino.UI.SaveFileDialog { Filter = "glTF binary (*.glb)|*.glb", FileName = path, Title = "Export GLB for geoBIM.app" };
                if (!dlg.ShowSaveDialog()) return Result.Cancel;
                path = dlg.FileName;
            }
            else if (RhinoGet.GetString("GLB file", false, ref path) != Result.Success) return Result.Cancel;
            try
            {
                var res = Shared.Export(doc, objects, name, path, mode, true);
                if (res.Cancelled) return Result.Cancel;
                var pl = Shared.GlbPlacement(res.Model);
                RhinoApp.WriteLine("geoBIM.app: written {0}{1}", path, pl == null ? "" : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    " — placement lon {0:0.000000}, lat {1:0.000000}, {2:0.##} m above sea level, heading {3:0.#}°", pl.Lon, pl.Lat, pl.Height, pl.Heading));
                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("geoBIM.app: export failed — {0}", ex.Message);
                return Result.Failure;
            }
        }
    }

    // ------------------------------------------------------------------
    [Guid("de663117-0942-4c47-8678-11e22b700844")]
    public class GeoBIMUploadCommand : Command
    {
        public override string EnglishName => "GeoBIMUpload";

        static int _busy;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var p = GeoBimPlugin.Instance;
            if (string.IsNullOrEmpty(p.Token))
            {
                RhinoApp.WriteLine("geoBIM.app: no plugin token yet. In geoBIM.app open Account → Rhino plugin → Create, then run GeoBIMSettings → Token.");
                return Result.Cancel;
            }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                RhinoApp.WriteLine("geoBIM.app: an upload is still running.");
                return Result.Cancel;
            }
            bool started = false;
            try
            {
                var r = Shared.SelectObjects(doc, out var objects);
                if (r != Result.Success) return r;

                string server = p.Server;
                string name = Shared.DocName(doc);
                // Format: IFC (converted to 3D Tiles, properties) or GLB (as is, with
                // PBR materials such as polished steel); remembered per document,
                // each format has its own model on the server
                var glbFormat = new OptionToggle(doc.Strings.GetValue("geobim", Shared.DocFormatKey) == "glb", "IFC", "GLB");
                var asNew = new OptionToggle(false, "Replace", "New");
                bool asNewTouched = false;
                string key, previous;
                while (true)
                {
                    key = glbFormat.CurrentValue ? Shared.GlbSlugKey(server) : Shared.SlugKey(server);
                    previous = doc.Strings.GetValue("geobim", key);
                    if (!asNewTouched) asNew.CurrentValue = string.IsNullOrEmpty(previous);
                    var gs = new GetString();
                    gs.SetCommandPrompt(string.IsNullOrEmpty(previous)
                        ? "Model name in geoBIM.app"
                        : $"Model name in geoBIM.app (replaces \"{previous}\")");
                    gs.SetDefaultString(name);
                    gs.AcceptNothing(true);
                    int optFormat = gs.AddOptionToggle("Format", ref glbFormat);
                    int optModel = string.IsNullOrEmpty(previous) ? -1 : gs.AddOptionToggle("Model", ref asNew);
                    var gr = gs.Get();
                    if (gr == GetResult.Option)
                    {
                        if (gs.OptionIndex() == optModel) asNewTouched = true;
                        if (gs.OptionIndex() == optFormat) asNewTouched = false;
                        continue;
                    }
                    if (gr == GetResult.String) { var s = gs.StringResult().Trim(); if (s.Length > 0) name = s; break; }
                    if (gr == GetResult.Nothing) break;
                    return Result.Cancel;
                }
                bool glb = glbFormat.CurrentValue;
                doc.Strings.SetString("geobim", Shared.DocFormatKey, glb ? "glb" : "ifc");
                string replace = asNew.CurrentValue || string.IsNullOrEmpty(previous) ? null : previous;

                var dir = Path.Combine(Path.GetTempPath(), "geobim-rhino");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, Safe(name) + (glb ? ".glb" : ".ifc"));
                ExportResult res;
                try { res = Shared.Export(doc, objects, name, path, mode, glb); }
                catch (Exception ex) { RhinoApp.WriteLine("geoBIM.app: export failed — {0}", ex.Message); return Result.Failure; }
                if (res.Cancelled) return Result.Cancel;
                if (res.Elements == 0) { RhinoApp.WriteLine("geoBIM.app: no geometry to upload."); return Result.Nothing; }
                var placement = glb ? Shared.GlbPlacement(res.Model) : null;

                uint docSerial = doc.RuntimeSerialNumber;
                var client = new GeoBimClient(server, p.Token);
                RhinoApp.WriteLine(string.Format("geoBIM.app: uploading {0} {1:N1} MB to {2}{3}…", glb ? "GLB" : "IFC", new FileInfo(path).Length / 1048576.0, server,
                    replace != null ? $" (replaces {replace})" : ""));
                Rhino.UI.StatusBar.ShowProgressMeter(docSerial, 0, 100, "geoBIM.app upload", true, true);
                started = true;
                Task.Run(() => Run(client, path, name, replace, key, docSerial, placement));
                return Result.Success;
            }
            finally
            {
                if (!started) Interlocked.Exchange(ref _busy, 0);
            }
        }

        static string Safe(string s) => Shared.Safe(s);

        static async Task Run(GeoBimClient client, string path, string name, string replace, string key, uint docSerial, GeoBimClient.Placement placement)
        {
            int lastPct = -1;
            var progress = new Progress<double>(f =>
            {
                int pct = (int)Math.Round(f * 100);
                if (pct == lastPct) return;
                lastPct = pct;
                Shared.Ui(() => Rhino.UI.StatusBar.UpdateProgressMeter(docSerial, pct, true));
            });
            try
            {
                GeoBimClient.UploadResult up;
                try
                {
                    up = await client.Upload(path, name, replace, progress, default, placement).ConfigureAwait(false);
                }
                catch (GeoBimClient.ApiException ex) when (replace != null && ex.Status == HttpStatusCode.NotFound)
                {
                    Shared.Ui(() => RhinoApp.WriteLine("geoBIM.app: the previous model is gone on the server — uploading as a new model."));
                    up = await client.Upload(path, name, null, progress, default, placement).ConfigureAwait(false);
                }
                // Only the upload blocks a new one; the conversion is followed in the background
                Interlocked.Exchange(ref _busy, 0);
                Shared.Ui(() =>
                {
                    Rhino.UI.StatusBar.HideProgressMeter(docSerial);
                    var doc = RhinoDoc.FromRuntimeSerialNumber(docSerial);
                    doc?.Strings.SetString("geobim", key, up.Slug);
                    RhinoApp.WriteLine("geoBIM.app: uploaded as \"{0}\", converting on the server…", up.Slug);
                });

                string lastLine = null;
                var deadline = DateTime.UtcNow.AddHours(3);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    JsonElement st;
                    try { st = await client.Status(up.JobId).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is GeoBimClient.ApiException)) { continue; }   // network hiccup: keep polling
                    var status = Str(st, "status");
                    if (status == "done")
                    {
                        var placed = Str(st, "placement");
                        var warning = Str(st, "warning");
                        var url = client.ModelUrl(up.Slug);
                        Shared.Ui(() =>
                        {
                            RhinoApp.WriteLine("geoBIM.app: ready — {0}{1}", url, string.IsNullOrEmpty(placed) ? "" : $" (placement: {placed})");
                            if (!string.IsNullOrEmpty(warning)) RhinoApp.WriteLine("geoBIM.app: note — {0}", warning);
                            Shared.OpenUrl(url);
                        });
                        return;
                    }
                    if (status == "error")
                    {
                        var msg = Str(st, "message");
                        Shared.Ui(() => RhinoApp.WriteLine("geoBIM.app: conversion failed — {0}", msg ?? "unknown error"));
                        return;
                    }
                    var line = status == "queued" ? "waiting for another conversion" : "converting" + (Str(st, "progress") is string pr ? " — " + pr : "");
                    if (line != lastLine)
                    {
                        lastLine = line;
                        Shared.Ui(() => RhinoApp.WriteLine("geoBIM.app: {0}", line));
                    }
                }
                Shared.Ui(() => RhinoApp.WriteLine("geoBIM.app: still converting after 3 hours — check the model list in geoBIM.app."));
            }
            catch (Exception ex)
            {
                Shared.Ui(() =>
                {
                    Rhino.UI.StatusBar.HideProgressMeter(docSerial);
                    RhinoApp.WriteLine("geoBIM.app: upload failed — {0}", ex.Message);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// Upload to Cesium ion: the selection becomes an IFC in east/north/up metres
    /// around the model's position (GeoLocation.For: map origin, map coordinates or
    /// Rhino's Location); ion's BIM/CAD tiler places its origin at that lon/lat and
    /// the height above sea level plus the geoid undulation N (ion heights are
    /// ellipsoidal). Every upload is a new ion asset.
    /// Scripted: _-GeoBIMUploadIon _Enter GeoidN=48.7 "name"
    /// </summary>
    [Guid("d192ead3-ee5e-4009-9cb6-a78788f1f712")]
    public class GeoBIMUploadIonCommand : Command
    {
        public override string EnglishName => "GeoBIMUploadIon";

        static int _busy;

        static bool InMapCoordinates(RhinoDoc doc)
        {
            var fp = GeoLocation.Footprint(doc);
            double scale = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
            return fp != null && CrsDetect.LooksLikeMap((fp[0].X + fp[2].X) / 2 * scale, (fp[0].Y + fp[2].Y) / 2 * scale);
        }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var p = GeoBimPlugin.Instance;
            if (MapOrigin.FromDoc(doc) == null && !doc.EarthAnchorPoint.EarthLocationIsSet() && !InMapCoordinates(doc))
            {
                RhinoApp.WriteLine("Cesium ion: this model has no position yet — set it with GeoBIMLocation.");
                return Result.Cancel;
            }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                RhinoApp.WriteLine("Cesium ion: an upload is still running.");
                return Result.Cancel;
            }
            bool started = false;
            try
            {
                var r = Shared.SelectObjects(doc, out var objects);
                if (r != Result.Success) return r;

                string name = Shared.DocName(doc);
                string stored = doc.Strings.GetValue("geobim", Shared.DocGeoidKey);
                // 0 counts as not set: the undulation is almost nowhere 0, and 0 is what an Enter on an empty prompt gives
                bool geoidKnown = double.TryParse(stored ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out double geoidValue) && geoidValue != 0;
                var geoid = new OptionDouble(geoidKnown ? geoidValue : 0, -120, 120);
                var upload = new OptionToggle(true, "No", "Yes");
                while (true)
                {
                    var gs = new GetString();
                    gs.SetCommandPrompt(geoidKnown
                        ? "Asset name in Cesium ion"
                        : "Asset name in Cesium ion (set GeoidN: geoid undulation in metres at the site, e.g. 48.7 in eastern Switzerland)");
                    gs.SetDefaultString(name);
                    gs.AcceptNothing(true);
                    int optGeoid = gs.AddOptionDouble("GeoidN", ref geoid);
                    gs.AddOptionToggle("Upload", ref upload);
                    var gr = gs.Get();
                    if (gr == GetResult.Option) { if (gs.OptionIndex() == optGeoid) geoidKnown = geoid.CurrentValue != 0; continue; }
                    if (gr == GetResult.String) { var s = gs.StringResult().Trim(); if (s.Length > 0) name = s; break; }
                    if (gr == GetResult.Nothing) break;
                    return Result.Cancel;
                }
                if (!geoidKnown)
                {
                    if (mode != RunMode.Interactive)
                    {
                        RhinoApp.WriteLine("Cesium ion: GeoidN is not set for this document — pass it as an option (GeoidN=48.7).");
                        return Result.Cancel;
                    }
                    var gn = new GetNumber();   // no default: an Enter must not silently mean 0
                    gn.SetCommandPrompt("Geoid undulation N at the site in metres (ellipsoid height − height above sea level, e.g. 48.7 in eastern Switzerland)");
                    gn.SetLowerLimit(-120, false);
                    gn.SetUpperLimit(120, false);
                    gn.AcceptNothing(false);
                    if (gn.Get() != GetResult.Number || gn.Number() == 0)
                    {
                        RhinoApp.WriteLine("Cesium ion: no geoid undulation given — upload cancelled.");
                        return Result.Cancel;
                    }
                    geoid.CurrentValue = gn.Number();
                }
                double geoidN = geoid.CurrentValue;
                doc.Strings.SetString("geobim", Shared.DocGeoidKey, geoidN.ToString("0.###", CultureInfo.InvariantCulture));

                var dir = Path.Combine(Path.GetTempPath(), "geobim-rhino");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, Shared.Safe(name) + "_ion.ifc");
                ExportResult res;
                try { res = Shared.ExportIon(doc, objects, name, path); }
                catch (Exception ex) { RhinoApp.WriteLine("Cesium ion: export failed — {0}", ex.Message); return Result.Failure; }
                if (res.Elements == 0) { RhinoApp.WriteLine("Cesium ion: no geometry to upload."); return Result.Nothing; }

                double height = res.IonElevation + geoidN;
                if (!upload.CurrentValue)
                {
                    RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "Cesium ion: not uploaded (Upload=No). IFC written to {0} — origin lon {1:0.0000000}, lat {2:0.0000000}, {3:0.###} m ellipsoidal.",
                        path, res.IonLon, res.IonLat, height));
                    return Result.Success;
                }
                if (string.IsNullOrEmpty(p.IonToken))
                {
                    RhinoApp.WriteLine("Cesium ion: no access token yet. Create one at ion.cesium.com → Access Tokens (scopes assets:list, assets:read, assets:write), then run GeoBIMSettings → IonToken.");
                    return Result.Cancel;
                }
                string description = string.Format(CultureInfo.InvariantCulture,
                    "Uploaded from Rhino ({0}) with geoBIM.app for Rhino {1}. Origin at {2:0.0000000}, {3:0.0000000}, " +
                    "{4:0.###} m above sea level + geoid N {5:0.###} m = {6:0.###} m ellipsoidal.",
                    Shared.DocName(doc), GeoBimPlugin.PluginVersion, res.IonLat, res.IonLon, res.IonElevation, geoidN, height);

                uint docSerial = doc.RuntimeSerialNumber;
                var client = new IonClient(p.IonToken);
                RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Cesium ion: uploading IFC {0:N1} MB as \"{1}\" — origin lon {2:0.0000000}, lat {3:0.0000000}, {4:0.###} m ellipsoidal…",
                    new FileInfo(path).Length / 1048576.0, name, res.IonLon, res.IonLat, height));
                Rhino.UI.StatusBar.ShowProgressMeter(docSerial, 0, 100, "Cesium ion upload", true, true);
                started = true;
                Task.Run(() => Run(client, path, name, description, res.IonLon, res.IonLat, height, docSerial));
                return Result.Success;
            }
            finally
            {
                if (!started) Interlocked.Exchange(ref _busy, 0);
            }
        }

        static async Task Run(IonClient client, string path, string name, string description, double lon, double lat, double height, uint docSerial)
        {
            int lastPct = -1;
            var progress = new Progress<double>(f =>
            {
                int pct = (int)Math.Round(f * 100);
                if (pct == lastPct) return;
                lastPct = pct;
                Shared.Ui(() => Rhino.UI.StatusBar.UpdateProgressMeter(docSerial, pct, true));
            });
            try
            {
                var asset = await client.CreateAsset(name, description, lon, lat, height).ConfigureAwait(false);
                Shared.Ui(() =>
                {
                    RhinoDoc.FromRuntimeSerialNumber(docSerial)?.Strings.SetString("geobim", Shared.DocIonAssetKey, asset.Id.ToString(CultureInfo.InvariantCulture));
                });
                await IonClient.PutS3(asset.Upload, path, progress).ConfigureAwait(false);
                await client.Complete(asset).ConfigureAwait(false);
                Interlocked.Exchange(ref _busy, 0);
                Shared.Ui(() =>
                {
                    Rhino.UI.StatusBar.HideProgressMeter(docSerial);
                    RhinoApp.WriteLine("Cesium ion: uploaded as asset {0}, tiling on ion…", asset.Id);
                });

                string lastLine = null;
                var deadline = DateTime.UtcNow.AddHours(3);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(5000).ConfigureAwait(false);
                    JsonElement st;
                    try { st = await client.GetAsset(asset.Id).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is IonClient.ApiException)) { continue; }   // network hiccup: keep polling
                    var status = st.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "";
                    if (status == "COMPLETE")
                    {
                        var url = IonClient.AssetUrl(asset.Id);
                        Shared.Ui(() =>
                        {
                            RhinoApp.WriteLine("Cesium ion: ready — {0}", url);
                            Shared.OpenUrl(url);
                        });
                        return;
                    }
                    if (status == "DATA_ERROR" || status == "ERROR")
                    {
                        Shared.Ui(() => RhinoApp.WriteLine("Cesium ion: tiling failed ({0}) — see {1}", status, IonClient.AssetUrl(asset.Id)));
                        return;
                    }
                    string line = status == "IN_PROGRESS" && st.TryGetProperty("percentComplete", out var pc) && pc.ValueKind == JsonValueKind.Number
                        ? $"tiling — {pc.GetDouble():0}%" : "waiting (" + status + ")";
                    if (line != lastLine)
                    {
                        lastLine = line;
                        Shared.Ui(() => RhinoApp.WriteLine("Cesium ion: {0}", line));
                    }
                }
                Shared.Ui(() => RhinoApp.WriteLine("Cesium ion: still tiling after 3 hours — check {0}", IonClient.AssetUrl(asset.Id)));
            }
            catch (Exception ex)
            {
                Shared.Ui(() =>
                {
                    Rhino.UI.StatusBar.HideProgressMeter(docSerial);
                    RhinoApp.WriteLine("Cesium ion: upload failed — {0}", ex.Message);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }
    }

    // ------------------------------------------------------------------
    [Guid("b3d2a8c4-5e1f-4f6a-9c7d-2e8b1a4f6d30")]
    [CommandStyle(Style.ScriptRunner)] // may run _-ShowToolbar (fallback in Rhino 8)
    public class GeoBIMToolbarCommand : Command
    {
        public override string EnglishName => "GeoBIMToolbar";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (GeoBimPlugin.ShowToolbar()) return Result.Success;
            RhinoApp.WriteLine("geoBIM.app: toolbar file not found next to the plugin ({0}).", GeoBimPlugin.ToolbarPath);
            return Result.Failure;
        }
    }

    // ------------------------------------------------------------------
    [Guid("69dea895-bf7c-4600-9dbb-72bd2640ffcc")]
    public class GeoBIMOpenCommand : Command
    {
        public override string EnglishName => "GeoBIMOpen";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var p = GeoBimPlugin.Instance;
            // the model of the format used last (GLB and IFC are separate models)
            bool glb = doc.Strings.GetValue("geobim", Shared.DocFormatKey) == "glb";
            var slug = doc.Strings.GetValue("geobim", glb ? Shared.GlbSlugKey(p.Server) : Shared.SlugKey(p.Server))
                       ?? doc.Strings.GetValue("geobim", glb ? Shared.SlugKey(p.Server) : Shared.GlbSlugKey(p.Server));
            var url = string.IsNullOrEmpty(slug) ? p.Server : new GeoBimClient(p.Server, p.Token).ModelUrl(slug);
            Shared.OpenUrl(url);
            return Result.Success;
        }
    }
}
