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
        }

        private static void Log(string s) => ConsoleService.Instance.Log("[anim-pkg smoke] " + s);

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

                // keyframe authoring through the undo system
                string bone = skel.Nodes.Select(n => n.Name).FirstOrDefault(n => n.EndsWith("LeftForeArm", StringComparison.OrdinalIgnoreCase)) ?? w.Document.Tracks[0].Bone;
                w.SelectBone(bone);
                w.SetTime(0.5f);
                var track = w.Document.FindTrack(bone);
                int before = track?.Rot.Count ?? 0;
                w.KeyBoneAt(bone, 0.5f, false);
                track = w.Document.FindTrack(bone);
                int afterKey = track?.Rot.Count ?? 0;
                bool dirty = w.IsDirty;
                UndoRedoManager.Instance.Undo();
                track = w.Document.FindTrack(bone);
                int afterUndo = track?.Rot.Count ?? 0;
                Log("key " + AnimUtil.DisplayBoneName(bone) + ": rot keys " + before + " -> " + afterKey + " -> undo " + afterUndo + ", dirty=" + dirty);
                bool keyed = dirty && afterUndo == before && (afterKey >= before);

                // pose the selected bone like a joint drag, show the overlay for the capture
                w.SetTime(w.Document.DurationSec * 0.4f);
                await SmokeRegistry.Settle(700);
                SmokeRegistry.Capture(w, "anim_editor.png");
                return played && keyed;
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
                    try { var rows = ComponentEditors.Build(c, e).ToList(); cards++; types.Add(c.GetType().Name); if (rows.Count == 0) Log("empty card " + c.GetType().Name + " on " + e.Name); }
                    catch (Exception ex) { failures++; Log("card " + c.GetType().Name + " on " + e.Name + " threw " + ex.GetType().Name + ": " + ex.Message); }
                }
            Log("scene cards built: " + cards + " (" + string.Join(", ", types.OrderBy(x => x)) + "), failures " + failures);
            PropertyRows.ClearRefreshers();

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
                var stack = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
                foreach (var t in g)
                {
                    Component c = t == typeof(Transform) ? (Component)ent.Transform : ent.Components.FirstOrDefault(x => x.GetType() == t);
                    var host = (t == typeof(Animator) || t == typeof(TwoBoneIk)) && target != null ? (Component)target.Components.FirstOrDefault(x => x.GetType() == t) ?? c : c;
                    if (host == null) continue;
                    var card = new Border { Classes = { "card" }, Padding = new Thickness(10) };
                    var body = new StackPanel();
                    body.Children.Add(new TextBlock { Text = host.DisplayName + (ReferenceEquals(host.Entity, ent) ? "" : "  (on " + host.Entity?.Name + ")"), FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
                    try { foreach (var row in ComponentEditors.Build(host, host.Entity)) body.Children.Add(row); cards++; types.Add(t.Name); }
                    catch (Exception ex) { failures++; Log("synthetic card " + t.Name + " threw " + ex.GetType().Name + ": " + ex.Message); }
                    card.Child = body;
                    stack.Children.Add(card);
                }
                var win = new Window { Title = "Inspector cards " + gi, Width = 460, Height = 980, Content = new ScrollViewer { Content = stack } };
                EditorWindows.Show(win);
                await SmokeRegistry.Settle(700);
                SmokeRegistry.Capture(win, "inspector_cards_" + gi + ".png");
                win.Close();
                PropertyRows.ClearRefreshers();
            }
            // HandPose + the remaining shapes build without a window
            foreach (var c in comps)
            {
                try { ComponentEditors.Build(c, ent).ToList(); types.Add(c.GetType().Name); }
                catch (Exception ex) { failures++; Log("synthetic card " + c.GetType().Name + " threw " + ex.GetType().Name + ": " + ex.Message); }
            }
            PropertyRows.ClearRefreshers();
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
