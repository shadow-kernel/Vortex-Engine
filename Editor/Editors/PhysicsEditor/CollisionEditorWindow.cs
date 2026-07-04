using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.PhysicsEditor
{
    /// <summary>
    /// A dedicated, floating Collision Editor — pick an entity in the scene and give it a collider (Box, Sphere,
    /// Capsule or edge-accurate Mesh), tune it, and mark it a trigger. Built programmatically so it matches the
    /// engine's dark UI without adding a XAML page. Live: it follows the current selection and writes straight onto
    /// the entity's Collider component (the CollisionService reads these to make the world solid).
    /// </summary>
    public sealed class CollisionEditorWindow : Window
    {
        private static CollisionEditorWindow _open;
        private StackPanel _body;
        private TextBlock _entityName;
        private CollisionPreviewControl _preview;

        /// <summary>Non-null = explicit-target mode: this window edits EXACTLY this entity and ignores the global
        /// SelectionService. Used by the isolated Prefab Editor, whose entity lives outside the scene — following the
        /// scene selection there would edit the WRONG entity (or show "No entity selected").</summary>
        private readonly GameEntity _fixedTarget;

        /// <summary>Raised after this editor structurally changes its target entity (collider type switched or
        /// removed, contact script attached or removed) so an external inspector — e.g. the isolated Prefab
        /// Editor's — can re-render its component cards instead of keeping stale ones bound to removed components.</summary>
        public event Action TargetModified;

        public static CollisionEditorWindow Open(Window owner, GameEntity fixedTarget = null)
        {
            if (_open != null)
            {
                if (ReferenceEquals(_open._fixedTarget, fixedTarget))
                {
                    try { _open.Activate(); return _open; } catch { _open = null; }
                }
                else
                {
                    // The open window is bound to a different target (scene selection vs. an isolated prefab
                    // entity) — re-using it would edit the WRONG entity. Close it and open one for this target.
                    try { _open.Close(); } catch { }
                    _open = null;
                }
            }
            _open = new CollisionEditorWindow(fixedTarget) { Owner = owner };
            _open.Show();
            return _open;
        }

        private CollisionEditorWindow(GameEntity fixedTarget)
        {
            _fixedTarget = fixedTarget;
            Title = "Collision Editor";
            Width = 400; Height = 760; MinWidth = 360; MinHeight = 520;
            Background = Br("#FF161618");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { LastChildFill = true };

            var header = new Border { Background = Br("#FF1B1B1E"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12, 16, 12) };
            var hs = new StackPanel();
            hs.Children.Add(new TextBlock { Text = "◈  Collision Editor", Foreground = Br("#FFF5F5F7"), FontSize = 15, FontWeight = FontWeights.Bold });
            _entityName = new TextBlock { Text = "No entity selected", Foreground = Br("#FF8A8A92"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
            hs.Children.Add(_entityName);
            header.Child = hs;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Live preview: the selected object in an empty sunlit world with its collider in green + a fine grid.
            var previewWrap = new Border { Height = 250, Background = Br("#FF0F0F12"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(0, 0, 0, 1) };
            _preview = new CollisionPreviewControl();
            previewWrap.Child = _preview;
            DockPanel.SetDock(previewWrap, Dock.Top);
            root.Children.Add(previewWrap);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 14, 16, 16) };
            _body = new StackPanel();
            scroll.Content = _body;
            root.Children.Add(scroll);

            Content = root;

            // COEXIST with the live main viewport (don't pause it): this window is modeless, and pausing froze
            // the scene view on its last frame — so toggling Is Trigger here showed no recolour until close.
            // The coexist counter keeps the tick running with full per-frame scene re-submission, which makes the
            // preview's queue swaps (Redraw) and the main viewport's frames mutually self-contained.
            Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActiveCoexistPreviews++;

            if (_fixedTarget == null)   // explicit-target mode never follows the scene selection
                SelectionService.Instance.SelectionChanged += OnSelectionChanged;
            Closed += (s, e) =>
            {
                try { if (_fixedTarget == null) SelectionService.Instance.SelectionChanged -= OnSelectionChanged; } catch { }
                try { _preview?.Dispose(); } catch { }
                try
                {
                    Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActiveCoexistPreviews--;
                    Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit();
                    // Only free the SHARED offscreen render target if no other preview dialog/window uses it.
                    if (Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs <= 0 &&
                        Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActiveCoexistPreviews <= 0)
                        Editor.Core.Services.Rendering.AssetPreviewRenderer.DestroyPreviewTarget();
                }
                catch { }
                _open = null;
            };
            Rebuild();
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(Rebuild));
        }

        private GameEntity Ent => _fixedTarget ?? SelectionService.Instance.SelectedEntity;

        private void Rebuild()
        {
            _body.Children.Clear();
            var ent = Ent;
            _entityName.Text = ent != null
                ? ("Entity:  " + ent.Name + (_fixedTarget != null ? "   ·   isolated prefab" : ""))
                : "No entity selected";
            _preview?.SetTarget(ent);

            if (ent == null)
            {
                _body.Children.Add(Note("Select an entity in the Scene Hierarchy to give it a collider."));
                _body.Children.Add(HelpBlock());
                return;
            }

            var col = ent.GetComponent<Collider>();

            // ---- collider type row ----
            _body.Children.Add(Label("COLLIDER SHAPE"));
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            row.Children.Add(TypeButton("Box", col is BoxCollider, () => SetType<BoxCollider>(ent)));
            row.Children.Add(TypeButton("Sphere", col is SphereCollider, () => SetType<SphereCollider>(ent)));
            row.Children.Add(TypeButton("Capsule", col is CapsuleCollider, () => SetType<CapsuleCollider>(ent)));
            row.Children.Add(TypeButton("Mesh", col is MeshCollider, () => SetType<MeshCollider>(ent)));
            if (col != null) row.Children.Add(TypeButton("Remove", false, () => { RemoveComponentFromTarget(ent, col); RaiseTargetModified(); Rebuild(); }));
            _body.Children.Add(row);

            if (col == null)
            {
                _body.Children.Add(Note("This entity has no collider — pick a shape above. Box/Sphere/Capsule are fast; Mesh is edge-accurate (uses the object's real triangles, exactly as rendered)."));
                _body.Children.Add(HelpBlock());
                return;
            }

            // ---- common: center + trigger ----
            _body.Children.Add(Label("CENTER (offset from the entity)"));
            _body.Children.Add(Vector3Row(col.Center, v => col.Center = v));

            // ---- type-specific ----
            if (col is BoxCollider box)
            {
                _body.Children.Add(Label("SIZE"));
                _body.Children.Add(Vector3Row(box.Size, v => box.Size = v));
            }
            else if (col is SphereCollider sph)
            {
                _body.Children.Add(Label("RADIUS"));
                _body.Children.Add(FloatRow(sph.Radius, v => sph.Radius = v));
            }
            else if (col is CapsuleCollider cap)
            {
                _body.Children.Add(Label("RADIUS"));
                _body.Children.Add(FloatRow(cap.Radius, v => cap.Radius = v));
                _body.Children.Add(Label("HEIGHT"));
                _body.Children.Add(FloatRow(cap.Height, v => cap.Height = v));
            }
            else if (col is MeshCollider mesh)
            {
                var mr = ent.GetComponent<MeshRenderer>();
                _body.Children.Add(Note(mr != null && !string.IsNullOrEmpty(mr.MeshPath)
                    ? "Edge-accurate: collides against this object's real triangles" + (mr.MeshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) ? " (exact analytic shape for a primitive)." : " — exactly as rendered.")
                    : "Mesh collider needs a Mesh Renderer on this entity."));
            }

            var trig = new CheckBox { Content = "Is Trigger (overlap only — no solid blocking)", Foreground = Br("#FFC8C8CE"), IsChecked = col.IsTrigger, Margin = new Thickness(0, 12, 0, 4) };
            // On toggle: persist to the component and Rebuild() (which re-targets the preview) so the collider net
            // recolours — amber = trigger, green = solid — in this preview AND the main viewport (which reads
            // IsTrigger live every frame). Before, both looked identical, so the toggle appeared to do nothing.
            trig.Checked += (s, e) => { col.IsTrigger = true; Rebuild(); };
            trig.Unchecked += (s, e) => { col.IsTrigger = false; Rebuild(); };
            _body.Children.Add(trig);

            // Any collider — trigger OR solid — can carry a script that reacts to contact: OnTriggerEnter/Stay/Exit
            // for a trigger (no-fly zone, "change color on touch"); OnCollisionEnter for a solid (e.g. take damage
            // when a character touches this wall/hazard).
            BuildColliderScriptSection(ent, col);

            _body.Children.Add(ActionButton("Auto-fit to mesh", () => { AutoFit(ent, col); Rebuild(); }));
            _body.Children.Add(HelpBlock());
        }

        /// <summary>Collider-script UI: attach/replace/remove a VortexBehaviour on this entity and see which contact
        /// events it can override — works for triggers (OnTriggerEnter/Stay/Exit) AND solids (OnCollisionEnter).</summary>
        private void BuildColliderScriptSection(GameEntity ent, Collider col)
        {
            _body.Children.Add(Label("SCRIPT — reacts to contact"));
            var script = ent.GetComponent<Editor.ECS.Components.Scripting.Script>();
            string cur = script != null ? script.ScriptClassName : null;
            string evts = col.IsTrigger
                ? "OnTriggerEnter / OnTriggerStay / OnTriggerExit"
                : "OnCollisionEnter (fires when a character touches this solid — e.g. take damage on contact)";
            _body.Children.Add(Note(string.IsNullOrEmpty(cur)
                ? "Attach a script, then override " + evts + " to react when a character touches this collider."
                : "Attached: " + cur + "  —  override " + evts + " in it."));
            _body.Children.Add(ActionButton(string.IsNullOrEmpty(cur) ? "Attach Script…" : "Change Script…", () => ShowTriggerScriptPicker(ent)));
            if (script != null)
                _body.Children.Add(ActionButton("Remove Script", () => { RemoveComponentFromTarget(ent, script); RaiseTargetModified(); Rebuild(); }));
        }

        private void ShowTriggerScriptPicker(GameEntity ent)
        {
            var menu = new ContextMenu();
            try
            {
                foreach (var rel in ScriptingService.EnumerateScripts())
                {
                    var r = rel;
                    var mi = new MenuItem { Header = System.IO.Path.GetFileNameWithoutExtension(r) };
                    mi.Click += (s, e) => AttachTriggerScript(ent, r);
                    menu.Items.Add(mi);
                }
            }
            catch { }
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var nw = new MenuItem { Header = "New Script…" };
            nw.Click += (s, e) => CreateAndAttachTriggerScript(ent);
            menu.Items.Add(nw);
            menu.IsOpen = true;
        }

        private void AttachTriggerScript(GameEntity ent, string rel)
        {
            if (ent == null || string.IsNullOrEmpty(rel)) return;
            var existing = ent.GetComponent<Editor.ECS.Components.Scripting.Script>();
            if (existing != null) RemoveComponentFromTarget(ent, existing);
            AddComponentToTarget(ent, new Editor.ECS.Components.Scripting.Script(ent, rel));
            if (_fixedTarget == null) SelectionService.Instance.Select(ent); // refresh the main inspector too
            RaiseTargetModified();
            Rebuild();
        }

        private void CreateAndAttachTriggerScript(GameEntity ent)
        {
            try
            {
                var baseName = string.IsNullOrWhiteSpace(ent.Name) ? "TriggerBehaviour" : ent.Name + "Trigger";
                var abs = ScriptingService.CreateScript(baseName);
                var rel = ScriptingService.MakeRelative(ScriptingService.ProjectRoot, abs);
                AttachTriggerScript(ent, rel);
                ScriptingService.OpenInVisualStudio(abs);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CollisionEditor] " + ex.Message); }
        }

        private void SetType<T>(GameEntity ent) where T : Collider, new()
        {
            var existing = ent.GetComponent<Collider>();
            if (existing != null) RemoveComponentFromTarget(ent, existing);
            var c = new T { Entity = ent };
            if (existing != null) { c.Center = existing.Center; c.IsTrigger = existing.IsTrigger; }
            AddComponentToTarget(ent, c);
            AutoFit(ent, c);
            if (_fixedTarget == null) SelectionService.Instance.Select(ent); // refresh the main inspector too
            RaiseTargetModified();
            Rebuild();
        }

        /// <summary>Add a component honoring the target mode: undoable for scene entities, DIRECT for an explicit
        /// fixed target — the isolated Prefab Editor's entity is a throwaway outside the scene, and a global undo
        /// entry referencing it would let a later Ctrl+Z in the main editor mutate a ghost.</summary>
        private void AddComponentToTarget(GameEntity ent, Component c)
        {
            if (_fixedTarget != null) ent.AddComponentDirect(c);
            else ent.AddComponent(c);
        }

        /// <summary>Remove counterpart of <see cref="AddComponentToTarget"/> — same undo-hygiene rule.</summary>
        private void RemoveComponentFromTarget(GameEntity ent, Component c)
        {
            if (_fixedTarget != null) ent.Components.Remove(c);
            else ent.RemoveComponent(c);
        }

        private void RaiseTargetModified()
        {
            var h = TargetModified;
            if (h != null) { try { h(); } catch { } }
        }

        private static void AutoFit(GameEntity ent, Collider col)
        {
            // Unit defaults work because the CollisionService scales the collider by the entity transform; for a
            // non-primitive you can tweak the numbers by hand. Keeps it simple + predictable.
            if (col is BoxCollider b) b.Size = new Vector3(1, 1, 1);
            else if (col is SphereCollider s) s.Radius = 0.5f;
            else if (col is CapsuleCollider c) { c.Radius = 0.5f; c.Height = 2f; }
        }

        // ---- tiny styled UI helpers ----
        private static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        private static TextBlock Label(string t) => new TextBlock { Text = t, Foreground = Br("#FF6E6E77"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 6) };

        private static UIElement Note(string t) => new Border { Background = Br("#FF1E1E22"), BorderBrush = Br("#FF2C2C32"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 9, 11, 10), Margin = new Thickness(0, 0, 0, 12), Child = new TextBlock { Text = t, Foreground = Br("#FFA9A9B2"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, LineHeight = 16 } };

        private UIElement HelpBlock() => new Border { Background = Br("#FF17171A"), BorderBrush = Br("#FF262630"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 9, 11, 10), Margin = new Thickness(0, 16, 0, 0), Child = new TextBlock { Foreground = Br("#FF8A8A92"), FontSize = 11, TextWrapping = TextWrapping.Wrap, LineHeight = 16, Text = "How it works: give your level objects (ground, walls, props) a collider here. In the game, the character capsule collides against them (Vortex.Physics.MoveCharacter) — solid ground, no walking through walls, no clipping. Box/Sphere/Capsule are cheapest; Mesh is edge-accurate (the object's real triangles)." } };

        private Button TypeButton(string text, bool active, Action onClick)
        {
            var b = new Button
            {
                Content = text, Margin = new Thickness(0, 0, 7, 7), Padding = new Thickness(13, 6, 13, 6),
                Cursor = System.Windows.Input.Cursors.Hand, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = active ? Brushes.White : Br("#FFC8C8CE"),
                Background = active ? Br("#FF6C5CE7") : Br("#FF212127"),
                BorderBrush = active ? Br("#FF6C5CE7") : Br("#FF2C2C32"), BorderThickness = new Thickness(1)
            };
            b.Click += (s, e) => { try { onClick(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CollisionEditor] " + ex.Message); } };
            return b;
        }

        private Button ActionButton(string text, Action onClick)
        {
            var b = new Button
            {
                Content = text, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 8, 14, 8),
                HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 12.5, Foreground = Br("#FFE9E9ED"), Background = Br("#FF26262B"),
                BorderBrush = Br("#FF3A3A42"), BorderThickness = new Thickness(1)
            };
            b.Click += (s, e) => { try { onClick(); } catch { } };
            return b;
        }

        private UIElement Vector3Row(Vector3 v, Action<Vector3> set)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
            float x = v.X, y = v.Y, z = v.Z;
            var fx = NumBox(x, nv => { x = nv; set(new Vector3(x, y, z)); }, "X", "#FFE06C6C");
            var fy = NumBox(y, nv => { y = nv; set(new Vector3(x, y, z)); }, "Y", "#FF7CE0A3");
            var fz = NumBox(z, nv => { z = nv; set(new Vector3(x, y, z)); }, "Z", "#FF6C9CE0");
            Grid.SetColumn(fx, 0); Grid.SetColumn(fy, 1); Grid.SetColumn(fz, 2);
            g.Children.Add(fx); g.Children.Add(fy); g.Children.Add(fz);
            return g;
        }

        private UIElement FloatRow(float v, Action<float> set)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            p.Children.Add(NumBox(v, set, null, "#FFC8C8CE"));
            return p;
        }

        private UIElement NumBox(float value, Action<float> set, string tag, string tagColor)
        {
            var wrap = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
            if (tag != null) wrap.Children.Add(new TextBlock { Text = tag, Foreground = Br(tagColor), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = 12 });
            var tb = new TextBox
            {
                Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Background = Br("#FF202023"), Foreground = Br("#FFF0F0F3"), BorderBrush = Br("#FF34343C"),
                BorderThickness = new Thickness(1), Padding = new Thickness(7, 5, 7, 5), MinWidth = 62,
                CaretBrush = Br("#FF6C5CE7")
            };
            tb.TextChanged += (s, e) =>
            {
                if (float.TryParse(tb.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nv)) set(nv);
            };
            wrap.Children.Add(tb);
            return wrap;
        }
    }
}
