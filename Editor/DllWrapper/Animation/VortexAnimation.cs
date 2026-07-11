using System;
using System.Runtime.InteropServices;

namespace Editor.DllWrapper
{
    /// <summary>
    /// VortexAPI - Skeletal animation interop (skeleton/clip queries + skinned submission).
    /// Mirrors VortexAPI/Api/AnimationApi.cpp. See ANIMATION_SYSTEM_DESIGN.md.
    /// </summary>
    public static partial class VortexAPI
    {
        #region Skeleton queries

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelSkeletonNodes(
            [MarshalAs(UnmanagedType.LPStr)] string filepath,
            [In, Out] int[] outParents, [In, Out] float[] outLocalBind,
            IntPtr outNames, int maxNodes, int maxNameLen);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelSkeletonNodesFromMemory(
            byte[] data, int length, [MarshalAs(UnmanagedType.LPStr)] string extHint,
            [In, Out] int[] outParents, [In, Out] float[] outLocalBind,
            IntPtr outNames, int maxNodes, int maxNameLen);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelSkeletonBones(
            [MarshalAs(UnmanagedType.LPStr)] string filepath,
            [In, Out] int[] outNodeIndices, [In, Out] float[] outInverseBind, int maxBones);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelSkeletonBonesFromMemory(
            byte[] data, int length, [MarshalAs(UnmanagedType.LPStr)] string extHint,
            [In, Out] int[] outNodeIndices, [In, Out] float[] outInverseBind, int maxBones);

        /// <summary>One hierarchy node of a model's skeleton (bind-pose local transform, row-major float[16]).</summary>
        public class SkeletonNodeInfo
        {
            public string Name = "";
            public int Parent = -1;            // index into the node array; -1 = root
            public float[] LocalBind;          // row-major 4x4
        }

        /// <summary>One bone-palette entry: which node it follows + its inverse-bind matrix.</summary>
        public class SkeletonBoneInfo
        {
            public int NodeIndex;
            public float[] InverseBind;        // row-major 4x4
        }

        /// <summary>Full node hierarchy of a model (null/empty when the model has no skeleton).</summary>
        public static SkeletonNodeInfo[] GetSkeletonNodes(string filepath)
            => ReadSkeletonNodes(() => GetModelSkeletonNodes(filepath, null, null, IntPtr.Zero, 0, 0),
                (parents, local, names, max, nameLen) => GetModelSkeletonNodes(filepath, parents, local, names, max, nameLen));

        /// <summary>Node hierarchy for a model whose bytes live in the in-RAM asset pak (shipped game).</summary>
        public static SkeletonNodeInfo[] GetSkeletonNodesFromMemory(byte[] data, string extHint)
        {
            if (data == null || data.Length == 0) return new SkeletonNodeInfo[0];
            return ReadSkeletonNodes(() => GetModelSkeletonNodesFromMemory(data, data.Length, extHint ?? "", null, null, IntPtr.Zero, 0, 0),
                (parents, local, names, max, nameLen) => GetModelSkeletonNodesFromMemory(data, data.Length, extHint ?? "", parents, local, names, max, nameLen));
        }

