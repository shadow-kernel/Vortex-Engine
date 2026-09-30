using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;
using Matrix4x4 = System.Numerics.Matrix4x4;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell.Animation
{
    /// <summary>Helpers shared by the Keyframe Editor, the Socket Editor and the animation inspector cards.</summary>
    public static class AnimUtil
    {
        /// <summary>Importer pivot pseudo-nodes (Assimp FBX `$AssimpFbx$` chains) — hidden in bone lists and overlays;
        /// selection and keys always use the REAL node names.</summary>
        public static bool IsHiddenNode(string name)
            => name != null && name.IndexOf("$AssimpFbx$", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Display-only bone name: strip `_$AssimpFbx$_Xxx` suffixes and namespace prefixes
        /// ("mixamorig:LeftHand" -> "LeftHand"). The full name lives in tooltips, tracks and components.</summary>
        public static string DisplayBoneName(string bone)
        {
            string n = bone ?? "";
            int fx = n.IndexOf("_$AssimpFbx$", StringComparison.OrdinalIgnoreCase);
            if (fx > 0) n = n.Substring(0, fx);
            int c = n.LastIndexOf(':');
            if (c >= 0 && c + 1 < n.Length) n = n.Substring(c + 1);
            return n;
        }

        /// <summary>Project-relative (or absolute) path -> absolute path.</summary>
        public static string ToAbsolute(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                string p = path.Replace('\\', '/');
                if (Path.IsPathRooted(p)) return p;
                var root = ProjectData.Current?.Path;
                return string.IsNullOrEmpty(root) ? p : Path.Combine(root, p);
            }
            catch { return path; }   // illegal characters in a hand-edited path
        }

        /// <summary>Absolute path inside the project -> project-relative with forward slashes.</summary>
        public static string ToRelative(string path) => Panels.Inspector.PropertyRows.ToProjectRelative(path);

        /// <summary>A model file path (strips a '#submeshN' selector, resolves project-relative). Null for primitives.</summary>
        public static string ModelFile(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath) || meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return null;
            string p = meshPath;
            int h = p.LastIndexOf('#');
            if (h > 0 && p.IndexOf("#submesh", h, StringComparison.OrdinalIgnoreCase) == h) p = p.Substring(0, h);
            return ToAbsolute(p);
        }

        /// <summary>The first real model (glb/gltf/fbx…) rendered in an entity's subtree — the character a socket targets.</summary>
        public static string FindModelMeshInSubtree(GameEntity e)
        {
            if (e == null) return null;
            var mr = e.GetComponent<MeshRenderer>();
            if (mr != null && !string.IsNullOrEmpty(mr.MeshPath) && !mr.MeshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                var mp = mr.MeshPath.ToLowerInvariant();
                if (mp.Contains(".glb") || mp.Contains(".gltf") || mp.Contains(".fbx") || mp.Contains(".dae") || mp.Contains(".obj")) return mr.MeshPath;
            }
            if (e.Children != null)
                foreach (var c in e.Children) { var m = FindModelMeshInSubtree(c); if (m != null) return m; }
            return null;
        }

        /// <summary>The entity's own MeshRenderer path, or the first descendant's (multi-submesh models import as a
        /// parent container whose '#submeshN' children carry the paths).</summary>
        public static string FindMeshPath(GameEntity entity)
        {
            if (entity == null) return null;
            var mr = entity.GetComponent<MeshRenderer>();
            if (mr != null && !string.IsNullOrEmpty(mr.MeshPath)) return mr.MeshPath;
            if (entity.Children != null)
                foreach (var c in entity.Children) { var p = FindMeshPath(c); if (p != null) return p; }
            return null;
        }

        /// <summary>Every entity of the scene (depth first).</summary>
        public static IEnumerable<GameEntity> AllEntities(Scene scene)
        {
            if (scene?.Entities == null) yield break;
            var stack = new Stack<GameEntity>(scene.Entities.Reverse());
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (e == null) continue;
                yield return e;
                if (e.Children != null) for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
            }
        }

        // Euler order matches Quaternion.CreateFromYawPitchRoll: yaw = Y, pitch = X, roll = Z (the Keyframe Editor's fields).
        public static Vector3 ToEulerDeg(Quaternion q)
        {
            var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
            float sp = Math.Max(-1f, Math.Min(1f, -m.M32));
            float pitch = (float)Math.Asin(sp);
            float yaw, roll;
            if (Math.Abs(sp) > 0.9999f) { yaw = (float)Math.Atan2(-m.M13, m.M11); roll = 0f; }   // gimbal: roll folds into yaw
            else { yaw = (float)Math.Atan2(m.M31, m.M33); roll = (float)Math.Atan2(m.M12, m.M22); }
            const float toDeg = 180f / (float)Math.PI;
            return new Vector3(pitch * toDeg, yaw * toDeg, roll * toDeg);
        }

        public static Quaternion FromEulerDeg(Vector3 e)
        {
            const float toRad = (float)Math.PI / 180f;
            return Quaternion.CreateFromYawPitchRoll(e.Y * toRad, e.X * toRad, e.Z * toRad);
        }

        // ---------------------------------------------------------------- native file dialogs

        public static async Task<string> OpenFile(Window owner, string title, string startDir, string typeName, params string[] patterns)
        {
            if (owner?.StorageProvider == null) return null;
            try
            {
                var opts = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
                if (patterns != null && patterns.Length > 0) opts.FileTypeFilter = new[] { new FilePickerFileType(typeName) { Patterns = patterns }, FilePickerFileTypes.All };
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir)) opts.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startDir);
                var res = await owner.StorageProvider.OpenFilePickerAsync(opts);
                return res != null && res.Count > 0 ? res[0].TryGetLocalPath() : null;
            }
            catch { return null; }
        }

        public static async Task<string> SaveFile(Window owner, string title, string startDir, string suggestedName, string typeName, string extension)
        {
            if (owner?.StorageProvider == null) return null;
            try
            {
                var opts = new FilePickerSaveOptions
                {
                    Title = title, SuggestedFileName = suggestedName, DefaultExtension = extension?.TrimStart('.'), ShowOverwritePrompt = true,
                    FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { "*" + extension } } }
                };
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir)) opts.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startDir);
                var f = await owner.StorageProvider.SaveFilePickerAsync(opts);
                return f?.TryGetLocalPath();
            }
            catch { return null; }
        }

        /// <summary>Project sub-folder if it exists, else the project root.</summary>
        public static string ProjectDir(params string[] parts)
        {
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return null;
            try { var d = Path.Combine(new[] { root }.Concat(parts).ToArray()); if (Directory.Exists(d)) return d; } catch { }
            return root;
        }
    }
}
