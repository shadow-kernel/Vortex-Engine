using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using VortexEditor.Shell.Animation;
using VortexEditor.Shell.Physics;
using static VortexEditor.Panels.Inspector.PropertyRows;
using Component = Editor.ECS.Component;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Collision Editor: give an entity its collider(s) — Box, Sphere, Capsule or edge-accurate Mesh — tune them, mark
    /// them triggers, fit them to the mesh, attach a contact script and set up its Rigidbody + physics material, with a
    /// live preview of ONLY that object and its collider nets. In scene mode it follows the selection (like the Windows
    /// editor); on an entity outside the scene (the isolated prefab editor) it edits exactly that entity, without undo
    /// entries that would outlive it.
    /// </summary>
    public sealed class CollisionEditorWindow : Window
    {
        private static CollisionEditorWindow _open;

        /// <summary>Open (or re-target) the Collision Editor for this entity.</summary>
        public static void Open(Editor.ECS.GameEntity entity)
        {
            if (_open != null)
            {
                bool isolated = entity != null && !InActiveScene(entity);
                if (!isolated && !_open._fixed) { _open.SetTarget(entity); _open.Activate(); return; }
                _open.Close();
            }
            EditorWindows.Show(new CollisionEditorWindow(entity));
        }

        public static CollisionEditorWindow Current => _open;

        /// <summary>Raised after this editor structurally changed its target (colliders added / removed / switched,
        /// script attached) so an external inspector (e.g. the isolated prefab editor's) can re-render its cards.</summary>
        public event Action TargetModified;

        private GameEntity _ent;
        private readonly bool _fixed;             // entity outside the active scene: never follow the selection
        private readonly CollisionPreview _preview = new CollisionPreview();
        private readonly StackPanel _body = new StackPanel();
        private readonly TextBlock _entityName = new TextBlock { Classes = { "small", "secondary" }, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly List<Action> _refreshers = new List<Action>();
        private Collider _sel;
        private bool _rebuildQueued;

        public CollisionPreview Preview => _preview;
        public GameEntity Target => _ent;
        public Collider SelectedCollider => _sel;

        public CollisionEditorWindow(GameEntity entity)
        {
            _ent = entity;
            _fixed = entity != null && !InActiveScene(entity);
            Title = "Collision Editor";
            Width = 460; Height = 860; MinWidth = 380; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel();
            var header = new Border { Background = AnimUi.Res("VxToolbarBrush"), BorderBrush = AnimUi.Res("VxHairlineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12) };
            var hs = new StackPanel();
            var ht = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            ht.Children.Add(new Controls.VxIcon { Icon = "Collider", Foreground = AnimUi.Res("VxGreenBrush") });
            ht.Children.Add(new TextBlock { Text = "Collision Editor", FontSize = 15, FontWeight = FontWeight.Bold });
            hs.Children.Add(ht);
            hs.Children.Add(_entityName);
            header.Child = hs;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var previewWrap = new Border { Height = 260, BorderBrush = AnimUi.Res("VxHairlineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = _preview };
            DockPanel.SetDock(previewWrap, Dock.Top);
            root.Children.Add(previewWrap);
            root.Children.Add(new ScrollViewer { Content = _body, Padding = new Thickness(16, 14, 16, 16), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            Content = root;

            if (!_fixed) SelectionService.Instance.SelectionChanged += OnSelectionChanged;
            UndoRedoManager.Instance.StateChanged += OnUndoState;
            Opened += (s, e) => { _open = this; AnimUi.FitToScreen(this); };
            Closed += (s, e) =>
            {
                try { if (!_fixed) SelectionService.Instance.SelectionChanged -= OnSelectionChanged; } catch { }
                try { UndoRedoManager.Instance.StateChanged -= OnUndoState; } catch { }
                try { _preview.Dispose(); } catch { }
                if (ReferenceEquals(_open, this)) _open = null;
                SceneRenderService.RuntimeDirty = true;
                EditorCommands.Window?.Inspector?.Refresh();
            };
            Rebuild();
        }

        private static bool InActiveScene(GameEntity e)
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene?.Entities == null || e == null) return false;
            var root = e; while (root.Parent != null) root = root.Parent;
            return scene.Entities.Contains(root);
        }

        private void OnSelectionChanged(object sender, EventArgs e) => Dispatcher.UIThread.Post(() => SetTarget(SelectionService.Instance.SelectedEntity));
        private void OnUndoState(object sender, EventArgs e) => QueueRebuild();

        /// <summary>Edit another entity (scene mode).</summary>
        public void SetTarget(GameEntity e)
        {
            if (ReferenceEquals(e, _ent)) return;
            _ent = e; _sel = null;
            Rebuild();
        }

        private void QueueRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Dispatcher.UIThread.Post(() => { _rebuildQueued = false; if (IsVisible) Rebuild(); });
        }

        private void Changed(bool structural)
        {
            SceneRenderService.RuntimeDirty = true;
            _preview.Refresh();
            if (structural)
            {
                try { TargetModified?.Invoke(); } catch { }
                if (!_fixed) EditorCommands.Window?.Inspector?.Refresh();
                Rebuild();
            }
            else foreach (var r in _refreshers) { try { r(); } catch { } }
        }

        // ===================================================================== body

        private void Rebuild()
        {
            _body.Children.Clear();
            _refreshers.Clear();
            var ent = _ent;
            _entityName.Text = ent != null ? "Entity:  " + ent.Name + (_fixed ? "   ·   isolated prefab" : "") : "No entity selected";
            _preview.SetTarget(ent);
            if (ent == null)
            {
                _body.Children.Add(AnimUi.NoteBox("Select an entity in the Scene hierarchy to give it a collider."));
                _body.Children.Add(HelpBlock());
                return;
            }
            var colliders = ent.GetComponents<Collider>();
            if (_sel == null || !colliders.Contains(_sel)) _sel = colliders.FirstOrDefault();
            _preview.Selected = colliders.Length > 1 ? _sel : null;

            using (CaptureRefreshers(_refreshers))
            {
                // ---- collider list (compound bodies) ----
                if (colliders.Length > 1)
                {
                    _body.Children.Add(AnimUi.MicroHeader("COLLIDERS  (" + colliders.Length + " — a compound body; the character controller uses the first)"));
                    var list = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
                    int i = 0;
                    foreach (var c in colliders)
                    {
                        var cc = c;
                        list.Children.Add(Chip((++i) + ". " + ShapeName(c) + (c.IsTrigger ? " (trigger)" : ""), ReferenceEquals(c, _sel), () => { _sel = cc; Rebuild(); }));
                    }
                    _body.Children.Add(list);
                }

                // ---- collider type row (switches the selected collider's shape) ----
                _body.Children.Add(AnimUi.MicroHeader(colliders.Length == 0 ? "COLLIDER SHAPE" : "COLLIDER SHAPE  (switch keeps centre, trigger and material)"));
                var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
                row.Children.Add(Chip("Box", _sel is BoxCollider, () => SetType<BoxCollider>()));
                row.Children.Add(Chip("Sphere", _sel is SphereCollider, () => SetType<SphereCollider>()));
                row.Children.Add(Chip("Capsule", _sel is CapsuleCollider, () => SetType<CapsuleCollider>()));
                row.Children.Add(Chip("Mesh", _sel is MeshCollider, () => SetType<MeshCollider>()));
                if (_sel != null)
                {
                    row.Children.Add(Chip("Remove", false, () => { var c = _sel; RemoveComp(c); _sel = null; Changed(true); }, danger: true));
                    var add = Chip("+ Add another", false, null);
                    add.Click += (s, e) =>
                    {
                        var m = new MenuFlyout();
                        foreach (var (label, make) in new (string, Func<Collider>)[] { ("Box", () => new BoxCollider(ent)), ("Sphere", () => new SphereCollider(ent)), ("Capsule", () => new CapsuleCollider(ent)), ("Mesh", () => new MeshCollider(ent)) })
                        {
                            var mi = new MenuItem { Header = label + " collider" };
                            mi.Click += (a, b) => { var c = make(); AddComp(c); AutoFit(ent, c); _sel = c; Changed(true); };
                            m.Items.Add(mi);
                        }
                        m.ShowAt(add);
                    };
                    ToolTip.SetTip(add, "Add one more collider (compound rigid body — e.g. a table top + legs)");
                    row.Children.Add(add);
                }
                _body.Children.Add(row);

                if (_sel == null)
                {
                    _body.Children.Add(AnimUi.NoteBox("This entity has no collider — pick a shape above. Box / Sphere / Capsule are fast; Mesh is edge-accurate (uses the object's real triangles, exactly as rendered)."));
                    BuildRigidbodySection(ent);
                    _body.Children.Add(HelpBlock());
                    return;
                }
                var col = _sel;

                // ---- centre + shape ----
                _body.Children.Add(AnimUi.MicroHeader("CENTER (offset from the entity)"));
                _body.Children.Add(Vector3(() => col.Center, v => { col.Center = v; Changed(false); }, 0.05));
                switch (col)
                {
                    case BoxCollider box:
                        _body.Children.Add(AnimUi.MicroHeader("SIZE", 10));
                        _body.Children.Add(Vector3(() => box.Size, v => { box.Size = v; Changed(false); }, 0.05, 0.001f));
                        break;
                    case SphereCollider sph:
                        _body.Children.Add(AnimUi.MicroHeader("RADIUS", 10));
                        _body.Children.Add(Narrow(FloatBox(() => sph.Radius, v => { sph.Radius = v; Changed(false); }, 0.05, 0.001f)));
                        break;
                    case CapsuleCollider cap:
                        _body.Children.Add(AnimUi.MicroHeader("RADIUS", 10));
                        _body.Children.Add(Narrow(FloatBox(() => cap.Radius, v => { cap.Radius = v; Changed(false); }, 0.05, 0.001f)));
                        _body.Children.Add(AnimUi.MicroHeader("HEIGHT (total, caps included)", 10));
                        _body.Children.Add(Narrow(FloatBox(() => cap.Height, v => { cap.Height = v; Changed(false); }, 0.05, 0.001f)));
                        _body.Children.Add(AnimUi.MicroHeader("AXIS", 10));
                        _body.Children.Add(Choice(() => cap.Direction, v => { cap.Direction = v; Changed(false); }, "X axis", "Y axis (upright)", "Z axis"));
                        break;
                    case MeshCollider mesh:
                        {
                            var mr = _ent.GetComponent<MeshRenderer>();
                            bool hasMesh = mr != null && !string.IsNullOrEmpty(mr.MeshPath);
                            if (hasMesh) _body.Children.Add(AnimUi.NoteBox("Edge-accurate: collides against this object's real triangles" + (mr.MeshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) ? " (exact analytic shape for a primitive)." : " — exactly as rendered.")));
                            else _body.Children.Add(Danger("No Mesh Renderer on this entity — this collider produces NO collision. Add it to the child that has the mesh, or use a Box collider."));
                            _body.Children.Add(Row("Convex", Bool(() => mesh.Convex, v => { mesh.Convex = v; Changed(false); }), "Convex hull (required for a dynamic rigid body; faster)"));
                            break;
                        }
                }

                // ---- trigger (recolours the net: amber = trigger, green = solid — here and in the viewport) ----
                _body.Children.Add(new Border { Height = 8 });
                _body.Children.Add(Check("Is Trigger (overlap only — no solid blocking)", () => col.IsTrigger, v => { col.IsTrigger = v; Changed(true); }));

                // ---- physics material ----
                _body.Children.Add(AnimUi.MicroHeader("PHYSICS MATERIAL (rigid bodies)", 12));
                _body.Children.Add(Row("Friction", SliderRow(() => col.Material != null ? col.Material.Friction : 0.5f, v => { Mat(col).Friction = v; Changed(false); }, 0, 1, "0.##", 0f, 5f), "0 = ice … 1 = rubber (default 0.5)"));
                _body.Children.Add(Row("Bounciness", SliderRow(() => col.Material != null ? col.Material.Bounciness : 0f, v => { Mat(col).Bounciness = v; Changed(false); }, 0, 1), "0 = no bounce … 1 = perfectly elastic"));

                BuildColliderScriptSection(_ent, col);

                var fitRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                var fit = new Button { Content = "Auto-fit to mesh", Margin = new Thickness(0, 0, 8, 6) };
                ToolTip.SetTip(fit, "Size and centre this collider to the mesh bounds (unit defaults without a mesh)");
                fit.Click += (s, e) => { AutoFit(_ent, col); Changed(false); };
                fitRow.Children.Add(fit);
                var reset = new Button { Content = "Reset centre", Margin = new Thickness(0, 0, 8, 6) };
                reset.Click += (s, e) => { col.Center = new Editor.ECS.Vector3(0, 0, 0); Changed(false); };
                fitRow.Children.Add(reset);
                _body.Children.Add(fitRow);

                BuildRigidbodySection(_ent);
            }
            _body.Children.Add(HelpBlock());
        }

        private static Control Narrow(Control c) { c.Width = 120; c.HorizontalAlignment = HorizontalAlignment.Left; return c; }

        private static string ShapeName(Collider c) => c is BoxCollider ? "Box" : c is SphereCollider ? "Sphere" : c is CapsuleCollider ? "Capsule" : c is MeshCollider ? "Mesh" : c.ColliderType.ToString();

        private Button Chip(string text, bool active, Action click, bool danger = false)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 7, 7), Padding = new Thickness(13, 5), FontWeight = FontWeight.SemiBold };
            if (active) b.Classes.Add("accent");
            if (danger) b.Foreground = AnimUi.Res("VxRedBrush");
            if (click != null) b.Click += (s, e) => { try { click(); } catch (Exception ex) { EditorCommands.Fail(text, ex); } };
            return b;
        }

        private static PhysicsMaterial Mat(Collider c) { if (c.Material == null) c.Material = new PhysicsMaterial(); return c.Material; }

        // ---- component add / remove honouring the target mode (undoable in the scene, direct on an isolated entity)
        private void AddComp(Component c)
        {
            c.Entity = _ent;
            if (_fixed) _ent.Components.Add(c); else _ent.AddComponent(c);
        }
        private void RemoveComp(Component c)
        {
            if (c == null) return;
            if (_fixed) _ent.Components.Remove(c); else _ent.RemoveComponent(c);
        }

        private void SetType<T>() where T : Collider, new()
        {
            var ent = _ent;
            var existing = _sel;
            if (existing is T) return;
            var c = new T { Entity = ent };
            if (existing != null)
            {
                c.Center = existing.Center; c.IsTrigger = existing.IsTrigger; c.Material = existing.Material;
                RemoveComp(existing);
            }
            AddComp(c);
            AutoFit(ent, c);
            if (existing != null && !(c is MeshCollider)) c.Center = existing.Center;   // the switch keeps the authored centre
            if (c is MeshCollider mc) mc.MeshPath = ent.GetComponent<MeshRenderer>()?.MeshPath;
            _sel = c;
            Changed(true);
        }

        /// <summary>Size + centre a collider to the entity's mesh bounds (local space — the collision service applies the
        /// entity transform); unit defaults when the entity has no measurable mesh.</summary>
        public static void AutoFit(GameEntity ent, Collider c)
        {
            float hx = 0.5f, hy = 0.5f, hz = 0.5f, cx = 0, cy = 0, cz = 0; bool fitted = false;
            var mr = ent?.GetComponent<MeshRenderer>();
            string mp = mr?.MeshPath;
            if (!string.IsNullOrEmpty(mp) && !mp.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var m = Editor.Core.Services.Rendering.PreviewModel.Load(AnimUtil.ModelFile(mp)))
                    {
                        if (m != null)
                        {
                            var mn = new System.Numerics.Vector3(float.MaxValue); var mx = new System.Numerics.Vector3(float.MinValue);
                            int sub = -1; int h = mp.LastIndexOf("#submesh", StringComparison.OrdinalIgnoreCase); if (h > 0) int.TryParse(mp.Substring(h + 8), out sub);
                            for (int i = 0; i < m.Scene.Items.Count; i++)
                            {
                                if (sub >= 0 && i != sub) continue;
                                var it = m.Scene.Items[i];
                                if (!VortexAPI.GetMeshBounds(it.Mesh, out float sx, out float sy, out float sz)) continue;
                                VortexAPI.GetMeshBoundsCenter(it.Mesh, out float bx, out float by, out float bz);
                                var he = new System.Numerics.Vector3(sx, sy, sz) * 0.5f; var ce = new System.Numerics.Vector3(bx, by, bz);
                                mn = System.Numerics.Vector3.Min(mn, ce - he); mx = System.Numerics.Vector3.Max(mx, ce + he); fitted = true;
                            }
                            if (fitted)
                            {
                                var c3 = (mn + mx) * 0.5f; var e3 = (mx - mn) * 0.5f;
                                cx = c3.X; cy = c3.Y; cz = c3.Z; hx = e3.X; hy = e3.Y; hz = e3.Z;
                            }
                        }
                    }
                }
                catch { }
            }
            else if (!string.IsNullOrEmpty(mp))
            {
                // primitives: unit shapes centred on the origin (plane: flat)
                if (mp.EndsWith("Plane", StringComparison.OrdinalIgnoreCase) || mp.EndsWith("Quad", StringComparison.OrdinalIgnoreCase)) hy = 0.01f;
                if (mp.EndsWith("Cylinder", StringComparison.OrdinalIgnoreCase) || mp.EndsWith("Capsule", StringComparison.OrdinalIgnoreCase)) { hx = hz = 0.5f; hy = 0.5f; }
                fitted = true;
            }
            if (!(c is MeshCollider)) c.Center = new Editor.ECS.Vector3(cx, cy, cz);   // a mesh collider IS the mesh: no offset
            switch (c)
            {
                case BoxCollider b: b.Size = new Editor.ECS.Vector3(Math.Max(0.01f, hx * 2), Math.Max(0.01f, hy * 2), Math.Max(0.01f, hz * 2)); break;
                case SphereCollider s: s.Radius = Math.Max(0.01f, Math.Max(hx, Math.Max(hy, hz))); break;
                case CapsuleCollider cp:
                    // along the longest axis
                    int axis = hy >= hx && hy >= hz ? 1 : hx >= hz ? 0 : 2;
                    float len = axis == 0 ? hx : axis == 1 ? hy : hz;
                    float rad = axis == 0 ? Math.Max(hy, hz) : axis == 1 ? Math.Max(hx, hz) : Math.Max(hx, hy);
                    cp.Direction = axis; cp.Radius = Math.Max(0.01f, rad); cp.Height = Math.Max(cp.Radius * 2, len * 2);
                    if (!fitted) { cp.Radius = 0.5f; cp.Height = 2f; cp.Direction = 1; }
                    break;
            }
        }

        // ---- contact script (trigger: OnTriggerEnter/Stay/Exit, solid: OnCollisionEnter)
        private void BuildColliderScriptSection(GameEntity ent, Collider col)
        {
            _body.Children.Add(AnimUi.MicroHeader("SCRIPT — reacts to contact", 14));
            var script = ent.GetComponent<Script>();
            string cur = script?.ScriptClassName;
            string evts = col.IsTrigger ? "OnTriggerEnter / OnTriggerStay / OnTriggerExit" : "OnCollisionEnter (fires when a character touches this solid — e.g. take damage on contact)";
            _body.Children.Add(AnimUi.NoteBox(string.IsNullOrEmpty(cur) ? "Attach a script, then override " + evts + " to react when a character touches this collider." : "Attached: " + cur + "  —  override " + evts + " in it."));
            var row = new WrapPanel();
            var attach = new Button { Content = string.IsNullOrEmpty(cur) ? "Attach Script…" : "Change Script…", Margin = new Thickness(0, 0, 8, 6) };
            attach.Click += (s, e) => ShowScriptPicker(attach);
            row.Children.Add(attach);
            if (script != null)
            {
                var open = new Button { Content = "Open", Margin = new Thickness(0, 0, 8, 6) };
                open.Click += (s, e) => EditorCommands.OpenInIde(AnimUtil.ToAbsolute(script.ScriptPath));
                row.Children.Add(open);
                var rm = new Button { Content = "Remove Script", Margin = new Thickness(0, 0, 8, 6) };
                rm.Click += (s, e) => { RemoveComp(script); Changed(true); };
                row.Children.Add(rm);
            }
            _body.Children.Add(row);
        }

        private void ShowScriptPicker(Control anchor)
        {
            var menu = new MenuFlyout();
            try
            {
                foreach (var rel in ScriptingService.EnumerateScripts())
                {
                    var r = rel;
                    var mi = new MenuItem { Header = Path.GetFileNameWithoutExtension(r) };
                    mi.Click += (s, e) => AttachScript(r);
                    menu.Items.Add(mi);
                }
            }
            catch { }
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var nw = new MenuItem { Header = "New Script…" };
            nw.Click += (s, e) =>
            {
                try
                {
                    var baseName = string.IsNullOrWhiteSpace(_ent?.Name) ? "TriggerBehaviour" : new string(_ent.Name.Where(char.IsLetterOrDigit).ToArray()) + "Trigger";
                    if (baseName.Length == 0 || char.IsDigit(baseName[0])) baseName = "TriggerBehaviour";
                    var abs = ScriptingService.CreateScript(baseName);
                    AttachScript(ScriptingService.MakeRelative(ScriptingService.ProjectRoot, abs));
                    EditorCommands.OpenInIde(abs);
                }
                catch (Exception ex) { EditorCommands.Fail("New script", ex); }
            };
            menu.Items.Add(nw);
            menu.ShowAt(anchor);
        }

        private void AttachScript(string rel)
        {
            if (_ent == null || string.IsNullOrEmpty(rel)) return;
            var existing = _ent.GetComponent<Script>();
            if (existing != null) RemoveComp(existing);
            AddComp(new Script(_ent, rel));
            Changed(true);
        }

        // ---- rigid body
        private void BuildRigidbodySection(GameEntity ent)
        {
            _body.Children.Add(AnimUi.MicroHeader("RIGIDBODY", 16));
            var rb = ent.GetComponent<Rigidbody>();
            if (rb == null)
            {
                _body.Children.Add(new TextBlock { Text = "No Rigidbody: the collider is static level geometry (characters collide with it). Add one to let physics move this object.", Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
                var add = new Button { Content = "Add Rigidbody" };
                add.Click += (s, e) => { AddComp(new Rigidbody(ent)); Changed(true); };
                _body.Children.Add(add);
                return;
            }
            _body.Children.Add(Row("Body type", Enum<RigidbodyType>(() => rb.BodyType, v => { rb.BodyType = v; Changed(false); }), "Dynamic = falls / stacks / gets pushed; Kinematic = you move it and it pushes others; Static = never moves"));
            _body.Children.Add(Row("Mass (kg)", FloatBox(() => rb.Mass, v => { rb.Mass = v; Changed(false); }, 0.1, 0.001f)));
            _body.Children.Add(Row("Drag", FloatBox(() => rb.Drag, v => { rb.Drag = v; Changed(false); }, 0.05, 0f), "Linear damping"));
            _body.Children.Add(Row("Angular drag", FloatBox(() => rb.AngularDrag, v => { rb.AngularDrag = v; Changed(false); }, 0.05, 0f)));
            _body.Children.Add(Row("Use gravity", Bool(() => rb.UseGravity, v => { rb.UseGravity = v; Changed(false); })));
            _body.Children.Add(Row("Interpolation", Enum<RigidbodyInterpolation>(() => rb.Interpolation, v => { rb.Interpolation = v; Changed(false); })));
            _body.Children.Add(Row("Collision", Enum<CollisionDetectionMode>(() => rb.CollisionDetection, v => { rb.CollisionDetection = v; Changed(false); })));
            _body.Children.Add(Row("Freeze position", Axes(() => rb.FreezePositionX, v => rb.FreezePositionX = v, () => rb.FreezePositionY, v => rb.FreezePositionY = v, () => rb.FreezePositionZ, v => rb.FreezePositionZ = v)));
            _body.Children.Add(Row("Freeze rotation", Axes(() => rb.FreezeRotationX, v => rb.FreezeRotationX = v, () => rb.FreezeRotationY, v => rb.FreezeRotationY = v, () => rb.FreezeRotationZ, v => rb.FreezeRotationZ = v)));
            var rm = new Button { Content = "Remove Rigidbody", Margin = new Thickness(LabelWidth + 10, 4, 0, 0) };
            rm.Click += (s, e) => { RemoveComp(rb); Changed(true); };
            _body.Children.Add(rm);
        }

        private Control Axes(Func<bool> gx, Action<bool> sx, Func<bool> gy, Action<bool> sy, Func<bool> gz, Action<bool> sz)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            foreach (var (l, g, s) in new[] { ("X", gx, sx), ("Y", gy, sy), ("Z", gz, sz) })
            {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                cell.Children.Add(Bool(g, v => { s(v); Changed(false); }));
                cell.Children.Add(new TextBlock { Text = l, Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(cell);
            }
            return sp;
        }

        private static Control HelpBlock() => new Border
        {
            Background = AnimUi.Res("VxFieldBrush"), BorderBrush = AnimUi.Res("VxHairlineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11, 9, 11, 10), Margin = new Thickness(0, 16, 0, 0),
            Child = new TextBlock
            {
                Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, LineHeight = 16,
                Text = "How it works: give level objects (ground, walls, props) a collider here. In the game the character capsule collides against them (Vortex.Physics.MoveCharacter) — solid ground, no walking through walls. Box / Sphere / Capsule are cheapest; Mesh is edge-accurate (the object's real triangles). Add a Rigidbody to simulate the object (several colliders form one compound body)."
            }
        };
    }
}
