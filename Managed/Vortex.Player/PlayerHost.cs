using System;
using System.IO;
using Editor.Core.Data;
using Editor.Core.Input;
using Editor.Core.Native;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.Scripting;
using Editor.UI.Vui;

namespace Vortex.Player
{
    /// <summary>
    /// The standalone game host: boots the engine runtime, loads the project + start scene, starts audio and
    /// scripts, then hands the process main thread to the native GameHost, which calls Tick() every frame
    /// (input -> engine step -> scripts -> retained UI -> camera -> scene submit) and renders + presents.
    /// A port of the WPF App.BootPlayer / GameHostTick pair without any UI framework.
    /// </summary>
    internal static class PlayerHost
    {
        private static VortexAPI.GameTickDelegate _tick;   // keep the native callback alive
        private static string _logPath;
        private static bool _init;
        private static bool _lmbPrev, _f12Prev;
        private static object _submittedScene;
        private static int _frames;
        private static DateTime _fpsT0 = DateTime.MinValue;
        private static bool _reloadPending;
        private static int _hotReloadState = -1;   // -1 hidden, 0 reloading, 1 done, 2 error
        private static string _hotReloadMsg;
        private static DateTime _hotReloadAt;
        private static readonly char[] _vuiCharBuf = new char[64];
        private static readonly int[] _vuiKeyBuf = new int[64];
        private static PlayerOptions _options;
        private static double _runSeconds;
        private static bool _captured;
        private static InputScript _script;
        private static readonly Editor.Core.Viewport.DebugFreeCamera _debugCam = new Editor.Core.Viewport.DebugFreeCamera();
        private static bool _fullscreenApplied;

