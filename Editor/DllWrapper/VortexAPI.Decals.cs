using System.Runtime.InteropServices;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>Decal interop (#120): the renderer's per-submit decal list — mirrors VortexAPI/Api/DecalApi.cpp.</summary>
    public static partial class VortexAPI
    {
        [DllImport(_dllName, CallingConvention = _cc)] private static extern void ClearDecals();
        [DllImport(_dllName, CallingConvention = _cc)] private static extern void AddDecal(float[] world, long materialId, float r, float g, float b, float a, float angleFade, float fadeDistance, int blend, int sortOrder);
        [DllImport(_dllName, CallingConvention = _cc)] private static extern int GetDecalCount();

        /// <summary>Drop every submitted decal (the DecalService refills the list on each scene submit).</summary>
        public static void ClearAllDecals() { try { ClearDecals(); } catch { } }

        /// <summary>Submit one decal: <paramref name="world"/> places the unit box (16 floats, row-major, row 3 = translation; it
        /// projects along its local +Y), <paramref name="materialId"/> is an engine material (its albedo texture + base colour;
        /// <see cref="ID.INVALID_ID"/> = untextured), blend 0 lit / 1 multiply / 2 additive.</summary>
        public static void SubmitDecal(float[] world, long materialId, float r, float g, float b, float a, float angleFade, float fadeDistance, int blend, int sortOrder)
        {
            if (world == null || world.Length < 16) return;
            try { AddDecal(world, materialId, r, g, b, a, angleFade, fadeDistance, blend, sortOrder); } catch { }
        }

        /// <summary>Decals the renderer holds right now (after a submit).</summary>
        public static int SubmittedDecalCount() { try { return GetDecalCount(); } catch { return 0; } }

        private static long _decalGizmoMaterial = ID.INVALID_ID;   // magenta net: the decal's projection box

        /// <summary>The selected decal's box as an always-on-top wire net (<paramref name="world"/> as for <see cref="SubmitDecal"/>).</summary>
        public static void RenderDecalGizmo(float[] world)
        {
            if (world == null || world.Length < 16) return;
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoCube == ID.INVALID_ID) return;
            if (_decalGizmoMaterial == ID.INVALID_ID) _decalGizmoMaterial = MakeUnlitMaterial(0.95f, 0.35f, 0.85f);
            if (_decalGizmoMaterial == ID.INVALID_ID) return;
            SubmitGizmoWireForRendering(_gizmoCube, _decalGizmoMaterial, world);
        }
    }
}
