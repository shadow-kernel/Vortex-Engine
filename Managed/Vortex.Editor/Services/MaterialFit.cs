using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Editor.Core.Assets;
using Editor.Core.Assets.Store;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Services
{
    /// <summary>
    /// Assigning a material that knows its real-world size (store materials: one Poly Haven tile covers 15 m, an
    /// ambientCG tile 2 m …) to a cube or plane primitive: instead of stretching one tile over the whole object, the
    /// object gets a copy of the material tiled for its size — "&lt;name&gt;_24x92m.vmat" next to the original. The
    /// original stays as it is (other objects keep using it); assigning again to an object of the same size reuses the
    /// copy. Imported models keep their authored UVs and get the material unchanged.
    /// </summary>
    public static class MaterialFit
    {
        private static readonly Regex SizeSuffix = new Regex(@"_[0-9.]+x[0-9.]+m$", RegexOptions.CultureInvariant);

        /// <summary>The project-relative .vmat to assign to <paramref name="renderer"/>: <paramref name="vmatRel"/>
        /// itself, or a copy tiled for the object's size. <paramref name="note"/> says what happened (for a toast).</summary>
        public static string ForRenderer(GameEntity entity, MeshRenderer renderer, string vmatRel, out string note)
        {
            note = null;
            try
            {
                string root = ProjectData.Current?.Path;
                if (string.IsNullOrEmpty(root) || entity == null || renderer == null || string.IsNullOrEmpty(vmatRel)) return vmatRel;
                string full = Path.IsPathRooted(vmatRel) ? vmatRel : Path.Combine(root, vmatRel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) return vmatRel;
                var vm = VortexMaterial.Load(full);
                if (vm?.RealWorldSize == null) return vmatRel;
                WorldScale(entity, out float sx, out float sy, out float sz);
                var tiling = MaterialBuilder.FitTiling(renderer.MeshPath, sx, sy, sz, vm.RealWorldSize, out float eu, out float ev);
                if (tiling == null) return vmatRel;
                var cur = vm.UVTiling;
                if (cur != null && cur.Length >= 2 && Math.Abs(cur[0] - tiling[0]) < 0.01f && Math.Abs(cur[1] - tiling[1]) < 0.01f) return vmatRel;

                // a copy of a copy is named after the original
                string baseName = SizeSuffix.Replace(Path.GetFileNameWithoutExtension(full), "");
                string size = Fmt(eu) + "x" + Fmt(ev) + "m";
                string variant = Path.Combine(Path.GetDirectoryName(full), baseName + "_" + size + ".vmat");
                vm.UVTiling = tiling;
                vm.Name = SizeSuffix.Replace(vm.Name ?? baseName, "") + " " + Fmt(eu) + "×" + Fmt(ev) + " m";
                if (!vm.Save(variant)) return vmatRel;
                note = Path.GetFileNameWithoutExtension(variant) + " — tiled " + Fmt(tiling[0]) + " × " + Fmt(tiling[1]) +
                       " for " + Fmt(eu) + " × " + Fmt(ev) + " m (one tile = " + Fmt(vm.RealWorldSize[0]) + " m)";
                return Path.GetRelativePath(root, variant).Replace('\\', '/');
            }
            catch { return vmatRel; }
        }

        /// <summary>The entity's scale in the world (local scales multiplied up the parent chain; rotations ignored —
        /// floors and walls are axis-aligned).</summary>
        public static void WorldScale(GameEntity e, out float sx, out float sy, out float sz)
        {
            sx = sy = sz = 1f;
            for (var cur = e; cur != null; cur = cur.Parent)
            {
                var t = cur.Transform;
                if (t == null) continue;
                sx *= t.LocalScale.X; sy *= t.LocalScale.Y; sz *= t.LocalScale.Z;
            }
        }

        private static string Fmt(float v) => v.ToString(v >= 10f ? "0.#" : "0.##", CultureInfo.InvariantCulture);
    }
}
