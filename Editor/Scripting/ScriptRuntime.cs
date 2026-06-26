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

        private readonly Dictionary<long, GameEntity> _entitiesById = new Dictionary<long, GameEntity>();
        private readonly List<Vortex.VortexBehaviour> _behaviours = new List<Vortex.VortexBehaviour>();
        private bool _active;

        /// <summary>Last compile diagnostics (empty on success) — surfaced to the user.</summary>
        public string LastBuildLog { get; private set; } = "";

        /// <summary>Compile the project's scripts and start every attached behaviour.</summary>
        public void Begin(Scene scene)
        {
            End();
            if (scene?.Entities == null) return;

            Vortex.VortexBehaviour.Host = this;
            Vortex.Input.Host = this;

            _entitiesById.Clear();
            foreach (var e in scene.Entities) MapEntitiesRecursive(e);

            Assembly asm = Compile(out string log);
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

        private void MapEntitiesRecursive(GameEntity e)
        {
            if (e == null) return;
            if (Utilities.ID.IsValid(e.EntityId)) _entitiesById[e.EntityId] = e;
            if (e.Children != null)
                foreach (var c in e.Children) MapEntitiesRecursive(c);
        }

        private void InstantiateRecursive(GameEntity e, Assembly asm)
        {
            if (e == null) return;
            var script = e.GetComponent<Script>();
            if (script != null && !string.IsNullOrEmpty(script.ScriptClassName) && Utilities.ID.IsValid(e.EntityId))
            {
                var type = FindBehaviourType(asm, script.ScriptClassName);
                if (type != null)
                {
                    try
                    {
                        var behaviour = (Vortex.VortexBehaviour)Activator.CreateInstance(type);
                        behaviour.EntityId = e.EntityId;
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

        bool Vortex.IScriptHost.GetKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return Enum.TryParse(key, true, out Key k) && Keyboard.IsKeyDown(k);
        }
    }
}
