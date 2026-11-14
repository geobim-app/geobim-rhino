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
using System.Text;
using System.Text.Json;

namespace GeoBimRhino
{
    /// <summary>
    /// IfcModelData → binary glTF 2.0 (.glb) for geoBIM.app, which loads it as is
    /// with Cesium (no conversion, materials kept). One node + mesh per element,
    /// vertices in metres relative to the model origin. glTF is Y-up: model
    /// (x east, y north, z up) → glTF (x, z, -y), the frame Cesium maps to east,
    /// up and north at the placement point.
    ///
    /// Material per element: the Rhino PBR material when there is one (base
    /// colour, metallic, roughness, opacity — e.g. polished steel), else the
    /// display colour as a matt, non-metallic material.
    /// </summary>
    public static class GlbWriter
    {
        public static void Write(IfcModelData model, string path)
        {
            var bin = new MemoryStream();
            var bufferViews = new List<object>();
            var accessors = new List<object>();
            var meshes = new List<object>();
            var nodes = new List<object>();
            var materials = new List<object>();
            var materialIndex = new Dictionary<string, int>();

            foreach (var e in model.Elements)
            {
                var m = e.Mesh;
                int nv = m.VertexCount;
                if (nv == 0 || m.Triangles.Count == 0) continue;

                // Positions (x, z, -y) as float32, with bounds
                var pos = new float[nv * 3];
                float[] min = { float.MaxValue, float.MaxValue, float.MaxValue };
                float[] max = { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < nv; i++)
                {
                    pos[3 * i] = (float)m.Coordinates[3 * i];
                    pos[3 * i + 1] = (float)m.Coordinates[3 * i + 2];
                    pos[3 * i + 2] = (float)-m.Coordinates[3 * i + 1];
                    for (int k = 0; k < 3; k++)
                    {
                        min[k] = Math.Min(min[k], pos[3 * i + k]);
                        max[k] = Math.Max(max[k], pos[3 * i + k]);
                    }
                }
                int posAcc = AddAccessor(bin, bufferViews, accessors, Bytes(pos), 34962, 5126, nv, "VEC3", min, max);

                var attributes = new Dictionary<string, int> { ["POSITION"] = posAcc };
                if (m.Normals.Count == nv * 3)
                {
                    var nrm = new float[nv * 3];
                    for (int i = 0; i < nv; i++)
                    {
                        nrm[3 * i] = m.Normals[3 * i];
                        nrm[3 * i + 1] = m.Normals[3 * i + 2];
                        nrm[3 * i + 2] = -m.Normals[3 * i + 1];
                    }
                    attributes["NORMAL"] = AddAccessor(bin, bufferViews, accessors, Bytes(nrm), 34962, 5126, nv, "VEC3", null, null);
                }

                var idx = m.Triangles.Select(t => (uint)t).ToArray();
                int idxAcc = AddAccessor(bin, bufferViews, accessors, Bytes(idx), 34963, 5125, idx.Length, "SCALAR", null, null);

                int mat = Material(e, materials, materialIndex);
                meshes.Add(new Dictionary<string, object>
                {
                    ["name"] = e.Name,
                    ["primitives"] = new[] { new Dictionary<string, object> { ["attributes"] = attributes, ["indices"] = idxAcc, ["material"] = mat, ["mode"] = 4 } }
                });
                var extras = new Dictionary<string, string>();
                foreach (var kv in e.RhinoProps.Concat(e.UserText)) if (!extras.ContainsKey(kv.Key)) extras[kv.Key] = kv.Value;
                nodes.Add(new Dictionary<string, object> { ["name"] = e.Name, ["mesh"] = meshes.Count - 1, ["extras"] = extras });
            }
            if (nodes.Count == 0) throw new InvalidOperationException("no geometry to write");

            var gltf = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object>
                {
                    ["version"] = "2.0",
                    ["generator"] = model.Application + " " + model.ApplicationVersion,
                    ["extras"] = new Dictionary<string, object> { ["name"] = model.ProjectName }
                },
                ["scene"] = 0,
                ["scenes"] = new[] { new Dictionary<string, object> { ["name"] = model.ProjectName, ["nodes"] = Enumerable.Range(0, nodes.Count).ToArray() } },
                ["nodes"] = nodes,
                ["meshes"] = meshes,
                ["materials"] = materials,
                ["accessors"] = accessors,
                ["bufferViews"] = bufferViews,
                ["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = bin.Length } }
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(gltf);
            int jsonPad = (4 - json.Length % 4) % 4;
            int binPad = (int)((4 - bin.Length % 4) % 4);
            int jsonLen = json.Length + jsonPad, binLen = (int)bin.Length + binPad;
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(0x46546C67u);                    // "glTF"
                w.Write(2u);
                w.Write((uint)(12 + 8 + jsonLen + 8 + binLen));
                w.Write((uint)jsonLen);
                w.Write(0x4E4F534Au);                    // "JSON"
                w.Write(json);
                for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
                w.Write((uint)binLen);
                w.Write(0x004E4942u);                    // "BIN\0"
                bin.Position = 0;
                bin.CopyTo(fs);
                for (int i = 0; i < binPad; i++) w.Write((byte)0);
            }
        }

        static int Material(IfcElementData e, List<object> materials, Dictionary<string, int> index)
        {
            double r, g, b, a, metallic, roughness;
            string name;
            if (e.Pbr != null)
            {
                r = e.Pbr.R; g = e.Pbr.G; b = e.Pbr.B; a = Math.Min(e.Pbr.Alpha, 1 - e.Transparency);
                metallic = e.Pbr.Metallic; roughness = e.Pbr.Roughness; name = e.Pbr.Name;
            }
            else
            {
                // display colour is sRGB, glTF factors are linear
                r = SrgbToLinear(e.R); g = SrgbToLinear(e.G); b = SrgbToLinear(e.B); a = 1 - e.Transparency;
                metallic = 0; roughness = 0.8; name = "";
            }
            string key = string.Format(CultureInfo.InvariantCulture, "{0:0.####}/{1:0.####}/{2:0.####}/{3:0.###}/{4:0.###}/{5:0.###}/{6}", r, g, b, a, metallic, roughness, name);
            if (index.TryGetValue(key, out int i)) return i;
            var mat = new Dictionary<string, object>
            {
                ["pbrMetallicRoughness"] = new Dictionary<string, object>
                {
                    ["baseColorFactor"] = new[] { r, g, b, a },
                    ["metallicFactor"] = metallic,
                    ["roughnessFactor"] = roughness
                },
                ["doubleSided"] = true
            };
            if (!string.IsNullOrEmpty(name)) mat["name"] = name;
            if (a < 0.999) mat["alphaMode"] = "BLEND";
            materials.Add(mat);
            index[key] = materials.Count - 1;
            return materials.Count - 1;
        }

        static double SrgbToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        static int AddAccessor(MemoryStream bin, List<object> views, List<object> accessors, byte[] data,
                               int target, int componentType, int count, string type, float[] min, float[] max)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            long offset = bin.Length;
            bin.Write(data, 0, data.Length);
            views.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length, ["target"] = target });
            var acc = new Dictionary<string, object> { ["bufferView"] = views.Count - 1, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
            if (min != null) { acc["min"] = min; acc["max"] = max; }
            accessors.Add(acc);
            return accessors.Count - 1;
        }

        static byte[] Bytes(float[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
        static byte[] Bytes(uint[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
    }
}