        public static int Run(PlayerOptions options)
        {
            string exeDir = AppContext.BaseDirectory;
            _options = options;
            _logPath = Path.Combine(Path.GetTempPath(), "vortex_player.log");
            try { File.Delete(_logPath); } catch { }
            Console.WriteLine("log: " + _logPath);

            // Native engine + host input wiring before the first engine call.
            NativeLoader.Register();
            if (!string.IsNullOrEmpty(options.InputScriptPath))
            {
                if (!File.Exists(options.InputScriptPath)) { Console.Error.WriteLine("input script not found: " + options.InputScriptPath); return 2; }
                string capDir = options.CaptureDir ?? (!string.IsNullOrEmpty(options.CapturePath) ? Path.GetDirectoryName(Path.GetFullPath(options.CapturePath)) : Path.GetTempPath());
                Directory.CreateDirectory(capDir);
                _script = InputScript.Load(options.InputScriptPath, capDir);
                Log("input script: " + _script.Count + " commands, captures -> " + capDir);
            }
            // Scripted keys are OR-ed with the physical keyboard; a scripted run counts as focused so gameplay input is live.
            HostInput.KeyDown = vk => (_script != null && _script.IsDown(vk)) || VortexAPI.GameHostKeyDown(vk);
            HostInput.WindowFocused = () => _script != null || VortexAPI.GameHostHasFocus();
            HostInput.Gamepad = null;   // SDL3 gamepads: follow-up

            Log("boot: exe=" + exeDir);
            // Engine/script log -> stdout (the editor shows the same entries in its console panel).
            var console = ConsoleService.Instance;
            console.EntryAdded += () =>
            {
                try
                {
                    if (console.Entries.Count == 0) return;
                    var e = console.Entries[console.Entries.Count - 1];
                    var w = e.Level == LogLevel.Error ? Console.Error : Console.Out;
                    w.WriteLine("[" + e.LevelTag + "] " + e.Message);
                }
                catch { }
            };
            VortexAPI.InitEngineRuntime();
            if (!string.IsNullOrEmpty(NativeLoader.ShaderDirectory))
                VortexAPI.SetShaderDirectory(NativeLoader.ShaderDirectory);
            Log("native: " + (NativeLoader.ResolvedLibraryPath ?? "(default probing)") + " shaders: " + (NativeLoader.ShaderDirectory ?? "(exe-relative)"));

            // player.vortex: a DEBUG export references the original project (loose files, hot-reload on);
            // a RELEASE export ships Assets.vpak next to the executable.
            bool debugExport = false;
            string debugProjectPath = null;
            try
            {
                string marker = Path.Combine(exeDir, "player.vortex");
                if (File.Exists(marker))
                {
                    string raw = File.ReadAllText(marker);
                    if (raw.Replace(" ", "").IndexOf("\"debug\":true", StringComparison.OrdinalIgnoreCase) >= 0) debugExport = true;
                    int ki = raw.IndexOf("\"projectPath\"", StringComparison.OrdinalIgnoreCase);
                    if (ki >= 0)
                    {
                        int c = raw.IndexOf(':', ki);
                        int q1 = c >= 0 ? raw.IndexOf('"', c + 1) : -1;
                        int q2 = q1 >= 0 ? raw.IndexOf('"', q1 + 1) : -1;
                        if (q1 >= 0 && q2 > q1) debugProjectPath = raw.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\");
                    }
                }
            }
            catch { }
            PlayModeService.Instance.IsReleaseMode = !debugExport;
            try { EditorViewportService.Instance.AreGizmosVisible = false; } catch { }

            string pak = Path.Combine(exeDir, "Assets.vpak");
            if (File.Exists(pak))
            {
                AssetVfs.Mount(pak);
                Log("mounted pak: " + AssetVfs.FileCount + " files");
                if (AssetVfs.TryGetBytes("GameScripts.dll", out byte[] dllBytes) && dllBytes.Length > 0)
                {
                    try { ScriptRuntime.Instance.PrecompiledAssembly = System.Reflection.Assembly.Load(dllBytes); }
                    catch (Exception sx) { Log("scripts DLL load failed (recompiling from source if available): " + sx.Message); }
                }
            }

            string projDir = !string.IsNullOrEmpty(options.ProjectPath) ? Path.GetFullPath(options.ProjectPath)
                : (!string.IsNullOrEmpty(debugProjectPath) && Directory.Exists(debugProjectPath) ? debugProjectPath : exeDir);
            if (!string.IsNullOrEmpty(options.ProjectPath)) PlayModeService.Instance.IsReleaseMode = false;

            Console.WriteLine("Vortex Player — loading " + projDir);
            var project = ProjectService.Instance.LoadProjectFromPath(projDir);
            if (project == null)
            {
                Console.Error.WriteLine("No project found at " + projDir + " (expected project.vortex)");
                return 2;
            }
            ProjectData.Current = project;
            if (!string.IsNullOrEmpty(options.SceneName) && project.Scenes != null)
                foreach (var s in project.Scenes)
                    if (s != null && string.Equals(s.Name, options.SceneName, StringComparison.OrdinalIgnoreCase)) { project.ActiveScene = s; break; }

            var scene = project.ActiveScene;
            if (scene != null)
            {
                GameRuntime.MountSceneAssets(scene);
                scene.Load();
                scene.ActivateEntities();
                scene.IsActive = true;
                SceneRenderService.Instance.PreloadSceneAssets(scene);
                Log("scene: " + scene.Name + " entities=" + (scene.Entities != null ? scene.Entities.Count : 0));
                if (options.DumpScene) DumpScene(scene);
            }
            else Log("project has no active scene");

            var cam = CameraService.Instance.GetMainCamera();
            if (cam.IsValid) CameraService.Instance.SetActiveCamera(cam);

            PlayModeService.Instance.IsExternalWindow = true;
            PlayModeService.Instance.SetGameView(true);
            PlayModeService.Instance.Play();
            if (scene != null) AudioPlaybackService.Instance.BeginPlay(scene);
            if (scene != null) ScriptRuntime.Instance.Begin(scene);

            _tick = Tick;
            VortexAPI.SetGameTickCallback(_tick);
            VortexAPI.SetPostMainView(true);
            if (options.RenderScale > 0f && options.RenderScale < 0.999f) VortexAPI.SetRenderScale(options.RenderScale);
            PlayModeService.Instance.NativeGameHostRunning = true;
            // Shipped game: window title, size and fullscreen come from the project settings (Project Settings ▸ Game);
            // development runs keep the project name and the command-line size.
            var settings = project.Settings;
            bool shipped = PlayModeService.Instance.IsReleaseMode;
            string title = shipped && settings != null && !string.IsNullOrWhiteSpace(settings.ProductName) ? settings.ProductName : (project.Name ?? "Vortex");
            uint winW = options.Width, winH = options.Height;
            if (shipped && settings != null && !options.SizeGiven && settings.DefaultScreenWidth >= 320 && settings.DefaultScreenHeight >= 240)
            {
                winW = (uint)settings.DefaultScreenWidth; winH = (uint)settings.DefaultScreenHeight;
            }
            _fullscreenApplied = !(shipped && settings != null && settings.FullscreenByDefault);
            if (shipped && settings != null) { try { VortexAPI.SetVSyncEnabled(settings.VSync); } catch { } }
            bool ok = VortexAPI.RunGameHost(winW, winH, title);   // blocks until the window closes
            PlayModeService.Instance.NativeGameHostRunning = false;
            Log("game host returned " + ok);

            // --dump-scene: a second dump with the FINAL transforms (physics-moved props, script-moved entities)
            // before the runtime rolls the scene back — automated checks read this one.
            if (options.DumpScene)
            {
                var finalScene = ProjectData.Current?.ActiveScene ?? scene;
                if (finalScene != null) { try { Console.WriteLine("--- exit ---"); DumpScene(finalScene); } catch { } }
            }

            try { ScriptRuntime.Instance.End(); } catch { }
            try { AudioPlaybackService.Instance.EndPlay(); } catch { }
            try { PhysicsNative.Shutdown(); } catch { }
            VortexAPI.ShutdownEngineRuntime();
            return ok ? 0 : 3;
        }

