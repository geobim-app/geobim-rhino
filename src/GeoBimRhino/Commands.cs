// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// Placement for a GLB upload from the EarthAnchorPoint: lon/lat of the model
        /// origin, height above sea level (the server adds the geoid), heading of
        /// the model's +Y axis clockwise from true north. Null without a Location.
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
            // GLB is placed by lat/lon (EarthAnchorPoint); map coordinates would need a
            // projection the plugin doesn't have, so don't ask for an EPSG code there
            // and leave the document's map origin to the IFC export
            var mapOrigin = MapOrigin.FromDoc(doc);
            var res = ModelExport.Build(doc, objects, name, (e, n) => glb ? 0 : ResolveEpsg(doc, e, n, mode), glb ? null : mapOrigin);
            if (res.Cancelled) return res;
            if (glb)
            {
                if (res.Model.GeoRef == GeoRefKind.MapConversion)
                    throw new InvalidOperationException("GLB needs the model near the origin with a Location (EarthAnchorPoint) — for map coordinates use Format=IFC");
                if (mapOrigin != null && res.Model.GeoRef != GeoRefKind.SiteLatLon)
                    RhinoApp.WriteLine("geoBIM.app: GLB is placed by the Location (EarthAnchorPoint), not by the map origin — set a Location or use Format=IFC.");
                if (res.Elements > 0) GlbWriter.Write(res.Model, path);
            }
            else IfcWriter.Write(res.Model, path);
            RhinoApp.WriteLine(string.Format("geoBIM.app: {0} elements, {1:N0} triangles{2}; {3}",
                res.Elements, res.Triangles, res.Skipped > 0 ? $", {res.Skipped} objects without surfaces skipped" : "", res.GeoRefInfo));
            return res;
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
                    string t = "";
                    if (RhinoGet.GetString("Plugin token (geoBIM.app → Account → Rhino plugin → Create)", false, ref t) != Result.Success) continue;
                    t = t.Trim();
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
    /// <summary>
    /// Map origin of this document: the model stays in local coordinates and the
    /// IFC export writes IfcMapConversion from E0/N0/H0 (and a rotation), so no
    /// second copy of the model in real map coordinates is needed.
    /// Scripted: -GeoBIMMapOrigin 2755000,1206000,0,0 or -GeoBIMMapOrigin Clear
    /// </summary>
    [Guid("714cd07f-7895-4bf7-a22c-4a77bc5acd3e")]
    public class GeoBIMMapOriginCommand : Command
    {
        public override string EnglishName => "GeoBIMMapOrigin";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var current = MapOrigin.FromDoc(doc);
            RhinoApp.WriteLine(current == null
                ? "geoBIM.app: this document has no map origin (local coordinates are placed by the Location, if any)."
                : $"geoBIM.app: map origin of this document: {current.Describe()} — local x = E − E0, y = N − N0, z = H − H0.");

            var gs = new GetString();
            gs.SetCommandPrompt("Map coordinates of the model origin E,N[,H[,rotation°]] (Enter keeps)");
            if (current != null) gs.SetDefaultString(current.ToString());
            gs.AcceptNothing(true);
            int optClear = gs.AddOption("Clear");
            var r = gs.Get();
            if (r == GetResult.Option && gs.OptionIndex() == optClear)
            {
                MapOrigin.Clear(doc);
                RhinoApp.WriteLine("geoBIM.app: map origin removed.");
                return Result.Success;
            }
            if (r == GetResult.Nothing) return Result.Success;
            if (r != GetResult.String) return Result.Cancel;

            var text = gs.StringResult().Trim();
            if (text.Equals("Clear", StringComparison.OrdinalIgnoreCase))
            {
                MapOrigin.Clear(doc);
                RhinoApp.WriteLine("geoBIM.app: map origin removed.");
                return Result.Success;
            }
            var mo = MapOrigin.Parse(text);
            if (mo == null)
            {
                RhinoApp.WriteLine("geoBIM.app: expected E,N[,H[,rotation]] in metres and degrees, e.g. 2755000,1206000,0,0");
                return Result.Failure;
            }
            if (!CrsDetect.LooksLikeMap(mo.E, mo.N))
            {
                RhinoApp.WriteLine("geoBIM.app: E {0:0} N {1:0} are not map coordinates (more than 100 km from 0 expected).", mo.E, mo.N);
                return Result.Failure;
            }
            mo.Save(doc);
            var guess = CrsDetect.Guess(mo.E, mo.N, GeoBimPlugin.Instance.DefaultUtmEpsg);
            RhinoApp.WriteLine("geoBIM.app: map origin set to {0}{1}. The IFC export writes IfcMapConversion; GLB still uses the Location.",
                mo.Describe(), guess.Epsg != null ? $" (EPSG:{guess.Epsg}, {guess.Region})" : $" ({guess.Region}: the coordinate system is asked on export)");
            return Result.Success;
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

        static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "model";
        }

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
    [Guid("b3d2a8c4-5e1f-4f6a-9c7d-2e8b1a4f6d30")]
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
