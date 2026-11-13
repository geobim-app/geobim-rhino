// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GeoBimRhino
{
    // ------------------------------------------------------------------
    // Plain model handed to the writer (no Rhino types: testable alone)
    // ------------------------------------------------------------------

    public class IfcMesh
    {
        /// <summary>x, y, z per vertex, metres, relative to the model origin</summary>
        public List<double> Coordinates = new List<double>();
        /// <summary>0-based vertex indices, three per triangle</summary>
        public List<int> Triangles = new List<int>();
        public int VertexCount => Coordinates.Count / 3;
    }

    public class IfcElementData
    {
        public Guid Id;                 // Rhino object id (stable GlobalId)
        public string IfcClass = "IfcBuildingElementProxy";
        public string Name = "";
        public string ObjectType = "";
        public string Layer = "";
        public double R = 0.8, G = 0.8, B = 0.8, Transparency;
        public IfcMesh Mesh = new IfcMesh();
        public List<KeyValuePair<string, string>> RhinoProps = new List<KeyValuePair<string, string>>();
        public List<KeyValuePair<string, string>> UserText = new List<KeyValuePair<string, string>>();
    }

    public enum GeoRefKind { None, MapConversion, SiteLatLon }

    public class IfcModelData
    {
        public string ProjectName = "Rhino model";
        public string SiteName = "Site";
        public Guid ProjectId = Guid.NewGuid();
        public List<IfcElementData> Elements = new List<IfcElementData>();

        public GeoRefKind GeoRef = GeoRefKind.None;
        // MapConversion: map coordinates of the model origin + CRS
        public string CrsName = "";          // "EPSG:25832"
        public string VerticalDatum = "";    // e.g. "DHHN2016"
        public double Eastings, Northings, OrthogonalHeight;
        public double XAxisAbscissa = 1, XAxisOrdinate = 0;
        // SiteLatLon: WGS84 position of the model origin
        public double Latitude, Longitude, Elevation;
        // Direction of true north in model x/y (unit vector), for SiteLatLon
        public double TrueNorthX = 0, TrueNorthY = 1;

        public string Application = "geoBIM.app Rhino plugin";
        public string ApplicationVersion = "0.1.0";
    }

    // ------------------------------------------------------------------
    // IFC 4 (ADD2 TC1) STEP writer
    // ------------------------------------------------------------------

    public static class IfcWriter
    {
        /// <summary>
        /// Classes the plugin writes; all share the IFC 4 attribute list of
        /// IfcBuildingElementProxy (… Tag, PredefinedType) so one entity
        /// template fits, PredefinedType = NOTDEFINED.
        /// </summary>
        public static readonly string[] SupportedClasses =
        {
            "IfcBuildingElementProxy", "IfcWall", "IfcSlab", "IfcBeam", "IfcColumn", "IfcRoof",
            "IfcFooting", "IfcMember", "IfcPlate", "IfcRailing", "IfcRamp", "IfcStair",
            "IfcCovering", "IfcCurtainWall", "IfcChimney", "IfcShadingDevice", "IfcFurniture",
            "IfcGeographicElement"
        };

        /// <summary>"wall", "IfcWall", "IFCWALL" → "IfcWall"; null when unknown</summary>
        public static string NormalizeClass(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            if (!t.StartsWith("ifc", StringComparison.OrdinalIgnoreCase)) t = "Ifc" + t;
            return SupportedClasses.FirstOrDefault(c => string.Equals(c, t, StringComparison.OrdinalIgnoreCase));
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>STEP real: always with a decimal point</summary>
        public static string Real(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            var s = v.ToString("0.0#######", Inv);
            return s == "-0.0" ? "0.0" : s;
        }

        /// <summary>STEP string with IFC encoding: '' for quotes, \\ for backslash, \X2\…\X0\ for non-ASCII</summary>
        public static string Str(string s)
        {
            if (s == null) return "$";
            var sb = new StringBuilder("'");
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\'') { sb.Append("''"); i++; }
                else if (c == '\\') { sb.Append("\\\\"); i++; }
                else if (c >= 32 && c < 127) { sb.Append(c); i++; }
                else if (c < 32) { sb.Append(' '); i++; }
                else
                {
                    sb.Append("\\X2\\");
                    while (i < s.Length && s[i] >= 127)
                    {
                        sb.Append(((int)s[i]).ToString("X4"));
                        i++;
                    }
                    sb.Append("\\X0\\");
                }
            }
            return sb.Append('\'').ToString();
        }

        const string B64 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

        /// <summary>IFC compressed GlobalId (22 characters) of a Guid</summary>
        public static string CompressGuid(Guid g)
        {
            // Bytes in the order of the canonical hex string
            var hex = g.ToString("N");
            var bytes = new byte[16];
            for (int i = 0; i < 16; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            var sb = new StringBuilder(22);
            // first char: top 2 bits of byte 0
            sb.Append(B64[bytes[0] >> 6]);
            // the remaining 126 bits in 21 six-bit characters
            int bitPos = 2;
            for (int k = 0; k < 21; k++)
            {
                int v = 0;
                for (int b = 0; b < 6; b++, bitPos++)
                {
                    int bit = (bytes[bitPos >> 3] >> (7 - (bitPos & 7))) & 1;
                    v = (v << 1) | bit;
                }
                sb.Append(B64[v]);
            }
            return sb.ToString();
        }

        /// <summary>Stable Guid from a string (MD5, version bits ignored — only uniqueness matters)</summary>
        public static Guid DerivedGuid(string seed)
        {
            using (var md5 = MD5.Create())
            {
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(seed)));
            }
        }

        class Step
        {
            readonly StringBuilder _data = new StringBuilder();
            int _next = 1;
            public int Add(string entity)
            {
                int id = _next++;
                _data.Append('#').Append(id).Append('=').Append(entity).Append(";\n");
                return id;
            }
            public string Data => _data.ToString();
        }

        static string Ref(int id) => "#" + id;
        static string Refs(IEnumerable<int> ids) => "(" + string.Join(",", ids.Select(Ref)) + ")";

        // Compound plane angle (deg, min, sec, millionths of a second)
        static string Angle(double deg)
        {
            int sign = deg < 0 ? -1 : 1;
            double a = Math.Abs(deg);
            int d = (int)Math.Floor(a);
            double mf = (a - d) * 60;
            int m = (int)Math.Floor(mf);
            double sf = (mf - m) * 60;
            int s = (int)Math.Floor(sf);
            int micro = (int)Math.Round((sf - s) * 1e6);
            if (micro >= 1000000) { micro -= 1000000; s++; }
            if (s >= 60) { s -= 60; m++; }
            if (m >= 60) { m -= 60; d++; }
            return string.Format(Inv, "({0},{1},{2},{3})", sign * d, sign * m, sign * s, sign * micro);
        }

        public static void Write(IfcModelData model, string path)
        {
            File.WriteAllText(path, Build(model, Path.GetFileName(path)), new UTF8Encoding(false));
        }

        public static string Build(IfcModelData model, string fileName)
        {
            var st = new Step();
            string P(Guid g) => Str(CompressGuid(g));

            int person = st.Add("IFCPERSON($,$,'geoBIM.app',$,$,$,$,$)");
            int org = st.Add("IFCORGANIZATION($,'geoBIM.app',$,$,$)");
            int po = st.Add($"IFCPERSONANDORGANIZATION({Ref(person)},{Ref(org)},$)");
            int app = st.Add($"IFCAPPLICATION({Ref(org)},{Str(model.ApplicationVersion)},{Str(model.Application)},'geobim-rhino')");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int owner = st.Add($"IFCOWNERHISTORY({Ref(po)},{Ref(app)},$,.ADDED.,$,$,$,{now})");

            int lenUnit = st.Add("IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)");
            int areaUnit = st.Add("IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.)");
            int volUnit = st.Add("IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.)");
            int angUnit = st.Add("IFCSIUNIT(*,.PLANEANGLEUNIT.,$,.RADIAN.)");
            int units = st.Add($"IFCUNITASSIGNMENT({Refs(new[] { lenUnit, areaUnit, volUnit, angUnit })})");

            int origin = st.Add("IFCCARTESIANPOINT((0.,0.,0.))");
            int wcs = st.Add($"IFCAXIS2PLACEMENT3D({Ref(origin)},$,$)");
            int north = st.Add($"IFCDIRECTION(({Real(model.TrueNorthX)},{Real(model.TrueNorthY)}))");
            int ctx = st.Add($"IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,{Ref(wcs)},{Ref(north)})");
            int body = st.Add($"IFCGEOMETRICREPRESENTATIONSUBCONTEXT('Body','Model',*,*,*,*,{Ref(ctx)},$,.MODEL_VIEW.,$)");

            int project = st.Add($"IFCPROJECT({P(model.ProjectId)},{Ref(owner)},{Str(model.ProjectName)},$,$,$,$,({Ref(ctx)}),{Ref(units)})");

            if (model.GeoRef == GeoRefKind.MapConversion)
            {
                string vdatum = string.IsNullOrEmpty(model.VerticalDatum) ? "$" : Str(model.VerticalDatum);
                int crs = st.Add($"IFCPROJECTEDCRS({Str(model.CrsName)},$,$,{vdatum},$,$,{Ref(lenUnit)})");
                st.Add($"IFCMAPCONVERSION({Ref(ctx)},{Ref(crs)},{Real(model.Eastings)},{Real(model.Northings)},{Real(model.OrthogonalHeight)},{Real(model.XAxisAbscissa)},{Real(model.XAxisOrdinate)},1.)");
            }

            int sitePlacement = st.Add($"IFCLOCALPLACEMENT($,{Ref(wcs)})");
            string lat = "$", lon = "$", elev = "$";
            if (model.GeoRef == GeoRefKind.SiteLatLon)
            {
                lat = Angle(model.Latitude);
                lon = Angle(model.Longitude);
                elev = Real(model.Elevation);
            }
            Guid siteId = DerivedGuid(model.ProjectId + "/site");
            int site = st.Add($"IFCSITE({P(siteId)},{Ref(owner)},{Str(model.SiteName)},$,$,{Ref(sitePlacement)},$,$,.ELEMENT.,{lat},{lon},{elev},$,$)");
            st.Add($"IFCRELAGGREGATES({P(DerivedGuid(model.ProjectId + "/agg"))},{Ref(owner)},$,$,{Ref(project)},({Ref(site)}))");

            var elementIds = new List<int>();
            var layerReps = new Dictionary<string, List<int>>();
            var styleCache = new Dictionary<string, int>();

            foreach (var e in model.Elements)
            {
                if (e.Mesh.VertexCount < 3 || e.Mesh.Triangles.Count < 3) continue;

                var pts = new StringBuilder("IFCCARTESIANPOINTLIST3D((");
                for (int i = 0; i < e.Mesh.Coordinates.Count; i += 3)
                {
                    if (i > 0) pts.Append(',');
                    pts.Append('(').Append(Real(e.Mesh.Coordinates[i])).Append(',')
                       .Append(Real(e.Mesh.Coordinates[i + 1])).Append(',')
                       .Append(Real(e.Mesh.Coordinates[i + 2])).Append(')');
                }
                pts.Append("))");
                int pointList = st.Add(pts.ToString());

                var idx = new StringBuilder("(");
                for (int t = 0; t < e.Mesh.Triangles.Count; t += 3)
                {
                    if (t > 0) idx.Append(',');
                    idx.Append('(').Append(e.Mesh.Triangles[t] + 1).Append(',')
                       .Append(e.Mesh.Triangles[t + 1] + 1).Append(',')
                       .Append(e.Mesh.Triangles[t + 2] + 1).Append(')');
                }
                idx.Append(')');
                int faceSet = st.Add($"IFCTRIANGULATEDFACESET({Ref(pointList)},$,$,{idx},$)");

                // Colour (one surface style per distinct colour)
                string styleKey = string.Format(Inv, "{0:0.###}/{1:0.###}/{2:0.###}/{3:0.###}", e.R, e.G, e.B, e.Transparency);
                if (!styleCache.TryGetValue(styleKey, out int style))
                {
                    int colour = st.Add($"IFCCOLOURRGB($,{Real(e.R)},{Real(e.G)},{Real(e.B)})");
                    int shading = st.Add($"IFCSURFACESTYLESHADING({Ref(colour)},{Real(e.Transparency)})");
                    style = st.Add($"IFCSURFACESTYLE($,.BOTH.,({Ref(shading)}))");
                    styleCache[styleKey] = style;
                }
                st.Add($"IFCSTYLEDITEM({Ref(faceSet)},({Ref(style)}),$)");

                int rep = st.Add($"IFCSHAPEREPRESENTATION({Ref(body)},'Body','Tessellation',({Ref(faceSet)}))");
                int shape = st.Add($"IFCPRODUCTDEFINITIONSHAPE($,$,({Ref(rep)}))");
                int placement = st.Add($"IFCLOCALPLACEMENT({Ref(sitePlacement)},{Ref(wcs)})");

                string cls = NormalizeClass(e.IfcClass) ?? "IfcBuildingElementProxy";
                int el = st.Add($"{cls.ToUpperInvariant()}({P(e.Id)},{Ref(owner)},{Str(e.Name)},$,{Str(e.ObjectType)},{Ref(placement)},{Ref(shape)},$,.NOTDEFINED.)");
                elementIds.Add(el);

                if (!string.IsNullOrEmpty(e.Layer))
                {
                    if (!layerReps.TryGetValue(e.Layer, out var reps)) layerReps[e.Layer] = reps = new List<int>();
                    reps.Add(rep);
                }

                AddPset(st, owner, el, e.Id, "Pset_Rhino", e.RhinoProps);
                AddPset(st, owner, el, e.Id, "Pset_RhinoUserText", e.UserText);
            }

            if (elementIds.Count > 0)
            {
                st.Add($"IFCRELCONTAINEDINSPATIALSTRUCTURE({P(DerivedGuid(model.ProjectId + "/contained"))},{Ref(owner)},$,$,{Refs(elementIds)},{Ref(site)})");
            }
            foreach (var kv in layerReps)
            {
                st.Add($"IFCPRESENTATIONLAYERASSIGNMENT({Str(kv.Key)},$,{Refs(kv.Value)},$)");
            }

            var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", Inv);
            var sb = new StringBuilder();
            sb.Append("ISO-10303-21;\n");
            sb.Append("HEADER;\n");
            sb.Append("FILE_DESCRIPTION(('ViewDefinition [ReferenceView_V1.2]'),'2;1');\n");
            sb.Append($"FILE_NAME({Str(fileName)},'{stamp}',(''),('geoBIM.app'),{Str(model.Application + " " + model.ApplicationVersion)},'Rhino 8','');\n");
            sb.Append("FILE_SCHEMA(('IFC4'));\n");
            sb.Append("ENDSEC;\n");
            sb.Append("DATA;\n");
            sb.Append(st.Data);
            sb.Append("ENDSEC;\n");
            sb.Append("END-ISO-10303-21;\n");
            return sb.ToString();
        }

        static void AddPset(Step st, int owner, int element, Guid elementId, string name, List<KeyValuePair<string, string>> props)
        {
            if (props == null || props.Count == 0) return;
            var ids = new List<int>();
            foreach (var p in props)
            {
                if (string.IsNullOrWhiteSpace(p.Key)) continue;
                ids.Add(st.Add($"IFCPROPERTYSINGLEVALUE({Str(p.Key)},$,IFCLABEL({Str(p.Value ?? "")}),$)"));
            }
            if (ids.Count == 0) return;
            string g(string s) => Str(CompressGuid(DerivedGuid(elementId + "/" + name + s)));
            int pset = st.Add($"IFCPROPERTYSET({g("")},{Ref(owner)},{Str(name)},$,{Refs(ids)})");
            st.Add($"IFCRELDEFINESBYPROPERTIES({g("/rel")},{Ref(owner)},$,$,({Ref(element)}),{Ref(pset)})");
        }
    }
}
