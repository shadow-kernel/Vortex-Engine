using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Editor.Core.Services.Build
{
    /// <summary>
    /// Makes a project's .gltf files self-contained for the asset pak (#370). A shipped game imports its models from
    /// memory, and Assimp cannot open the sibling .bin a .gltf points its buffers at — the import came up empty and
    /// the entity was invisible (the Range's weapons, arms and props). At export time every external buffer is
    /// embedded as a base64 data: URI and the consumed .bin stays out of the pak. .glb / .fbx / .obj are untouched.
    /// </summary>
    public static class GltfEmbed
    {
        public const string DataUriPrefix = "data:application/octet-stream;base64,";

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // keep '+' and '/' of the base64 readable
        };

        public static bool IsGltf(string path) =>
            path != null && path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The glTF with its external buffers embedded, or null when the file ships as it is: no external buffer, a
        /// sibling that cannot be read (then nothing is embedded at all), or no glTF JSON. <paramref name="embedded"/>
        /// receives the sibling names (URL-decoded, as the file wrote them) that were embedded.
        /// </summary>
        public static byte[] EmbedExternalBuffers(byte[] gltf, Func<string, byte[]> readSibling, List<string> embedded)
        {
            if (gltf == null || gltf.Length == 0 || readSibling == null) return null;
            JsonNode root;
            try { using (var ms = new MemoryStream(gltf)) root = JsonNode.Parse(ms); }
            catch { return null; }
            if (!(root is JsonObject obj) || !(obj["buffers"] is JsonArray buffers)) return null;

            var names = new List<string>();
            foreach (var entry in buffers)
            {
                if (!(entry is JsonObject buffer)) continue;
                string uri = null;
                try { uri = buffer["uri"]?.GetValue<string>(); } catch { }
                if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                if (uri.IndexOf("://", StringComparison.Ordinal) >= 0) continue;   // a remote buffer stays remote

                string name = Uri.UnescapeDataString(uri);
                byte[] bytes = null;
                try { bytes = readSibling(name); } catch { }
                if (bytes == null) return null;   // an unreadable sibling: ship the file unchanged

                buffer["uri"] = DataUriPrefix + Convert.ToBase64String(bytes);
                buffer["byteLength"] = bytes.Length;
                names.Add(name);
            }
            if (names.Count == 0) return null;
            embedded?.AddRange(names);
            return Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions));
        }

        /// <summary>
        /// Embed the .gltf files among <paramref name="files"/> (absolute paths): the new bytes per glTF that changed,
        /// keyed by its path; the consumed sibling .bin files are added to <paramref name="consumed"/> as full paths.
        /// </summary>
        public static Dictionary<string, byte[]> EmbedAll(IEnumerable<string> files, HashSet<string> consumed)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (files == null) return result;
            foreach (var f in files)
            {
                if (!IsGltf(f)) continue;
                string dir = Path.GetDirectoryName(f) ?? "";
                byte[] bytes;
                try { bytes = File.ReadAllBytes(f); } catch { continue; }

                var names = new List<string>();
                var embedded = EmbedExternalBuffers(bytes, name =>
                {
                    string p = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(p) ? File.ReadAllBytes(p) : null;
                }, names);
                if (embedded == null) continue;

                result[f] = embedded;
                if (consumed != null)
                    foreach (var n in names)
                        consumed.Add(Path.GetFullPath(Path.Combine(dir, n.Replace('/', Path.DirectorySeparatorChar))));
            }
            return result;
        }
    }
}
