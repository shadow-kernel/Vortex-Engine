using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Animation;
using Editor.Editors.AnimationEditor;
using Vec3 = System.Numerics.Vector3;

namespace Editor.Editors.SocketEditor
{
    /// <summary>
    /// Bone Socket / Attachment editor: load a skinned CHARACTER (posed by a clip so you tune in a real pose),
    /// load an ATTACHMENT (weapon/accessory), pick the socket BONE, and drag the position/rotation/scale offset
    /// with LIVE 3D preview until it sits perfectly in the hand. Saves a reusable ".vsocket" next to the
    /// attachment; WeaponMount (and any attach) reads it at runtime. The same tool attaches an accessory to a
    /// weapon bone, a weapon to a hand, etc.
    /// </summary>
    public sealed class SocketEditorWindow : Window
    {
        private static SocketEditorWindow _open;

        private readonly AnimationPreviewControl _preview = new AnimationPreviewControl();
        private ComboBox _boneBox;
        private TextBox _px, _py, _pz, _rx, _ry, _rz, _scale;
        private TextBlock _status;
        private string _charPath, _attPath, _posePath;
        private VortexAnimClip _poseClip;
        private Editor.ECS.Components.Animation.BoneAttachment _boundAttachment;   // set = Save writes the component, not a .vsocket
        private System.Windows.Threading.DispatcherTimer _retry;
        private int _retryLeft;

        public static void Open(Window owner, string attachmentPath = null, string characterPath = null)
        {
            if (_open != null) { try { _open.Activate(); } catch { } return; }
            var w = new SocketEditorWindow { Owner = owner };
            _open = w;
            w.Show();
            string proj = Editor.Core.Data.ProjectData.Current?.Path ?? "";
            // Default to PREFABS (everything is a prefab) — resolved to their mesh for the preview.
            string chr = characterPath ?? Combine(proj, "Assets/Prefabs/soldier.ventity");
            string att = attachmentPath ?? Combine(proj, "Assets/Prefabs/npc_gun.ventity");
            try { w.LoadCharacter(chr, null); w.LoadAttachment(att); } catch (Exception ex) { w.SetStatus("Load error: " + ex.Message); }
            w.EnsureMeshesLoaded();
            try { w._preview.Focus(); } catch { }   // so the arrow/IJKL nudge keys work immediately
        }

        /// <summary>Open the editor bound to a BoneAttachment socket (from the prefab editor / inspector): preview
        /// the character + the socket's referenced weapon prefab, tune the bone + offset live, and Save writes
        /// straight back to the component (which the prefab editor then serializes into the .ventity).</summary>
        public static void OpenForAttachment(Window owner, Editor.ECS.Components.Animation.BoneAttachment att)
        {
            if (att == null) { Open(owner); return; }
            if (_open != null) { try { _open.Activate(); } catch { } return; }
            var w = new SocketEditorWindow { Owner = owner };
            _open = w; w.Show();
            w._boundAttachment = att;
            string proj = Editor.Core.Data.ProjectData.Current?.Path ?? "";
            // Character preview: the socket's ACTUAL skeletal target (the character this weapon is really
            // attached to in the scene) so the preview rig == the in-game rig — otherwise the offset is
            // tuned against a different skeleton and the weapon lands wrong (e.g. in the head). Falls back
            // to the default soldier prefab only when the target can't be resolved.
            string chr = ResolveTargetCharacterMesh(att) ?? Combine(proj, "Assets/Prefabs/soldier.ventity");
            // Attachment: the socket's referenced prefab; else the default weapon prefab.
            string attSrc = !string.IsNullOrEmpty(att.SocketPrefabPath)
                          ? Combine(proj, att.SocketPrefabPath)
                          : Combine(proj, "Assets/Prefabs/npc_gun.ventity");
            try { w.LoadCharacter(chr, null); w.LoadAttachment(attSrc); } catch (Exception ex) { w.SetStatus("Load error: " + ex.Message); }
            w.SeedFromComponent(att);
            w.EnsureMeshesLoaded();
            try { w._preview.Focus(); } catch { }
        }

