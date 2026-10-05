using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Dev tool behind <c>VORTEX_SHOWCASE=&lt;dir&gt;</c>: records a scripted tour of the editor for the README showcase
    /// (docs/showcase) — a camera flight over the open scene, Play mode with injected aim/fire input, then every editor
    /// window (material, VFX, animation, model, texture, prefab, collision, navigation, audio mixer, build, git, project
    /// hub) — as PNG frame sequences, one folder per shot with a <c>meta.json</c> (the main window's native viewport is
    /// captured separately and composited offline). Quits the editor when done.
    /// Re-takes: <c>VORTEX_SHOWCASE_ONLY=05,06</c> records just those shots; <c>VORTEX_SHOWCASE_MATERIAL</c> picks the
    /// material (file pattern); <c>VORTEX_SHOWCASE_DEBUG=1</c> writes the preview framing of each window to debug.txt.
    /// <c>VORTEX_SHOWCASE_SESSION=&lt;seconds&gt;</c> records the main window at 1.5 fps instead of the tour, while Claude Code
    /// (or anything else) drives the editor from outside.
    /// </summary>
    internal static class ShowcaseRecorder
    {
        public static string Dir => Environment.GetEnvironmentVariable("VORTEX_SHOWCASE");
        public static bool Enabled => !string.IsNullOrEmpty(Dir);
        /// <summary>Optional <c>VORTEX_SHOWCASE_ONLY=05,06</c>: record just these shots (re-takes).</summary>
        private static bool Wanted(string name)
        {
            string only = Environment.GetEnvironmentVariable("VORTEX_SHOWCASE_ONLY");
            if (string.IsNullOrEmpty(only)) return true;
            foreach (var p in only.Split(',')) if (p.Trim().Length > 0 && name.StartsWith(p.Trim(), StringComparison.Ordinal)) return true;
            return false;
        }

        private static MainWindow _main;
        private static readonly HashSet<int> _keys = new HashSet<int>();

        public static async Task Run(MainWindow main)
        {
            _main = main;
            var log = ConsoleService.Instance;
            try
            {
                Directory.CreateDirectory(Dir);
                main.Width = 1600; main.Height = 900;
                await Task.Delay(2500);
                // VORTEX_SHOWCASE_SESSION=<seconds>: no scripted tour — record the main window while something outside drives
                // the editor (Claude Code through the MCP server: the v3.0 trailer)
                if (double.TryParse(Environment.GetEnvironmentVariable("VORTEX_SHOWCASE_SESSION"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double session) && session > 0)
                {
                    await Shot("session", "Claude builds with you", "Claude Code drives the editor through its MCP server", MainShot, session, 1.5, null, viewport: true);
                    log.Log("showcase: recorded to " + Dir);
                    return;
                }
                string root = ProjectData.Current?.Path ?? "";
                var scene = ProjectData.Current?.ActiveScene;

                // 1) camera flight over the scene, then a target selected (inspector shows its components)
                GameEntity target = Find(scene, "Target_L3_25m") ?? Find(scene, "Player");
                // recorded slowly (the capture runs at ~7 fps) and sped up in the edit -> a smooth flight
                const double flight = 12.0;
                if (Wanted("01_scene")) await Shot("01_scene", "Scene editor", "hierarchy · inspector · gizmos · live PBR viewport", MainShot, flight, 7, t =>
                {
                    double k = Smooth(Math.Min(1.0, t / flight));
                    float x = (float)Lerp(-26, 6, k), y = (float)Lerp(16, 3.2, k), z = (float)Lerp(-30, -6, k);
                    float yaw = (float)Lerp(42, 6, k), pitch = (float)Lerp(28, 9, k);
                    Editor.Core.Viewport.EditorViewportSession.Main?.Camera.SetPositionAndRotation(x, y, z, yaw, pitch);
                    if (t > flight * 0.5 && target != null && !ReferenceEquals(SelectionService.Instance.SelectedEntity, target)) SelectionService.Instance.Select(target);
                }, viewport: true);

                // 2) Play mode inside the editor: aim down sights + fire (input injected into the host key state)
                if (Wanted("02_play")) await PlayShot();

                // 3) every editor window
                string w = Path.Combine(root, "Assets", "Weapons", "Scorpion");
                await WindowShot("03_material", "Material editor", "PBR maps · channel packing · live preview", () => EditorWindows.MaterialEditor(FirstFile(Path.Combine(root, "Assets", "Materials", "Gen"), Environment.GetEnvironmentVariable("VORTEX_SHOWCASE_MATERIAL") ?? "worn_planks_1.5*.vmat") ?? Path.Combine(w, "materials", "Scorpion_Factory.vmat")), 3.0);
                await WindowShot("04_vfx", "VFX editor", "GPU-style particle emitters · curves · live simulation", () => EditorWindows.VfxEditor(Path.Combine(root, "Assets", "VFX", "Explosion.vfx")), 4.0);
                // a rigged character clip when the project has one (the Horror Starter's soldier), else the Scorpion pack
                string anim = FirstExisting(Path.Combine(root, "Assets", "Models", "Character", "animations", "rifle_reload.vanim"), Path.Combine(w, "animations", "Reload.vanim"));
                await WindowShot("05_animation", "Animation editor", "skeletal clips · keyframes · events", () => EditorWindows.AnimationEditor(anim), 3.5, settleMs: 4000,
                    prepare: win => (win as AnimationEditorWindow)?.SetPlaying(true));
                string model = FirstExisting(Path.Combine(root, "Assets", "Models", "Props", "portable_generator", "portable_generator.gltf"), Path.Combine(w, "scene.gltf"));
                await WindowShot("06_model", "Model viewer", "glTF / FBX import · PBR materials", () => EditorWindows.AssetViewer(model), 4.0, settleMs: 3000, prepare: win =>
                {
                    foreach (var tb in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Avalonia.Controls.Primitives.ToggleButton>())
                        if ((tb.Content as string) == "Turntable") tb.IsChecked = true;
                });
                await WindowShot("07_texture", "Texture editor", "import settings · channels · mips", () => EditorWindows.TextureEditor(Path.Combine(root, "Assets", "Textures", "Range", "target_silhouette.jpg")), 2.5);
                await WindowShot("08_prefab", "Prefab editor", "reusable entities · overrides", () => EditorWindows.PrefabEditor(Path.Combine(root, "Assets", "Prefabs", "Shell.ventity")), 2.5);
                var cont = Find(scene, "Cont_3") ?? Find(scene, "Cont_1");
                if (cont != null) await WindowShot("09_collision", "Collision editor", "colliders · physics shapes (Jolt)", () => EditorWindows.CollisionEditor(cont), 2.5);
                await WindowShot("10_navigation", "AI navigation", "navmesh baking · agents", () => EditorWindows.Navigation(), 2.5);
                await WindowShot("11_audio", "Audio mixer", "buses · effects · 3D sound", () => EditorWindows.AudioMixer(), 2.5);
                await WindowShot("12_build", "Build & export", "macOS · Windows · packed release builds", () => EditorCommands.Build(), 2.5);
                await WindowShot("13_git", "Git", "version control built in", () => EditorWindows.Git(), 2.5);
                await WindowShot("14_hub", "Project hub", "templates · recent projects", () => EditorCommands.NewProject(), 3.0, prepare: hub =>
                {
                    // show the Tactical Shooter card (preview image + description)
                    var list = hub.FindControl<ListBox>("TemplateList");
                    if (list?.ItemsSource == null) return;
                    foreach (var item in list.ItemsSource)
                        if ((item?.GetType().GetProperty("Name")?.GetValue(item) as string ?? item?.ToString() ?? "").IndexOf("Tactical", StringComparison.OrdinalIgnoreCase) >= 0) { list.SelectedItem = item; break; }
                });
                log.Log("showcase: recorded to " + Dir);
            }
            catch (Exception ex) { log.LogError("showcase failed: " + ex); }
            finally
            {
                RestoreInput();
                await Task.Delay(500);
                try { main.CloseConfirmed(); } catch { }
            }
        }

        // ------------------------------------------------------------------ shots
        private static async Task PlayShot()
        {
            object Rig(string field)
            {
                var t = Editor.Scripting.ScriptRuntime.Instance.ScriptAssembly?.GetType("PlayerRig");
                return t?.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            }
            InjectInput();
            EditorCommands.Play();
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(250);
                if (Rig("Ready") is bool r && r && Rig("ActiveWeapon") != null && !(Rig("Switching") is bool s && s)) break;
            }
            await Task.Delay(2600);   // the first draw (equip clip) plays out
            Press(0x32); await Task.Delay(250); Release(0x32);   // key 2: switch to the CZ Scorpion EVO (slot 2)
            await Task.Delay(4800);   // holster + draw
            await Shot("02_play", "Play in editor", "CoD-style weapon handling · iron-sight ADS · recoil", MainShot, 5.5, 7, t =>
            {
                if (t > 0.3 && t < 0.45) Press(0x02); else Release(0x02);        // right mouse: aim (toggle)
                if (t > 1.3 && t < 2.4) Press(0x01);                             // fire burst
                else if (t > 3.1 && t < 4.6) Press(0x01);
                else Release(0x01);
            }, viewport: true);
            Release(0x01); Release(0x02);
            EditorCommands.Stop();
            RestoreInput();
            await Task.Delay(1200);
        }

        private static async Task WindowShot(string name, string title, string subtitle, Action open, double seconds, int settleMs = 1400, Action<Window> prepare = null)
        {
            if (!Wanted(name)) return;
            var before = Windows().ToList();
            try { open(); } catch (Exception ex) { ConsoleService.Instance.LogWarning("showcase " + name + ": " + ex.Message); return; }
            Window win = null;
            for (int i = 0; i < 40 && win == null; i++)
            {
                await Task.Delay(100);
                win = Windows().FirstOrDefault(x => !before.Contains(x) && !ReferenceEquals(x, _main));
            }
            if (win == null) { ConsoleService.Instance.LogWarning("showcase " + name + ": no window opened"); return; }
            try { win.Width = Math.Max(win.Width, 1280); win.Height = Math.Max(win.Height, 760); } catch { }
            try { prepare?.Invoke(win); } catch (Exception ex) { ConsoleService.Instance.LogWarning("showcase " + name + " prepare: " + ex.Message); }
            await Task.Delay(settleMs);   // previews load / first simulation frames
            if (Environment.GetEnvironmentVariable("VORTEX_SHOWCASE_DEBUG") == "1") DumpPreviews(name, win);
            await Shot(name, title, subtitle, () => win, seconds, 7, null, viewport: false);
            try { win.Close(); } catch { }
            await Task.Delay(400);
        }

        private static Window MainShot() => _main;

        /// <summary>Capture <paramref name="seconds"/> of a window at ~<paramref name="fps"/> into Dir/name/f0000.png (+ the
        /// native viewport as v0000.bmp), calling <paramref name="tick"/> with the elapsed time before every frame.</summary>
        private static async Task Shot(string name, string title, string subtitle, Func<Window> target, double seconds, double fps, Action<double> tick, bool viewport)
        {
            string dir = Path.Combine(Dir, name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var sw = Stopwatch.StartNew();
            int n = 0; double frameMs = 1000.0 / fps;
            var times = new List<double>();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                double t = sw.Elapsed.TotalSeconds;
                tick?.Invoke(t);
                await Task.Delay(Math.Max(15, (int)(frameMs * 0.5)));   // let layout + a render tick happen
                var w = target();
                if (w == null) break;
                if (!SaveVisual(w, Path.Combine(dir, $"f{n:0000}.png"))) break;
                if (viewport) Editor.DllWrapper.VortexAPI.CaptureFrame(Path.Combine(dir, $"v{n:0000}.bmp"));
                times.Add(t); n++;
                double next = (n * frameMs) / 1000.0;
                int wait = (int)((next - sw.Elapsed.TotalSeconds) * 1000);
                if (wait > 0) await Task.Delay(wait);
            }
            // where the native viewport sits inside the main window (logical px) — the offline composite pastes it there
            string vp = "null";
            if (viewport)
            {
                var ev = _main.ViewportPanel?.EngineView;
                var tv = ev != null ? ev.TransformToVisual(_main) : null;
                if (ev != null && tv.HasValue)
                {
                    var p = new Point(0, 0).Transform(tv.Value);
                    vp = FormattableString.Invariant($"[{p.X:0.#}, {p.Y:0.#}, {ev.Bounds.Width:0.#}, {ev.Bounds.Height:0.#}]");
                }
            }
            string ts = string.Join(", ", times.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            File.WriteAllText(Path.Combine(dir, "meta.json"),
                "{\n  \"title\": " + Json(title) + ",\n  \"subtitle\": " + Json(subtitle) + ",\n  \"frames\": " + n + ",\n  \"viewport\": " + vp + ",\n  \"times\": [" + ts + "]\n}\n");
            ConsoleService.Instance.Log("showcase: " + name + " — " + n + " frames");
        }

        private static bool SaveVisual(Window w, string path)
        {
            try
            {
                var b = w.Bounds;
                if (b.Width < 2 || b.Height < 2) return false;
                var rtb = new RenderTargetBitmap(new PixelSize((int)b.Width, (int)b.Height), new Vector(96, 96));
                rtb.Render(w);
                rtb.Save(path);
                return true;
            }
            catch (Exception ex) { ConsoleService.Instance.LogWarning("showcase capture: " + ex.Message); return false; }
        }

        private static void DumpPreviews(string name, Window win)
        {
            var sb = new System.Text.StringBuilder();
            Action<string> log = line => sb.AppendLine(line);
            foreach (var pv in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<VortexEditor.Controls.PreviewViewport>())
            {
                var sc = pv.Scene;
                string F(float[] a, int n) => a == null ? "null" : string.Join(",", a.Take(n).Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
                log("showcase-dbg " + name + ": bounds=" + F(sc?.Bounds, 4) + " scale=" + (sc?.BoundsScale ?? 0) + " items=" + (sc?.Items?.Count ?? 0)
                    + " cam yaw=" + pv.Camera.Yaw + " pitch=" + pv.Camera.Pitch + " dist=" + pv.Camera.DistScale);
                if (sc?.Items == null) continue;
                foreach (var it in sc.Items.Take(8))
                    log("showcase-dbg   mesh=" + it?.Mesh + " world=" + F(it?.World, 16) + " bones=" + it?.BoneCount + " pal0=" + F(it?.BonePalette, 16));
            }
            File.AppendAllText(Path.Combine(Dir, "debug.txt"), sb.ToString());
        }

        // ------------------------------------------------------------------ input injection (Play shot)
        private static Func<int, bool> _origKey;
        private static Func<bool> _origFocus;
        private static bool _injected;

        private static void InjectInput()
        {
            if (_injected) return;
            _origKey = Editor.Core.Input.HostInput.KeyDown;
            _origFocus = Editor.Core.Input.HostInput.WindowFocused;
            var orig = _origKey;
            Editor.Core.Input.HostInput.KeyDown = vk => _keys.Contains(vk) || (orig != null && orig(vk));
            Editor.Core.Input.HostInput.WindowFocused = () => true;
            _injected = true;
        }

        private static void RestoreInput()
        {
            if (!_injected) return;
            Editor.Core.Input.HostInput.KeyDown = _origKey;
            Editor.Core.Input.HostInput.WindowFocused = _origFocus;
            _keys.Clear(); _injected = false;
        }

        private static void Press(int vk) => _keys.Add(vk);
        private static void Release(int vk) => _keys.Remove(vk);

        // ------------------------------------------------------------------ helpers
        private static IEnumerable<Window> Windows()
            => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? (IEnumerable<Window>)Array.Empty<Window>();

        private static GameEntity Find(Scene scene, string name)
        {
            if (scene?.Entities == null) return null;
            foreach (var e in scene.Entities) { var hit = FindIn(e, name); if (hit != null) return hit; }
            return null;
        }

        private static GameEntity FindIn(GameEntity e, string name)
        {
            if (e.Name == name) return e;
            if (e.Children != null) foreach (var c in e.Children) { var hit = FindIn(c, name); if (hit != null) return hit; }
            return null;
        }

        private static string FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists) ?? paths.LastOrDefault();

        private static string FirstFile(string dir, string pattern)
            => Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderBy(f => f).FirstOrDefault() : null;

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
        private static double Smooth(double t) => t * t * (3 - 2 * t);
        private static string Json(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
