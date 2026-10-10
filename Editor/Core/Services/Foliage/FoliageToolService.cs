using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.Foliage;
using Editor.Core.Services.Terrain;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.DllWrapper;
using Editor.ECS;
using Vector3 = System.Numerics.Vector3;

namespace Editor.Core.Services.Foliage
{
    public enum FoliageTool { None = 0, Paint = 1, Erase = 2 }

    /// <summary>
    /// The viewport foliage brush (#125): with Paint or Erase chosen and a Foliage entity selected, the left button paints
    /// instances of the chosen type (or of every type, weighted) onto the surface under the cursor — terrains and the
    /// tops of meshes — or erases them. A stroke is one undo step and saves the data file when it ends.
    /// </summary>
    public static class FoliageToolService
    {
        public static FoliageTool Tool { get; private set; } = FoliageTool.None;
        /// <summary>Brush radius in metres.</summary>
        public static float Radius { get; set; } = 6f;
        /// <summary>0..1: how densely one dab paints (× the type's density) / how thoroughly it erases.</summary>
        public static float Strength { get; set; } = 1f;
        /// <summary>The type to paint, or -1 for every type of the layer weighted by their Weight.</summary>
        public static int TypeIndex { get; set; } = -1;

        public static event Action ToolChanged;

        public static Vector3? Hover { get; private set; }
        public static bool IsStroking => _strokeEntity != null;

        private static GameEntity _strokeEntity;
        private static readonly Dictionary<string, List<FoliageInstance>> _before = new Dictionary<string, List<FoliageInstance>>(StringComparer.OrdinalIgnoreCase);
        private static Vector3 _lastDab;
        private static bool _hasLastDab;
        private static int _seed = 1;
        private static readonly Random _pick = new Random(17);

        public static void SetTool(FoliageTool tool)
        {
            if (Tool == tool) return;
            Tool = tool;
            Hover = null;
            ToolChanged?.Invoke();
        }

        public static GameEntity SelectedFoliage()
        {
            var e = SelectionService.Instance.SelectedEntity;
            var f = e?.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            return f != null && f.IsEnabled ? e : null;
        }

        public static bool Active => Tool != FoliageTool.None && SelectedFoliage() != null;

        /// <summary>1 paint (cyan), 2 erase (amber) — the brush disc colour.</summary>
        public static int BrushKind() => Tool == FoliageTool.Erase ? 2 : 1;

        /// <summary>The surface point under a viewport position: terrains first, then the nearest mesh box.</summary>
        public static bool CursorHit(float nx, float ny, float aspect, out Vector3 hit)
        {
            hit = default(Vector3);
            var ray = RaycastService.Instance.ScreenToRayWithAspect(nx, ny, aspect, 1f);
            var o = new Vector3(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
            var d = new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
            GameEntity terrain;
            Vector3 th; bool haveTerrain = TerrainService.Raycast(o, d, 5000f, out th, out terrain);
            float best = haveTerrain ? Vector3.Distance(o, th) : float.MaxValue;
            if (haveTerrain) hit = th;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene != null)
            {
                try
                {
                    var hits = RaycastService.Instance.RaycastAll(ray, scene);
                    foreach (var h in hits)
                    {
                        if (h.Entity == null) continue;
                        if (h.Entity.GetComponent<Editor.ECS.Components.Rendering.Foliage>() != null || h.Entity.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null) continue;
                        if (h.Distance < best) { best = h.Distance; hit = new Vector3(h.Point.X, h.Point.Y, h.Point.Z); }
                        break;   // sorted by distance
                    }
                }
                catch { }
            }
            if (best == float.MaxValue)
            {
                // the ground plane as a last resort
                if (d.Y < -1e-4f) { float t = -o.Y / d.Y; if (t > 0f && t < 2000f) { hit = o + d * t; return true; } }
                return false;
            }
            return true;
        }

        public static void UpdateHover(float nx, float ny, float aspect)
        {
            if (!Active) { Hover = null; return; }
            Vector3 hit;
            Hover = CursorHit(nx, ny, aspect, out hit) ? hit : (Vector3?)null;
        }