        /// <summary>The mesh file of the character this socket ACTUALLY attaches to in the scene, so the
        /// Socket Editor previews on the same rig the game uses (preview placement == in-game placement).
        /// Null when it can't be resolved (no scene / no skeletal target / no model mesh).</summary>
        private static string ResolveTargetCharacterMesh(Editor.ECS.Components.Animation.BoneAttachment att)
        {
            try
            {
                if (att?.Entity == null) return null;
                var scene = Editor.Core.Services.SceneService.Instance.CurrentScene;
                var target = Editor.Core.Animation.BoneSocketService.Instance.ResolveTargetOf(scene, att.Entity);
                if (target == null) return null;
                string mesh = FindModelMeshInSubtree(target);
                if (string.IsNullOrEmpty(mesh)) return null;
                int h = mesh.IndexOf('#'); if (h > 0) mesh = mesh.Substring(0, h);   // strip #submeshN
                string proj = Editor.Core.Data.ProjectData.Current?.Path;
                return Path.IsPathRooted(mesh) ? mesh
                     : (proj != null ? Path.Combine(proj, mesh.Replace('/', Path.DirectorySeparatorChar)) : mesh);
            }
            catch { return null; }
        }

        private static string FindModelMeshInSubtree(Editor.ECS.GameEntity e)
        {
            if (e == null) return null;
            var mr = e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
            if (mr != null && !string.IsNullOrEmpty(mr.MeshPath))
            {
                var mp = mr.MeshPath.ToLowerInvariant();
                if (mp.Contains(".glb") || mp.Contains(".gltf") || mp.Contains(".fbx")) return mr.MeshPath;
            }
            if (e.Children != null)
                foreach (var c in e.Children) { var m = FindModelMeshInSubtree(c); if (m != null) return m; }
            return null;
        }

        private void SeedFromComponent(Editor.ECS.Components.Animation.BoneAttachment att)
        {
            if (att == null) return;
            if (!string.IsNullOrEmpty(att.BoneName)) SelectBone(att.BoneName);
            _px.Text = G(att.OffsetPosition.X); _py.Text = G(att.OffsetPosition.Y); _pz.Text = G(att.OffsetPosition.Z);
            _rx.Text = G(att.OffsetRotation.X); _ry.Text = G(att.OffsetRotation.Y); _rz.Text = G(att.OffsetRotation.Z);
            float s = att.OffsetScale.X; _scale.Text = G(s <= 0.0001f ? 1f : s);
            Apply();
        }

        /// <summary>The GPU render device (ResourceRegistry) is only initialized once a project VIEWPORT has
        /// rendered a frame — so if this window opens before that (e.g. right after boot), the mesh import
        /// returns nothing and the preview is empty. Re-bind on a short timer until the device is ready. This
        /// is a no-op in the normal case (opened from a loaded project = device already warm = first try wins).</summary>
        private void EnsureMeshesLoaded()
        {
            if (_preview.HasMeshes || string.IsNullOrEmpty(_charPath)) return;
            _retryLeft = 40; // ~20s at 500ms
            _retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _retry.Tick += (s, e) =>
            {
                if (_preview.HasMeshes || _retryLeft-- <= 0) { _retry.Stop(); _retry = null; return; }
                try { LoadCharacter(_charPath, _posePath); if (!string.IsNullOrEmpty(_attPath)) LoadAttachment(_attPath); } catch { }
            };
            _retry.Start();
        }

        private SocketEditorWindow()
        {
            Title = "Socket / Attachment Editor";
            Width = 1280; Height = 820; MinWidth = 980; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Br("#FF161618");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            BuildUI();
            _preview.NormalizeToHuman = true;   // render cm-authored characters at ~human size so weapons are proportional
            _preview.Focusable = true;
            PreviewKeyDown += OnNudgeKey;       // same in-game feel: arrows/PgUp-Dn move, I/K J/L U/O rotate
            Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs++;
            Closed += (s, e) =>
            {
                try { _retry?.Stop(); _retry = null; } catch { }
                try { _preview.Dispose(); } catch { }
                try
                {
                    Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs--;
                    Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit();
                    if (Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs <= 0 &&
                        Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActiveCoexistPreviews <= 0)
                        Editor.Core.Services.Rendering.AssetPreviewRenderer.DestroyPreviewTarget();
                }
                catch { }
                if (ReferenceEquals(_open, this)) _open = null;
            };
        }