        private static void Tick(float dt)
        {
            try
            {
                var sr = ScriptRuntime.Instance;
                if (!_init)
                {
                    _init = true;
                    try { VortexAPI.ShowGrid(false); VortexAPI.ShowGizmos(false); } catch { }
                    try { Log("GPU=" + VortexAPI.GpuName() + " renderScale=" + VortexAPI.GetRenderScale()); } catch { }
                }
                if (!_fullscreenApplied) { _fullscreenApplied = true; try { VortexAPI.GameHostToggleFullscreen(); } catch { } }

                // Script + shader hot reload on focus (development runs only), deferred by one frame for the overlay.
                if (!PlayModeService.Instance.IsReleaseMode && VortexAPI.GameHostConsumeFocusGained())
                {
                    bool shadersDirty = false, scriptsDirty = false;
                    try { shadersDirty = VortexAPI.AnyMaterialShaderDirty(); } catch { }
                    try { scriptsDirty = sr.ScriptsChanged(); } catch { }
                    if (shadersDirty || scriptsDirty) { _reloadPending = true; _hotReloadState = 0; _hotReloadMsg = "Hot-reloading…"; _hotReloadAt = DateTime.UtcNow; }
                }
                else if (_reloadPending)
                {
                    _reloadPending = false;
                    var parts = new System.Collections.Generic.List<string>();
                    bool error = false; string errMsg = null;
                    try { int n = VortexAPI.ReloadMaterialShaders(); if (n > 0) parts.Add(n + (n == 1 ? " shader" : " shaders")); } catch { }
                    try
                    {
                        sr.ReloadScripts();
                        if (sr.LastReloadOutcome == ScriptRuntime.ReloadOutcome.Reloaded) parts.Add(sr.LastReloadSummary);
                        else if (sr.LastReloadOutcome == ScriptRuntime.ReloadOutcome.CompileError) { error = true; errMsg = sr.LastReloadError; }
                    }
                    catch (Exception hx) { error = true; errMsg = hx.Message; }
                    if (error) { _hotReloadState = 2; _hotReloadMsg = errMsg ?? "compile error"; Log("hot-reload FAILED: " + errMsg); }
                    else if (parts.Count > 0) { _hotReloadState = 1; _hotReloadMsg = string.Join(" + ", parts); Log("hot-reloaded: " + _hotReloadMsg); }
                    else _hotReloadState = -1;
                    _hotReloadAt = DateTime.UtcNow;
                }

                // Smoke-test mode (CI / port verification): capture a frame, then close the window.
                _runSeconds += Math.Min(dt, 0.25f);
                if (_options.ExitAfterSeconds > 0f)
                {
                    if (!_captured && !string.IsNullOrEmpty(_options.CapturePath) && _runSeconds >= Math.Max(0.5, _options.ExitAfterSeconds - 0.5))
                    {
                        _captured = true;
                        try { VortexAPI.CaptureFrame(_options.CapturePath); Log("capture -> " + _options.CapturePath); } catch (Exception cx) { Log("capture failed: " + cx.Message); }
                    }
                    if (_runSeconds >= _options.ExitAfterSeconds) { Log("exit-after reached"); VortexAPI.RequestGameHostExit(); }
                }

                bool playing = PlayModeService.Instance.State == PlayState.Playing;
                int cw = Math.Max(1, VortexAPI.GameHostClientWidth());
                int ch = Math.Max(1, VortexAPI.GameHostClientHeight());
                float mx = VortexAPI.GameHostMouseX();
                float my = VortexAPI.GameHostMouseY();
                bool down = VortexAPI.GameHostMouseDown();
                bool pressed = down && !_lmbPrev; _lmbPrev = down;

                bool f12 = VortexAPI.GameHostKeyDown(0x7B);
                if (f12 && !_f12Prev)
                {
                    try
                    {
                        string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                        if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
                        string path = Path.Combine(dir, "vortex_screenshot.bmp");
                        VortexAPI.CaptureFrame(path);
                        Log("SCREENSHOT -> " + path);
                    }
                    catch { }
                }
                _f12Prev = f12;

                bool wantCapture = playing && VuiStack.Instance.WantsCursorCapture(sr.CursorLocked);
                VortexAPI.SetGameHostMouseCaptured(wantCapture);
                if (wantCapture)
                {
                    float dxl = VortexAPI.GameHostMouseDX(), dyl = VortexAPI.GameHostMouseDY();
                    dxl = Math.Clamp(dxl, -200f, 200f); dyl = Math.Clamp(dyl, -200f, 200f);
                    Vortex.Input.MouseDeltaX = dxl; Vortex.Input.MouseDeltaY = dyl;
                }
                else { Vortex.Input.MouseDeltaX = 0f; Vortex.Input.MouseDeltaY = 0f; }

                if (_script != null)
                {
                    var (sdx, sdy) = _script.Advance(_runSeconds, dt, path => { VortexAPI.CaptureFrame(path); Log("capture -> " + path); });
                    Vortex.Input.MouseDeltaX += sdx; Vortex.Input.MouseDeltaY += sdy;
                    if (_script.ExitRequested) { Log("input script: exit"); VortexAPI.RequestGameHostExit(); }
                }

                // Debug free-fly camera (P / Caps Lock) - development runs only, stripped from a shipped Release.
                _debugCam.PreScripts(ProjectData.Current?.ActiveScene, playing && !PlayModeService.Instance.IsReleaseMode, true);

                Vortex.Input.ScrollDelta = (playing && !VuiStack.Instance.HasActiveScreens) ? VortexAPI.GameHostMouseWheel() : 0f;

                sr.SetUIFrame(cw, ch, mx, my, down, pressed);
                VortexAPI.UIBegin(cw, ch);

                if (_fpsT0 == DateTime.MinValue) _fpsT0 = DateTime.Now;
                _frames++;
                var now = DateTime.Now;
                if ((now - _fpsT0).TotalMilliseconds >= 2000)
                {
                    var p = ProjectData.Current;
                    Log("FPS=" + _frames / 2 + " scene=" + (p != null && p.ActiveScene != null ? p.ActiveScene.Name : "?")
                        + " draws=" + VortexAPI.DrawCalls + " drawn=" + VortexAPI.InstancesDrawn + "/" + VortexAPI.InstancesTested);
                    _frames = 0; _fpsT0 = now;
                }

                if (playing)
                {
                    VortexAPI.StepEngineRuntime(dt);
                    sr.Update(dt);
                    GameRuntime.ProcessPendingSceneSwitch();
                    AudioPlaybackService.Instance.Tick();
                }

                if (VuiStack.Instance.HasActiveScreens)
                {
                    VuiStack.Instance.TickAll(cw, ch, BuildVuiInput(mx, my, down, pressed));
                    var acts = VuiStack.Instance.ConsumeFiredActions();
                    if (acts != null) sr.InvokeUiActions(acts);
                }

                DrawHotReloadOverlay(cw, ch);
                if (playing && !PlayModeService.Instance.IsReleaseMode) _debugCam.DrawHint(cw, ch);

                var scene = ProjectData.Current != null ? ProjectData.Current.ActiveScene : null;
                if (!_debugCam.ApplyView(dt)) PlayCameraHelper.ApplyMainCamera(scene);

                bool dynamicDirty = playing && (SceneRenderService.RuntimeDirty || Editor.Core.Animation.AnimationService.Instance.HasActiveAnimators);
                if (scene != null && (!ReferenceEquals(scene, _submittedScene) || WorldService.Dirty || dynamicDirty))
                {
                    SceneRenderService.Instance.SubmitScene(scene);
                    WorldService.Submit();
                    WorldService.ClearDirty();
                    SceneRenderService.RuntimeDirty = false;
                    _submittedScene = scene;
                }
            }
            catch (Exception ex) { Log("[Tick] " + ex); }
        }