        public static bool TryBeginStroke(float nx, float ny, float aspect, bool shift, bool ctrl)
        {
            if (!Active) return false;
            var e = SelectedFoliage();
            Vector3 hit;
            if (!CursorHit(nx, ny, aspect, out hit)) return false;
            FoliageData data;
            if (!FoliageService.TryGetData(e, out data)) return false;
            _strokeEntity = e;
            _before.Clear();
            foreach (var l in data.Layers) _before[l.TypeName ?? ""] = l.Snapshot();
            _hasLastDab = false;
            Hover = hit;
            Dab(hit, shift);
            return true;
        }

        public static void ContinueStroke(float nx, float ny, float aspect, bool shift, bool ctrl)
        {
            if (_strokeEntity == null) return;
            Vector3 hit;
            if (!CursorHit(nx, ny, aspect, out hit)) return;
            Hover = hit;
            if (_hasLastDab && Vector3.Distance(hit, _lastDab) < Radius * 0.4f) return;
            Dab(hit, shift);
        }

        private static void Dab(Vector3 hit, bool shift)
        {
            var e = _strokeEntity;
            var f = e.GetComponent<Editor.ECS.Components.Rendering.Foliage>();
            if (f == null) return;
            bool erase = Tool == FoliageTool.Erase || shift;
            if (erase)
            {
                FoliageService.Erase(e, TypeIndex, hit, Radius * Math.Max(0.2f, Math.Min(1f, Strength)));
            }
            else
            {
                if (f.Types.Count == 0) return;
                float s = Math.Max(0.02f, Math.Min(1f, Strength)) * 0.35f;
                if (TypeIndex >= 0 && TypeIndex < f.Types.Count)
                    FoliageService.Paint(e, TypeIndex, hit, Radius, s, _seed++);
                else
                {
                    // every type at once: each gets a share of the brush, the weights decide the share
                    float total = 0f;
                    foreach (var t in f.Types) total += Math.Max(0f, t.Weight);
                    for (int i = 0; i < f.Types.Count; i++)
                    {
                        float share = total > 0f ? Math.Max(0f, f.Types[i].Weight) / total : 1f / f.Types.Count;
                        if (share <= 0f) continue;
                        FoliageService.Paint(e, i, hit, Radius, s * share * f.Types.Count, _seed++);
                    }
                }
            }
            _lastDab = hit; _hasLastDab = true;
            try { Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }

        public static void EndStroke()
        {
            var e = _strokeEntity;
            if (e == null) return;
            FoliageData data;
            if (FoliageService.TryGetData(e, out data))
            {
                var after = new Dictionary<string, List<FoliageInstance>>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in data.Layers) after[l.TypeName ?? ""] = l.Snapshot();
                var before = new Dictionary<string, List<FoliageInstance>>(_before, StringComparer.OrdinalIgnoreCase);
                bool changed = false;
                foreach (var kv in after)
                {
                    List<FoliageInstance> b;
                    if (!before.TryGetValue(kv.Key, out b) || b.Count != kv.Value.Count) { changed = true; break; }
                }
                if (changed)
                {
                    string name = Tool == FoliageTool.Erase ? "Erase foliage" : "Paint foliage";
                    Action apply(Dictionary<string, List<FoliageInstance>> state) => () =>
                    {
                        FoliageData d;
                        if (!FoliageService.TryGetData(e, out d)) return;
                        foreach (var kv in state) d.Layer(kv.Key, true).Set(kv.Value);
                        foreach (var l in d.Layers) if (!state.ContainsKey(l.TypeName ?? "")) l.Clear();
                        FoliageService.MarkDirty(e);
                        FoliageService.Save(e);
                        try { Viewport.EditorViewportSession.RequestResubmit(); } catch { }
                    };
                    try { UndoRedoManager.Instance.Execute(new ActionCommand(name, apply(after), apply(before)), false); } catch { }
                    FoliageService.MarkDirty(e);
                    FoliageService.Save(e);
                }
            }
            _strokeEntity = null;
            _before.Clear();
        }

        public static void SubmitGizmo()
        {
            if (!Active || Hover == null) return;
            var h = Hover.Value;
            VortexAPI.RenderTerrainBrush(h.X, h.Y, h.Z, Radius, BrushKind());
        }
    }
}
