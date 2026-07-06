using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Editor.Core.Animation
{
    /// <summary>One hierarchy node (bind-pose local transform, pre-decomposed for keyframe fallbacks).</summary>
    public class SkeletonNode
    {
        public string Name = "";
        public int Parent = -1;
        public Matrix4x4 LocalBind = Matrix4x4.Identity;
        // Bind decomposition — tracks with missing key types fall back to these components.
        public Vector3 BindTranslation = Vector3.Zero;
        public Quaternion BindRotation = Quaternion.Identity;
        public Vector3 BindScale = Vector3.One;
    }

    /// <summary>One bone-palette entry (what skinned vertices reference via u8 indices).</summary>
    public class SkeletonBone
    {
        public int NodeIndex;
        public Matrix4x4 InverseBind = Matrix4x4.Identity;
    }

    /// <summary>
    /// A model's skeleton, fetched once from the native importer (VFS-aware for shipped games) and cached
    /// by AnimationService. All math is System.Numerics (row-vector convention — matches DirectXMath).
    /// </summary>
    public class SkeletonDef
    {
        public SkeletonNode[] Nodes = new SkeletonNode[0];
        public SkeletonBone[] Bones = new SkeletonBone[0];

        private float[] _bindPalette;
        private Matrix4x4[] _bindWorlds;
        private Dictionary<string, int> _nodeByName;

        public bool IsValid => Bones != null && Bones.Length > 0 && Nodes != null && Nodes.Length > 0;

        public int FindNode(string name)
        {
            if (_nodeByName == null)
            {
                _nodeByName = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < Nodes.Length; i++)
                    if (!_nodeByName.ContainsKey(Nodes[i].Name)) _nodeByName[Nodes[i].Name] = i;
            }
            return name != null && _nodeByName.TryGetValue(name, out int idx) ? idx : -1;
        }

        /// <summary>
        /// Load a model's skeleton via the native importer. absOrVfsPath is the model's full path in the
        /// editor, or its pak key in a shipped game (loaders check the VFS first, like every asset).
        /// Returns null when the model has no bones.
        /// </summary>
        public static SkeletonDef Load(string absOrVfsPath)
        {
            if (string.IsNullOrEmpty(absOrVfsPath)) return null;

            DllWrapper.VortexAPI.SkeletonNodeInfo[] nodes;
            DllWrapper.VortexAPI.SkeletonBoneInfo[] bones;

            if (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.TryGetBytes(absOrVfsPath, out var bytes))
            {
                string ext = Path.GetExtension(absOrVfsPath).TrimStart('.');
                nodes = DllWrapper.VortexAPI.GetSkeletonNodesFromMemory(bytes, ext);
                bones = DllWrapper.VortexAPI.GetSkeletonBonesFromMemory(bytes, ext);
            }
            else
            {
                nodes = DllWrapper.VortexAPI.GetSkeletonNodes(absOrVfsPath);
                bones = DllWrapper.VortexAPI.GetSkeletonBones(absOrVfsPath);
            }

            if (bones == null || bones.Length == 0 || nodes == null || nodes.Length == 0) return null;

            var def = new SkeletonDef
            {
                Nodes = new SkeletonNode[nodes.Length],
                Bones = new SkeletonBone[bones.Length]
            };
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = new SkeletonNode
                {
                    Name = nodes[i].Name,
                    Parent = nodes[i].Parent,
                    LocalBind = ToMatrix(nodes[i].LocalBind)
                };
                if (Matrix4x4.Decompose(n.LocalBind, out var s, out var r, out var t))
                {
                    n.BindScale = s; n.BindRotation = r; n.BindTranslation = t;
                }
                else
                {
                    // Non-TRS bind matrix (shear/degenerate): keep at least the translation so an
                    // untracked node doesn't collapse to the origin while a sibling animates.
                    n.BindTranslation = n.LocalBind.Translation;
                }
                def.Nodes[i] = n;
            }
            for (int i = 0; i < bones.Length; i++)
            {
                def.Bones[i] = new SkeletonBone
                {
                    NodeIndex = bones[i].NodeIndex,
                    InverseBind = ToMatrix(bones[i].InverseBind)
                };
            }
            return def;
        }

        /// <summary>World transform per node at BIND pose (child-local composed up the hierarchy).</summary>
        public Matrix4x4[] BindNodeWorlds()
        {
            var worlds = new Matrix4x4[Nodes.Length];
            for (int i = 0; i < Nodes.Length; i++)
            {
                var n = Nodes[i];
                worlds[i] = (n.Parent >= 0 && n.Parent < i) ? n.LocalBind * worlds[n.Parent] : n.LocalBind;
            }
            return worlds;
        }

        /// <summary>Cached bind-pose node worlds (bone sockets query these when nothing is playing).
        /// Callers must NOT mutate the returned array.</summary>
        public Matrix4x4[] BindNodeWorldsCached() => _bindWorlds ?? (_bindWorlds = BindNodeWorlds());

        /// <summary>
        /// The bind-pose palette (inverseBind x bindWorld per bone, flattened row-major). Used when a
        /// skinned mesh renders without an active animation — the character shows its authored pose.
        /// </summary>
        public float[] BindPosePalette()
        {
            if (_bindPalette != null) return _bindPalette;
            var worlds = BindNodeWorlds();
            _bindPalette = FlattenPalette(worlds);
            return _bindPalette;
        }

        /// <summary>Palette from node worlds: palette[b] = inverseBind[b] * world[node[b]], flattened row-major.</summary>
        public float[] FlattenPalette(Matrix4x4[] nodeWorlds)
        {
            var palette = new float[Bones.Length * 16];
            for (int b = 0; b < Bones.Length; b++)
            {
                int node = Bones[b].NodeIndex;
                Matrix4x4 m = (node >= 0 && node < nodeWorlds.Length)
                    ? Bones[b].InverseBind * nodeWorlds[node]
                    : Matrix4x4.Identity;
                WriteMatrix(palette, b * 16, m);
            }
            return palette;
        }

        public static Matrix4x4 ToMatrix(float[] f)
        {
            if (f == null || f.Length < 16) return Matrix4x4.Identity;
            return new Matrix4x4(
                f[0], f[1], f[2], f[3],
                f[4], f[5], f[6], f[7],
                f[8], f[9], f[10], f[11],
                f[12], f[13], f[14], f[15]);
        }

        public static void WriteMatrix(float[] dst, int offset, Matrix4x4 m)
        {
            dst[offset + 0] = m.M11; dst[offset + 1] = m.M12; dst[offset + 2] = m.M13; dst[offset + 3] = m.M14;
            dst[offset + 4] = m.M21; dst[offset + 5] = m.M22; dst[offset + 6] = m.M23; dst[offset + 7] = m.M24;
            dst[offset + 8] = m.M31; dst[offset + 9] = m.M32; dst[offset + 10] = m.M33; dst[offset + 11] = m.M34;
            dst[offset + 12] = m.M41; dst[offset + 13] = m.M42; dst[offset + 14] = m.M43; dst[offset + 15] = m.M44;
        }
    }
}
