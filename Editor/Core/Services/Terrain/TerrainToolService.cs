using System;
using System.Numerics;
using Editor.Core.Data;
using Editor.Core.Terrain;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.DllWrapper;
using Editor.ECS;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace Editor.Core.Services.Terrain
{
    public enum TerrainTool { None = 0, Raise = 1, Lower = 2, Smooth = 3, Flatten = 4, Paint = 5 }

    /// <summary>
    /// The viewport terrain tools (#124): while a tool is active and the selected entity is a terrain, the left mouse
    /// button sculpts or paints under the cursor instead of picking (Shift inverts raise / lower, Ctrl smooths). A stroke
    /// is one undo step (the touched heights / splat rectangle before and after) and saves the terrain's data file when
    /// it ends. The brush shows as a wire disc on the surface under the cursor.
    /// </summary>
    public static class TerrainToolService
    {
        public static TerrainTool Tool { get; set; } = TerrainTool.None;
        /// <summary>Brush radius in metres.</summary>
        public static float Radius { get; set; } = 6f;
        /// <summary>0..1: how much one stroke step changes.</summary>
        public static float Strength { get; set; } = 0.5f;
        /// <summary>0..1: the flat part of the brush (0 = a smooth dome).</summary>
        public static float Hardness { get; set; } = 0.25f;
        /// <summary>The layer the Paint tool paints (0..3).</summary>
        public static int PaintLayer { get; set; }
        /// <summary>Flatten: the target height (world Y). Sampled where the stroke starts unless <see cref="FlattenFixed"/>.</summary>
        public static float FlattenHeight { get; set; }
        public static bool FlattenFixed { get; set; }

        public static event Action ToolChanged;

        /// <summary>World point under the cursor on the selected terrain (null while the cursor is off it).</summary>
        public static Vector3? Hover { get; private set; }
        public static bool IsStroking => _strokeEntity != null;

        private static GameEntity _strokeEntity;
        private static TerrainData _strokeData;
        private static float[] _beforeHeights;
        private static byte[] _beforeSplat;
        private static SampleRect _strokeRect;
        private static bool _strokeHeights, _strokeSplat;
        private static TerrainTool _strokeTool;
        private static float _strokeTarget;
        private static Vector3 _lastApply;
        private static bool _hasLastApply;

        public static void SetTool(TerrainTool tool)
        {
            if (Tool == tool) return;
            Tool = tool;
            Hover = null;
            ToolChanged?.Invoke();
        }

        /// <summary>The selected entity when it carries an enabled Terrain, else null.</summary>
        public static GameEntity SelectedTerrain()
        {
            var e = SelectionService.Instance.SelectedEntity;
            var t = e?.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
            return t != null && t.IsEnabled ? e : null;
        }

        /// <summary>A tool is chosen and the selection is a terrain: the viewport routes the left button here.</summary>
        public static bool Active => Tool != TerrainTool.None && SelectedTerrain() != null;

        private static TerrainTool Effective(bool shift, bool ctrl)
        {
            if (Tool == TerrainTool.None) return TerrainTool.None;
            if (ctrl) return TerrainTool.Smooth;
            if (shift)
            {
                if (Tool == TerrainTool.Raise) return TerrainTool.Lower;
                if (Tool == TerrainTool.Lower) return TerrainTool.Raise;
            }
            return Tool;
        }

        /// <summary>0 sculpt, 1 paint, 2 smooth / flatten — the brush gizmo colour.</summary>
        public static int BrushKind(bool shift = false, bool ctrl = false)
        {
            var t = Effective(shift, ctrl);
            return t == TerrainTool.Paint ? 1 : (t == TerrainTool.Smooth || t == TerrainTool.Flatten ? 2 : 0);
        }

        private static bool CursorHit(float nx, float ny, float aspect, GameEntity terrain, out Vector3 hit)
        {
            hit = default(Vector3);
            var ray = RaycastService.Instance.ScreenToRayWithAspect(nx, ny, aspect, 1f);
            var o = new Vector3(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
            var d = new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
            GameEntity hitTerrain;
            return TerrainService.Raycast(o, d, 5000f, out hit, out hitTerrain, terrain);
        }

        /// <summary>Move the brush preview (normalised viewport coordinates).</summary>
        public static void UpdateHover(float nx, float ny, float aspect)
        {
            var terrain = SelectedTerrain();
            if (terrain == null || Tool == TerrainTool.None) { Hover = null; return; }
            Vector3 hit;
            Hover = CursorHit(nx, ny, aspect, terrain, out hit) ? hit : (Vector3?)null;
        }

        /// <summary>Left button down: start a stroke when a tool is active and the cursor is on the selected terrain.</summary>
        public static bool TryBeginStroke(float nx, float ny, float aspect, bool shift, bool ctrl)
        {
            if (!Active) return false;
            var terrain = SelectedTerrain();
            Vector3 hit;
            if (!CursorHit(nx, ny, aspect, terrain, out hit)) return false;
            TerrainData data; float cell;
            if (!TerrainService.TryGetData(terrain, out data, out cell)) return false;

            _strokeEntity = terrain;
            _strokeData = data;
            _beforeHeights = (float[])data.Heights.Clone();
            _beforeSplat = (byte[])data.Splat.Clone();
            _strokeRect = SampleRect.Empty;
            _strokeHeights = _strokeSplat = false;
            _strokeTool = Effective(shift, ctrl);
            _strokeTarget = FlattenFixed ? FlattenHeight : hit.Y;
            _hasLastApply = false;
            Hover = hit;
            Apply(hit);
            return true;
        }

        /// <summary>Left button held and moved: continue the stroke.</summary>
        public static void ContinueStroke(float nx, float ny, float aspect, bool shift, bool ctrl)
        {
            if (_strokeEntity == null) return;
            Vector3 hit;
            if (!CursorHit(nx, ny, aspect, _strokeEntity, out hit)) return;
            Hover = hit;
            // space the brush dabs: a quarter radius apart keeps a dragged stroke even
            if (_hasLastApply && Vector3.Distance(hit, _lastApply) < Radius * 0.25f) return;
            Apply(hit);
        }

        private static void Apply(Vector3 worldHit)
        {
            var e = _strokeEntity;
            var data = _strokeData;
            float cell;
            TerrainData d2;
            if (!TerrainService.TryGetData(e, out d2, out cell) || !ReferenceEquals(d2, data)) return;
            var local = TerrainService.WorldToLocal(e, worldHit);
            float cx = local.X / cell, cz = local.Z / cell, r = Math.Max(0.5f, Radius / cell);
            float s = Math.Max(0.01f, Math.Min(1f, Strength));
            SampleRect rect;
            bool heights = true;
            switch (_strokeTool)
            {
                case TerrainTool.Raise: rect = data.Raise(cx, cz, r, 0.35f * s * Math.Max(0.5f, Radius * 0.15f), Hardness); break;
                case TerrainTool.Lower: rect = data.Raise(cx, cz, r, -0.35f * s * Math.Max(0.5f, Radius * 0.15f), Hardness); break;
                case TerrainTool.Smooth: rect = data.Smooth(cx, cz, r, 0.5f * s, Hardness); break;
                case TerrainTool.Flatten:
                {
                    // the target is a world height: express it in the terrain's local frame
                    var lt = TerrainService.WorldToLocal(e, new Vector3(worldHit.X, _strokeTarget, worldHit.Z));
                    rect = data.Flatten(cx, cz, r, lt.Y, 0.5f * s, Hardness);
                    break;
                }
                case TerrainTool.Paint: rect = data.Paint(cx, cz, r, PaintLayer, 0.45f * s, Hardness); heights = false; break;
                default: return;
            }
            _strokeRect = _strokeRect.Union(rect);
            if (heights) _strokeHeights = true; else _strokeSplat = true;
            _lastApply = worldHit; _hasLastApply = true;
            TerrainService.MarkDirty(e, rect, heights, !heights);
            try { Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }

        /// <summary>Left button up: close the stroke as one undo step and save the terrain.</summary>
        public static void EndStroke()
        {
            var e = _strokeEntity;
            var data = _strokeData;
            if (e == null || data == null) { _strokeEntity = null; return; }
            var rect = _strokeRect;
            bool heights = _strokeHeights, splat = _strokeSplat;
            if (!rect.IsEmpty)
            {
                var afterH = heights ? data.CopyHeights(rect) : null;
                var afterS = splat ? data.CopySplat(rect) : null;
                var beforeH = heights ? Crop(_beforeHeights, data.Resolution, rect) : null;
                var beforeS = splat ? CropSplat(_beforeSplat, data.Resolution, rect) : null;
                string name = "Terrain " + _strokeTool;
                Action redo = () =>
                {
                    if (heights) data.RestoreHeights(rect, afterH);
                    if (splat) data.RestoreSplat(rect, afterS);
                    TerrainService.MarkDirty(e, rect, heights, splat);
                    TerrainService.Save(e);
                    try { Viewport.EditorViewportSession.RequestResubmit(); } catch { }
                };
                Action undo = () =>
                {
                    if (heights) data.RestoreHeights(rect, beforeH);
                    if (splat) data.RestoreSplat(rect, beforeS);
                    TerrainService.MarkDirty(e, rect, heights, splat);
                    TerrainService.Save(e);
                    try { Viewport.EditorViewportSession.RequestResubmit(); } catch { }
                };
                try { UndoRedoManager.Instance.Execute(new ActionCommand(name, redo, undo), false); } catch { }
                TerrainService.Save(e);
            }
            _strokeEntity = null; _strokeData = null; _beforeHeights = null; _beforeSplat = null;
            _strokeRect = SampleRect.Empty;
        }

        private static float[] Crop(float[] full, int res, SampleRect r)
        {
            var o = new float[r.Width * r.Height];
            for (int z = r.Z0, k = 0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++, k++) o[k] = full[z * res + x];
            return o;
        }

        private static byte[] CropSplat(byte[] full, int res, SampleRect r)
        {
            var o = new byte[r.Width * r.Height * 4];
            for (int z = r.Z0, k = 0; z <= r.Z1; z++)
                for (int x = r.X0; x <= r.X1; x++, k += 4) Buffer.BlockCopy(full, (z * res + x) * 4, o, k, 4);
            return o;
        }

        /// <summary>Draw the brush disc under the cursor (called from the overlay pass).</summary>
        public static void SubmitGizmo()
        {
            if (!Active || Hover == null) return;
            var h = Hover.Value;
            VortexAPI.RenderTerrainBrush(h.X, h.Y, h.Z, Radius, BrushKind());
        }
    }
}
