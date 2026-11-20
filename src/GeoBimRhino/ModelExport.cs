// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace GeoBimRhino
{
    public class ExportResult
    {
        public IfcModelData Model;
        public int Elements;
        public int Skipped;          // curves, points, annotations, empty meshes
        public long Triangles;
        public string GeoRefInfo = "";
        public string Epsg;          // EPSG code written into IfcMapConversion, else null
        public bool Cancelled;       // the user cancelled the CRS question
        // Cesium ion export: WGS84 position of the IFC origin (the base point from
        // GeoLocation.For) and its height above sea level (no geoid yet)
        public double IonLon, IonLat, IonElevation;
        // GLB / Cesium ion: why the map position could not be used (then Rhino's Location places it), else null
        public string PlacementNote;
        // Cesium ion: coordinate system of the position (null for Rhino's Location), for the geoid choice
        public int? IonEpsg;
    }

    public enum ExportTarget { Ifc, Glb, Ion }

    /// <summary>
    /// Rhino objects → IfcModelData. One IFC element per Rhino object (block
    /// instances: one per component, with the instance transform), geometry as
    /// render meshes in metres, relative to an origin chosen by the
    /// georeferencing so float vertices keep millimetres:
    ///  1. map origin stored in the document (local coordinates, see MapOrigin):
    ///     IfcMapConversion with that origin, geometry unchanged;
    ///  2. map coordinates (|x| or |y| &gt; 100 km): IfcMapConversion, EPSG from
    ///     the caller (setting, document choice, CrsDetect, or asked);
    ///  3. EarthAnchorPoint set: IfcSite RefLatitude/RefLongitude/RefElevation,
    ///     model base point as origin, model north as TrueNorth;
    ///  4. otherwise none: geoBIM.app drops the model at the centre of the view.
    /// GLB and Cesium ion are placed by latitude/longitude instead (see GeoLocation.For).
    /// </summary>
    public static class ModelExport
    {
        class Item
        {
            public RhinoObject Source;      // top-level object (or the instance for components)
            public RhinoObject Object;      // object carrying geometry and attributes
            public GeometryBase Geometry;
            public Transform Xform = Transform.Identity;
            public Guid Id;
            public string Block;
            public System.Drawing.Color ParentColor;
        }

        /// <param name="resolveEpsg">EPSG for map coordinates (easting, northing of the model centre in metres); null cancels</param>
        /// <param name="mapOrigin">map origin of local coordinates (the document's, see MapOrigin), or null to ignore it</param>
        /// <param name="target">IFC: georeferencing as listed above. GLB: placed by latitude/longitude from
        /// GeoLocation.For (map origin or map coordinates when they convert, else Rhino's Location).
        /// Cesium ion: geometry in east/north/up metres around that base point (true north turned to
        /// +Y, as EarthLink exports), no georeferencing in the IFC itself; the position travels
        /// separately (IonLon/IonLat/IonElevation).</param>
        public static ExportResult Build(RhinoDoc doc, IList<RhinoObject> objects, string name, Func<double, double, int?> resolveEpsg,
                                         MapOrigin mapOrigin = null, ExportTarget target = ExportTarget.Ifc)
        {
            var res = new ExportResult();
            var model = new IfcModelData
            {
                ProjectName = string.IsNullOrWhiteSpace(name) ? "Rhino model" : name,
                SiteName = string.IsNullOrWhiteSpace(name) ? "Site" : name,
                ApplicationVersion = GeoBimPlugin.PluginVersion,
                ProjectId = IfcWriter.DerivedGuid("geobim-rhino/project/" + (doc.Path ?? doc.Name ?? name))
            };
            res.Model = model;

            // Collect geometry (blocks resolved, nested ones too)
            var items = new List<Item>();
            foreach (var obj in objects) Collect(doc, obj, obj, Transform.Identity, null, obj.Attributes.DrawColor(doc), "", items, ref res.Skipped, 0);
            if (items.Count == 0) return res;

            double scale = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
            var bbox = BoundingBox.Empty;
            foreach (var it in items) bbox.Union(it.Geometry.GetBoundingBox(it.Xform));
            var c = bbox.Center;
            double cx = c.X * scale, cy = c.Y * scale;

            // ---- Georeferencing → origin (model units) ----
            Point3d origin;
            var anchor = doc.EarthAnchorPoint;
            bool inMapCoords = CrsDetect.LooksLikeMap(cx, cy);
            var rotate = Transform.Identity;
            if (target != ExportTarget.Ifc)
            {
                var a = GeoLocation.For(doc, mapOrigin, cx, cy, scale, out string note);
                res.PlacementNote = note;
                if (target == ExportTarget.Ion)
                {
                    if (a == null)
                        throw new InvalidOperationException("Cesium ion needs the model's position — set it with GeoBIMLocation");
                    origin = a.BasePoint;
                    // turn true north onto +Y (east/north/up), so ion needs no heading
                    double north = Math.Atan2(-a.North.X, a.North.Y);
                    rotate = Transform.Rotation(-north, Vector3d.ZAxis, Point3d.Origin);
                    res.IonLat = a.Lat;
                    res.IonLon = a.Lon;
                    res.IonElevation = a.Elevation;
                    res.IonEpsg = a.Epsg;
                    res.GeoRefInfo = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "east/north/up around {0:0.000000}, {1:0.000000}, {2:0.##} m from the {3} (true north {4:0.###}° clockwise from +Y)",
                        res.IonLat, res.IonLon, res.IonElevation, a.Source, -north * 180.0 / Math.PI);
                }
                else if (a != null)
                {
                    origin = a.BasePoint;
                    model.GeoRef = GeoRefKind.SiteLatLon;
                    model.Latitude = a.Lat;
                    model.Longitude = a.Lon;
                    model.Elevation = a.Elevation;
                    model.TrueNorthX = a.North.X;
                    model.TrueNorthY = a.North.Y;
                    res.GeoRefInfo = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0:0.000000}, {1:0.000000}, {2:0.##} m from the {3}", model.Latitude, model.Longitude, model.Elevation, a.Source);
                }
                else
                {
                    origin = new Point3d(c.X, c.Y, bbox.Min.Z);
                    res.GeoRefInfo = "no position — place the model in geoBIM.app and save its position, or set it with GeoBIMLocation";
                }
            }
            else if (mapOrigin != null && !inMapCoords)
            {
                // Local coordinates + stored map origin: the geometry stays as it is
                mapOrigin.ToMap(cx, cy, out double me, out double mn);
                int? chosen = resolveEpsg(me, mn);
                if (chosen == null) { res.Cancelled = true; return res; }
                int epsg = chosen.Value;
                origin = Point3d.Origin;
                model.GeoRef = GeoRefKind.MapConversion;
                model.CrsName = "EPSG:" + epsg;
                model.Eastings = mapOrigin.E;
                model.Northings = mapOrigin.N;
                model.OrthogonalHeight = mapOrigin.H;
                model.XAxisAbscissa = mapOrigin.XAxisAbscissa;
                model.XAxisOrdinate = mapOrigin.XAxisOrdinate;
                res.Epsg = epsg.ToString();
                res.GeoRefInfo = $"map origin, EPSG:{epsg} ({mapOrigin.Describe()})";
            }
            else if (inMapCoords)
            {
                int? chosen = resolveEpsg(cx, cy);
                if (chosen == null) { res.Cancelled = true; return res; }
                int epsg = chosen.Value;
                double ox = Math.Round(cx / 1000.0) * 1000.0, oy = Math.Round(cy / 1000.0) * 1000.0;
                origin = new Point3d(ox / scale, oy / scale, 0);
                model.GeoRef = GeoRefKind.MapConversion;
                model.CrsName = "EPSG:" + epsg;
                model.Eastings = ox;
                model.Northings = oy;
                model.OrthogonalHeight = 0;
                res.Epsg = epsg.ToString();
                res.GeoRefInfo = $"map coordinates, EPSG:{epsg} (origin E {ox:0} N {oy:0})"
                                 + (mapOrigin != null ? " — the document's map origin is ignored, the geometry already lies in map coordinates" : "");
            }
            else if (anchor != null && anchor.EarthLocationIsSet())
            {
                origin = anchor.ModelBasePoint;
                model.GeoRef = GeoRefKind.SiteLatLon;
                model.Latitude = anchor.EarthBasepointLatitude;
                model.Longitude = anchor.EarthBasepointLongitude;
                model.Elevation = anchor.EarthBasepointElevation;
                var n = anchor.ModelNorth;
                n.Z = 0;
                if (n.Unitize()) { model.TrueNorthX = n.X; model.TrueNorthY = n.Y; }
                res.GeoRefInfo = $"EarthAnchorPoint {model.Latitude:0.000000}, {model.Longitude:0.000000}, {model.Elevation:0.##} m";
            }
            else
            {
                origin = new Point3d(c.X, c.Y, bbox.Min.Z);
                res.GeoRefInfo = "no georeferencing — place the model in geoBIM.app and save its position";
            }

            var toIfc = Transform.Scale(Point3d.Origin, scale) * rotate * Transform.Translation(-(Vector3d)origin);
            var mp = doc.GetCurrentMeshingParameters() ?? MeshingParameters.QualityRenderMesh;

            foreach (var it in items)
            {
                var mesh = ToMesh(it.Geometry, toIfc * it.Xform, mp);
                if (mesh == null || mesh.Faces.Count == 0) { res.Skipped++; continue; }
                var e = Element(doc, it, mesh);
                model.Elements.Add(e);
                res.Elements++;
                res.Triangles += e.Mesh.Triangles.Count / 3;
            }
            return res;
        }

        static void Collect(RhinoDoc doc, RhinoObject top, RhinoObject obj, Transform xf, string block,
                            System.Drawing.Color parentColor, string path, List<Item> items, ref int skipped, int depth)
        {
            if (obj is InstanceObject inst)
            {
                if (depth > 16) { skipped++; return; }
                var def = inst.InstanceDefinition;
                if (def == null) { skipped++; return; }
                var ixf = xf * inst.InstanceXform;
                var color = obj.Attributes.ColorSource == ObjectColorSource.ColorFromParent ? parentColor : obj.Attributes.DrawColor(doc);
                int k = 0;
                foreach (var child in def.GetObjects())
                {
                    Collect(doc, top, child, ixf, def.Name, color, path + "/" + inst.Id + ":" + (k++), items, ref skipped, depth + 1);
                }
                return;
            }
            var g = obj.Geometry;
            if (!(g is Brep || g is Extrusion || g is SubD || g is Mesh || g is Surface)) { skipped++; return; }
            items.Add(new Item
            {
                Source = top,
                Object = obj,
                Geometry = g,
                Xform = xf,
                Id = path.Length == 0 ? obj.Id : IfcWriter.DerivedGuid(top.Id + path + "/" + obj.Id),
                Block = block,
                ParentColor = parentColor
            });
        }

        static Mesh ToMesh(GeometryBase g, Transform xf, MeshingParameters mp)
        {
            var dup = g.Duplicate();
            Mesh mesh = null;
            switch (dup)
            {
                case Mesh m:
                    m.Transform(xf);
                    mesh = m;
                    break;
                case Brep b:
                    b.Transform(xf);
                    mesh = Join(Mesh.CreateFromBrep(b, mp));
                    break;
                case Extrusion ex:
                    var eb = ex.ToBrep();
                    eb.Transform(xf);
                    mesh = Join(Mesh.CreateFromBrep(eb, mp));
                    break;
                case SubD sd:
                    sd.Transform(xf);
                    mesh = Mesh.CreateFromSubD(sd, 3);
                    break;
                case Surface s:
                    var sb = Brep.CreateFromSurface(s);
                    if (sb != null) { sb.Transform(xf); mesh = Join(Mesh.CreateFromBrep(sb, mp)); }
                    break;
            }
            if (mesh == null) return null;
            mesh.Faces.ConvertQuadsToTriangles();
            mesh.Compact();
            return mesh;
        }

        static Mesh Join(Mesh[] parts)
        {
            if (parts == null || parts.Length == 0) return null;
            var m = new Mesh();
            m.Append(parts);
            return m;
        }

        static IfcElementData Element(RhinoDoc doc, Item it, Mesh mesh)
        {
            var obj = it.Object;
            var attrs = obj.Attributes;
            var layer = doc.Layers.FindIndex(attrs.LayerIndex);
            string layerPath = layer?.FullPath ?? "";

            var e = new IfcElementData { Id = it.Id, Layer = layerPath };
            var color = attrs.ColorSource == ObjectColorSource.ColorFromParent ? it.ParentColor : attrs.DrawColor(doc);
            e.R = color.R / 255.0; e.G = color.G / 255.0; e.B = color.B / 255.0;
            var mat = obj.GetMaterial(true);
            if (mat != null) e.Transparency = Math.Max(0, Math.Min(1, mat.Transparency));
            // PBR for GLB: GetMaterial() is only a simplified copy of the render
            // material, so ask the render material for its physically based form
            PhysicallyBasedMaterial pb = mat != null && mat.IsPhysicallyBased ? mat.PhysicallyBased : null;
            if (pb == null)
            {
                var rmat = obj.GetRenderMaterial(true);
                if (rmat != null)
                {
                    try { pb = rmat.ConvertToPhysicallyBased(Rhino.Render.RenderTexture.TextureGeneration.Skip); }
                    catch (Exception) { pb = null; }
                }
            }
            if (pb != null)
            {
                var bc = pb.BaseColor;
                e.Pbr = new PbrData
                {
                    R = Clamp01(bc.R), G = Clamp01(bc.G), B = Clamp01(bc.B),
                    Alpha = Clamp01(pb.Opacity),
                    Metallic = Clamp01(pb.Metallic),
                    Roughness = Clamp01(pb.Roughness),
                    Name = mat?.Name ?? ""
                };
            }

            string type = TypeName(obj.Geometry);
            string objName = attrs.Name;
            if (string.IsNullOrWhiteSpace(objName)) objName = it.Block ?? "";
            string leaf = layer?.Name ?? "";
            e.Name = !string.IsNullOrWhiteSpace(objName) ? objName : (leaf.Length > 0 ? $"{type} ({leaf})" : type);
            e.ObjectType = it.Block != null ? "Block " + it.Block : type;

            // IFC class: user text "IfcClass", else a layer name such as "IfcWall"
            e.IfcClass = IfcWriter.NormalizeClass(attrs.GetUserString("IfcClass"))
                         ?? (it.Source != obj ? IfcWriter.NormalizeClass(it.Source.Attributes.GetUserString("IfcClass")) : null)
                         ?? ClassFromLayer(layerPath)
                         ?? "IfcBuildingElementProxy";

            e.RhinoProps.Add(new KeyValuePair<string, string>("Layer", layerPath));
            if (!string.IsNullOrWhiteSpace(attrs.Name)) e.RhinoProps.Add(new KeyValuePair<string, string>("Name", attrs.Name));
            e.RhinoProps.Add(new KeyValuePair<string, string>("ObjectId", obj.Id.ToString()));
            e.RhinoProps.Add(new KeyValuePair<string, string>("Type", type));
            if (it.Block != null) e.RhinoProps.Add(new KeyValuePair<string, string>("Block", it.Block));
            if (mat != null && !string.IsNullOrWhiteSpace(mat.Name)) e.RhinoProps.Add(new KeyValuePair<string, string>("Material", mat.Name));

            AddUserText(e, attrs.GetUserStrings());
            if (it.Source != obj) AddUserText(e, it.Source.Attributes.GetUserStrings());

            // Vertices and triangles
            var v = mesh.Vertices;
            for (int i = 0; i < v.Count; i++)
            {
                var p = v.Point3dAt(i);
                e.Mesh.Coordinates.Add(Math.Round(p.X, 5));
                e.Mesh.Coordinates.Add(Math.Round(p.Y, 5));
                e.Mesh.Coordinates.Add(Math.Round(p.Z, 5));
            }
            foreach (var f in mesh.Faces)
            {
                if (f.A == f.B || f.B == f.C || f.A == f.C) continue;
                e.Mesh.Triangles.Add(f.A);
                e.Mesh.Triangles.Add(f.B);
                e.Mesh.Triangles.Add(f.C);
            }
            // Vertex normals (render meshes keep creases as split vertices)
            if (mesh.Normals.Count != v.Count) mesh.Normals.ComputeNormals();
            if (mesh.Normals.Count == v.Count)
            {
                for (int i = 0; i < v.Count; i++)
                {
                    var n = mesh.Normals[i];
                    e.Mesh.Normals.Add(n.X);
                    e.Mesh.Normals.Add(n.Y);
                    e.Mesh.Normals.Add(n.Z);
                }
            }
            return e;
        }

        static double Clamp01(double x) => double.IsNaN(x) ? 0 : Math.Max(0, Math.Min(1, x));

        static void AddUserText(IfcElementData e, NameValueCollection strings)
        {
            if (strings == null) return;
            foreach (string key in strings.AllKeys)
            {
                if (string.IsNullOrWhiteSpace(key) || e.UserText.Any(kv => kv.Key == key)) continue;
                e.UserText.Add(new KeyValuePair<string, string>(key, strings[key]));
            }
        }

        static string ClassFromLayer(string layerPath)
        {
            foreach (var part in layerPath.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                var word = part.Trim().Split(' ', '-', '_')[0];
                if (word.StartsWith("Ifc", StringComparison.OrdinalIgnoreCase))
                {
                    var cls = IfcWriter.NormalizeClass(word);
                    if (cls != null) return cls;
                }
            }
            return null;
        }

        static string TypeName(GeometryBase g)
        {
            switch (g)
            {
                case Extrusion _: return "Extrusion";
                case Brep b: return b.IsSolid ? "Solid" : "Polysurface";
                case SubD _: return "SubD";
                case Mesh _: return "Mesh";
                case Surface _: return "Surface";
                default: return g.ObjectType.ToString();
            }
        }
    }
}
