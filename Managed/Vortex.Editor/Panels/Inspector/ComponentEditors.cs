using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;
using VortexEditor.Controls;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;
using Transform = Editor.ECS.Components.Transform;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>Per-component property editors. Each returns the rows for one component card.</summary>
    public static class ComponentEditors
    {
        private static readonly string[] ModelPatterns = { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.blend", "*.vmesh" };
        private static readonly string[] TexturePatterns = { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr", "*.dds" };
        private static readonly string[] AudioPatterns = { "*.wav", "*.mp3", "*.ogg", "*.flac", "*.vsndc" };
        private static readonly string[] MaterialPatterns = { "*.vmat" };
        private static readonly string[] AnimPatterns = { "*.vanim" };
        private static readonly string[] PrefabPatterns = { "*.ventity" };

        public static (string icon, string brush) Style(Component c)
        {
            switch (c)
            {
                case Transform _: return ("Move", "VxTealBrush");
                case MeshRenderer _: return ("Cube", "VxAccentBrush");
                case Camera _: return ("Camera", "VxPurpleBrush");
                case Light _: return ("Light", "VxYellowBrush");
                case Skybox _: return ("World", "VxTealBrush");
                case Script _: return ("Script", "VxOrangeBrush");
                case Collider _: return ("Collider", "VxGreenBrush");
                case Rigidbody _: return ("Sphere", "VxGreenBrush");
                case AudioSource _: case AudioListener _: case ReverbZone _: return ("Audio", "VxGreenBrush");
                case Animator _: case BoneAttachment _: case TwoBoneIk _: case HandPose _: return ("Bone", "VxPinkBrush");
                default: return ("Gear", "VxTextSecondaryBrush");
            }
        }

        /// <summary>Custom inspector cards registered from other files (by exact component type) — checked before the
        /// built-in cards, so a component's card can live next to the feature that owns it:
        /// <c>[ModuleInitializer] static void Register() =&gt; ComponentEditors.Custom[typeof(LookAtIk)] = (c, e) =&gt; Rows((LookAtIk)c);</c></summary>
        public static readonly Dictionary<Type, Func<Component, GameEntity, IEnumerable<Control>>> Custom = new Dictionary<Type, Func<Component, GameEntity, IEnumerable<Control>>>();

        public static IEnumerable<Control> Build(Component c, GameEntity entity)
        {
            if (c != null && Custom.TryGetValue(c.GetType(), out var custom)) return custom(c, entity);
            switch (c)
            {
                case Transform t: return TransformRows(t);
                case MeshRenderer m: return MeshRendererRows(m);
                case Camera cam: return CameraRows(cam, entity);
                case Light l: return LightRows(l);
                case Skybox s: return SkyboxRows(s);
                case Script sc: return ScriptRows(sc, entity);
                case BoxCollider bc: return ColliderRows(bc, ColliderRowsBox(bc));
                case SphereCollider sc2: return ColliderRows(sc2, new[] { Row("Radius", FloatBox(() => sc2.Radius, v => sc2.Radius = v, 0.05, 0.001f)) });
                case CapsuleCollider cc: return ColliderRows(cc, new[] { Row("Radius", FloatBox(() => cc.Radius, v => cc.Radius = v, 0.05, 0.001f)), Row("Height", FloatBox(() => cc.Height, v => cc.Height = v, 0.05, 0.001f)), Row("Direction", Choice(() => cc.Direction, v => cc.Direction = v, "X axis", "Y axis", "Z axis")) });
                case MeshCollider mc: return ColliderRows(mc, new Control[] { Row("Mesh", AssetPath(() => mc.MeshPath, v => mc.MeshPath = v, "Mesh", ModelPatterns, () => AssetPickerDialog.Pick("Models", ModelPatterns))), Row("Convex", Bool(() => mc.Convex, v => mc.Convex = v)) });
                case Rigidbody rb: return RigidbodyRows(rb);
                case AudioSource a: return AudioSourceRows(a);
                case AudioListener _: return new[] { Note("Receives 3D audio from this entity's position. One listener per scene.") };
                case ReverbZone rz: return ReverbRows(rz);
                case Animator an: return AnimatorRows(an);
                case BoneAttachment ba: return BoneAttachmentRows(ba);
                case TwoBoneIk ik: return IkRows(ik);
                case HandPose hp: return HandPoseRows(hp);
                default: return GenericRows(c);
            }
        }

        // ---------------------------------------------------------------- transform
        private static IEnumerable<Control> TransformRows(Transform t)
        {
            yield return Row("Position", Vector3(() => t.LocalPosition, v => t.LocalPosition = v));
            yield return Row("Rotation", Vector3(() => t.LocalRotation, v => t.LocalRotation = v, 1));
            yield return Row("Scale", Vector3(() => t.LocalScale, v => t.LocalScale = v, 0.1, 0.001f));
            var reset = new Button { Content = "Reset", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
            reset.Click += (s, e) => { t.Reset(); RefreshAll(); SceneRenderService.RuntimeDirty = true; };
            yield return Row("", reset);
        }

        // ---------------------------------------------------------------- mesh renderer
        private static readonly string[] Primitives = { "None", "Cube", "Sphere", "Plane", "Cylinder", "Cone", "Capsule", "Quad" };
        private static IEnumerable<Control> MeshRendererRows(MeshRenderer m)
        {
            var combo = new ComboBox { MinHeight = 22, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var p in Primitives) combo.Items.Add(p);
            combo.Items.Add("Custom mesh…");
            void Sync()
            {
                string mp = m.MeshPath ?? "";
                if (string.IsNullOrEmpty(mp)) combo.SelectedIndex = 0;
                else if (mp.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) { int i = Array.FindIndex(Primitives, p => string.Equals(p, mp.Substring(10), StringComparison.OrdinalIgnoreCase)); combo.SelectedIndex = i < 0 ? Primitives.Length : i; }
                else combo.SelectedIndex = Primitives.Length;
            }
            Sync();
            combo.SelectionChanged += (s, e) =>
            {
                int i = combo.SelectedIndex;
                if (i == 0) { if (!string.IsNullOrEmpty(m.MeshPath)) m.MeshPath = ""; }
                else if (i > 0 && i < Primitives.Length) { string np = "Primitive:" + Primitives[i]; if (m.MeshPath != np) m.MeshPath = np; }
                SceneRenderService.RuntimeDirty = true;
            };
            Refreshers[combo] = Sync;
            yield return Row("Mesh", combo);
            yield return Row("Mesh file", AssetPath(() => IsPrimitive(m.MeshPath) ? "" : m.MeshPath, v => { m.MeshPath = v ?? ""; SceneRenderService.RuntimeDirty = true; }, "Model", ModelPatterns, () => AssetPickerDialog.Pick("Models", ModelPatterns)));
            yield return Row("Material", AssetPath(() => m.MaterialPath, v => { m.MaterialPath = v; SceneRenderService.RuntimeDirty = true; }, "Material", MaterialPatterns, () => AssetPickerDialog.Pick("Materials", MaterialPatterns)));
            var edit = new Button { Content = "Edit Material…", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !string.IsNullOrEmpty(m.MaterialPath) };
            edit.Click += (s, e) => { if (!string.IsNullOrEmpty(m.MaterialPath)) EditorCommands.Window?.OpenMaterialEditor(ProjectRelativeToAbsolute(m.MaterialPath)); };
            yield return Row("", edit);
            yield return Row("Color", Color(() => (m.ColorR, m.ColorG, m.ColorB), (r, g, b) => { m.ColorR = r; m.ColorG = g; m.ColorB = b; SceneRenderService.RuntimeDirty = true; }));
            yield return Row("Opacity", SliderRow(() => m.ColorA, v => { m.ColorA = v; SceneRenderService.RuntimeDirty = true; }, 0, 1));
            yield return Row("Metallic", SliderRow(() => m.Metallic, v => m.Metallic = v, 0, 1));
            yield return Row("Roughness", SliderRow(() => m.Roughness, v => m.Roughness = v, 0, 1));
            yield return Row("Normal strength", SliderRow(() => m.NormalStrength, v => m.NormalStrength = v, 0, 2));
            yield return Row("Cast shadows", Bool(() => m.CastShadows, v => m.CastShadows = v));
            yield return Row("Receive shadows", Bool(() => m.ReceiveShadows, v => m.ReceiveShadows = v));
            yield return Row("Render layer", Choice(() => m.RenderLayer, v => { m.RenderLayer = v; SceneRenderService.RuntimeDirty = true; }, "World", "First-person (viewmodel)", "Third-person only"));
        }
        private static bool IsPrimitive(string p) => !string.IsNullOrEmpty(p) && p.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase);
        public static string ProjectRelativeToAbsolute(string p)
        {
            if (string.IsNullOrEmpty(p) || Path.IsPathRooted(p)) return p;
            var root = ProjectData.Current?.Path; return string.IsNullOrEmpty(root) ? p : Path.Combine(root, p.Replace('\\', '/'));
        }

        // ---------------------------------------------------------------- camera
        private static IEnumerable<Control> CameraRows(Camera c, GameEntity entity)
        {
            yield return Row("Role", Choice(() => c.IsMainCamera ? 1 : 0, v => { c.IsMainCamera = v == 1; c.CameraType = v == 1 ? CameraType.MainCamera : CameraType.GameCamera; SceneRenderService.RuntimeDirty = true; }, "Scene camera", "Main camera (player view)"));
            if (!c.IsMainCamera && !SceneHasMainCamera(entity)) yield return Warning("No main camera in this scene: Play will have nothing to render through. Make one camera the main camera.");
            yield return Row("Projection", Enum<CameraProjection>(() => c.Projection, v => c.Projection = v));
            yield return Row("Field of view", SliderRow(() => c.FieldOfView, v => c.FieldOfView = v, 10, 150, "0.#"));
            yield return Row("Ortho size", FloatBox(() => c.OrthographicSize, v => c.OrthographicSize = v, 0.5, 0.01f));
            yield return Row("Near clip", FloatBox(() => c.NearClip, v => c.NearClip = v, 0.01, 0.001f));
            yield return Row("Far clip", FloatBox(() => c.FarClip, v => c.FarClip = v, 10, 0.1f));
            yield return Row("Clear", Enum<CameraClearFlags>(() => c.ClearFlags, v => c.ClearFlags = v));
            yield return Row("Background", Color(() => (c.BackgroundR, c.BackgroundG, c.BackgroundB), (r, g, b) => { c.BackgroundR = r; c.BackgroundG = g; c.BackgroundB = b; }));
            yield return Row("Depth", IntBox(() => c.Depth, v => c.Depth = v, -100, 100));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var look = new Button { Content = "Look Through", Classes = { "ghost" } };
            look.Click += (s, e) => Editor.Core.Viewport.EditorViewportSession.Main?.ViewThroughCamera(entity);
            var prev = new Button { Content = "Preview", Classes = { "ghost" } };
            prev.Click += (s, e) => CameraPreviewService.Instance.TogglePreview(entity);
            row.Children.Add(look); row.Children.Add(prev);
            yield return Row("", row);
        }
        private static bool SceneHasMainCamera(GameEntity e)
        {
            var scene = e?.Scene ?? ProjectData.Current?.ActiveScene;
            return scene != null && PlayCameraHelper.FindMainCamera(scene) != null;
        }

        // ---------------------------------------------------------------- light
        private static IEnumerable<Control> LightRows(Light l)
        {
            yield return Row("Type", Enum<LightType>(() => l.LightType, v => { l.LightType = v; SceneRenderService.RuntimeDirty = true; }));
            yield return Row("Color", Color(() => (l.ColorR, l.ColorG, l.ColorB), (r, g, b) => { l.ColorR = r; l.ColorG = g; l.ColorB = b; SceneRenderService.RuntimeDirty = true; }));
            yield return Row("Intensity", SliderRow(() => l.Intensity, v => { l.Intensity = v; SceneRenderService.RuntimeDirty = true; }, 0, 10));
            yield return Row("Range", SliderRow(() => l.Range, v => { l.Range = v; SceneRenderService.RuntimeDirty = true; }, 0.1, 100, "0.#"));
            yield return Row("Spot angle", SliderRow(() => l.SpotAngle, v => { l.SpotAngle = v; SceneRenderService.RuntimeDirty = true; }, 1, 179, "0.#"));
            yield return Row("Inner angle", SliderRow(() => l.InnerSpotAngle, v => { l.InnerSpotAngle = v; SceneRenderService.RuntimeDirty = true; }, 0, 179, "0.#"));
            yield return Row("Shadows", Enum<ShadowType>(() => l.ShadowType, v => { l.ShadowType = v; SceneRenderService.RuntimeDirty = true; }));
            yield return Row("Shadow strength", SliderRow(() => l.ShadowStrength, v => l.ShadowStrength = v, 0, 1));
            yield return Row("Shadow bias", SliderRow(() => l.ShadowBias, v => l.ShadowBias = v, 0, 0.05f, "0.####"));
            yield return Row("Resolution", Choice(() => ResIndex(l.ShadowResolution), v => l.ShadowResolution = new[] { 512, 1024, 2048, 4096 }[v], "512", "1024", "2048", "4096"));
        }
        private static int ResIndex(int r) => r >= 4096 ? 3 : r >= 2048 ? 2 : r >= 1024 ? 1 : 0;

        // ---------------------------------------------------------------- skybox
        private static IEnumerable<Control> SkyboxRows(Skybox s)
        {
            yield return Row("Type", Enum<SkyboxType>(() => s.SkyboxType, v => { s.SkyboxType = v; SceneRenderService.RuntimeDirty = true; }, new[] { "Solid Color", "Gradient", "Cubemap", "Texture (HDR)" }));
            yield return Row("Ambient", SliderRow(() => s.AmbientIntensity, v => { s.AmbientIntensity = v; SceneRenderService.RuntimeDirty = true; }, 0, 3));
            yield return Row("Exposure", SliderRow(() => s.Exposure, v => { s.Exposure = v; SceneRenderService.RuntimeDirty = true; }, 0, 5));
            yield return Row("Sky", Color(() => (s.TopColorR, s.TopColorG, s.TopColorB), (r, g, b) => { s.TopColorR = r; s.TopColorG = g; s.TopColorB = b; SceneRenderService.RuntimeDirty = true; }));
            yield return Row("Horizon", Color(() => (s.HorizonColorR, s.HorizonColorG, s.HorizonColorB), (r, g, b) => { s.HorizonColorR = r; s.HorizonColorG = g; s.HorizonColorB = b; SceneRenderService.RuntimeDirty = true; }));
            yield return Row("Ground", Color(() => (s.BottomColorR, s.BottomColorG, s.BottomColorB), (r, g, b) => { s.BottomColorR = r; s.BottomColorG = g; s.BottomColorB = b; SceneRenderService.RuntimeDirty = true; }));
            yield return Row("Cubemap", AssetPath(() => s.CubemapPath, v => { s.CubemapPath = v; SceneRenderService.RuntimeDirty = true; }, "Texture", TexturePatterns, () => AssetPickerDialog.Pick("Textures", TexturePatterns)));
            yield return Row("Texture", AssetPath(() => s.TexturePath, v => { s.TexturePath = v; SceneRenderService.RuntimeDirty = true; }, "Texture", TexturePatterns, () => AssetPickerDialog.Pick("Textures", TexturePatterns)));
            yield return Row("Sky mesh", AssetPath(() => s.SkyboxMeshPath, v => { s.SkyboxMeshPath = v; SceneRenderService.RuntimeDirty = true; }, "Model", ModelPatterns, () => AssetPickerDialog.Pick("Models", ModelPatterns)));
        }

        // ---------------------------------------------------------------- script
        private static IEnumerable<Control> ScriptRows(Script sc, GameEntity entity)
        {
            yield return Row("Class", new TextBlock { Text = sc.ScriptClassName ?? "(unknown)", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Medium });
            yield return Row("File", new TextBlock { Text = sc.ScriptPath ?? "", Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center });
            string abs = ProjectRelativeToAbsolute(sc.ScriptPath);
            if (!string.IsNullOrEmpty(sc.ScriptPath) && !File.Exists(abs)) yield return Warning("Script file not found. Use Change… to re-link it.");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var open = new Button { Content = "Open in Editor", Classes = { "ghost" } };
            open.Click += (s, e) => EditorCommands.OpenInIde(abs);
            var change = new Button { Content = "Change…", Classes = { "ghost" } };
            change.Click += async (s, e) => { var p = await AssetPickerDialog.Pick("Scripts", new[] { "*.cs" }); if (p != null) { sc.ScriptPath = ScriptingService.MakeRelative(ProjectData.Current?.Path ?? "", ProjectRelativeToAbsolute(p)); EditorCommands.Window?.Inspector?.Refresh(); } };
            row.Children.Add(open); row.Children.Add(change);
            yield return Row("", row);

            Type type = null;
            try { type = ScriptRuntime.Instance.GetScriptTypeForInspector(sc.ScriptClassName); } catch { }
            if (type == null) { yield return Note("Fields appear here once the project's scripts compile."); yield break; }
            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(f => ScriptRuntime.IsInspectableFieldType(f.FieldType)).ToList();
            if (fields.Count == 0) { yield return Note("This script has no public fields."); yield break; }
            object defaults = null; try { defaults = Activator.CreateInstance(type); } catch { }
            foreach (var f in fields)
            {
                var ff = f;
                string current = sc.GetFieldValue(ff.Name);
                if (current == null && defaults != null) { try { current = ScriptRuntime.FormatFieldValue(ff.GetValue(defaults)); } catch { } }
                Control editor;
                if (ff.FieldType == typeof(bool))
                    editor = Bool(() => string.Equals(sc.GetFieldValue(ff.Name) ?? current, "true", StringComparison.OrdinalIgnoreCase), v => sc.SetFieldValue(ff.Name, v ? "true" : "false"));
                else if (ff.FieldType == typeof(float) || ff.FieldType == typeof(double) || ff.FieldType == typeof(int))
                    editor = FloatBox(() => float.TryParse(sc.GetFieldValue(ff.Name) ?? current, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f,
                                      v => sc.SetFieldValue(ff.Name, ff.FieldType == typeof(int) ? ((int)Math.Round(v)).ToString() : Fmt(v)), ff.FieldType == typeof(int) ? 1 : 0.1);
                else
                    editor = Text(() => (sc.GetFieldValue(ff.Name) ?? current ?? "").Replace(ScriptRuntime.ArraySeparator, ','), v => sc.SetFieldValue(ff.Name, v.Replace(',', ScriptRuntime.ArraySeparator)));
                yield return Row(Pretty(ff.Name), editor, ff.FieldType.Name);
            }
        }

        // ---------------------------------------------------------------- physics
        private static IEnumerable<Control> ColliderRows(Collider c, IEnumerable<Control> shape)
        {
            yield return Row("Is trigger", Bool(() => c.IsTrigger, v => { c.IsTrigger = v; SceneRenderService.RuntimeDirty = true; }));
            yield return Row("Center", Vector3(() => c.Center, v => { c.Center = v; SceneRenderService.RuntimeDirty = true; }));
            foreach (var r in shape) yield return r;
            // Physics material (nested object, so the generic grid would skip it): the collider's Material is null
            // until the first edit — the defaults (friction 0.5, bounciness 0) show, the object is created on write.
            yield return Note("Physics material — friction 0.5 / bounciness 0 are the defaults; used by rigid-body physics.");
            yield return Row("Friction", SliderRow(() => c.Material != null ? c.Material.Friction : 0.5f, v => Mat(c).Friction = v, 0, 1));
            yield return Row("Bounciness", SliderRow(() => c.Material != null ? c.Material.Bounciness : 0f, v => Mat(c).Bounciness = v, 0, 1));
            var open = new Button { Content = "Open Collision Editor…", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (s, e) => EditorCommands.Window?.OpenCollisionEditor(c.Entity);
            yield return Row("", open);
        }
        private static PhysicsMaterial Mat(Collider c) { if (c.Material == null) c.Material = new PhysicsMaterial(); return c.Material; }
        private static IEnumerable<Control> ColliderRowsBox(BoxCollider b) { yield return Row("Size", Vector3(() => b.Size, v => { b.Size = v; SceneRenderService.RuntimeDirty = true; }, 0.1, 0.001f)); }

        private static IEnumerable<Control> RigidbodyRows(Rigidbody rb)
        {
            yield return Note("Simulated in play when the entity also has a Collider. Dynamic = falls/stacks/gets pushed, Kinematic = you move it and it pushes others, Static = never moves.");
            yield return Row("Body type", Enum<RigidbodyType>(() => rb.BodyType, v => rb.BodyType = v));
            yield return Row("Mass (kg)", FloatBox(() => rb.Mass, v => rb.Mass = v, 0.1, 0f));
            yield return Row("Drag (linear damping)", FloatBox(() => rb.Drag, v => rb.Drag = v, 0.05, 0));
            yield return Row("Angular drag", FloatBox(() => rb.AngularDrag, v => rb.AngularDrag = v, 0.05, 0));
            yield return Row("Use gravity", Bool(() => rb.UseGravity, v => rb.UseGravity = v));
            yield return Row("Interpolation", Enum<RigidbodyInterpolation>(() => rb.Interpolation, v => rb.Interpolation = v));
            yield return Row("Collision", Enum<CollisionDetectionMode>(() => rb.CollisionDetection, v => rb.CollisionDetection = v));
            var fp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            fp.Children.Add(Labeled("X", Bool(() => rb.FreezePositionX, v => rb.FreezePositionX = v))); fp.Children.Add(Labeled("Y", Bool(() => rb.FreezePositionY, v => rb.FreezePositionY = v))); fp.Children.Add(Labeled("Z", Bool(() => rb.FreezePositionZ, v => rb.FreezePositionZ = v)));
            yield return Row("Freeze position", fp);
            var fr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            fr.Children.Add(Labeled("X", Bool(() => rb.FreezeRotationX, v => rb.FreezeRotationX = v))); fr.Children.Add(Labeled("Y", Bool(() => rb.FreezeRotationY, v => rb.FreezeRotationY = v))); fr.Children.Add(Labeled("Z", Bool(() => rb.FreezeRotationZ, v => rb.FreezeRotationZ = v)));
            yield return Row("Freeze rotation", fr);
        }
        private static Control Labeled(string l, Control c) { var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 }; sp.Children.Add(c); sp.Children.Add(new TextBlock { Text = l, Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center }); return sp; }

        // ---------------------------------------------------------------- audio
        private static IEnumerable<Control> AudioSourceRows(AudioSource a)
        {
            yield return Row("Clip", AssetPath(() => a.AudioClipPath, v => a.AudioClipPath = v, "Audio", AudioPatterns, () => AssetPickerDialog.Pick("Audio", AudioPatterns)));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var play = new Button { Content = "Preview", Classes = { "ghost" } };
            play.Click += (s, e) => { try { AudioPreviewService.Instance.Start(a, true); } catch (Exception ex) { EditorCommands.Fail("Preview", ex); } };
            var stop = new Button { Content = "Stop", Classes = { "ghost" } };
            stop.Click += (s, e) => { try { AudioPreviewService.Instance.Stop(); } catch { } };
            row.Children.Add(play); row.Children.Add(stop);
            yield return Row("", row);
            yield return Row("Volume", SliderRow(() => a.Volume, v => a.Volume = v, 0, 1));
            yield return Row("Pitch", SliderRow(() => a.Pitch, v => a.Pitch = v, 0.1, 3));
            yield return Row("Loop", Bool(() => a.Loop, v => a.Loop = v));
            yield return Row("Play on awake", Bool(() => a.PlayOnAwake, v => a.PlayOnAwake = v));
            yield return Row("Mute", Bool(() => a.Mute, v => a.Mute = v));
            yield return Row("Output bus", Choice(() => a.OutputBus, v => a.OutputBus = v, Editor.DllWrapper.VortexAudio.BusNames));
            yield return Row("Spatial blend", SliderRow(() => a.SpatialBlend, v => a.SpatialBlend = v, 0, 1));
            yield return Row("Min distance", FloatBox(() => a.MinDistance, v => a.MinDistance = v, 0.5, 0.01f));
            yield return Row("Max distance", FloatBox(() => a.MaxDistance, v => a.MaxDistance = v, 1, 0.1f));
            yield return Row("Rolloff", Enum<AudioRolloffMode>(() => a.RolloffMode, v => a.RolloffMode = v));
            yield return Row("Priority", IntBox(() => a.Priority, v => a.Priority = v, 0, 256));
            yield return Row("Stereo pan", SliderRow(() => a.StereoPan, v => a.StereoPan = v, -1, 1));
            yield return Row("Reverb mix", SliderRow(() => a.ReverbZoneMix, v => a.ReverbZoneMix = v, 0, 1));
            yield return Row("Doppler", SliderRow(() => a.DopplerLevel, v => a.DopplerLevel = v, 0, 5));
            yield return Row("Spread", SliderRow(() => a.Spread, v => a.Spread = v, 0, 360, "0"));
            yield return Row("Streaming", Bool(() => a.Streaming, v => a.Streaming = v));
            yield return Row("HRTF", Bool(() => a.EnableHrtf, v => a.EnableHrtf = v));
            yield return Row("Occlusion", Bool(() => a.EnableOcclusion, v => a.EnableOcclusion = v));
        }

        private static IEnumerable<Control> ReverbRows(ReverbZone r)
        {
            yield return Row("Shape", Choice(() => r.Shape, v => { r.Shape = v; SceneRenderService.RuntimeDirty = true; }, "Sphere", "Box"));
            yield return Row("Radius", FloatBox(() => r.Radius, v => { r.Radius = v; SceneRenderService.RuntimeDirty = true; }, 0.5, 0.1f));
            yield return Row("Box extents", Vector3(() => r.BoxExtents, v => { r.BoxExtents = v; SceneRenderService.RuntimeDirty = true; }, 0.5, 0.1f));
            yield return Row("Falloff", SliderRow(() => r.Falloff, v => r.Falloff = v, 0, 10));
            yield return Row("Decay time", SliderRow(() => r.DecayTime, v => r.DecayTime = v, 0.1, 20));
            yield return Row("Wet level", SliderRow(() => r.WetLevel, v => r.WetLevel = v, 0, 1));
            yield return Row("Pre-delay (ms)", SliderRow(() => r.PreDelayMs, v => r.PreDelayMs = v, 0, 200, "0"));
        }

        // ---------------------------------------------------------------- animation
        private static IEnumerable<Control> AnimatorRows(Animator an)
        {
            var list = new StackPanel { Spacing = 4 };
            void Rebuild()
            {
                list.Children.Clear();
                if (an.Clips == null) an.Clips = new List<AnimatorClipEntry>();
                foreach (var clip in an.Clips)
                {
                    var c = clip;
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,Auto") };
                    var name = new TextBox { Text = c.Name, MinHeight = 22, Watermark = "name" };
                    name.LostFocus += (s, e) => c.Name = name.Text;
                    var path = new TextBox { Text = c.Path, MinHeight = 22, Watermark = "clip.vanim", Margin = new Thickness(4, 0), IsReadOnly = true };
                    path.PointerReleased += async (s, e) => { var p = await AssetPickerDialog.Pick("Animations", AnimPatterns); if (p != null) { c.Path = p; path.Text = p; } };
                    var del = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Minus" } };
                    del.Click += (s, e) => { an.Clips.Remove(c); Rebuild(); };
                    Grid.SetColumn(path, 1); Grid.SetColumn(del, 2);
                    g.Children.Add(name); g.Children.Add(path); g.Children.Add(del);
                    list.Children.Add(g);
                }
                var add = new Button { Content = "Add clip", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
                add.Click += async (s, e) => { var p = await AssetPickerDialog.Pick("Animations", AnimPatterns); if (p != null) { an.Clips.Add(new AnimatorClipEntry { Name = Path.GetFileNameWithoutExtension(p), Path = p }); Rebuild(); } };
                list.Children.Add(add);
            }
            Rebuild();
            yield return Row("Clips", list);
            yield return Row("Default clip", Text(() => an.DefaultClip, v => an.DefaultClip = v, "clip name"));
            yield return Row("Play on start", Bool(() => an.PlayOnStart, v => an.PlayOnStart = v));
            yield return Row("Speed", SliderRow(() => an.Speed, v => an.Speed = v, 0, 3));
        }

        private static IEnumerable<Control> BoneAttachmentRows(BoneAttachment b)
        {
            yield return Row("Target entity", Text(() => b.TargetEntityId, v => b.TargetEntityId = v, "entity id"));
            yield return Row("Bone", Text(() => b.BoneName, v => b.BoneName = v, "bone name"));
            yield return Row("Socket prefab", AssetPath(() => b.SocketPrefabPath, v => b.SocketPrefabPath = v, "Prefab", PrefabPatterns, () => AssetPickerDialog.Pick("Prefabs", PrefabPatterns)));
            yield return Row("Socket layer", Choice(() => b.SocketRenderLayer, v => b.SocketRenderLayer = v, "World", "First-person", "Third-person only"));
            yield return Row("Offset position", Vector3(() => b.OffsetPosition, v => b.OffsetPosition = v, 0.01));
            yield return Row("Offset rotation", Vector3(() => b.OffsetRotation, v => b.OffsetRotation = v, 1));
            yield return Row("Offset scale", Vector3(() => b.OffsetScale, v => b.OffsetScale = v, 0.05, 0.001f));
            var open = new Button { Content = "Open Socket Editor…", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (s, e) => EditorCommands.Window?.OpenSocketEditor(b.Entity);
            yield return Row("", open);
        }

        private static IEnumerable<Control> IkRows(TwoBoneIk ik)
        {
            yield return Row("Tip bone", Text(() => ik.TipBone, v => ik.TipBone = v));
            yield return Row("Target bone", Text(() => ik.TargetBone, v => ik.TargetBone = v));
            yield return Row("Target offset", Vector3(() => ik.TargetOffsetPosition, v => ik.TargetOffsetPosition = v, 0.01));
            yield return Row("Target rotation", Vector3(() => ik.TargetOffsetRotation, v => ik.TargetOffsetRotation = v, 1));
            yield return Row("Weight", SliderRow(() => ik.Weight, v => ik.Weight = v, 0, 1));
            yield return Row("Pole angle", SliderRow(() => ik.PoleAngle, v => ik.PoleAngle = v, -180, 180, "0"));
            yield return Row("Apply tip rotation", Bool(() => ik.ApplyTipRotation, v => ik.ApplyTipRotation = v));
            yield return Row("Auto grip", Bool(() => ik.AutoGrip, v => ik.AutoGrip = v));
        }

        // Hand Pose: editor-authored finger grip (per-finger curl of the three joints) — live in the viewport.
        private static IEnumerable<Control> HandPoseRows(HandPose hp)
        {
            yield return Row("Hand", Enum<HandSide>(() => hp.Side, v => hp.Side = v));
            yield return Row("Weight", SliderRow(() => hp.Weight, v => hp.Weight = v, 0, 1));
            yield return Row("Index (j1 j2 j3 °)", Vector3(() => hp.Index, v => hp.Index = v, 1));
            yield return Row("Middle", Vector3(() => hp.Middle, v => hp.Middle = v, 1));
            yield return Row("Ring", Vector3(() => hp.Ring, v => hp.Ring = v, 1));
            yield return Row("Pinky", Vector3(() => hp.Pinky, v => hp.Pinky = v, 1));
            yield return Row("Thumb", Vector3(() => hp.Thumb, v => hp.Thumb = v, 1));
            yield return Row("Spread", SliderRow(() => hp.Spread, v => hp.Spread = v, -30, 30, "0"));
            yield return Row("Bone prefix", Text(() => hp.BonePrefix, v => hp.BonePrefix = v));
            yield return Row("Bone format", Text(() => hp.BoneFormat, v => hp.BoneFormat = v));
            yield return Row("Curl axis (0 X, 1 Y, 2 Z)", IntBox(() => hp.CurlAxis, v => hp.CurlAxis = v));
            yield return Row("Curl sign", FloatBox(() => hp.CurlSign, v => hp.CurlSign = v));
        }

        // ---------------------------------------------------------------- fallback: reflection
        private static IEnumerable<Control> GenericRows(Component c)
        {
            foreach (var p in c.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                if (p.Name == "Id" || p.Name == "Entity" || p.Name == "IsEnabled") continue;
                var pp = p;
                Control ed = null;
                if (p.PropertyType == typeof(float)) ed = FloatBox(() => (float)pp.GetValue(c), v => pp.SetValue(c, v));
                else if (p.PropertyType == typeof(int)) ed = IntBox(() => (int)pp.GetValue(c), v => pp.SetValue(c, v));
                else if (p.PropertyType == typeof(bool)) ed = Bool(() => (bool)pp.GetValue(c), v => pp.SetValue(c, v));
                else if (p.PropertyType == typeof(string)) ed = Text(() => (string)pp.GetValue(c), v => pp.SetValue(c, v));
                else if (p.PropertyType == typeof(Editor.ECS.Vector3)) ed = Vector3(() => (Editor.ECS.Vector3)pp.GetValue(c), v => pp.SetValue(c, v));
                if (ed != null) yield return Row(Pretty(p.Name), ed);
            }
        }
    }
}
