using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Editor.Core.Services.Build;

namespace VortexTests
{
    /// <summary>glTF files ship self-contained in the asset pak (#370): external .bin buffers are embedded as data
    /// URIs at export time, because the shipped game imports from memory where Assimp cannot open a sibling file.</summary>
    public static class GltfEmbedTests
    {
        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

        [Test]
        public static void ExternalBuffersBecomeDataUris(TestContext t)
        {
            var gltf = Utf8("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"scene.bin\",\"byteLength\":4},{\"uri\":\"data:application/octet-stream;base64,AA==\",\"byteLength\":1}],\"meshes\":[]}");
            var names = new List<string>();
            var outBytes = GltfEmbed.EmbedExternalBuffers(gltf, n => n == "scene.bin" ? new byte[] { 0, 1, 2, 3 } : null, names);
            t.NotNull(outBytes, "something changed");
            string json = Encoding.UTF8.GetString(outBytes);
            t.True(json.Contains("\"uri\":\"data:application/octet-stream;base64,AAECAw==\""), "the external buffer is a data URI now: " + json);
            t.True(json.Contains("\"byteLength\":4"), "the byte length is kept");
            t.True(json.Contains("\"asset\"") && json.Contains("\"meshes\""), "the rest of the document survives");
            t.True(json.Contains("base64,AA=="), "an already embedded buffer is left alone");
            t.Equal(1, names.Count, "one sibling consumed");
            t.Equal("scene.bin", names[0], "...by name");
        }

        [Test]
        public static void NothingToDoLeavesTheFileAlone(TestContext t)
        {
            var embeddedOnly = Utf8("{\"buffers\":[{\"uri\":\"data:application/octet-stream;base64,AA==\",\"byteLength\":1}]}");
            t.True(GltfEmbed.EmbedExternalBuffers(embeddedOnly, n => new byte[1], null) == null, "already self-contained");
            var missing = Utf8("{\"buffers\":[{\"uri\":\"a.bin\",\"byteLength\":1},{\"uri\":\"gone.bin\",\"byteLength\":1}]}");
            var names = new List<string>();
            t.True(GltfEmbed.EmbedExternalBuffers(missing, n => n == "a.bin" ? new byte[1] : null, names) == null, "an unreadable sibling: shipped as it is");
            t.Equal(0, names.Count, "...and nothing is reported consumed");
            t.True(GltfEmbed.EmbedExternalBuffers(Utf8("not json"), n => new byte[1], null) == null, "garbage in, nothing out");
            t.True(GltfEmbed.EmbedExternalBuffers(Utf8("{\"meshes\":[]}"), n => new byte[1], null) == null, "no buffers at all");
            t.True(GltfEmbed.EmbedExternalBuffers(Utf8("{\"buffers\":[{\"uri\":\"https://x.test/a.bin\"}]}"), n => new byte[1], null) == null, "a remote buffer stays remote");
        }

        [Test]
        public static void UrlEncodedNamesResolve(TestContext t)
        {
            var gltf = Utf8("{\"buffers\":[{\"uri\":\"my%20model.bin\",\"byteLength\":2}]}");
            string asked = null;
            var outBytes = GltfEmbed.EmbedExternalBuffers(gltf, n => { asked = n; return new byte[] { 7, 7 }; }, null);
            t.NotNull(outBytes, "embedded");
            t.Equal("my model.bin", asked, "the sibling is looked up by its decoded name");
            t.True(Encoding.UTF8.GetString(outBytes).Contains("base64,Bwc="), "with its bytes");
        }

        [Test]
        public static void EmbedAllPacksAFolder(TestContext t)
        {
            string dir = t.Path("gltf");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "crate.gltf"), "{\"buffers\":[{\"uri\":\"crate.bin\",\"byteLength\":3}]}");
            File.WriteAllBytes(Path.Combine(dir, "crate.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(dir, "crate.png"), new byte[] { 9 });
            File.WriteAllText(Path.Combine(dir, "box.glb"), "glb");
            var files = Directory.GetFiles(dir);
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var embedded = GltfEmbed.EmbedAll(files, consumed);
            t.Equal(1, embedded.Count, "one glTF changed");
            t.True(embedded.ContainsKey(Path.Combine(dir, "crate.gltf")), "keyed by its path");
            t.Equal(1, consumed.Count, "one .bin consumed");
            t.True(consumed.Contains(Path.GetFullPath(Path.Combine(dir, "crate.bin"))), "the crate's buffer");
            t.True(Encoding.UTF8.GetString(embedded[Path.Combine(dir, "crate.gltf")]).Contains("base64,AQID"), "with its bytes");
        }
    }
}