        private static VuiInput BuildVuiInput(float mx, float my, bool down, bool pressed)
        {
            int wheel = VortexAPI.GameHostMouseWheel();
            int cc = 0; for (int c; cc < _vuiCharBuf.Length && (c = VortexAPI.GameHostNextChar()) >= 0;) _vuiCharBuf[cc++] = (char)c;
            int kc = 0; for (int k; kc < _vuiKeyBuf.Length && (k = VortexAPI.GameHostNextKeyPressed()) > 0;) _vuiKeyBuf[kc++] = k;
            return new VuiInput { Mx = mx, My = my, Down = down, Pressed = pressed, Wheel = wheel, Chars = _vuiCharBuf, CharCount = cc, KeyEvents = _vuiKeyBuf, KeyCount = kc };
        }

        private static void DrawHotReloadOverlay(int cw, int ch)
        {
            if (_hotReloadState < 0) return;
            double elapsed = (DateTime.UtcNow - _hotReloadAt).TotalSeconds;
            double D = _hotReloadState == 0 ? 3.0 : (_hotReloadState == 2 ? 4.5 : 1.6);
            if (elapsed < 0 || elapsed > D) { _hotReloadState = -1; return; }
            float t = (float)(elapsed / D);
            float fade = (_hotReloadState == 0) ? 1f : (t < 0.7f ? 1f : (1f - (t - 0.7f) / 0.3f));
            if (fade < 0f) fade = 0f;
            float tr, tg, tb, br, bg, bb; string title;
            if (_hotReloadState == 1) { tr = 0.60f; tg = 0.90f; tb = 0.65f; br = 0.42f; bg = 0.80f; bb = 0.46f; title = "Hot-reloaded  ✓"; }
            else if (_hotReloadState == 2) { tr = 1.0f; tg = 0.60f; tb = 0.60f; br = 0.92f; bg = 0.35f; bb = 0.35f; title = "Hot-reload failed  ✗"; }
            else { tr = 0.78f; tg = 0.75f; tb = 0.98f; br = 0.55f; bg = 0.48f; bb = 0.95f; title = "Hot-reloading…"; }
            VortexAPI.UIRect(0f, 0f, cw, ch, 0.02f, 0.02f, 0.03f, 0.5f * fade, 0f);
            float cardW = _hotReloadState == 2 ? 560f : 420f, cardH = 104f;
            float x = (cw - cardW) * 0.5f, y = (ch - cardH) * 0.5f;
            VortexAPI.UIRect(x, y, cardW, cardH, 0.09f, 0.08f, 0.10f, 0.97f * fade, 12f);
            VortexAPI.UIText(x + 26f, y + 20f, cardW - 52f, 26f, title, 17f, tr, tg, tb, fade, 0, 600);
            VortexAPI.UIText(x + 26f, y + 48f, cardW - 52f, 22f, _hotReloadMsg ?? "", 12f, 0.80f, 0.80f, 0.84f, fade, 0, 400);
            float bx = x + 26f, by = y + cardH - 22f, bw = cardW - 52f;
            VortexAPI.UIRect(bx, by, bw, 5f, 0.16f, 0.16f, 0.18f, fade, 3f);
            if (_hotReloadState == 0)
            {
                float w = bw * 0.35f;
                float px = bx + (bw - w) * (0.5f + 0.5f * (float)Math.Sin(elapsed * 6.0));
                VortexAPI.UIRect(px, by, w, 5f, br, bg, bb, fade, 3f);
            }
            else VortexAPI.UIRect(bx, by, bw, 5f, br, bg, bb, fade, 3f);
        }

