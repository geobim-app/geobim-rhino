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

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { RhinoApp.WriteLine("geoBIM.app: could not open the browser ({0}) — {1}", ex.Message, url); }
        }

        public static void Ui(Action a) => RhinoApp.InvokeOnUiThread(a);

        public static ExportResult Export(RhinoDoc doc, List<RhinoObject> objects, string name, string path)
        {
            var p = GeoBimPlugin.Instance;
            var res = ModelExport.Build(doc, objects, name, p.Epsg, p.DefaultUtmEpsg);
            IfcWriter.Write(res.Model, path);
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
                go.SetCommandPrompt($"geoBIM.app — server {p.Server}, token {GeoBimPlugin.MaskedToken(p.Token)}, EPSG {p.Epsg} (default UTM {p.DefaultUtmEpsg})");
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
                Shared.Export(doc, objects, name, path);
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
                string key = Shared.SlugKey(server);
                string previous = doc.Strings.GetValue("geobim", key);
                string name = Shared.DocName(doc);
                var asNew = new OptionToggle(string.IsNullOrEmpty(previous), "Replace", "New");
                var gs = new GetString();
                gs.SetCommandPrompt(string.IsNullOrEmpty(previous)
                    ? "Model name in geoBIM.app"
                    : $"Model name in geoBIM.app (replaces \"{previous}\")");
                gs.SetDefaultString(name);
                gs.AcceptNothing(true);
                if (!string.IsNullOrEmpty(previous)) gs.AddOptionToggle("Model", ref asNew);
                while (true)
                {
                    var gr = gs.Get();
                    if (gr == GetResult.Option) continue;
                    if (gr == GetResult.String) { var s = gs.StringResult().Trim(); if (s.Length > 0) name = s; break; }
                    if (gr == GetResult.Nothing) break;
                    return Result.Cancel;
                }
                string replace = asNew.CurrentValue ? null : previous;

                var dir = Path.Combine(Path.GetTempPath(), "geobim-rhino");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, Safe(name) + ".ifc");
                ExportResult res;
                try { res = Shared.Export(doc, objects, name, path); }
                catch (Exception ex) { RhinoApp.WriteLine("geoBIM.app: export failed — {0}", ex.Message); return Result.Failure; }
                if (res.Elements == 0) { RhinoApp.WriteLine("geoBIM.app: no geometry to upload."); return Result.Nothing; }

                uint docSerial = doc.RuntimeSerialNumber;
                var client = new GeoBimClient(server, p.Token);
                RhinoApp.WriteLine("geoBIM.app: uploading {0:N1} MB to {1}{2}…", new FileInfo(path).Length / 1048576.0, server,
                    replace != null ? $" (replaces {replace})" : "");
                Rhino.UI.StatusBar.ShowProgressMeter(docSerial, 0, 100, "geoBIM.app upload", true, true);
                started = true;
                Task.Run(() => Run(client, path, name, replace, key, docSerial));
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

        static async Task Run(GeoBimClient client, string path, string name, string replace, string key, uint docSerial)
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
                    up = await client.Upload(path, name, replace, progress).ConfigureAwait(false);
                }
                catch (GeoBimClient.ApiException ex) when (replace != null && ex.Status == HttpStatusCode.NotFound)
                {
                    Shared.Ui(() => RhinoApp.WriteLine("geoBIM.app: the previous model is gone on the server — uploading as a new model."));
                    up = await client.Upload(path, name, null, progress).ConfigureAwait(false);
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
                        var placement = Str(st, "placement");
                        var warning = Str(st, "warning");
                        var url = client.ModelUrl(up.Slug);
                        Shared.Ui(() =>
                        {
                            RhinoApp.WriteLine("geoBIM.app: ready — {0}{1}", url, string.IsNullOrEmpty(placement) ? "" : $" (placement: {placement})");
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
    [Guid("69dea895-bf7c-4600-9dbb-72bd2640ffcc")]
    public class GeoBIMOpenCommand : Command
    {
        public override string EnglishName => "GeoBIMOpen";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var p = GeoBimPlugin.Instance;
            var slug = doc.Strings.GetValue("geobim", Shared.SlugKey(p.Server));
            var url = string.IsNullOrEmpty(slug) ? p.Server : new GeoBimClient(p.Server, p.Token).ModelUrl(slug);
            Shared.OpenUrl(url);
            return Result.Success;
        }
    }
}
