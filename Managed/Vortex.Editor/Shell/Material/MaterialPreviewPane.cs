using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Assets;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using VortexEditor.Controls;

namespace VortexEditor.Shell.Material
{
    /// <summary>
    /// The material editor's big live preview: the edited material on a sphere, cube or plane in the shared
    /// <see cref="PreviewViewport"/> (drag = orbit, right-drag = pan, wheel = zoom, double-click = reset). Edits only mark
    /// the material dirty; it is rebuilt right before the next render, so a slider drag re-renders at most once per
    /// preview tick. The pane owns its meshes and the throwaway engine material and releases them when it leaves the tree.
    /// </summary>
    internal sealed class MaterialPreviewPane : Grid
    {
        public readonly PreviewViewport Viewport = new PreviewViewport();
        private readonly PreviewScene _scene = new PreviewScene();
        private readonly PreviewItem _item = new PreviewItem();
        private readonly Dictionary<string, long> _meshes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<VortexMaterial> _source;
        private readonly Dictionary<string, RadioButton> _shapeButtons = new Dictionary<string, RadioButton>();
        private long _material = -1;
        private bool _dirty = true;
        private bool _released;

        /// <summary>How many times the preview material was rebuilt (tests: an edit must re-render the preview).</summary>
        public int Builds { get; private set; }
        public string Shape { get; private set; } = "Sphere";

        /// <param name="source">Returns the material to show with ABSOLUTE texture paths (a copy the pane may keep).</param>
        public MaterialPreviewPane(Func<VortexMaterial> source)
        {
            _source = source;
            Background = EditorKit.Brush("VxViewportBgBrush");
            _scene.Items.Add(_item);
            Viewport.BeforeRender = () => { if (_dirty) Rebuild(); };
            Children.Add(Viewport);

            // overlay toolbar: shape (left), auto-rotate + reset (right)
            var shapes = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var s in new[] { "Sphere", "Cube", "Plane" })
            {
                string shape = s;
                var rb = new RadioButton { GroupName = "matpreviewshape_" + GetHashCode(), Content = s, IsChecked = s == "Sphere" };
                rb.IsCheckedChanged += (x, e) => { if (rb.IsChecked == true) SetShape(shape); };
                _shapeButtons[s] = rb;
                shapes.Children.Add(rb);
            }
            var seg = new Border { Classes = { "segmented" }, Child = shapes };
            ToolTip.SetTip(seg, "Preview shape");
            var spin = new ToggleButton { Classes = { "icon" }, Content = new VxIcon { Icon = "Rotate" } };
            ToolTip.SetTip(spin, "Auto-rotate");
            spin.IsCheckedChanged += (s, e) => Viewport.AutoRotate = spin.IsChecked == true ? 0.6f : 0f;
            var reset = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Home" } };
            ToolTip.SetTip(reset, "Reset view (or double-click the preview)");
            reset.Click += (s, e) => ResetCamera();
            var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Top };
            bar.Children.Add(seg);
            Grid.SetColumn(spin, 2); bar.Children.Add(spin);
            Grid.SetColumn(reset, 3); bar.Children.Add(reset);
            Children.Add(bar);

            DetachedFromVisualTree += (s, e) => Release();
            SetShape("Sphere");
        }

        /// <summary>The material changed — rebuild it before the next render.</summary>
        public void MarkDirty() { _dirty = true; Viewport.Invalidate(); }

        public void SetShape(string shape)
        {
            if (_released) return;
            Shape = shape;
            if (_shapeButtons.TryGetValue(shape, out var rb) && rb.IsChecked != true) rb.IsChecked = true;
            if (!_meshes.TryGetValue(shape, out long mesh) || mesh < 0)
            {
                try
                {
                    mesh = shape == "Cube" ? VortexAPI.CreateCubeMesh(1f) : shape == "Plane" ? VortexAPI.CreatePlaneMesh(1.4f, 1.4f) : VortexAPI.CreateSphereMesh(0.62f);
                }
                catch { mesh = -1; }
                _meshes[shape] = mesh;
            }
            _item.Mesh = mesh;
            _item.Material = _material;
            Viewport.Scene = _scene;   // re-frame for the new shape
            ResetCamera();
        }

        private void ResetCamera()
        {
            var cam = PreviewCamera.Default;
            if (Shape == "Plane") cam.Pitch = 0.95f;
            Viewport.Camera = cam;
        }

        private void Rebuild()
        {
            _dirty = false;
            if (_released) return;
            long old = _material;
            try
            {
                var vm = _source();
                _material = vm != null ? MaterialService.Instance.BuildEngineMaterial(vm) : -1;
                if (_material >= 0 && vm != null) MaterialLive.BindPlatformShader(_material, vm.ShaderAsset);
            }
            catch { _material = -1; }
            _item.Material = _material;
            Builds++;
            if (old >= 0) { try { VortexAPI.DeleteMaterial(old); } catch { } }
        }

        /// <summary>Recompile changed custom shaders (hot reload when the window regains focus) and re-render.</summary>
        public void ReloadShadersIfChanged()
        {
            try { if (VortexAPI.AnyMaterialShaderDirty()) { VortexAPI.ReloadMaterialShaders(); MarkDirty(); } } catch { }
        }

        public void Release()
        {
            if (_released) return;
            _released = true;
            Viewport.Scene = null;
            // the last preview render left our meshes in the engine's active queue: swap them out before freeing them
            if (_meshes.Values.Any(m => m >= 0)) MaterialLive.PurgeRenderQueue();
            foreach (var m in _meshes.Values) if (m >= 0) { try { VortexAPI.DeleteMesh(m); } catch { } }
            _meshes.Clear();
            if (_material >= 0) { try { VortexAPI.DeleteMaterial(_material); } catch { } _material = -1; }
        }
    }
}