        private static SkeletonNodeInfo[] ReadSkeletonNodes(
            Func<int> sizeQuery, Func<int[], float[], IntPtr, int, int, int> fill)
        {
            const int maxNameLen = 128;
            try
            {
                int total = sizeQuery();
                if (total <= 0) return new SkeletonNodeInfo[0];

                var parents = new int[total];
                var localBind = new float[total * 16];
                var namePtrs = new IntPtr[total];
                IntPtr namesArray = IntPtr.Zero;
                try
                {
                    for (int i = 0; i < total; i++) namePtrs[i] = Marshal.AllocHGlobal(maxNameLen);
                    namesArray = Marshal.AllocHGlobal(IntPtr.Size * total);
                    Marshal.Copy(namePtrs, 0, namesArray, total);

                    int count = fill(parents, localBind, namesArray, total, maxNameLen);
                    if (count < 0) count = 0;
                    var result = new SkeletonNodeInfo[count];
                    for (int i = 0; i < count; i++)
                    {
                        var lb = new float[16];
                        Array.Copy(localBind, i * 16, lb, 0, 16);
                        result[i] = new SkeletonNodeInfo
                        {
                            Name = Marshal.PtrToStringAnsi(namePtrs[i]) ?? ("Node_" + i),
                            Parent = parents[i],
                            LocalBind = lb
                        };
                    }
                    return result;
                }
                finally
                {
                    for (int i = 0; i < total; i++) if (namePtrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(namePtrs[i]);
                    if (namesArray != IntPtr.Zero) Marshal.FreeHGlobal(namesArray);
                }
            }
            catch { return new SkeletonNodeInfo[0]; }
        }

        /// <summary>The model's bone palette (empty when not skinned).</summary>
        public static SkeletonBoneInfo[] GetSkeletonBones(string filepath)
        {
            try
            {
                int total = GetModelSkeletonBones(filepath, null, null, 0);
                if (total <= 0) return new SkeletonBoneInfo[0];
                var nodeIdx = new int[total];
                var invBind = new float[total * 16];
                int count = GetModelSkeletonBones(filepath, nodeIdx, invBind, total);
                return PackBones(nodeIdx, invBind, count);
            }
            catch { return new SkeletonBoneInfo[0]; }
        }

        /// <summary>Bone palette for a model whose bytes live in the in-RAM asset pak (shipped game).</summary>
        public static SkeletonBoneInfo[] GetSkeletonBonesFromMemory(byte[] data, string extHint)
        {
            if (data == null || data.Length == 0) return new SkeletonBoneInfo[0];
            try
            {
                int total = GetModelSkeletonBonesFromMemory(data, data.Length, extHint ?? "", null, null, 0);
                if (total <= 0) return new SkeletonBoneInfo[0];
                var nodeIdx = new int[total];
                var invBind = new float[total * 16];
                int count = GetModelSkeletonBonesFromMemory(data, data.Length, extHint ?? "", nodeIdx, invBind, total);
                return PackBones(nodeIdx, invBind, count);
            }
            catch { return new SkeletonBoneInfo[0]; }
        }

        private static SkeletonBoneInfo[] PackBones(int[] nodeIdx, float[] invBind, int count)
        {
            if (count < 0) count = 0;
            var result = new SkeletonBoneInfo[count];
            for (int i = 0; i < count; i++)
            {
                var ib = new float[16];
                Array.Copy(invBind, i * 16, ib, 0, 16);
                result[i] = new SkeletonBoneInfo { NodeIndex = nodeIdx[i], InverseBind = ib };
            }
            return result;
        }

        #endregion

        #region Animation clip queries (editor import — shipped games load .vanim JSON)

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelAnimationCount([MarshalAs(UnmanagedType.LPStr)] string filepath);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelAnimationInfo([MarshalAs(UnmanagedType.LPStr)] string filepath,
            int animIndex, byte[] outName, int nameCap, out float outDurationSec);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetModelAnimationData([MarshalAs(UnmanagedType.LPStr)] string filepath,
            int animIndex, float[] outData, int maxFloats);

        /// <summary>How many animation clips the model file embeds.</summary>
        public static int GetAnimationCount(string filepath)
        {
            try { return string.IsNullOrEmpty(filepath) ? 0 : GetModelAnimationCount(filepath); }
            catch { return 0; }
        }

        /// <summary>Name + duration (seconds) of one embedded clip; null on failure.</summary>
        public static bool GetAnimationInfo(string filepath, int index, out string name, out float durationSec)
        {
            name = null; durationSec = 0f;
            try
            {
                var buf = new byte[256];
                if (GetModelAnimationInfo(filepath, index, buf, buf.Length, out durationSec) == 0) return false;
                int len = Array.IndexOf(buf, (byte)0); if (len < 0) len = buf.Length;
                name = System.Text.Encoding.UTF8.GetString(buf, 0, len);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Raw flattened channel data of one embedded clip (see AnimationApi.cpp for the layout);
        /// null when the clip is missing. Parsed by AnimationService.ClipFromModelData.</summary>
        public static float[] GetAnimationData(string filepath, int index)
        {
            try
            {
                int needed = GetModelAnimationData(filepath, index, null, 0);
                if (needed <= 0) return null;
                var buf = new float[needed];
                int w = GetModelAnimationData(filepath, index, buf, needed);
                return w > 0 ? buf : null;
            }
            catch { return null; }
        }

        #endregion

        #region Skinned rendering

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsMeshSkinned(long meshId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SubmitSkinnedMeshForRendering(long meshId, long materialId,
            float[] worldMatrix, float[] boneMatrices, int boneCount);

        /// <summary>Whether a registered mesh carries skinning data (52-byte vertex).</summary>
        public static bool MeshIsSkinned(long meshId)
        {
            try { return meshId >= 0 && IsMeshSkinned(meshId); }
            catch { return false; }
        }

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SubmitSkinnedMeshForRenderingEx(long meshId, long materialId,
            float[] worldMatrix, float[] boneMatrices, int boneCount, int layer);


        /// <summary>Submit a skinned mesh for this frame: row-major world float[16] + bone palette
        /// (boneCount row-major 4x4s, each = inverseBind * boneWorld). Re-submit every frame the pose
        /// changes. layer 1 = first-person viewmodel arms (#175).</summary>
        public static void SubmitSkinnedMesh(long meshId, long materialId, float[] world, float[] bonePalette, int boneCount, int layer = 0)
        {
            if (world == null || bonePalette == null || boneCount <= 0) return;
            if (layer > 0)
            {
                try { SubmitSkinnedMeshForRenderingEx(meshId, materialId, world, bonePalette, boneCount, layer); return; }
                catch { /* older VortexAPI.dll — fall through to the unlayered export */ }
            }
            try { SubmitSkinnedMeshForRendering(meshId, materialId, world, bonePalette, boneCount); }
            catch { /* older VortexAPI.dll without skinning — degrade gracefully */ }
        }

        #endregion
    }
}
