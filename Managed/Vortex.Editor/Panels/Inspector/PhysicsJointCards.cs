using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;
using Vec3 = Editor.ECS.Vector3;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>
    /// Inspector cards of the physics joints (issue #103): Hinge / Ball / Slider / Fixed / Distance. Connected-entity
    /// picker from the scene ("World" = pinned in place), anchor + axis (with X/Y/Z shortcuts), limits, motor /
    /// spring / friction, break force, a door preset for hinges and a live readout (angle / position / force) while
    /// playing. Rows that don't apply to the current settings (limits off, motor mode) are hidden.
    /// Registered through <see cref="ComponentEditors.Custom"/>; smoke checks through <see cref="SmokeRegistry"/>.
    /// </summary>
    internal static class PhysicsJointCards
    {
        [ModuleInitializer]
        internal static void Register()
        {
            ComponentEditors.Custom[typeof(HingeJoint)] = (c, e) => HingeRows((HingeJoint)c, e);
            ComponentEditors.Custom[typeof(BallJoint)] = (c, e) => BallRows((BallJoint)c, e);
            ComponentEditors.Custom[typeof(SliderJoint)] = (c, e) => SliderRows((SliderJoint)c, e);
            ComponentEditors.Custom[typeof(FixedJoint)] = (c, e) => FixedRows((FixedJoint)c, e);
            ComponentEditors.Custom[typeof(DistanceJoint)] = (c, e) => DistanceRows((DistanceJoint)c, e);

            SmokeRegistry.Add("physics joint cards: every card builds", () => BuildAllCards(out _) == 5);
            SmokeRegistry.Add("physics joint cards: connected-entity reference round trip", ReferenceRoundTrip);
            SmokeRegistry.Add("physics joint cards: window capture", CaptureCards);
        }

        // ---------------------------------------------------------------------------------------------- cards

        private static IEnumerable<Control> HingeRows(HingeJoint h, GameEntity e)
        {
            var rows = new List<Control>
            {
                Note("Rotates about one axis through the anchor: doors, lids, levers, wheels. The pose at Play is angle 0."),
            };
            rows.AddRange(Requirements(e));
            rows.Add(Row("Connected to", ConnectedPicker(h, e), "Entity whose body this joint holds on to; World = pinned in place"));
            rows.Add(Row("Anchor", Vector3(() => h.Anchor, v => h.Anchor = v, 0.05), "Hinge point in local space (scaled with the entity): (-0.5, 0, 0) = left edge of a scaled cube"));
            rows.Add(Row("Axis", AxisEditor(() => h.Axis, v => h.Axis = v)));
            rows.Add(Row("Door preset", DoorPresets(h, e)));

            Action updateVisibility = null;
            var useLimits = Row("Use limits", Bool(() => h.UseLimits, v => { h.UseLimits = v; updateVisibility?.Invoke(); }));
            var min = Row("Min angle (°)", SliderRow(() => h.MinAngle, v => h.MinAngle = v, -180, 0, "0"));
            var max = Row("Max angle (°)", SliderRow(() => h.MaxAngle, v => h.MaxAngle = v, 0, 180, "0"));
            var mode = Row("Motor", Enum<JointMotorMode>(() => h.MotorMode, v => { h.MotorMode = v; updateVisibility?.Invoke(); }, MotorLabels));
            var speed = Row("Speed (°/s)", FloatBox(() => h.TargetVelocity, v => h.TargetVelocity = v, 10));
            var target = Row("Target angle (°)", SliderRow(() => h.TargetAngle, v => h.TargetAngle = v, -180, 180, "0"));
            var torque = Row("Max torque (N·m)", FloatBox(() => h.MaxTorque, v => h.MaxTorque = v, 10, 0f), "0 = unlimited");
            var freq = Row("Spring (Hz)", FloatBox(() => h.SpringFrequency, v => h.SpringFrequency = v, 0.1, 0.01f), "Stiffness as an oscillation frequency");
            var damp = Row("Spring damping", SliderRow(() => h.SpringDamping, v => h.SpringDamping = v, 0, 2), "0 = bouncy, 1 = no overshoot");
            var friction = Row("Friction (N·m)", FloatBox(() => h.Friction, v => h.Friction = v, 0.5, 0f), "Resists turning while the motor is off");
            updateVisibility = () =>
            {
                min.IsVisible = max.IsVisible = h.UseLimits;
                speed.IsVisible = h.MotorMode == JointMotorMode.Velocity;
                target.IsVisible = freq.IsVisible = damp.IsVisible = h.MotorMode == JointMotorMode.Position;
                torque.IsVisible = h.MotorMode != JointMotorMode.Off;
                friction.IsVisible = h.MotorMode == JointMotorMode.Off;
            };
            updateVisibility();
            Refreshers[useLimits] = updateVisibility;   // undo / reset
            rows.AddRange(new Control[] { useLimits, min, max, mode, speed, target, torque, freq, damp, friction });
            rows.AddRange(Tail(h));
            return rows;
        }

        private static IEnumerable<Control> BallRows(BallJoint b, GameEntity e)
        {
            var rows = new List<Control>
            {
                Note("Free rotation about the anchor: pendulums, hanging lamps, chains. Optional swing cone + twist range."),
            };
            rows.AddRange(Requirements(e));
            rows.Add(Row("Connected to", ConnectedPicker(b, e), "Entity whose body this joint holds on to; World = pinned in place"));
            rows.Add(Row("Anchor", Vector3(() => b.Anchor, v => b.Anchor = v, 0.05), "Pivot in local space (scaled with the entity)"));
            rows.Add(Row("Cone axis", AxisEditor(() => b.Axis, v => b.Axis = v), "Centre of the swing cone / twist axis (local); down = hanging"));
            Action updateVisibility = null;
            var useLimits = Row("Use limits", Bool(() => b.UseLimits, v => { b.UseLimits = v; updateVisibility?.Invoke(); }));
            var swing = Row("Swing limit (°)", SliderRow(() => b.SwingLimit, v => b.SwingLimit = v, 0, 180, "0"), "Half angle of the cone");
            var tmin = Row("Twist min (°)", SliderRow(() => b.TwistMin, v => b.TwistMin = v, -180, 180, "0"));
            var tmax = Row("Twist max (°)", SliderRow(() => b.TwistMax, v => b.TwistMax = v, -180, 180, "0"));
            updateVisibility = () => { swing.IsVisible = tmin.IsVisible = tmax.IsVisible = b.UseLimits; };
            updateVisibility();
            Refreshers[useLimits] = updateVisibility;
            rows.AddRange(new Control[] { useLimits, swing, tmin, tmax });
            rows.AddRange(Tail(b));
            return rows;
        }

        private static IEnumerable<Control> SliderRows(SliderJoint s, GameEntity e)
        {
            var rows = new List<Control>
            {
                Note("Moves along one axis only, no rotation: lifts, drawers, sliding doors, pistons. The pose at Play is position 0."),
            };
            rows.AddRange(Requirements(e));
            rows.Add(Row("Connected to", ConnectedPicker(s, e), "Entity whose body this joint holds on to; World = pinned in place"));
            rows.Add(Row("Anchor", Vector3(() => s.Anchor, v => s.Anchor = v, 0.05), "Joint point in local space (scaled with the entity)"));
            rows.Add(Row("Axis", AxisEditor(() => s.Axis, v => s.Axis = v)));
            Action updateVisibility = null;
            var useLimits = Row("Use limits", Bool(() => s.UseLimits, v => { s.UseLimits = v; updateVisibility?.Invoke(); }));
            var min = Row("Min (m)", FloatBox(() => s.MinPosition, v => s.MinPosition = v, 0.1, null, 0f), "Travel below the start pose (<= 0)");
            var max = Row("Max (m)", FloatBox(() => s.MaxPosition, v => s.MaxPosition = v, 0.1, 0f), "Travel above the start pose (>= 0)");
            var mode = Row("Motor", Enum<JointMotorMode>(() => s.MotorMode, v => { s.MotorMode = v; updateVisibility?.Invoke(); }, MotorLabels));
            var speed = Row("Speed (m/s)", FloatBox(() => s.TargetVelocity, v => s.TargetVelocity = v, 0.1));
            var target = Row("Target (m)", FloatBox(() => s.TargetPosition, v => s.TargetPosition = v, 0.1));
            var force = Row("Max force (N)", FloatBox(() => s.MaxForce, v => s.MaxForce = v, 100, 0f), "0 = unlimited; a lift needs more than its load's weight (m·9.81)");
            var freq = Row("Spring (Hz)", FloatBox(() => s.SpringFrequency, v => s.SpringFrequency = v, 0.1, 0.01f));
            var damp = Row("Spring damping", SliderRow(() => s.SpringDamping, v => s.SpringDamping = v, 0, 2));
            var friction = Row("Friction (N)", FloatBox(() => s.Friction, v => s.Friction = v, 1, 0f), "Resists sliding while the motor is off");
            updateVisibility = () =>
            {
                min.IsVisible = max.IsVisible = s.UseLimits;
                speed.IsVisible = s.MotorMode == JointMotorMode.Velocity;
                target.IsVisible = freq.IsVisible = damp.IsVisible = s.MotorMode == JointMotorMode.Position;
                force.IsVisible = s.MotorMode != JointMotorMode.Off;
                friction.IsVisible = s.MotorMode == JointMotorMode.Off;
            };
            updateVisibility();
            Refreshers[useLimits] = updateVisibility;
            rows.AddRange(new Control[] { useLimits, min, max, mode, speed, target, force, freq, damp, friction });
            rows.AddRange(Tail(s));
            return rows;
        }

        private static IEnumerable<Control> FixedRows(FixedJoint f, GameEntity e)
        {
            var rows = new List<Control>
            {
                Note("Welds this body to the connected one (or pins it in place). Give it a Break Force for planks and parts that snap off."),
            };
            rows.AddRange(Requirements(e));
            rows.Add(Row("Connected to", ConnectedPicker(f, e), "Entity whose body this joint holds on to; World = pinned in place"));
            rows.Add(Row("Anchor", Vector3(() => f.Anchor, v => f.Anchor = v, 0.05), "Weld point in local space (where the break force acts)"));
            rows.AddRange(Tail(f));
            return rows;
        }

        private static IEnumerable<Control> DistanceRows(DistanceJoint d, GameEntity e)
        {
            var rows = new List<Control>
            {
                Note("Keeps the anchor within Min..Max of the other end: ropes (min 0), rods (min = max), bungees (spring)."),
            };
            rows.AddRange(Requirements(e));
            rows.Add(Row("Connected to", ConnectedPicker(d, e), "Entity the other end is attached to; World = a fixed point"));
            rows.Add(Row("Anchor", Vector3(() => d.Anchor, v => d.Anchor = v, 0.05), "This end, local space (scaled with the entity)"));
            rows.Add(Row("Other end", Vector3(() => d.ConnectedAnchor, v => d.ConnectedAnchor = v, 0.05),
                "Local point on the connected entity; for the World: offset from this anchor in world space (0, 2, 0) = hung 2 m above"));
            rows.Add(Row("Min distance (m)", FloatBox(() => d.MinDistance, v => d.MinDistance = v, 0.1), "0 = can go slack (rope); -1 = the distance at Play"));
            rows.Add(Row("Max distance (m)", FloatBox(() => d.MaxDistance, v => d.MaxDistance = v, 0.1), "Rope length; -1 = the distance at Play"));
            rows.Add(Row("Spring (Hz)", FloatBox(() => d.SpringFrequency, v => d.SpringFrequency = v, 0.1, 0f), "> 0 = soft limits (bungee); 0 = rigid"));
            rows.Add(Row("Spring damping", SliderRow(() => d.SpringDamping, v => d.SpringDamping = v, 0, 2)));
            rows.AddRange(Tail(d));
            return rows;
        }

        private static readonly string[] MotorLabels = { "Off", "Velocity (motor)", "Position (spring)" };

        // ---------------------------------------------------------------------------------------------- shared rows

        /// <summary>What the entity still needs for the joint to do something.</summary>
        private static IEnumerable<Control> Requirements(GameEntity e)
        {
            if (e == null) yield break;
            if (e.GetComponent<Collider>() == null)
                yield return Warning("Needs a Collider: the joint acts on this entity's rigid body.");
            var rb = e.GetComponent<Rigidbody>();
            if (rb == null || rb.BodyType != RigidbodyType.Dynamic)
                yield return Note("Add a Dynamic Rigidbody so the joint can move this entity (a static / kinematic body only anchors the connected one).");
        }

        /// <summary>Break force + the live readout.</summary>
        private static IEnumerable<Control> Tail(PhysicsJoint j)
        {
            yield return Row("Break force (N)", FloatBox(() => j.BreakForce, v => j.BreakForce = v, 100, 0f),
                "0 = unbreakable. The joint snaps (and PhysicsService.JointBroken fires) above this force. Set it above the peak the Live row shows in normal use: a door slamming into its stop at walking speed peaks at ~3 kN");
            yield return Row("Live", LiveStatus(j));
        }

        /// <summary>Scene picker for <see cref="PhysicsJoint.ConnectedEntity"/>: World + every entity of the scene
        /// (indented by depth, the owner excluded); an unresolved reference shows as "'name' (not found)". Stores the
        /// name when it is unambiguous from the owner, else the entity's id.</summary>
        private static Control ConnectedPicker(PhysicsJoint j, GameEntity owner)
        {
            var combo = new ComboBox { MinHeight = 22, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch };
            var targets = new List<GameEntity>();
            bool filling = false;
            void Fill()
            {
                filling = true;
                try
                {
                    combo.Items.Clear();
                    targets.Clear();
                    combo.Items.Add("World (pinned in place)");
                    targets.Add(null);
                    var roots = SceneRoots(owner);
                    void Add(GameEntity x, int depth)
                    {
                        if (x == null) return;
                        if (!ReferenceEquals(x, owner))
                        {
                            combo.Items.Add(new string(' ', depth * 3) + (string.IsNullOrEmpty(x.Name) ? "(unnamed)" : x.Name));
                            targets.Add(x);
                        }
                        if (x.Children != null) foreach (var c in x.Children) Add(c, depth + 1);
                    }
                    foreach (var r in roots) Add(r, 0);
                    var current = j.ResolveConnectedEntity(roots, out bool notFound);
                    int index = current == null ? 0 : targets.IndexOf(current);
                    if (notFound)
                    {
                        combo.Items.Add("'" + j.ConnectedEntity + "' (not found)");
                        targets.Add(null);
                        index = combo.Items.Count - 1;
                    }
                    combo.SelectedIndex = Math.Max(0, index);
                }
                finally { filling = false; }
            }
            Fill();
            combo.SelectionChanged += (s, e) =>
            {
                if (filling || combo.SelectedIndex < 0 || combo.SelectedIndex >= targets.Count) return;
                var target = targets[combo.SelectedIndex];
                if (target == null && combo.SelectedIndex != 0) return;   // the "(not found)" entry: keep the stored text
                string reference = PhysicsJoint.ReferenceTo(owner, target, SceneRoots(owner));
                if (reference != j.ConnectedEntity) j.ConnectedEntity = reference;
            };
            ToolTip.SetTip(combo, "Stored as the entity's name (resolved nearest-first, so it works inside prefabs) or its id when the name is ambiguous");
            Refreshers[combo] = Fill;
            return combo;
        }

        private static IEnumerable<GameEntity> SceneRoots(GameEntity owner)
        {
            var scene = owner?.Scene ?? ProjectData.Current?.ActiveScene;
            if (scene?.Entities != null) return scene.Entities;
            var top = owner;
            while (top?.Parent != null) top = top.Parent;
            return top != null ? new[] { top } : Array.Empty<GameEntity>();
        }

        private static Control AxisEditor(Func<Vec3> get, Action<Vec3> set)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(Vector3(get, set, 0.1));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            void Axis(string label, Vec3 v)
            {
                var b = new Button { Content = label, Classes = { "ghost" }, MinWidth = 34 };
                ToolTip.SetTip(b, "Axis = local " + label);
                b.Click += (s, e) => { set(v); RefreshAll(); };
                buttons.Children.Add(b);
            }
            Axis("X", new Vec3(1f, 0f, 0f));
            Axis("Y", new Vec3(0f, 1f, 0f));
            Axis("Z", new Vec3(0f, 0f, 1f));
            Axis("-Y", new Vec3(0f, -1f, 0f));
            panel.Children.Add(buttons);
            return panel;
        }

        /// <summary>One click door: hinge on the left / right edge of a scaled cube (local x = -0.5 / +0.5), vertical
        /// axis, +-100 deg, a little friction; adds a Box Collider and a Dynamic Rigidbody when missing.</summary>
        private static Control DoorPresets(HingeJoint h, GameEntity e)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            void Preset(string label, float side)
            {
                var b = new Button { Content = label, Classes = { "ghost" } };
                ToolTip.SetTip(b, "Anchor on the local " + (side < 0 ? "left" : "right") + " edge, vertical axis, limits ±100°, friction 2 N·m; adds a Box Collider + Dynamic Rigidbody (20 kg) if missing");
                b.Click += (s, a) =>
                {
                    h.Anchor = new Vec3(side, 0f, 0f);
                    h.Axis = new Vec3(0f, 1f, 0f);
                    h.UseLimits = true;
                    h.MinAngle = -100f;
                    h.MaxAngle = 100f;
                    if (h.Friction <= 0f) h.Friction = 2f;
                    if (e != null)
                    {
                        if (e.GetComponent<Collider>() == null) e.AddComponent(new BoxCollider(e));
                        var rb = e.GetComponent<Rigidbody>();
                        if (rb == null) e.AddComponent(new Rigidbody(e) { BodyType = RigidbodyType.Dynamic, Mass = 20f });
                        else if (rb.BodyType != RigidbodyType.Dynamic) rb.BodyType = RigidbodyType.Dynamic;
                    }
                    SceneRenderService.RuntimeDirty = true;
                    Dispatcher.UIThread.Post(() => EditorCommands.Window?.Inspector?.Refresh());
                };
                row.Children.Add(b);
            }
            Preset("Door: hinge left", -0.5f);
            Preset("Door: hinge right", 0.5f);
            return row;
        }

        /// <summary>Angle / position / force of the simulated joint, polled while the card is visible.</summary>
        private static Control LiveStatus(PhysicsJoint j)
        {
            var text = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (s, e) => text.Text = Describe(j);
            text.AttachedToVisualTree += (s, e) => { text.Text = Describe(j); timer.Start(); };
            text.DetachedFromVisualTree += (s, e) => timer.Stop();
            text.Text = Describe(j);
            return text;
        }

        private static string Describe(PhysicsJoint j)
        {
            if (!PhysicsService.IsBuilt) return "Not simulating (press Play; View ▸ Physics Debug draws the joint).";
            if (!PhysicsService.HasJoint(j)) return j.IsEnabled ? "Not attached — see the Console." : "Disabled (enable it to attach at the current pose).";
            string s = PhysicsService.IsJointBroken(j) ? "BROKEN · " : "";
            if (j is HingeJoint) s += "angle " + Fmt(PhysicsService.GetHingeAngle(j), "0.0") + "° · ";
            else if (j is SliderJoint) s += "position " + Fmt(PhysicsService.GetSliderPosition(j), "0.000") + " m · ";
            return s + "force " + Fmt(PhysicsService.GetJointForce(j), "0") + " N (peak " + Fmt(PhysicsService.GetJointPeakForce(j), "0") + " N)";
        }

        // ---------------------------------------------------------------------------------------------- smoke

        /// <summary>Builds the card of every joint type for a detached test entity; returns how many built rows
        /// (components are added without the undo stack).</summary>
        private static int BuildAllCards(out StackPanel panel)
        {
            panel = new StackPanel { Spacing = 10, Margin = new Thickness(12) };
            var joints = new PhysicsJoint[] { new HingeJoint(), new BallJoint(), new SliderJoint(), new FixedJoint(), new DistanceJoint() };
            int built = 0;
            foreach (var j in joints)
            {
                var e = new GameEntity("JointSmoke_" + j.GetType().Name);
                j.Entity = e;
                e.Components.Add(j);
                var rows = new List<Control>(ComponentEditors.Build(j, e));
                if (rows.Count < 3) continue;
                panel.Children.Add(new TextBlock { Text = j.DisplayName, FontWeight = Avalonia.Media.FontWeight.SemiBold });
                foreach (var r in rows) panel.Children.Add(r);
                built++;
            }
            return built;
        }

        private static bool ReferenceRoundTrip()
        {
            // root / { Frame, Door (hinge) }, other / { Frame } : "Frame" resolves nearest-first to the door's sibling.
            var root = new GameEntity("SmokeRoot");
            var frame = new GameEntity("Frame") { Parent = root };
            var door = new GameEntity("Door") { Parent = root };
            root.Children.Add(frame);
            root.Children.Add(door);
            var other = new GameEntity("Other");
            var otherFrame = new GameEntity("Frame") { Parent = other };
            other.Children.Add(otherFrame);
            var roots = new[] { other, root };
            var hinge = new HingeJoint(door);
            door.Components.Add(hinge);
            hinge.ConnectedEntity = PhysicsJoint.ReferenceTo(door, frame, roots);
            bool nearest = hinge.ConnectedEntity == "Frame" && ReferenceEquals(hinge.ResolveConnectedEntity(roots, out _), frame);
            string id = PhysicsJoint.ReferenceTo(door, otherFrame, roots);   // from the door "Frame" means its sibling -> the id is stored
            bool byId = Guid.TryParse(id, out _) && ReferenceEquals(PhysicsJoint.ResolveReference(door, id, roots), otherFrame);
            bool byPath = ReferenceEquals(PhysicsJoint.ResolveReference(door, "Other/Frame", roots), otherFrame);
            hinge.ConnectedEntity = "Nope";
            bool missing = hinge.ResolveConnectedEntity(roots, out bool notFound) == null && notFound;
            hinge.ConnectedEntity = "";
            bool world = hinge.ResolveConnectedEntity(roots, out bool nf2) == null && !nf2;
            return nearest && byId && byPath && missing && world;
        }

        private static async Task<bool> CaptureCards()
        {
            if (BuildAllCards(out var panel) != 5) return false;
            var w = new Window { Title = "Physics joint cards", Width = 560, Height = 1500, Content = new ScrollViewer { Content = panel } };
            EditorWindows.Show(w);
            await SmokeRegistry.Settle(700);
            SmokeRegistry.Capture(w, "physics_joint_cards.png");
            w.Close();
            return true;
        }
    }
}