        private static void DumpScene(Editor.Core.Data.Scene scene)
        {
            Console.WriteLine("SCENE " + scene.Name);
            foreach (var e in scene.Entities) if (e != null && e.Parent == null) DumpEntity(e, 0);
        }

        private static void DumpEntity(Editor.ECS.GameEntity e, int depth)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(new string(' ', depth * 2)).Append(e.Name);
            if (!e.IsActive) sb.Append(" [inactive]");
            var t = e.GetComponent<Editor.ECS.Components.Transform>();
            if (t != null)
                sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "  pos=({0:0.##},{1:0.##},{2:0.##}) rot=({3:0.#},{4:0.#},{5:0.#}) scale=({6:0.###},{7:0.###},{8:0.###})",
                    t.LocalPosition.X, t.LocalPosition.Y, t.LocalPosition.Z, t.LocalRotation.X, t.LocalRotation.Y, t.LocalRotation.Z, t.LocalScale.X, t.LocalScale.Y, t.LocalScale.Z));
            foreach (var c in e.Components)
            {
                if (c == null || c is Editor.ECS.Components.Transform) continue;
                sb.Append("  <").Append(c.GetType().Name);
                if (c is Editor.ECS.Components.Rendering.MeshRenderer mr) sb.Append(" layer=" + mr.RenderLayer + " mesh=" + Path.GetFileName(mr.MeshPath ?? "") + (string.IsNullOrEmpty(mr.MaterialPath) ? "" : " mat=" + Path.GetFileName(mr.MaterialPath)));
                if (c is Editor.ECS.Components.Animation.TwoBoneIk ik) sb.Append(" tip=" + ik.TipBone + " target=" + ik.TargetBone + " w=" + ik.Weight + " autoGrip=" + ik.AutoGrip + " pole=" + ik.PoleAngle + " tipRot=" + ik.ApplyTipRotation + " off=" + ik.TargetOffsetPosition.X + "," + ik.TargetOffsetPosition.Y + "," + ik.TargetOffsetPosition.Z);
                if (c is Editor.ECS.Components.Animation.Animator an)
                {
                    sb.Append(" clips=" + (an.Clips != null ? an.Clips.Count : 0));
                    foreach (var bone in new[] { "mixamorig:Hips", "mixamorig:Head", "mixamorig:RightHand" })
                        if (Editor.Core.Animation.BoneSocketService.Instance.TryGetBoneTransform(e, bone, out var bp, out var br))
                            sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, " {0}=({1:0.##},{2:0.##},{3:0.##})", bone.Substring(10), bp.X, bp.Y, bp.Z));
                        else sb.Append(" " + bone.Substring(10) + "=?");
                }
                if (c is Editor.ECS.Components.Scripting.Script s)
                {
                    sb.Append(" " + Path.GetFileName(s.ScriptPath ?? ""));
                    if (s.FieldValues != null) foreach (var fv in s.FieldValues) if (fv != null) sb.Append(" " + fv.Name + "=" + fv.Value);
                }
                sb.Append('>');
            }
            Console.WriteLine(sb.ToString());
            foreach (var ch in e.Children) DumpEntity(ch, depth + 1);
        }

        private static void Log(string m)
        {
            try { File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\n"); } catch { }
            Console.WriteLine(m);
        }
    }
}