        private void BuildUI()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---- left panel ----
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Header("SOCKET / ATTACHMENT EDITOR"));
            panel.Children.Add(Hint("Bring an attachment onto a bone and position it precisely. Click the 3D view, then move with ←→ · PgUp/PgDn · ↑↓ and rotate with I/K · J/L · U/O (or type exact values). Orbit: drag · zoom: wheel. Save writes a .vsocket the game reads — placement here IS placement in-game."));

            panel.Children.Add(SectionLabel("Entity (attach ONTO this)"));
            var charBtn = FlatButton("Choose entity model / prefab…"); charBtn.Click += (s, e) => PickModel(true); panel.Children.Add(charBtn);
            panel.Children.Add(SectionLabel("Attachment"));
            var attBtn = FlatButton("Choose attachment model…"); attBtn.Click += (s, e) => PickModel(false); panel.Children.Add(attBtn);

            panel.Children.Add(SectionLabel("Socket bone"));
            _boneBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8), Height = 26 };
            _boneBox.SelectionChanged += (s, e) => Apply();
            panel.Children.Add(_boneBox);

            panel.Children.Add(SectionLabel("Position (m)"));
            panel.Children.Add(Row3(out _px, out _py, out _pz, 0f, 0f, 0f, 0.005f));
            panel.Children.Add(SectionLabel("Rotation (deg)"));
            panel.Children.Add(Row3(out _rx, out _ry, out _rz, 0f, 0f, 0f, 1f));
            panel.Children.Add(SectionLabel("Scale"));
            _scale = NumBox(1f); _scale.Width = 90; panel.Children.Add(_scale);

            var save = FlatButton("Save .vsocket"); save.Background = Br("#FF2E7D46"); save.Margin = new Thickness(0, 14, 0, 4);
            save.Click += (s, e) => Save(); panel.Children.Add(save);
            _status = Hint(""); panel.Children.Add(_status);

            var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                            Background = Br("#FF1B1B1E") };
            Grid.SetColumn(scroll, 0); root.Children.Add(scroll);

            var host = new Border { Background = Br("#FF0E0E10"), Child = _preview, Margin = new Thickness(0) };
            Grid.SetColumn(host, 1); root.Children.Add(host);
            Content = root;
        }

        // ---- data ----
        // A .ventity PREFAB resolves to its (first) mesh so you can attach prefab-to-prefab; a raw model loads directly.
        private static string ResolveMesh(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".ventity", StringComparison.OrdinalIgnoreCase)) return path;
            try
            {
                string mp = JStr(File.ReadAllText(path), "meshPath");
                if (string.IsNullOrEmpty(mp)) return path;
                int h = mp.IndexOf('#'); if (h > 0) mp = mp.Substring(0, h);        // strip #submeshN
                string proj = Editor.Core.Data.ProjectData.Current?.Path;
                string abs = Path.IsPathRooted(mp) ? mp
                           : (proj != null ? Path.Combine(proj, mp.Replace('/', Path.DirectorySeparatorChar)) : mp);
                return File.Exists(abs) ? abs : path;
            }
            catch { return path; }
        }

        private void LoadCharacter(string path, string posePath)
        {
            path = ResolveMesh(path);
            _charPath = path;
            // pose lives next to the RESOLVED character mesh in an "animations" folder
            _posePath = (!string.IsNullOrEmpty(posePath) && File.Exists(posePath)) ? posePath
                      : Path.Combine(Path.GetDirectoryName(path) ?? "", "animations", "rifle_idle.vanim");
            posePath = _posePath;
            _preview.BindModel(path);
            _poseClip = !string.IsNullOrEmpty(posePath) && File.Exists(posePath) ? VortexAnimClip.Load(posePath) : null;
            _preview.SetPose(_poseClip, 0f, null);
            // populate bones
            _boneBox.Items.Clear();
            var skel = AnimationService.Instance.GetSkeleton(path);
            if (skel != null && skel.Nodes != null)
                foreach (var n in skel.Nodes) if (!string.IsNullOrEmpty(n.Name)) _boneBox.Items.Add(n.Name);
            // default to a hand bone
            SelectBone("mixamorig:RightHand");
            Apply();
            SetStatus("entity: " + Path.GetFileName(path) + "  (" + (skel?.Nodes?.Length ?? 0) + " bones)");
        }

        private void LoadAttachment(string path)
        {
            path = ResolveMesh(path);   // a PREFAB resolves to its mesh
            _attPath = path;
            _preview.BindAttachment(path);
            // seed offset from an existing .vsocket if present (only when NOT bound to a component)
            if (_boundAttachment == null)
            {
                string sc = path + ".vsocket";
                if (File.Exists(sc)) TryLoadSocket(sc);
            }
            Apply();
        }

        private void PickModel(bool character)
        {
            // STA-thread FilePicker — a raw OpenFileDialog deadlocks the DXGI apartment and crashes the editor.
            string proj = Editor.Core.Data.ProjectData.Current?.Path;
            string picked = Editor.Core.Util.FilePicker.OpenFile(
                "Prefabs & Models|*.ventity;*.glb;*.gltf;*.fbx|All files|*.*", "Choose model or prefab", proj ?? "");
            if (string.IsNullOrEmpty(picked)) return;
            if (character) LoadCharacter(picked, null);
            else LoadAttachment(picked);
        }

        private void Apply()
        {
            string bone = _boneBox.SelectedItem as string;
            var pos = new Vec3(F(_px), F(_py), F(_pz));
            var rot = new Vec3(F(_rx), F(_ry), F(_rz));
            float sc = F(_scale); if (sc <= 0.0001f) sc = 1f;
            _preview.SetSocket(bone, pos, rot, sc);
        }

        // In-game-style live nudging: click the 3D view, then arrows/PgUp-Dn MOVE and I/K J/L U/O ROTATE the
        // attachment on the bone — the same feel as the debug tuner, but here in the editor where it belongs.
        // (Typing in a numeric field takes precedence so you can still enter exact values.)
        private void OnNudgeKey(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            const float p = 0.005f, r = 1f;
            switch (e.Key)
            {
                case Key.Left:     Nudge(_px, -p); break;
                case Key.Right:    Nudge(_px, +p); break;
                case Key.PageDown: Nudge(_py, -p); break;
                case Key.PageUp:   Nudge(_py, +p); break;
                case Key.Down:     Nudge(_pz, -p); break;
                case Key.Up:       Nudge(_pz, +p); break;
                case Key.K:        Nudge(_rx, -r); break;
                case Key.I:        Nudge(_rx, +r); break;
                case Key.J:        Nudge(_ry, -r); break;
                case Key.L:        Nudge(_ry, +r); break;
                case Key.U:        Nudge(_rz, -r); break;
                case Key.O:        Nudge(_rz, +r); break;
                default: return;
            }
            e.Handled = true;   // TextChanged -> Apply() repaints the preview live
        }

        private void Nudge(TextBox t, float d)
        {
            if (t == null) return;
            t.Text = (F(t) + d).ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void Save()
        {
            // Bound to a BoneAttachment socket (prefab editor): write bone + offset straight into the component.
            if (_boundAttachment != null)
            {
                float sc = F(_scale); if (sc <= 0.0001f) sc = 1f;
                string b = _boneBox.SelectedItem as string;
                if (!string.IsNullOrEmpty(b)) _boundAttachment.BoneName = b;
                _boundAttachment.OffsetPosition = new Editor.ECS.Vector3(F(_px), F(_py), F(_pz));
                _boundAttachment.OffsetRotation = new Editor.ECS.Vector3(F(_rx), F(_ry), F(_rz));
                _boundAttachment.OffsetScale = new Editor.ECS.Vector3(sc, sc, sc);
                SetStatus("saved to Bone Attachment — save the prefab to keep it");
                return;
            }
            if (string.IsNullOrEmpty(_attPath)) { SetStatus("no attachment loaded"); return; }
            string bone = _boneBox.SelectedItem as string ?? "";
            string json = "{\n" +
                "  \"bone\": \"" + bone + "\",\n" +
                "  \"posX\": " + G(F(_px)) + ", \"posY\": " + G(F(_py)) + ", \"posZ\": " + G(F(_pz)) + ",\n" +
                "  \"rotX\": " + G(F(_rx)) + ", \"rotY\": " + G(F(_ry)) + ", \"rotZ\": " + G(F(_rz)) + ",\n" +
                "  \"scale\": " + G(F(_scale)) + "\n}\n";
            try { File.WriteAllText(_attPath + ".vsocket", json); SetStatus("saved " + Path.GetFileName(_attPath) + ".vsocket"); }
            catch (Exception ex) { SetStatus("save failed: " + ex.Message); }
        }

        private void TryLoadSocket(string path)
        {
            try
            {
                string t = File.ReadAllText(path);
                _px.Text = J(t, "posX"); _py.Text = J(t, "posY"); _pz.Text = J(t, "posZ");
                _rx.Text = J(t, "rotX"); _ry.Text = J(t, "rotY"); _rz.Text = J(t, "rotZ");
                _scale.Text = J(t, "scale");
                string b = JStr(t, "bone"); if (!string.IsNullOrEmpty(b)) SelectBone(b);
            }
            catch { }
        }

        // ---- tiny helpers ----
        private void SelectBone(string b) { foreach (var it in _boneBox.Items) if ((string)it == b) { _boneBox.SelectedItem = it; return; } if (_boneBox.Items.Count > 0) _boneBox.SelectedIndex = 0; }
        private void SetStatus(string s) { if (_status != null) _status.Text = s; }
        private static string Combine(string a, string b) => string.IsNullOrEmpty(a) ? b : Path.Combine(a, b.Replace('/', Path.DirectorySeparatorChar));
        private static float F(TextBox t) { return t != null && float.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f; }
        private static string G(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static string J(string json, string key) { float v = JNum(json, key); return v.ToString("0.###", CultureInfo.InvariantCulture); }
        private static float JNum(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\""); if (i < 0) return 0f; i = json.IndexOf(':', i); if (i < 0) return 0f;
            int j = i + 1; while (j < json.Length && (json[j] == ' ')) j++;
            int k = j; while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '-' || json[k] == '.' || json[k] == '+' || json[k] == 'e' || json[k] == 'E')) k++;
            return float.TryParse(json.Substring(j, k - j), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        }
        private static string JStr(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\""); if (i < 0) return null; i = json.IndexOf(':', i); if (i < 0) return null;
            int q1 = json.IndexOf('"', i + 1); if (q1 < 0) return null; int q2 = json.IndexOf('"', q1 + 1); if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private Grid Row3(out TextBox a, out TextBox b, out TextBox c, float da, float db, float dc, float step)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 8) };
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            a = NumBox(da); b = NumBox(db); c = NumBox(dc);
            Grid.SetColumn(a, 0); Grid.SetColumn(b, 1); Grid.SetColumn(c, 2);
            g.Children.Add(a); g.Children.Add(b); g.Children.Add(c);
            return g;
        }
        private TextBox NumBox(float v)
        {
            var t = new TextBox { Text = v.ToString("0.###", CultureInfo.InvariantCulture), Margin = new Thickness(2),
                                  Background = Br("#FF232327"), Foreground = Br("#FFE6E6EA"), BorderBrush = Br("#FF3A3A40"),
                                  Padding = new Thickness(4, 3, 4, 3) };
            t.TextChanged += (s, e) => Apply();
            return t;
        }
        private Button FlatButton(string text) => new Button { Content = text, Margin = new Thickness(0, 2, 0, 6), Height = 28,
            Background = Br("#FF2A2A30"), Foreground = Br("#FFE6E6EA"), BorderBrush = Br("#FF3A3A40") };
        private TextBlock Header(string t) => new TextBlock { Text = t, FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = Br("#FF8FD6A6"), Margin = new Thickness(0, 0, 0, 6) };
        private TextBlock SectionLabel(string t) => new TextBlock { Text = t, FontSize = 11.5, Foreground = Br("#FF9AA0AC"),
            Margin = new Thickness(0, 8, 0, 2) };
        private TextBlock Hint(string t) => new TextBlock { Text = t, FontSize = 11, Foreground = Br("#FF7C818C"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        private static SolidColorBrush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }
}
