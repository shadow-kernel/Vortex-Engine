using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Rendering;
using VortexEditor.Controls;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;
using NVector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>
    /// Inspector cards for the rig-aware animation components (#147) — Hand Pose, Look-At IK, Foot IK — registered through
    /// <see cref="ComponentEditors.Custom"/>. Every card shows what the component resolved on the entity's skeleton (hand /
    /// finger / head / leg bones, palm normal, face direction) so the user sees at a glance that it works on this rig;
    /// bone names are only needed when detection is wrong (the pick buttons list the skeleton's bones).
    /// </summary>
    internal static class AnimationComponentCards
    {
        private static readonly string[] RigLabels = { "Auto-detect", "Mixamo", "Unreal", "Unity / Generic", "Rigify", "Custom (bone lists)" };

        [ModuleInitializer]
        internal static void Register()
        {
            ComponentEditors.Custom[typeof(HandPose)] = (c, e) => HandPoseRows((HandPose)c, e ?? c.Entity);
            ComponentEditors.Custom[typeof(LookAtIk)] = (c, e) => LookAtRows((LookAtIk)c, e ?? c.Entity);
            ComponentEditors.Custom[typeof(FootIk)] = (c, e) => FootIkRows((FootIk)c, e ?? c.Entity);
            ComponentEditors.Custom[typeof(Editor.ECS.Components.Physics.Ragdoll)] = (c, e) => RagdollRows((Editor.ECS.Components.Physics.Ragdoll)c, e ?? c.Entity);
            RegisterSmoke();
        }

        // ================================================================ Hand Pose

        private static IEnumerable<Control> HandPoseRows(HandPose hp, GameEntity entity)
        {
            WatchEnabled(hp);
            var report = new ReportView();
            Action update = () => report.Show(AnimationService.Instance.DescribeHandPose(entity, hp));
            update();
            Refreshers[report.Root] = update;
            Action<Action> edit = a => { a(); update(); };

            yield return Row("Hand", Enum<HandSide>(() => hp.Side, v => edit(() => hp.Side = v)));
            yield return Row("Rig", Enum<RigPreset>(() => hp.Rig, v => edit(() => hp.Rig = v), RigLabels),
                "Auto-detect finds the hand and finger bones on any skeleton; a preset only fixes the naming convention.");
            yield return Row("Pose", PresetButtons(hp, update));
            yield return Row("Weight", SliderRow(() => hp.Weight, v => hp.Weight = v, 0, 1));
            for (int f = 0; f < 5; f++) yield return Row(HandPose.Fingers[f], FingerEditor(hp, f), "Curl in degrees for the knuckle, middle and tip joint (positive closes the hand). The slider scales the finger between open and a full fist.");
            yield return Row("Spread", SliderRow(() => hp.Spread, v => hp.Spread = v, -30, 30, "0"));
            var mirror = new Button { Content = "Mirror to other hand", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(mirror, "Copy this pose and rig setup to the other hand (adds its Hand Pose if needed; bone names are mirrored).");
            mirror.Click += (s, e) =>
            {
                try { hp.MirrorToOtherHand(); SceneRenderService.RuntimeDirty = true; EditorCommands.Window?.Inspector?.Refresh(); }
                catch (Exception ex) { EditorCommands.Fail("Mirror hand pose", ex); }
            };
            yield return Row("", mirror);
            yield return Row("Detected", report.Root);

            var adv = new StackPanel { Spacing = 2 };
            adv.Children.Add(Note("Only needed when detection picks the wrong bones. Finger lists: joints knuckle → tip, comma separated (1–4 joints)."));
            adv.Children.Add(Row("Hand bone", BoneField(() => hp.HandBone, v => edit(() => hp.HandBone = v), entity, "hand", "wrist")));
            string[] keys = { "index", "middle", "ring", "pinky", "thumb" };
            for (int f = 0; f < 5; f++)
            {
                int ff = f;
                adv.Children.Add(Row(HandPose.Fingers[f] + " bones", BoneField(() => hp.ExplicitBones(ff), v => edit(() => SetExplicit(hp, ff, v)), entity, keys[f], ff == 3 ? "little" : null, true)));
            }
            adv.Children.Add(Row("Curl axis", Choice(() => hp.CurlAxis + 1, v => edit(() => { hp.CurlAxis = v - 1; }),
                "Auto (from the bind pose)", "Local X (manual)", "Local Y (manual)", "Local Z (manual)"),
                "Auto computes each joint's bend axis from the skeleton so positive values close the hand on any rig. Manual axes are for exotic rigs."));
            adv.Children.Add(Row("Curl sign", Choice(() => hp.CurlSign < 0f ? 1 : 0, v => edit(() => hp.CurlSign = v == 1 ? -1f : 1f), "+1", "−1"),
                "Manual axis only: which way around the local axis closes the hand."));
            adv.Children.Add(Row("Mixamo prefix", Text(() => hp.BonePrefix, v => edit(() => hp.BonePrefix = v)), "Mixamo preset: bone name prefix (a different prefix in the skeleton is detected)."));
            adv.Children.Add(Row("Mixamo pattern", Text(() => hp.BoneFormat, v => edit(() => hp.BoneFormat = v)), "{0} prefix, {1} Left/Right, {2} finger, {3} joint 1..3"));
            yield return new Expander { Header = "Bones & axis", Content = adv, IsExpanded = hp.Rig == RigPreset.Custom, Margin = new Thickness(0, 4, 0, 0) };
        }

        private static void SetExplicit(HandPose hp, int finger, string v)
        {
            switch (finger)
            {
                case 0: hp.IndexBones = v; break;
                case 1: hp.MiddleBones = v; break;
                case 2: hp.RingBones = v; break;
                case 3: hp.PinkyBones = v; break;
                default: hp.ThumbBones = v; break;
            }
        }

        private static Editor.ECS.Vector3 GetCurl(HandPose hp, int f) => hp.CurlOf(f);

        private static void SetCurl(HandPose hp, int f, Editor.ECS.Vector3 v)
        {
            switch (f)
            {
                case 0: hp.Index = v; break;
                case 1: hp.Middle = v; break;
                case 2: hp.Ring = v; break;
                case 3: hp.Pinky = v; break;
                default: hp.Thumb = v; break;
            }
        }

        /// <summary>Per finger: a slider between open (0) and a full fist (1) scaling all joints, plus the exact joint angles.</summary>
        private static Control FingerEditor(HandPose hp, int f)
        {
            Editor.ECS.Vector3 i, m, r, p, t; float s;
            HandPose.GetPreset(HandPosePreset.Fist, out i, out m, out r, out p, out t, out s);
            var fist = new[] { i, m, r, p, t }[f];
            float fistSum = fist.X + fist.Y + fist.Z;
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(SliderRow(
                () => { var c = GetCurl(hp, f); return Math.Max(0f, Math.Min(1.2f, (c.X + c.Y + c.Z) / fistSum)); },
                v => { SetCurl(hp, f, new Editor.ECS.Vector3(fist.X * v, fist.Y * v, fist.Z * v)); RefreshAll(); },
                0, 1.2, "0.00"));
            stack.Children.Add(Vector3(() => GetCurl(hp, f), v => SetCurl(hp, f, v), 1));
            return stack;
        }

        private static Control PresetButtons(HandPose hp, Action update)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (HandPosePreset p in System.Enum.GetValues(typeof(HandPosePreset)))
            {
                var preset = p;
                var b = new Button { Content = p.ToString(), Classes = { "ghost" }, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 2) };
                b.Click += (s, e) => { hp.ApplyPreset(preset); RefreshAll(); update(); SceneRenderService.RuntimeDirty = true; };
                wrap.Children.Add(b);
            }
            return wrap;
        }

        // ================================================================ Look-At IK

        private static IEnumerable<Control> LookAtRows(LookAtIk la, GameEntity entity)
        {
            WatchEnabled(la);
            var report = new ReportView();
            Action update = () => report.Show(AnimationService.Instance.DescribeLookAt(entity, la));
            update();
            Refreshers[report.Root] = update;
            Action<Action> edit = a => { a(); update(); };

            yield return Row("Rig", Enum<RigPreset>(() => la.Rig, v => edit(() => la.Rig = v), RigLabels));
            yield return Row("Look at", EntityField(() => la.TargetEntity, v => la.TargetEntity = v, entity),
                "Entity to look at (name or id). Characters are looked at on their head bone. Scripts: Animation.SetLookAtTarget(entity, point / otherEntity).");
            yield return Row("Target offset", Vector3(() => la.TargetOffset, v => la.TargetOffset = v, 0.05), "World-space offset added to the target point.");
            yield return Row("Weight", SliderRow(() => la.Weight, v => la.Weight = v, 0, 1));
            yield return Row("Head share", SliderRow(() => la.HeadWeight, v => edit(() => la.HeadWeight = v), 0, 1));
            yield return Row("Neck share", SliderRow(() => la.NeckWeight, v => edit(() => la.NeckWeight = v), 0, 1));
            yield return Row("Spine share", SliderRow(() => la.SpineWeight, v => edit(() => la.SpineWeight = v), 0, 1));
            yield return Row("Max yaw", SliderRow(() => la.MaxYaw, v => la.MaxYaw = v, 0, 180, "0"), "Largest sideways turn relative to the torso (degrees).");
            yield return Row("Max pitch", SliderRow(() => la.MaxPitch, v => la.MaxPitch = v, 0, 89, "0"), "Largest up/down turn relative to the torso (degrees).");
            yield return Row("Smoothing (s)", SliderRow(() => la.Smoothing, v => la.Smoothing = v, 0, 1, "0.00"));
            yield return Row("Face forward", Choice(() => la.ForwardAxis, v => edit(() => la.ForwardAxis = v), "Auto (from the bind pose)", "+Z", "−Z", "+X", "−X"),
                "Which way the character faces in its model. Auto reads it from the feet (heel → toes).");
            yield return Row("Detected", report.Root);
            var adv = new StackPanel { Spacing = 2 };
            adv.Children.Add(Note("Only needed when detection picks the wrong bones. Lists are comma separated, lowest bone first."));
            adv.Children.Add(Row("Head bone", BoneField(() => la.HeadBone, v => edit(() => la.HeadBone = v), entity, "head", null)));
            adv.Children.Add(Row("Neck bones", BoneField(() => la.NeckBones, v => edit(() => la.NeckBones = v), entity, "neck", null, true)));
            adv.Children.Add(Row("Spine bones", BoneField(() => la.SpineBones, v => edit(() => la.SpineBones = v), entity, "spine", "chest", true)));
            yield return new Expander { Header = "Bones", Content = adv, IsExpanded = la.Rig == RigPreset.Custom, Margin = new Thickness(0, 4, 0, 0) };
        }

        // ================================================================ Foot IK

        private static IEnumerable<Control> FootIkRows(FootIk fi, GameEntity entity)
        {
            WatchEnabled(fi);
            var report = new ReportView();
            Action update = () => report.Show(AnimationService.Instance.DescribeFootIk(entity, fi));
            update();
            Refreshers[report.Root] = update;
            Action<Action> edit = a => { a(); update(); };

            yield return Note("Active in Play: the feet are planted on the gameplay colliders (stairs, slopes, rubble).");
            yield return Row("Rig", Enum<RigPreset>(() => fi.Rig, v => edit(() => fi.Rig = v), RigLabels));
            yield return Row("Weight", SliderRow(() => fi.Weight, v => fi.Weight = v, 0, 1));
            yield return Row("Max step (m)", SliderRow(() => fi.MaxStep, v => fi.MaxStep = v, 0, 1, "0.00"), "Largest ground height difference the feet and pelvis follow.");
            yield return Row("Ray height (m)", SliderRow(() => fi.RayHeight, v => fi.RayHeight = v, 0.05, 2, "0.00"), "Rays start this far above each foot.");
            var hBox = FloatBox(() => fi.FootHeight < 0f ? 0f : fi.FootHeight, v => fi.FootHeight = v, 0.01, 0f, 1f);
            hBox.IsEnabled = fi.FootHeight >= 0f;
            var autoH = Bool(() => fi.FootHeight < 0f, v => { fi.FootHeight = v ? -1f : 0.1f; hBox.IsEnabled = !v; RefreshAll(); });
            var hRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            hRow.Children.Add(autoH); hRow.Children.Add(new TextBlock { Text = "auto", VerticalAlignment = VerticalAlignment.Center, Classes = { "small", "secondary" } });
            hBox.MinWidth = 70; hRow.Children.Add(hBox);
            yield return Row("Ankle height (m)", hRow, "Ankle above the sole; auto measures it from the bind pose.");
            yield return Row("Align to ground", Bool(() => fi.AlignToGround, v => fi.AlignToGround = v));
            yield return Row("Max foot angle", SliderRow(() => fi.MaxFootAngle, v => fi.MaxFootAngle = v, 0, 80, "0"));
            yield return Row("Adjust pelvis", Bool(() => fi.AdjustPelvis, v => fi.AdjustPelvis = v));
            yield return Row("Smoothing (s)", SliderRow(() => fi.Smoothing, v => fi.Smoothing = v, 0, 0.5, "0.00"));
            yield return Row("Ground layers", IntBox(() => fi.GroundLayers, v => fi.GroundLayers = v), "Bit mask of entity layers the feet stand on (-1 = all).");
            yield return Row("Detected", report.Root);
            var adv = new StackPanel { Spacing = 2 };
            adv.Children.Add(Note("Only needed when detection picks the wrong bones: the ankle bones (knee/hip are the joints above) and the pelvis."));
            adv.Children.Add(Row("Left foot", BoneField(() => fi.LeftFoot, v => edit(() => fi.LeftFoot = v), entity, "foot", "ankle")));
            adv.Children.Add(Row("Right foot", BoneField(() => fi.RightFoot, v => edit(() => fi.RightFoot = v), entity, "foot", "ankle")));
            adv.Children.Add(Row("Pelvis", BoneField(() => fi.PelvisBone, v => edit(() => fi.PelvisBone = v), entity, "hip", "pelvis")));
            yield return new Expander { Header = "Bones", Content = adv, IsExpanded = fi.Rig == RigPreset.Custom, Margin = new Thickness(0, 4, 0, 0) };
        }

        // ================================================================ Ragdoll (#104)

        private static IEnumerable<Control> RagdollRows(Editor.ECS.Components.Physics.Ragdoll rd, GameEntity entity)
        {
            WatchEnabled(rd);
            var report = new ReportView();
            Action update = () => report.Show(Editor.Core.Services.Physics.RagdollService.DescribeEntity(entity));
            update();
            Refreshers[report.Root] = update;

            yield return Note("Switch it on from a script, usually on death: Ragdoll.Activate(EntityId). The bodies are built from the current animated pose, so the hand-over never pops.");
            yield return Row("Activate on start", Bool(() => rd.ActivateOnStart, v => rd.ActivateOnStart = v), "Fall as soon as play starts (dead bodies, testing).");
            yield return Row("Mass (kg)", SliderRow(() => rd.Mass, v => rd.Mass = v, 5, 200, "0"));
            yield return Row("Thickness", SliderRow(() => rd.Thickness, v => rd.Thickness = v, 0.3, 3, "0.00"), "Scales every capsule's radius (armour, bulky creatures).");
            yield return Row("Friction", SliderRow(() => rd.Friction, v => rd.Friction = v, 0, 2, "0.00"), "Against the world: higher = the body slides less.");
            yield return Row("Damping", SliderRow(() => rd.Damping, v => rd.Damping = v, 0, 5, "0.00"), "0 = floppy, higher = the limbs settle faster.");
            yield return Row("Joint friction (N·m)", SliderRow(() => rd.JointFriction, v => rd.JointFriction = v, 0, 20, "0.0"), "Elbows and knees: 0 = loose, a few N·m = stiff.");
            yield return Row("Blend time (s)", SliderRow(() => rd.BlendTime, v => rd.BlendTime = v, 0, 3, "0.00"), "The playing clip (a death animation) hands over to the simulation over this time.");
            yield return Row("Disable colliders", Bool(() => rd.DisableColliders, v => rd.DisableColliders = v), "Remove the entity's own colliders while it is a ragdoll, so shots meet the limbs.");
            yield return Row("Detected", report.Root);
            var drop = new Button { Content = "Drop now", HorizontalAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(drop, "Play mode: turn this character into a ragdoll right now.");
            drop.Click += (s, e) =>
            {
                if (!Editor.Core.Services.Physics.PhysicsService.IsBuilt) { EditorCommands.Toast("Ragdoll: press Play first"); return; }
                if (!Editor.Core.Services.Physics.RagdollService.Activate(entity)) EditorCommands.Toast("Ragdoll: could not activate (see the console)");
            };
            yield return Row("Test", drop);
        }

        // ================================================================ shared pieces

        /// <summary>The card's "Detected" readout: resolved bones, then notes in the warning colour.</summary>
        private sealed class ReportView
        {
            public readonly Border Root;
            private readonly StackPanel _lines = new StackPanel { Spacing = 1 };
            private readonly TextBlock _status = new TextBlock { FontWeight = FontWeight.SemiBold, Classes = { "small" } };

            public ReportView()
            {
                var stack = new StackPanel { Spacing = 3 };
                stack.Children.Add(_status);
                stack.Children.Add(_lines);
                Root = new Border
                {
                    Background = TryBrush("VxFieldBrush"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 5),
                    Child = stack,
                };
            }

            public void Show(AnimationService.RigReport rep)
            {
                _lines.Children.Clear();
                _status.Text = rep.Ok ? "✓ Resolved on this skeleton" : "⚠ Not resolved";
                _status.Foreground = TryBrush(rep.Ok ? "VxGreenBrush" : "VxOrangeBrush");
                foreach (var l in rep.Lines)
                    _lines.Children.Add(new TextBlock { Text = l, Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap });
                foreach (var n in rep.Notes)
                    _lines.Children.Add(new TextBlock { Text = "• " + n, Classes = { "small" }, Foreground = TryBrush("VxOrangeBrush"), TextWrapping = TextWrapping.Wrap });
            }
        }

        private static IBrush TryBrush(string key)
        {
            try { return Application.Current?.FindResource(key) as IBrush; } catch { return null; }
        }

        /// <summary>A bone-name text box with a pick button listing the entity's skeleton (matches of the hint first).</summary>
        private static Control BoneField(Func<string> get, Action<string> set, GameEntity entity, string hint, string hint2, bool appendList = false)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var box = Text(get, set, appendList ? "detected — or list: a,b,c" : "detected");
            var pick = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "ChevronDown" }, Margin = new Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(pick, "Pick a bone of this skeleton");
            pick.Click += (s, e) =>
            {
                var skel = AnimationService.Instance.SkeletonOf(entity);
                var menu = new MenuFlyout();
                if (skel == null) { menu.Items.Add(new MenuItem { Header = "(no skeleton on this entity)", IsEnabled = false }); menu.ShowAt(pick); return; }
                var si = RigMap.Info(skel);
                var names = new List<(string name, int score)>();
                for (int i = 0; i < skel.Nodes.Length; i++)
                {
                    if (!si.Effective[i]) continue;
                    string n = skel.Nodes[i].Name ?? "";
                    string low = n.ToLowerInvariant();
                    int score = (hint != null && low.Contains(hint)) || (hint2 != null && low.Contains(hint2)) ? 0 : 1;
                    names.Add((n, score));
                }
                foreach (var nm in names.OrderBy(x => x.score).Take(160))
                {
                    var name = nm.name;
                    var mi = new MenuItem { Header = name };
                    mi.Click += (a, b) =>
                    {
                        string cur = get() ?? "";
                        string v = appendList && cur.Trim().Length > 0 ? cur.TrimEnd(' ', ',') + "," + name : name;
                        set(v); box.Text = get() ?? "";
                    };
                    menu.Items.Add(mi);
                }
                if (appendList || !string.IsNullOrEmpty(get()))
                {
                    var clear = new MenuItem { Header = "Clear (auto-detect)" };
                    clear.Click += (a, b) => { set(""); box.Text = ""; };
                    menu.Items.Insert(0, clear);
                }
                menu.ShowAt(pick);
            };
            Grid.SetColumn(pick, 1);
            g.Children.Add(box); g.Children.Add(pick);
            return g;
        }

        /// <summary>An entity reference (name) with a pick button listing the scene's entities.</summary>
        private static Control EntityField(Func<string> get, Action<string> set, GameEntity self)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var box = Text(get, set, "entity name (empty = scripts only)");
            var pick = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "ChevronDown" }, Margin = new Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(pick, "Pick an entity of the scene");
            pick.Click += (s, e) =>
            {
                var menu = new MenuFlyout();
                var scene = self?.Scene ?? ProjectData.Current?.ActiveScene;
                var list = new List<string>();
                void Walk(GameEntity x)
                {
                    if (x == null || list.Count >= 200) return;
                    if (x != self && !string.IsNullOrEmpty(x.Name) && !list.Contains(x.Name)) list.Add(x.Name);
                    if (x.Children != null) foreach (var c in x.Children) Walk(c);
                }
                if (scene?.Entities != null) foreach (var x in scene.Entities) Walk(x);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                int pi = list.FindIndex(n => string.Equals(n, "Player", StringComparison.OrdinalIgnoreCase));
                if (pi > 0) { var p = list[pi]; list.RemoveAt(pi); list.Insert(0, p); }
                var none = new MenuItem { Header = "None (script targets only)" };
                none.Click += (a, b) => { set(""); box.Text = ""; };
                menu.Items.Add(none);
                foreach (var n in list)
                {
                    var name = n;
                    var mi = new MenuItem { Header = name };
                    mi.Click += (a, b) => { set(name); box.Text = get() ?? ""; };
                    menu.Items.Add(mi);
                }
                menu.ShowAt(pick);
            };
            Grid.SetColumn(pick, 1);
            g.Children.Add(box); g.Children.Add(pick);
            return g;
        }

        private static readonly ConditionalWeakTable<Editor.ECS.Component, object> _watched = new ConditionalWeakTable<Editor.ECS.Component, object>();

        /// <summary>Re-pose the edit-mode preview when the card header's enable switch toggles the component.</summary>
        private static void WatchEnabled(Editor.ECS.Component c)
        {
            if (c == null || _watched.TryGetValue(c, out _)) return;
            _watched.Add(c, new object());
            c.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Editor.ECS.Component.IsEnabled) && c.Entity != null)
                    AnimationService.Instance.RefreshIk(c.Entity);
            };
            // Show the authored pose in the viewport as soon as the card appears (edit mode shows the bind pose until
            // something re-poses the rig); a no-op for an animator the running game is stepping.
            if (c.Entity != null) { try { AnimationService.Instance.RefreshPreview(c.Entity); } catch { } }
        }

        // ================================================================ smoke checks

        private static void RegisterSmoke()
        {
            SmokeRegistry.Add("anim cards: hand pose Fist closes every skinned rig in the project", async () =>
            {
                string root = ProjectData.Current?.Path;
                if (string.IsNullOrEmpty(root)) return false;
                var models = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.glb", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.fbx", SearchOption.AllDirectories))
                    .Where(p => { var sk = AnimationService.Instance.GetSkeleton(p); return sk != null && sk.IsValid; })
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
                if (models.Count == 0) { ConsoleService.Instance.LogWarning("anim smoke: no skinned model in the project"); return false; }
                bool all = true;
                foreach (var model in models)
                {
                    var e = SmokeRig(model);
                    var hp = new HandPose(e) { Side = HandSide.Left };
                    e.AddComponentDirectForSmoke(hp);
                    var rows = ComponentEditors.Build(hp, e).ToList();
                    var fistButton = FindButton(rows, "Fist");
                    var rep = AnimationService.Instance.DescribeHandPose(e, hp);
                    hp.ApplyPreset(HandPosePreset.Open);
                    var before = Worlds(e);
                    if (fistButton != null) fistButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var after = Worlds(e);
                    bool moved = FingersClosed(e, rep, before, after, out string detail);
                    ConsoleService.Instance.Log("anim smoke: " + Path.GetFileName(model) + " hand=" + (rep.Bones.TryGetValue("Hand", out var h) ? h[0] : "-") + " " + detail +
                                                " -> " + (moved && fistButton != null ? "closed" : "NOT closed"));
                    all &= moved && fistButton != null && rep.Ok;
                    // Visual proof: the posed hand, rendered offscreen.
                    try { CaptureHand(e, rep, model, "anim_handpose_" + Path.GetFileNameWithoutExtension(model) + ".png"); } catch { }
                }
                // The card itself, in a window.
                try
                {
                    var e = SmokeRig(models[0]);
                    var hp = new HandPose(e) { Side = HandSide.Right };
                    e.AddComponentDirectForSmoke(hp);
                    hp.ApplyPreset(HandPosePreset.Grip);
                    await ShowCard(hp, e, "anim_handpose_card.png");
                }
                catch { }
                return all;
            });

            SmokeRegistry.Add("anim cards: look-at turns the head to a target", async () =>
            {
                string model = FirstSkinnedModel();
                if (model == null) return false;
                var e = SmokeRig(model);
                var la = new LookAtIk(e) { Smoothing = 0f };
                e.AddComponentDirectForSmoke(la);
                var rep = AnimationService.Instance.DescribeLookAt(e, la);
                if (!rep.Ok || !rep.Bones.TryGetValue("Head", out var headName)) return false;
                var skel = AnimationService.Instance.SkeletonOf(e);
                int head = skel.FindNode(headName[0]);
                var bind = Worlds(e);
                var frame = RigMap.ModelFrame(RigMap.Info(skel));
                float unit = bind[head].Translation.Y > 10f ? 100f : 1f;
                NVector3 target = bind[head].Translation + (frame.Forward * 2f + frame.Left * 1.5f + frame.Up * 0.5f) * unit;
                AnimationService.Instance.SetLookAtPoint(e, target);   // the rig entity sits at the origin: model == world
                var posed = Worlds(e);
                var qb = Quaternion.CreateFromRotationMatrix(Norm(bind[head]));
                var qp = Quaternion.CreateFromRotationMatrix(Norm(posed[head]));
                var face = NVector3.Transform(NVector3.Transform(frame.Forward, Quaternion.Inverse(qb)), qp);
                var want = NVector3.Normalize(target - posed[head].Translation);
                double err = Math.Acos(Math.Max(-1f, Math.Min(1f, NVector3.Dot(NVector3.Normalize(face), want)))) * 180.0 / Math.PI;
                ConsoleService.Instance.Log("anim smoke: look-at head " + headName[0] + " face-to-target error " + err.ToString("0.00") + " deg");
                AnimationService.Instance.ClearLookAtTarget(e);
                await ShowCard(la, e, "anim_lookat_card.png");
                return err < 2.0;
            });

            SmokeRegistry.Add("anim cards: foot IK resolves legs + card builds", async () =>
            {
                string model = FirstSkinnedModel();
                if (model == null) return false;
                var e = SmokeRig(model);
                var fi = new FootIk(e);
                e.AddComponentDirectForSmoke(fi);
                var rep = AnimationService.Instance.DescribeFootIk(e, fi);
                ConsoleService.Instance.Log("anim smoke: foot IK " + string.Join(" | ", rep.Lines));
                await ShowCard(fi, e, "anim_footik_card.png");
                return rep.Ok && rep.Bones.ContainsKey("LeftFoot") && rep.Bones.ContainsKey("RightFoot") && rep.Bones.ContainsKey("Pelvis");
            });
        }

        private static string FirstSkinnedModel()
        {
            string root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return null;
            return Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.glb", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(p => { var sk = AnimationService.Instance.GetSkeleton(p); return sk != null && sk.IsValid && RigMap.FindHand(RigMap.Info(sk), -1, null) >= 0; });
        }

        /// <summary>A throw-away rig entity (NOT added to the scene): skinned model + Animator at the origin.</summary>
        private static GameEntity SmokeRig(string model)
        {
            var e = new GameEntity("AnimSmokeRig");
            var mr = new MeshRenderer(e) { MeshPath = model };
            e.AddComponentDirectForSmoke(mr);
            e.AddComponentDirectForSmoke(new Animator(e) { PlayOnStart = false });
            return e;
        }

        private static void AddComponentDirectForSmoke(this GameEntity e, Editor.ECS.Component c)
        {
            c.Entity = e;
            e.Components.Add(c);   // no undo entry for the throw-away smoke rig
        }

        private static Matrix4x4[] Worlds(GameEntity e)
        {
            AnimationService.Instance.RefreshIk(e);
            AnimationService.Instance.TryGetNodeWorlds(e, out var sk, out var w);
            return (Matrix4x4[])w.Clone();
        }

        private static bool FingersClosed(GameEntity e, AnimationService.RigReport rep, Matrix4x4[] before, Matrix4x4[] after, out string detail)
        {
            detail = "";
            var skel = AnimationService.Instance.SkeletonOf(e);
            if (skel == null) return false;
            var si = RigMap.Info(skel);
            int closed = 0, total = 0;
            foreach (var f in new[] { "Index", "Middle", "Ring", "Pinky" })
            {
                if (!rep.Bones.TryGetValue(f, out var joints) || joints.Length == 0) continue;
                total++;
                int k = skel.FindNode(joints[0]);
                int second = RigMap.MainChild(si, k);
                if (k < 0 || second < 0) continue;
                float seg = NVector3.Distance(before[second].Translation, before[k].Translation);
                float moved = NVector3.Distance(after[second].Translation, before[second].Translation);
                if (moved > 0.6f * seg) closed++;
            }
            detail = closed + "/" + total + " fingers closed";
            return total >= 3 && closed == total;
        }

        private static Button FindButton(IEnumerable<Control> rows, string content)
        {
            foreach (var r in rows)
            {
                var hit = FindButton(r, content);
                if (hit != null) return hit;
            }
            return null;
        }

        private static Button FindButton(Control c, string content)
        {
            if (c is Button b && (b.Content as string) == content) return b;
            IEnumerable<Control> kids = null;
            if (c is Panel p) kids = p.Children;
            else if (c is Decorator d && d.Child != null) kids = new[] { d.Child };
            else if (c is ContentControl cc && cc.Content is Control cx) kids = new[] { cx };
            if (kids == null) return null;
            foreach (var k in kids) { var hit = FindButton(k, content); if (hit != null) return hit; }
            return null;
        }

        private static async Task ShowCard(Editor.ECS.Component c, GameEntity e, string file)
        {
            var body = new StackPanel { Margin = new Thickness(10), Spacing = 2 };
            body.Children.Add(new TextBlock { Text = c.DisplayName, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var row in ComponentEditors.Build(c, e)) body.Children.Add(row);
            foreach (var x in body.Children.OfType<Expander>()) x.IsExpanded = true;
            var w = new Window { Title = c.DisplayName + " (smoke)", Width = 460, Height = 900, Content = new ScrollViewer { Content = body } };
            EditorWindows.Show(w);
            await SmokeRegistry.Settle(700);
            SmokeRegistry.Capture(w, file);
            w.Close();
        }

        private static Matrix4x4 Norm(Matrix4x4 m)
        {
            var r0 = NVector3.Normalize(new NVector3(m.M11, m.M12, m.M13));
            var r1 = NVector3.Normalize(new NVector3(m.M21, m.M22, m.M23));
            var r2 = NVector3.Normalize(new NVector3(m.M31, m.M32, m.M33));
            return new Matrix4x4(r0.X, r0.Y, r0.Z, 0, r1.X, r1.Y, r1.Z, 0, r2.X, r2.Y, r2.Z, 0, 0, 0, 0, 1);
        }

        /// <summary>Offscreen render of the posed hand into the smoke capture folder.</summary>
        private static void CaptureHand(GameEntity e, AnimationService.RigReport rep, string model, string file)
        {
            if (string.IsNullOrEmpty(SmokeRegistry.CaptureDir)) return;
            if (!rep.Bones.TryGetValue("Hand", out var hn) || !rep.Bones.TryGetValue("Middle", out var mn)) return;
            var skel = AnimationService.Instance.SkeletonOf(e);
            var w = Worlds(e);
            int hand = skel.FindNode(hn[0]), mid = skel.FindNode(mn[0]);
            if (hand < 0 || mid < 0) return;
            if (!AnimationService.Instance.TryGetPalette(e, model, out float[] pal, out int bc)) return;
            using (var pm = PreviewModel.Load(model))
            {
                if (pm == null) return;
                var center = (w[hand].Translation + w[mid].Translation * 1.5f) / 2.5f;
                float palm = NVector3.Distance(w[hand].Translation, w[mid].Translation);   // frames any unit / scale
                var scene = new PreviewScene { StudioLights = true, Bounds = new[] { center.X, center.Y, center.Z, Math.Max(1e-3f, 0.85f * palm) } };
                foreach (var it in pm.Scene.Items) scene.Items.Add(new PreviewItem { Mesh = it.Mesh, Material = it.Material, BonePalette = pal, BoneCount = bc });
                var fwd = RigMap.ModelFrame(RigMap.Info(skel)).Forward;
                var img = PreviewRenderer.Render(scene, 480, 360, new PreviewCamera { Yaw = (float)Math.Atan2(fwd.X, fwd.Z) - 0.25f, Pitch = -0.9f, DistScale = 1f, FovDeg = 35f });
                if (img == null) return;
                var wb = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = wb.Lock())
                    for (int y = 0; y < img.Height; y++)
                        Marshal.Copy(img.Bgra, y * img.Stride, fb.Address + y * fb.RowBytes, img.Width * 4);
                Directory.CreateDirectory(SmokeRegistry.CaptureDir);
                wb.Save(Path.Combine(SmokeRegistry.CaptureDir, file));
            }
        }
    }
}
