using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Scripting;

namespace Editor.Scripting
{
    /// <summary>
    /// In-engine gameplay scripting runtime — the Unity/Unreal-style "compile + run" loop. On Play it
    /// compiles every Assets/Scripts/*.cs (via the in-box C# compiler, no extra dependencies) into one
    /// assembly, instantiates the VortexBehaviour attached to each entity through its Script component,
    /// and calls Start once + Update every tick (OnDestroy on Stop). Behaviours affect the live game
    /// through this class (it is the IScriptHost: move entities, read input).
    /// </summary>
    public sealed class ScriptRuntime : Vortex.IScriptHost
    {
        private static ScriptRuntime _instance;
        public static ScriptRuntime Instance => _instance ?? (_instance = new ScriptRuntime());

        // Maps a UNIQUE per-behaviour handle -> the entity it's attached to. We assign our own handle
        // (not the engine EntityId) because engine ids can collide/be invalid for editor entities; a
        // collision previously made a script move the WRONG entity (e.g. the Ground instead of the camera).
        private readonly Dictionary<long, GameEntity> _entitiesById = new Dictionary<long, GameEntity>();
        private long _nextHandle;
        private readonly List<Vortex.VortexBehaviour> _behaviours = new List<Vortex.VortexBehaviour>();
        public string DebugBehaviourNames()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _behaviours.Count; i++) { if (i > 0) sb.Append(","); sb.Append(_behaviours[i].GetType().Name); }
            return _behaviours.Count + "[" + sb + "]";
        }
        private bool _active;

        /// <summary>Last compile diagnostics (empty on success) — surfaced to the user.</summary>
        public string LastBuildLog { get; private set; } = "";

        /// <summary>Compile the project's scripts and start every attached behaviour.</summary>
        /// <summary>When set (by the standalone player), gameplay runs from this PRE-COMPILED assembly
        /// instead of compiling source at startup — fast boot + no .cs source shipped with the game.</summary>
        public Assembly PrecompiledAssembly { get; set; }

        public void Begin(Scene scene)
        {
            End();
            if (scene?.Entities == null) return;

            Vortex.VortexBehaviour.Host = this;
            Vortex.Input.Host = this;
            Vortex.UI.Host = this;
            Vortex.Scene.Host = this;
            Vortex.Cursor.Host = this;
            Vortex.Application.Host = this;
            Vortex.Camera.Host = this;

            _entitiesById.Clear();
            _nextHandle = 0;

            string log;
            Assembly asm = PrecompiledAssembly;
            if (asm != null) log = "Using precompiled gameplay assembly: " + asm.GetName().Name;
            else asm = Compile(out log);
            LastBuildLog = log ?? "";
            if (!string.IsNullOrEmpty(LastBuildLog))
                System.Diagnostics.Debug.WriteLine("[ScriptRuntime] build:\n" + LastBuildLog);
            if (asm == null) { _active = true; return; } // no scripts / compile failed -> nothing to run

            foreach (var e in scene.Entities) InstantiateRecursive(e, asm);

            _active = true;
            for (int i = 0; i < _behaviours.Count; i++)
            {
                try { _behaviours[i].Start(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] Start error: " + ex.Message); }
            }
        }

        /// <summary>Tick every running behaviour.</summary>
        public void Update(float dt)
        {
            if (!_active) return;
            Vortex.Time.DeltaTime = dt;
            for (int i = 0; i < _behaviours.Count; i++)
            {
                try { _behaviours[i].Update(dt); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] Update error: " + ex.Message); }
            }
        }

        /// <summary>Stop all behaviours.</summary>
        public void End()
        {
            for (int i = 0; i < _behaviours.Count; i++)
            {
                try { _behaviours[i].OnDestroy(); } catch { }
            }
            _behaviours.Clear();
            _entitiesById.Clear();
            _active = false;
        }

        private void InstantiateRecursive(GameEntity e, Assembly asm)
        {
            if (e == null) return;
            var script = e.GetComponent<Script>();
            if (script != null && !string.IsNullOrEmpty(script.ScriptClassName))
            {
                var type = FindBehaviourType(asm, script.ScriptClassName);
                if (type != null)
                {
                    try
                    {
                        var behaviour = (Vortex.VortexBehaviour)Activator.CreateInstance(type);
                        // Assign a UNIQUE handle and map it to THIS entity — guarantees the behaviour can
                        // only ever read/move its own entity (no engine-EntityId collisions).
                        long handle = ++_nextHandle;
                        behaviour.EntityId = handle;
                        _entitiesById[handle] = e;
                        _behaviours.Add(behaviour);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ScriptRuntime] instantiate '" + script.ScriptClassName + "' failed: " + ex.Message); }
                }
            }
            if (e.Children != null)
                foreach (var c in e.Children) InstantiateRecursive(c, asm);
        }

        private static Type FindBehaviourType(Assembly asm, string className)
        {
            // Match by simple name, ignoring namespace; must derive from VortexBehaviour.
            foreach (var t in asm.GetTypes())
            {
                if (!typeof(Vortex.VortexBehaviour).IsAssignableFrom(t) || t.IsAbstract) continue;
                if (string.Equals(t.Name, className, StringComparison.Ordinal)) return t;
            }
            return null;
        }

        private Assembly Compile(out string log)
        {
            log = null;
            string dir = ScriptingService.ScriptsDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            // Compile every script EXCEPT the VS-only API stub (the real Vortex API lives in this assembly).
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("VortexScripting.cs", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (files.Length == 0) return null;

            try
            {
                using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
                {
                    var p = new CompilerParameters
                    {
                        GenerateInMemory = true,
                        GenerateExecutable = false,
                        TreatWarningsAsErrors = false
                    };
                    p.ReferencedAssemblies.Add("mscorlib.dll");
                    p.ReferencedAssemblies.Add("System.dll");
                    p.ReferencedAssemblies.Add("System.Core.dll");
                    // Reference this editor assembly so scripts get the real Vortex.* API + shared types.
                    p.ReferencedAssemblies.Add(typeof(Vortex.VortexBehaviour).Assembly.Location);

                    CompilerResults results = provider.CompileAssemblyFromFile(p, files);
                    if (results.Errors.HasErrors)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (CompilerError err in results.Errors)
                            if (!err.IsWarning)
                                sb.AppendLine($"{Path.GetFileName(err.FileName)}({err.Line}): {err.ErrorText}");
                        log = "Script compile failed:\n" + sb;
                        return null;
                    }
                    return results.CompiledAssembly;
                }
            }
            catch (Exception ex)
            {
                log = "Script compile exception: " + ex.Message;
                return null;
            }
        }

        // ---- IScriptHost (behaviours act on the live game through these; transforms go through the C#
        // model so the viewport reflects them immediately) ----

        Vortex.Vector3 Vortex.IScriptHost.GetPosition(long entityId)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
            {
                var p = e.Transform.LocalPosition;
                return new Vortex.Vector3(p.X, p.Y, p.Z);
            }
            return Vortex.Vector3.Zero;
        }

        void Vortex.IScriptHost.SetPosition(long entityId, Vortex.Vector3 position)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
                e.Transform.LocalPosition = new ECS.Vector3(position.X, position.Y, position.Z);
        }

        Vortex.Vector3 Vortex.IScriptHost.GetRotation(long entityId)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
            {
                var r = e.Transform.LocalRotation; // Euler degrees
                return new Vortex.Vector3(r.X, r.Y, r.Z);
            }
            return Vortex.Vector3.Zero;
        }

        void Vortex.IScriptHost.SetRotation(long entityId, Vortex.Vector3 eulerDegrees)
        {
            if (_entitiesById.TryGetValue(entityId, out var e) && e.Transform != null)
                e.Transform.LocalRotation = new ECS.Vector3(eulerDegrees.X, eulerDegrees.Y, eulerDegrees.Z);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // --- deferred scene-switch request (set by a script via Vortex.Scene.Load; applied by the driver) ---
        private string _pendingScene;
        void Vortex.IScriptHost.LoadScene(string name) { _pendingScene = name; }
        /// <summary>Returns the requested scene name (and clears it), or null. The runtime driver calls this
        /// AFTER Update() and performs the switch (so it never happens mid-tick).</summary>
        public string ConsumePendingScene() { var s = _pendingScene; _pendingScene = null; return s; }
        public string PendingScene { get { return _pendingScene; } } // diagnostic peek

        // --- mouse mode (game-controlled via Vortex.Cursor; the GameWindow enforces capture) ---
        private bool _cursorLocked; // default false: free cursor (menus/lobby) until a script locks it
        void Vortex.IScriptHost.SetCursorLocked(bool locked) { _cursorLocked = locked; }
        bool Vortex.IScriptHost.GetCursorLocked() { return _cursorLocked; }
        public bool CursorLocked { get { return _cursorLocked; } }

        // --- UI overlay frame state (fed by the runtime driver each frame: GameWindow / GamePreview) ---
        private float _uiW, _uiH, _mouseX, _mouseY;
        private bool _mouseDown, _mousePressed;

        /// <summary>Called once per frame before Update() so scripts can draw UI + hit-test the mouse.</summary>
        public void SetUIFrame(float width, float height, float mouseX, float mouseY, bool mouseDown, bool mousePressed)
        {
            _uiW = width; _uiH = height; _mouseX = mouseX; _mouseY = mouseY;
            _mouseDown = mouseDown; _mousePressed = mousePressed;
        }

        void Vortex.IScriptHost.UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius)
            => Editor.DllWrapper.VortexAPI.UIRect(x, y, w, h, r, g, b, a, radius);
        void Vortex.IScriptHost.UIText(float x, float y, float w, float h, string text, float size, float r, float g, float b, float a, int align, int weight)
            => Editor.DllWrapper.VortexAPI.UIText(x, y, w, h, text, size, r, g, b, a, align, weight);
        void Vortex.IScriptHost.UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick)
            => Editor.DllWrapper.VortexAPI.UILine(x1, y1, x2, y2, r, g, b, a, thick);
        float Vortex.IScriptHost.UIWidth() => _uiW;
        float Vortex.IScriptHost.UIHeight() => _uiH;
        float Vortex.IScriptHost.UIMouseX() => _mouseX;
        float Vortex.IScriptHost.UIMouseY() => _mouseY;
        bool Vortex.IScriptHost.UIMouseDown() => _mouseDown;
        bool Vortex.IScriptHost.UIMousePressed() => _mousePressed;

        void Vortex.IScriptHost.QuitGame()
        {
            try
            {
                // Standalone player: close the app. In-editor play: just stop play (don't kill the editor).
                if (Editor.Core.Services.PlayModeService.Instance.IsReleaseMode)
                    System.Windows.Application.Current?.Shutdown();
                else
                    Editor.Core.Services.PlayModeService.Instance.Stop();
            }
            catch { }
        }

        void Vortex.IScriptHost.SetCameraFov(float fovDegrees)
        {
            try { Editor.DllWrapper.VortexAPI.SetViewFOV(fovDegrees); }
            catch { }
        }

        bool Vortex.IScriptHost.GetKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (!Enum.TryParse(key, true, out Key k)) return false;
            // Use the global physical key state (not WPF Keyboard.IsKeyDown): while playing, focus is on
            // a native swapchain HWND (editor viewport or the standalone game window), where the WPF
            // keyboard device reports nothing — so WASD/jump would do nothing. GetAsyncKeyState works
            // regardless of which window/HWND has focus.
            int vk = KeyInterop.VirtualKeyFromKey(k);
            if (vk == 0) return false;
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }
    }
}
