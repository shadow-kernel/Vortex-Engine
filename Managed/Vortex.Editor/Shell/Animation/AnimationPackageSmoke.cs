using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using VortexEditor.Panels.Inspector;
using VortexEditor.Shell.Physics;

namespace VortexEditor.Shell.Animation
{
    /// <summary>Smoke checks of the Animation / Socket / Collision editors and the inspector cards (full smoke run).</summary>
    internal static class AnimationPackageSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("keyframe editor: skinned preview plays (frames differ), key + undo, capture", KeyframeEditorCheck);
            SmokeRegistry.Add("socket editor: posed character + attachment, nudge re-renders, save, capture", SocketEditorCheck);
            SmokeRegistry.Add("collision editor: object + collider wire net, trigger recolours, capture", CollisionEditorCheck);
            SmokeRegistry.Add("inspector cards: every component of the scene + all component types build", InspectorCardsCheck);
            SmokeRegistry.Add("collision editor: shape switch / trigger / rigidbody on an isolated template (no undo entries)", CollisionIsolatedCheck);
            SmokeRegistry.Add("socket editor: model asset mode writes a .vsocket", SocketAssetModeCheck);
        }

        // ------------------------------------------------------------------------------------------ collision: UI flow
        private static async Task<bool> CollisionIsolatedCheck()
        {
            var scene = ProjectData.Current?.ActiveScene;
            // an entity OUTSIDE the scene (like the Prefab Editor's template): structural edits must stay off the undo stack
            var ent = new GameEntity(scene, "SmokeTemplate");
            ent.Components.Add(new MeshRenderer(ent) { MeshPath = "Primitive:Cube" });
            ent.Components.Add(new BoxCollider(ent));
            CollisionEditorWindow.Open(ent);
            bool opened = await WaitFor(() => CollisionEditorWindow.Current != null && CollisionEditorWindow.Current.IsVisible && ReferenceEquals(CollisionEditorWindow.Current.Target, ent));
            var w = CollisionEditorWindow.Current;
            if (!opened || w == null) { Log("isolated: window did not open"); return false; }
            try
            {
                await SmokeRegistry.Settle(300);
                int structural = 0;
                EventHandler<CommandExecutedEventArgs> count = (s, e) => { if (e.Command?.Name?.IndexOf("Components", StringComparison.OrdinalIgnoreCase) >= 0) structural++; };
                UndoRedoManager.Instance.CommandExecuted += count;
                Click(FindButton(w, "Sphere"));
                bool sphere = ent.GetComponent<Collider>() is SphereCollider sc && sc.Radius > 0.4f;
                Click(FindButton(w, "Capsule"));
                var cap = ent.GetComponent<Collider>() as CapsuleCollider;
                bool capsule = cap != null && cap.Height >= cap.Radius * 2;
                var trig = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<CheckBox>().FirstOrDefault(c => (c.Content as TextBlock)?.Text?.StartsWith("Is Trigger") == true);
                if (trig != null) trig.IsChecked = true;
                bool trigger = cap != null && cap.IsTrigger;
                Click(FindButton(w, "Add Rigidbody"));
                bool rb = ent.GetComponent<Rigidbody>() != null;
                Click(FindButton(w, "Remove Rigidbody"));
                bool rbGone = ent.GetComponent<Rigidbody>() == null;
                UndoRedoManager.Instance.CommandExecuted -= count;
                bool rendered = await WaitFor(() => w.Preview.Viewport.LastImage != null && HasContent(w.Preview.Viewport.LastImage), 2000);
                Log("isolated template: sphere=" + sphere + " capsule=" + capsule + " trigger=" + trigger + " rigidbody add/remove=" + rb + "/" + rbGone + " structural undo entries=" + structural + " rendered=" + rendered);
                await SmokeRegistry.Settle(300);
                SmokeRegistry.Capture(w, "collision_editor_capsule.png");
                return sphere && capsule && trigger && rb && rbGone && structural == 0 && rendered;
            }
            finally { try { w.Close(); } catch { } await SmokeRegistry.Settle(200); }
        }

        // ------------------------------------------------------------------------------------------ socket: asset mode
        private static async Task<bool> SocketAssetModeCheck()
        {
            string root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return false;
            string att = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.glb", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(f).StartsWith("vm_vityaz", StringComparison.OrdinalIgnoreCase))
                      ?? Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.glb", SearchOption.AllDirectories).FirstOrDefault();
            if (att == null) return false;
            string socketFile = att + ".vsocket";
            bool existed = File.Exists(socketFile);
            string backup = existed ? File.ReadAllText(socketFile) : null;
            SocketEditorWindow.OpenForModel(att);
            bool opened = await WaitFor(() => SocketEditorWindow.Current != null && SocketEditorWindow.Current.IsVisible && SocketEditorWindow.Current.BoundEntity == null);
            var w = SocketEditorWindow.Current;
            if (!opened || w == null) { Log("asset mode: window did not open"); return false; }
            try
            {
                bool rendered = await WaitFor(() => w.Preview.Viewport.LastImage != null && HasContent(w.Preview.Viewport.LastImage));
                w.Nudge(new System.Numerics.Vector3(0.01f, 0, 0), System.Numerics.Vector3.Zero);
                bool saved = w.Save() && File.Exists(socketFile);
                string json = saved ? File.ReadAllText(socketFile) : "";
                bool ok = rendered && saved && json.Contains("\"bone\"") && json.Contains("\"posX\"");
                Log("asset mode on " + Path.GetFileName(att) + ": rendered=" + rendered + " saved=" + saved + " bone=" + w.BoneName);
                return ok;
            }
            finally
            {
                try { w.Close(); } catch { }
                try { if (existed) File.WriteAllText(socketFile, backup); else if (File.Exists(socketFile)) File.Delete(socketFile); } catch { }
                await SmokeRegistry.Settle(200);
            }
        }

        private static void Log(string s) => ConsoleService.Instance.Log("[anim-pkg smoke] " + s);

        // ---- UI drivers: real routed events on the real controls
        private static void RaiseKey(Window w, Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods = Avalonia.Input.KeyModifiers.None)
            => w.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = w });

        private static Button FindButton(Visual root, string text)
            => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Button>()
                .FirstOrDefault(b => (b.Content as string) == text || (b.Content as TextBlock)?.Text == text);

        private static void Click(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        private static async Task<bool> WaitFor(Func<bool> cond, int ms = 5000)
        {
            for (int t = 0; t < ms; t += 50) { if (cond()) return true; await Task.Delay(50); }
            return cond();
        }

        // ------------------------------------------------------------------------------------------ keyframe editor
        private static async Task<bool> KeyframeEditorCheck()
        {
            string root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return false;
            string clip = Path.Combine(root, "Assets", "Models", "Character", "animations", "rifle_idle.vanim");
            if (!File.Exists(clip)) clip = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.vanim", SearchOption.AllDirectories).FirstOrDefault();
            if (clip == null) { Log("no .vanim in the project"); return false; }

            AnimationEditorWindow.Open(clip);
            bool opened = await WaitFor(() => AnimationEditorWindow.Current != null && AnimationEditorWindow.Current.IsVisible);
            var w = AnimationEditorWindow.Current;
            if (!opened || w == null) { Log("window did not open"); return false; }
            try
            {
                bool rendered = await WaitFor(() => w.Preview.Viewport.LastImage != null && HasContent(w.Preview.Viewport.LastImage));
                var skel = w.Preview.Skeleton;
                bool skeletonOk = skel != null && skel.IsValid;
                int rows = w.Timeline.RowCount;
                Log("opened " + Path.GetFileName(clip) + ": rendered=" + rendered + " skeleton=" + (skel?.Nodes?.Length ?? 0) + " nodes, tracks=" + w.Document.Tracks.Count + " timeline rows=" + rows);
                if (!rendered || !skeletonOk || rows == 0) return false;

                // play a few frames: the pose must change the rendered pixels
                var a = w.Preview.Viewport.LastImage;
                int renders0 = w.Preview.RenderCount;
                w.SetPlaying(true);
                await Task.Delay(1500);
                var b = w.Preview.Viewport.LastImage;
                float t = w.PlayheadTime;
                w.SetPlaying(false);
                int renders = w.Preview.RenderCount - renders0;
                int diff = DiffPixels(a, b);
                Log("playback: " + renders + " renders, playhead " + t.ToString("0.00") + " s, changed pixels " + diff);
                bool played = renders >= 2 && b != null && !ReferenceEquals(a, b) && diff > 20 && t > 0.3f;

                // Space toggles playback (window key handling, like the Windows editor)
                RaiseKey(w, Avalonia.Input.Key.Space);
                bool spacePlays = w.IsPlaying;
                RaiseKey(w, Avalonia.Input.Key.Space);
                bool spaceStops = !w.IsPlaying;
                Log("space: play=" + spacePlays + " stop=" + spaceStops);

                // pose an UNKEYED bone like a joint drag (30° local), then Key Bone: a new track holding exactly that pose;
                // Cmd+Z removes the track again
                string bone = skel.Nodes.Select(n => n.Name).FirstOrDefault(n => n.EndsWith("LeftHandIndex4", StringComparison.OrdinalIgnoreCase) && w.Document.FindTrack(n) == null)
                           ?? skel.Nodes.Select(n => n.Name).FirstOrDefault(n => !AnimUtil.IsHiddenNode(n) && w.Document.FindTrack(n) == null && skel.Nodes[skel.FindNode(n)].Parent >= 0);
                bool keyed = false;
                if (bone != null)
                {
                    w.SelectBone(bone);
                    w.SetTime(0.5f);
                    await SmokeRegistry.Settle(250);
                    var beforePose = w.Preview.Viewport.LastImage;
                    int tracks0 = w.Document.Tracks.Count;
                    var delta = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, (float)(Math.PI / 6));
                    w.ApplyBoneRotation(bone, delta);
                    bool overridden = w.HasPoseOverride;
                    await WaitFor(() => !ReferenceEquals(w.Preview.Viewport.LastImage, beforePose), 2000);
                    w.KeyBoneAt(bone, 0.5f, useOverride: true);
                    var tr = w.Document.FindTrack(bone);
                    var node = skel.Nodes[skel.FindNode(bone)];
                    var expected = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Concatenate(node.BindRotation, delta));
                    var k = tr?.Rot.FirstOrDefault();
                    float dot = k == null ? 0 : Math.Abs(k.X * expected.X + k.Y * expected.Y + k.Z * expected.Z + k.W * expected.W);
                    bool created = tr != null && w.Document.Tracks.Count == tracks0 + 1 && dot > 0.999f;
                    RaiseKey(w, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Meta);   // Cmd+Z
                    bool undone = w.Document.FindTrack(bone) == null && w.Document.Tracks.Count == tracks0;
                    Log("pose " + AnimUtil.DisplayBoneName(bone) + ": override=" + overridden + " keyed track=" + created + " (quat dot " + dot.ToString("0.0000") + ") undo=" + undone + " dirty=" + w.IsDirty);
                    keyed = overridden && created && undone && w.IsDirty;
                }

                // events: the inspector's "+ Add Event @ Playhead" button, then undo
                int ev0 = w.Document.Events.Count;
                var addEvent = FindButton(w, "+ Add Event @ Playhead");
                if (addEvent != null) Click(addEvent);
                int ev1 = w.Document.Events.Count;
                UndoRedoManager.Instance.Undo();
                bool events = addEvent != null && ev1 == ev0 + 1 && w.Document.Events.Count == ev0;
                Log("event button: " + ev0 + " -> " + ev1 + " -> undo " + w.Document.Events.Count);

                // import an embedded clip from the bound model (when it has any), then undo restores the tracks
                bool imported = true;
                string model = AnimUtil.ToAbsolute(w.Document.Model);
                int embedded = 0;
                try { embedded = File.Exists(model) ? Editor.DllWrapper.VortexAPI.GetAnimationCount(model) : 0; } catch { }
                if (embedded > 0 && Editor.DllWrapper.VortexAPI.GetAnimationInfo(model, 0, out string en, out float ed))
                {
                    var oldTracks = w.Document.Tracks;
                    await w.ImportEmbedded(model, 0, en, ed, confirm: false);
                    bool replaced = !ReferenceEquals(w.Document.Tracks, oldTracks) && w.Document.Tracks.Count > 0;
                    UndoRedoManager.Instance.Undo();
                    imported = replaced && ReferenceEquals(w.Document.Tracks, oldTracks);
                    Log("import embedded \"" + en + "\" (" + embedded + " in model): replaced=" + replaced + " undo restored=" + ReferenceEquals(w.Document.Tracks, oldTracks));
                }
                else Log("bound model has no embedded clips — import skipped");

                // selected bone + overlay for the capture
                w.SelectBone(skel.Nodes.Select(n => n.Name).FirstOrDefault(n => n.EndsWith("LeftForeArm", StringComparison.OrdinalIgnoreCase)) ?? bone);
                w.SetTime(w.Document.DurationSec * 0.4f);
                await SmokeRegistry.Settle(700);
                SmokeRegistry.Capture(w, "anim_editor.png");
                return played && spacePlays && spaceStops && keyed && events && imported;
            }
            finally { try { w.CloseDiscarding(); } catch { } await SmokeRegistry.Settle(300); }
        }

        // ------------------------------------------------------------------------------------------ socket editor
        private static async Task<bool> SocketEditorCheck()
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) return false;
            // a real socket of the scene if there is one; else a temp attachment targeting an animated character by id
            GameEntity target = AnimUtil.AllEntities(scene).FirstOrDefault(e => e.GetComponent<Animator>() != null && AnimUtil.FindModelMeshInSubtree(e) != null);
            GameEntity ent = AnimUtil.AllEntities(scene).FirstOrDefault(e => e.GetComponent<BoneAttachment>() != null && BoneSocketService.Instance.ResolveTargetOf(scene, e) != null);
            bool temp = false;
            if (ent == null)
            {
                if (target == null) { Log("no animated character in the scene"); return false; }
                ent = new GameEntity(scene, "SmokeSocket");
                var ba = new BoneAttachment(ent) { TargetEntityId = target.Id.ToString() };
                ent.Components.Add(ba);
                temp = true;
            }
            var socketBefore = Snapshot(ent.GetComponent<BoneAttachment>());
            SocketEditorWindow.Open(ent);
            bool opened = await WaitFor(() => SocketEditorWindow.Current != null && SocketEditorWindow.Current.IsVisible);
            var w = SocketEditorWindow.Current;
            if (!opened || w == null) { Log("window did not open"); return false; }
            try
            {
                bool rendered = await WaitFor(() => w.Preview.Viewport.LastImage != null && HasContent(w.Preview.Viewport.LastImage));
                Log("socket on " + ent.Name + ": character " + Path.GetFileName(w.Preview.ModelPath ?? "-") + " (" + (w.Preview.Skeleton?.Nodes?.Length ?? 0) + " nodes), attachment " + Path.GetFileName(w.Preview.AttachmentPath ?? "-") + ", bone " + w.BoneName + ", rendered=" + rendered);
                if (!rendered || !w.Preview.HasMeshes || w.Preview.Skeleton == null || !w.Preview.HasAttachment || string.IsNullOrEmpty(w.BoneName)) return false;
                await SmokeRegistry.Settle(300);
                var a = w.Preview.Viewport.LastImage;
                w.Nudge(new System.Numerics.Vector3(0.05f, 0.03f, 0), new System.Numerics.Vector3(0, 25, 0));
                bool moved = await WaitFor(() => !ReferenceEquals(w.Preview.Viewport.LastImage, a), 2000);
                int diff = DiffPixels(a, w.Preview.Viewport.LastImage);
                Log("nudge re-rendered=" + moved + " changed pixels " + diff);
                await SmokeRegistry.Settle(500);
                SmokeRegistry.Capture(w, "socket_editor.png");
                bool saved = w.Save();
                var att = ent.GetComponent<BoneAttachment>();
                bool written = saved && att != null && att.BoneName == w.BoneName;
                Log("save -> component bone " + att?.BoneName + " offset " + att?.OffsetPosition.X.ToString("0.###"));
                return moved && diff > 20 && written;
            }
            finally
            {
                try { w.Close(); } catch { }
                // leave the scene as it was: restore the authored socket (the smoke save is undoable but explicit is safer)
                if (!temp) Restore(ent.GetComponent<BoneAttachment>(), socketBefore);
                await SmokeRegistry.Settle(300);
            }
        }

        private static object[] Snapshot(BoneAttachment b) => b == null ? null : new object[] { b.BoneName, b.OffsetPosition, b.OffsetRotation, b.OffsetScale, b.SocketRenderLayer };
        private static void Restore(BoneAttachment b, object[] s)
        {
            if (b == null || s == null) return;
            b.BoneName = (string)s[0]; b.OffsetPosition = (Editor.ECS.Vector3)s[1]; b.OffsetRotation = (Editor.ECS.Vector3)s[2]; b.OffsetScale = (Editor.ECS.Vector3)s[3]; b.SocketRenderLayer = (int)s[4];
        }

        // ------------------------------------------------------------------------------------------ collision editor
        private static async Task<bool> CollisionEditorCheck()
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) return false;
            var ent = AnimUtil.AllEntities(scene).FirstOrDefault(e => e.GetComponent<Collider>() != null && e.GetComponent<MeshRenderer>() != null && !(e.GetComponent<Collider>() is MeshCollider));
            bool temp = false;
            if (ent == null)
            {
                ent = new GameEntity(scene, "SmokeCollider");
                ent.Components.Add(new MeshRenderer(ent) { MeshPath = "Primitive:Cube" });
                ent.Components.Add(new BoxCollider(ent) { Size = new Editor.ECS.Vector3(1.2f, 1.2f, 1.2f) });
                temp = true;
            }
            var col = ent.GetComponent<Collider>();
            bool trigBefore = col.IsTrigger;
            CollisionEditorWindow.Open(ent);
            bool opened = await WaitFor(() => CollisionEditorWindow.Current != null && CollisionEditorWindow.Current.IsVisible);
            var w = CollisionEditorWindow.Current;
            if (!opened || w == null) { Log("window did not open"); return false; }
            try
            {
                bool rendered = await WaitFor(() => w.Preview.Viewport.LastImage != null && HasContent(w.Preview.Viewport.LastImage));
                await SmokeRegistry.Settle(400);
                var img = w.Preview.Viewport.LastImage;
                int green = CountColor(img, (r, g, b) => g > 150 && g > r + 50 && g > b + 30);
                Log("collider on " + ent.Name + " (" + col.GetType().Name + "): rendered=" + rendered + " green net pixels " + green + (temp ? " (temp entity)" : ""));
                SmokeRegistry.Capture(w, "collision_editor.png");
                // trigger toggle: the net turns amber
                col.IsTrigger = !trigBefore;
                w.Preview.Refresh();
                bool rerendered = await WaitFor(() => !ReferenceEquals(w.Preview.Viewport.LastImage, img), 2000);
                var img2 = w.Preview.Viewport.LastImage;
                // thin blended wires: amber reads roughly (186,165,92), green (123,184,150)
                int amber = CountColor(img2, (r, g, b) => r > 150 && r > g + 8 && g > b + 40);
                col.IsTrigger = trigBefore;
                Log("trigger toggle re-rendered=" + rerendered + " amber pixels " + amber + " changed " + DiffPixels(img, img2) + " sample " + ChangedSample(img, img2));
                bool ok = rendered && green > 30 && rerendered && (trigBefore ? green > 30 : amber > 30);
                if (ok && trigBefore) ok = CountColor(img2, (r, g, b) => g > 150 && g > r + 50 && g > b + 30) > 30;
                return ok;
            }
            finally { try { w.Close(); } catch { } if (!temp) col.IsTrigger = trigBefore; await SmokeRegistry.Settle(300); }
        }

        // ------------------------------------------------------------------------------------------ inspector cards
        private static async Task<bool> InspectorCardsCheck()
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) return false;
            int cards = 0, failures = 0;
            var types = new HashSet<string>();
            foreach (var e in AnimUtil.AllEntities(scene).ToList())
                foreach (var c in e.Components.ToList())
                {
                    try { List<Control> rows; using (PropertyRows.CaptureRefreshers(null)) rows = ComponentEditors.Build(c, e).ToList(); cards++; types.Add(c.GetType().Name); if (rows.Count == 0) Log("empty card " + c.GetType().Name + " on " + e.Name); }
                    catch (Exception ex) { failures++; Log("card " + c.GetType().Name + " on " + e.Name + " threw " + ex.GetType().Name + ": " + ex.Message); }
                }
            Log("scene cards built: " + cards + " (" + string.Join(", ", types.OrderBy(x => x)) + "), failures " + failures);

            // every component type on one isolated entity (not in the scene), hosted in windows so styles + Loaded run
            var target = AnimUtil.AllEntities(scene).FirstOrDefault(x => x.GetComponent<Animator>() != null);
            var ent = new GameEntity(scene, "SmokeCards");
            var script = ProjectScript();
            var comps = new List<Component>
            {
                new MeshRenderer(ent) { MeshPath = "Primitive:Cube" }, new Camera(ent), new Light(ent) { LightType = LightType.Spot }, new Skybox(ent),
                new Script(ent, script ?? "Scripts/Missing.cs"), new BoxCollider(ent), new SphereCollider(ent), new CapsuleCollider(ent), new MeshCollider(ent),
                new Rigidbody(ent), new AudioSource(ent), new AudioListener(ent), new ReverbZone(ent),
                new Animator(ent), new BoneAttachment(ent) { TargetEntityId = target?.Id.ToString() ?? "", BoneName = "mixamorig:RightHand" }, new TwoBoneIk(ent) { TipBone = "mixamorig:LeftHand", TargetBone = "mixamorig:RightHand" }, new HandPose(ent)
            };
            foreach (var c in comps) ent.Components.Add(c);
            var groups = new[]
            {
                new[] { typeof(Transform), typeof(MeshRenderer), typeof(Light) },
                new[] { typeof(Camera), typeof(Skybox), typeof(Script) },
                new[] { typeof(BoxCollider), typeof(CapsuleCollider), typeof(Rigidbody), typeof(AudioSource) },
                new[] { typeof(Animator), typeof(BoneAttachment), typeof(TwoBoneIk) },
            };
            // the Two-Bone IK / Animator cards read the owner's skeleton: build those on the real character when present
            var animOwner = target ?? ent;
            int gi = 0;
            foreach (var g in groups)
            {
                gi++;
                var windowRefreshers = new List<Action>();
                var stack = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
                foreach (var t in g)
                {
                    Component c = t == typeof(Transform) ? (Component)ent.Transform : ent.Components.FirstOrDefault(x => x.GetType() == t);
                    var host = (t == typeof(Animator) || t == typeof(TwoBoneIk)) && target != null ? (Component)target.Components.FirstOrDefault(x => x.GetType() == t) ?? c : c;
                    if (host == null) continue;
                    var card = new Border { Classes = { "card" }, Padding = new Thickness(10) };
                    var body = new StackPanel();
                    body.Children.Add(new TextBlock { Text = host.DisplayName + (ReferenceEquals(host.Entity, ent) ? "" : "  (on " + host.Entity?.Name + ")"), FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
                    try { using (PropertyRows.CaptureRefreshers(windowRefreshers)) foreach (var row in ComponentEditors.Build(host, host.Entity)) body.Children.Add(row); cards++; types.Add(t.Name); }
                    catch (Exception ex) { failures++; Log("synthetic card " + t.Name + " threw " + ex.GetType().Name + ": " + ex.Message); }
                    card.Child = body;
                    stack.Children.Add(card);
                }
                var win = new Window { Title = "Inspector cards " + gi, Width = 460, Height = 980, Content = new ScrollViewer { Content = stack } };
                EditorWindows.Show(win);
                await SmokeRegistry.Settle(700);
                SmokeRegistry.Capture(win, "inspector_cards_" + gi + ".png");
                foreach (var r in windowRefreshers) { try { r(); } catch { } }   // the cards re-read their values without errors
                win.Close();
            }
            // HandPose + the remaining shapes build without a window
            foreach (var c in comps)
            {
                try { using (PropertyRows.CaptureRefreshers(null)) ComponentEditors.Build(c, ent).ToList(); types.Add(c.GetType().Name); }
                catch (Exception ex) { failures++; Log("synthetic card " + c.GetType().Name + " threw " + ex.GetType().Name + ": " + ex.Message); }
            }
            try { EditorCommands.Window?.Inspector?.Refresh(); } catch { }
            Log("all cards: " + cards + " windows built, component types " + types.Count + ", failures " + failures);
            return failures == 0 && cards > 0;
        }

        private static string ProjectScript()
        {
            try
            {
                foreach (var rel in ScriptingService.EnumerateScripts())
                    if (rel.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0 || rel.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0) return rel;
                return ScriptingService.EnumerateScripts().FirstOrDefault();
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------------------------------ pixels
        internal static bool HasContent(PreviewImage img)
        {
            if (img == null) return false;
            var seen = new HashSet<int>();
            for (int y = 0; y < img.Height; y += Math.Max(1, img.Height / 24))
                for (int x = 0; x < img.Width; x += Math.Max(1, img.Width / 24))
                {
                    int o = y * img.Stride + x * 4;
                    seen.Add((img.Bgra[o] >> 3) | ((img.Bgra[o + 1] >> 3) << 5) | ((img.Bgra[o + 2] >> 3) << 10));
                }
            return seen.Count > 3;
        }

        internal static int DiffPixels(PreviewImage a, PreviewImage b)
        {
            if (a == null || b == null || a.Width != b.Width || a.Height != b.Height) return a != b ? int.MaxValue : 0;
            int n = 0;
            for (int i = 0; i + 3 < a.Bgra.Length; i += 4)
                if (Math.Abs(a.Bgra[i] - b.Bgra[i]) + Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) + Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]) > 24) n++;
            return n;
        }

        private static string ChangedSample(PreviewImage a, PreviewImage b)
        {
            if (a == null || b == null || a.Width != b.Width || a.Height != b.Height) return "(size differs)";
            var sb = new System.Text.StringBuilder();
            int n = 0;
            for (int i = 0; i + 3 < a.Bgra.Length && n < 6; i += 4 * 7)
                if (Math.Abs(a.Bgra[i] - b.Bgra[i]) + Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) + Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]) > 40)
                { sb.Append($"[{a.Bgra[i + 2]},{a.Bgra[i + 1]},{a.Bgra[i]}->{b.Bgra[i + 2]},{b.Bgra[i + 1]},{b.Bgra[i]}] "); n++; }
            return sb.ToString();
        }

        private static int CountColor(PreviewImage img, Func<int, int, int, bool> pred)
        {
            if (img == null) return 0;
            int n = 0;
            for (int i = 0; i + 3 < img.Bgra.Length; i += 4) if (pred(img.Bgra[i + 2], img.Bgra[i + 1], img.Bgra[i])) n++;
            return n;
        }
    }
}
