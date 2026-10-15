using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>OnEnable / OnDisable (#422) through the real script runtime: the order against Start and OnDestroy at play start,
    /// on Scene.SetActive (self and ancestors), the Script component's switch (Enabled, the Inspector checkbox), Instantiate,
    /// Destroy and play stop; once per activation, nothing for a child switched off on its own, several scripts per entity
    /// each with its own calls.</summary>
    public static class LifecycleTests
    {
        private const string Scripts = @"
using System.Collections.Generic;
using Vortex;

public static class Life { public static List<string> Log = new List<string>(); }

public class Tracer : VortexBehaviour
{
    public string Tag = ""?"";
    public int Updates, LateUpdates, Fixed;
    public override void OnEnable() { Life.Log.Add(Tag + "".OnEnable""); }
    public override void Start() { Life.Log.Add(Tag + "".Start""); }
    public override void OnDisable() { Life.Log.Add(Tag + "".OnDisable""); }
    public override void OnDestroy() { Life.Log.Add(Tag + "".OnDestroy""); }
    public override void Update(float dt) { Updates++; }
    public override void LateUpdate(float dt) { LateUpdates++; }
    public override void FixedUpdate(float dt) { Fixed++; }
}

public class SelfSwitcher : VortexBehaviour
{
    public override void OnDisable() { Scene.SetActive(EntityId, true); Life.Log.Add(""Switcher.OnDisable active="" + Scene.IsActive(EntityId)); }
}

public class Thrower : VortexBehaviour
{
    public override void OnEnable() { throw new System.InvalidOperationException(""enable boom""); }
    public override void OnDisable() { throw new System.InvalidOperationException(""disable boom""); }
}

public class Spawner : VortexBehaviour
{
    public string Prefab = """";
    public long Spawned;
    public override void Start()
    {
        Life.Log.Add(""Spawner.Instantiate"");
        Spawned = Scene.Instantiate(Prefab, new Vector3(0f, 0f, 0f));
        Life.Log.Add(""Spawner.Returned"");
    }
}

public class SelfDisabler : VortexBehaviour
{
    public int Updates;
    public override void Start() { Enabled = false; Life.Log.Add(""SelfDisabler.Start enabled="" + Enabled + "" active="" + IsActiveAndEnabled); }
    public override void OnDisable() { Life.Log.Add(""SelfDisabler.OnDisable""); }
    public override void Update(float dt) { Updates++; }
}
";

        private static Assembly Compile(TestContext t)
        {
            var file = t.Write("Scripts/Life.cs", Scripts);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the test scripts do not compile: " + log);
            return asm;
        }

        private static List<string> Log(Assembly asm) => (List<string>)asm.GetType("Life").GetField("Log").GetValue(null);
        private static object Field(object o, string name) => o.GetType().GetField(name).GetValue(o);

        private static GameEntity Entity(Scene s, GameEntity parent, string name, bool active = true)
        {
            var e = new GameEntity(s, name);
            if (parent != null) { e.Parent = parent; parent.Children.Add(e); } else s.Entities.Add(e);
            e.SetActiveFlagOnly(active);
            return e;
        }

        private static Script Trace(GameEntity e, string tag, string cls = "Tracer", bool enabled = true)
        {
            var sc = new Script(e, "Assets/Scripts/" + cls + ".cs");
            sc.SetFieldValue("Tag", tag);
            sc.IsEnabled = enabled;
            e.AddComponentDirect(sc);
            return sc;
        }

        private static string Joined(List<string> log) => string.Join(" | ", log);

        [Test]
        public static void PlayStartEnablesTheActiveScriptsBeforeAnyStart(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Start" };
            var a = Entity(scene, null, "A"); Trace(a, "A");
            var b = Entity(scene, null, "B"); Trace(b, "B"); Trace(b, "B2");   // two scripts on one entity (#408)
            var off = Entity(scene, null, "Off", active: false); Trace(off, "Off");
            var child = Entity(scene, off, "Child"); Trace(child, "Child");
            var lone = Entity(scene, a, "Lone", active: false); Trace(lone, "Lone");   // switched off on its own under an active parent
            var rt = ScriptRuntime.Instance;
            try
            {
                var log = Log(asm); log.Clear();
                rt.Begin(scene, asm);
                var starts = log.Where(l => l.EndsWith(".Start")).ToList();
                var enables = log.Where(l => l.EndsWith(".OnEnable")).ToList();
                t.Equal("A.OnEnable,B.OnEnable,B2.OnEnable", string.Join(",", enables), "the active scripts (each of an entity's scripts) get OnEnable: " + Joined(log));
                t.True(log.IndexOf("B2.OnEnable") < log.IndexOf("A.Start"), "every OnEnable before the first Start: " + Joined(log));
                t.Equal(6, starts.Count, "every script starts — under an inactive entity too, as before #422: " + Joined(log));
                t.False(log.Contains("Off.OnEnable") || log.Contains("Child.OnEnable") || log.Contains("Lone.OnEnable"), "no OnEnable while inactive in the hierarchy");
                t.True(rt.BehavioursOf(a)[0].IsActiveAndEnabled && !rt.BehavioursOf(off)[0].IsActiveAndEnabled, "IsActiveAndEnabled tells the two apart");
                rt.Update(1f / 60f);
                t.Equal(1, (int)Field(rt.BehavioursOf(off)[0], "Updates"), "an inactive entity's script keeps its Update (compatible)");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void SetActiveNotifiesTheSubtreeOncePerChange(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Toggle" };
            var root = Entity(scene, null, "Root"); Trace(root, "Root");
            var kid = Entity(scene, root, "Kid"); Trace(kid, "Kid"); Trace(kid, "Kid2");
            var lone = Entity(scene, root, "Lone", active: false); Trace(lone, "Lone");
            var deep = Entity(scene, lone, "Deep"); Trace(deep, "Deep");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                var log = Log(asm); log.Clear();
                long rootId = rt.HandleForEntity(root), kidId = rt.HandleForEntity(kid), loneId = rt.HandleForEntity(lone);

                Vortex.Scene.SetActive(rootId, false);
                t.Equal("Root.OnDisable,Kid.OnDisable,Kid2.OnDisable", string.Join(",", log), "switching off: the active subtree, each script once, parent first");
                log.Clear();
                Vortex.Scene.SetActive(rootId, false);
                t.Equal(0, log.Count, "switching off again: nothing");
                Vortex.Scene.SetActive(kidId, true);   // already on itself, under an off parent
                Vortex.Scene.SetActive(kidId, false);
                t.Equal(0, log.Count, "a child switched under an inactive parent: its activeInHierarchy does not change, no calls: " + Joined(log));
                Vortex.Scene.SetActive(kidId, true);
                log.Clear();

                Vortex.Scene.SetActive(rootId, true);
                t.Equal("Root.OnEnable,Kid.OnEnable,Kid2.OnEnable", string.Join(",", log), "switching on: the same scripts, no Start again");
                t.False(log.Any(l => l.StartsWith("Lone") || l.StartsWith("Deep")), "the child switched off on its own (and its subtree) stays out");
                log.Clear();
                Vortex.Scene.SetActive(loneId, true);
                t.Equal("Lone.OnEnable,Deep.OnEnable", string.Join(",", log), "switching the lone child on: it and its subtree");
                log.Clear();
                Vortex.Scene.SetActive(rootId, false);
                t.Equal("Root.OnDisable,Kid.OnDisable,Kid2.OnDisable,Lone.OnDisable,Deep.OnDisable", string.Join(",", log), "now all of them go");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void TheScriptSwitchStopsTheTicksAndCallsTheHooks(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Switch" };
            var e = Entity(scene, null, "Turret");
            var sc = Trace(e, "T");
            var later = Trace(e, "Later", enabled: false);   // switched off in the Inspector: no behaviour at play start, as before
            var self = Entity(scene, null, "Self"); Trace(self, "S", "SelfDisabler");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                var log = Log(asm);
                var b = rt.BehavioursOf(e)[0];
                t.Equal(1, rt.BehavioursOf(e).Count, "the switched-off Script has no behaviour yet");
                t.True(log.Contains("SelfDisabler.OnDisable") && log.Contains("SelfDisabler.Start enabled=False active=False"), "Enabled = false in Start: OnDisable at once: " + Joined(log));
                rt.Update(1f / 30f);
                t.Equal(0, (int)Field(rt.BehavioursOf(self)[0], "Updates"), "a disabled script gets no Update");
                log.Clear();

                b.Enabled = false;
                t.Equal("T.OnDisable", string.Join(",", log), "Enabled = false: OnDisable");
                t.False(sc.IsEnabled, "… the Script component's switch is off (the Inspector shows it)");
                int u = (int)Field(b, "Updates"), l = (int)Field(b, "LateUpdates"), f = (int)Field(b, "Fixed");
                rt.Update(1f / 30f);
                t.True((int)Field(b, "Updates") == u && (int)Field(b, "LateUpdates") == l && (int)Field(b, "Fixed") == f, "no Update, LateUpdate or FixedUpdate while disabled");
                log.Clear();
                b.Enabled = true;
                t.Equal("T.OnEnable", string.Join(",", log), "Enabled = true: OnEnable, no second Start");
                rt.Update(1f / 30f);
                t.True((int)Field(b, "Updates") == u + 1 && (int)Field(b, "Fixed") > f, "ticks again");

                // the Inspector's checkbox (a property change from outside the script): applied at the next tick
                log.Clear();
                sc.IsEnabled = false;
                t.Equal(0, log.Count, "not inside the property change");
                rt.Update(1f / 60f);
                t.Equal("T.OnDisable", string.Join(",", log), "the next tick applies it");
                sc.IsEnabled = true;
                rt.Update(1f / 60f);
                t.True(log.Last() == "T.OnEnable", "and back on: " + Joined(log));

                // a Script switched off at play start comes alive when it is switched on
                log.Clear();
                later.IsEnabled = true;
                rt.Update(1f / 60f);
                t.Equal(2, rt.BehavioursOf(e).Count, "switched on while playing: its behaviour is made");
                t.Equal("Later.OnEnable,Later.Start", string.Join(",", log), "… OnEnable, then Start");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void DestroyAndPlayStopDisableBeforeTheyDestroy(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Destroy" };
            var root = Entity(scene, null, "Root"); Trace(root, "Root");
            var kid = Entity(scene, root, "Kid"); Trace(kid, "Kid");
            var lone = Entity(scene, root, "Lone", active: false); Trace(lone, "Lone");
            var keep = Entity(scene, null, "Keep"); Trace(keep, "Keep");
            var sleeper = Entity(scene, null, "Sleeper", active: false); Trace(sleeper, "Sleeper");
            var rt = ScriptRuntime.Instance;
            var log = Log(asm);
            try
            {
                rt.Begin(scene, asm);
                log.Clear();
                Vortex.Scene.Destroy(rt.HandleForEntity(root));
                t.Equal("Root.OnDisable,Kid.OnDisable,Root.OnDestroy,Kid.OnDestroy,Lone.OnDestroy", string.Join(",", log),
                        "Destroy: OnDisable for the active scripts, then OnDestroy for every one (the inactive child: OnDestroy only)");
                log.Clear();
            }
            finally { rt.End(); }
            t.Equal("Keep.OnDisable,Keep.OnDestroy,Sleeper.OnDestroy", string.Join(",", log), "play stop: OnDisable first, then OnDestroy; an inactive script only OnDestroy");
        }

        [Test]
        public static void SpawnedPrefabsEnableBeforeTheyStartInsideInstantiate(TestContext t)
        {
            var asm = Compile(t);
            var template = new GameEntity("Crate");
            var sc = new Script(template, "Assets/Scripts/Tracer.cs"); sc.SetFieldValue("Tag", "Crate"); template.AddComponentDirect(sc);
            string prefab = t.Write("Crate.ventity", DataSerializer.ToJson(template));
            var scene = new Scene { Name = "Spawn" };
            var spawner = Entity(scene, null, "Spawner");
            var s = new Script(spawner, "Assets/Scripts/Spawner.cs"); s.SetFieldValue("Prefab", prefab); spawner.AddComponentDirect(s);
            var rt = ScriptRuntime.Instance;
            try
            {
                var log = Log(asm); log.Clear();
                rt.Begin(scene, asm);
                t.Equal("Spawner.Instantiate,Crate.OnEnable,Crate.Start,Spawner.Returned", string.Join(",", log), "the spawn's OnEnable and Start run inside Instantiate, OnEnable first");
                long id = (long)Field(rt.BehavioursOf(spawner)[0], "Spawned");
                t.True(id != 0 && rt.BehavioursOf(id)[0].IsActiveAndEnabled, "the spawned script is active");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void HookExceptionsReachTheConsoleAndSelfSwitchingIsRefused(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Edge" };
            var thrower = Entity(scene, null, "Thrower"); Trace(thrower, "X", "Thrower");
            var sw = Entity(scene, null, "Switcher"); Trace(sw, "W", "SelfSwitcher");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                bool enableLogged = ConsoleService.Instance.Entries.Any(x => x.Level == LogLevel.Error && x.Message.Contains("Thrower.OnEnable(): enable boom"));
                t.True(enableLogged, "an OnEnable exception reaches the Console (#322)");
                Vortex.Scene.SetActive(rt.HandleForEntity(thrower), false);
                bool disableLogged = ConsoleService.Instance.Entries.Any(x => x.Level == LogLevel.Error && x.Message.Contains("Thrower.OnDisable(): disable boom"));
                t.True(disableLogged, "an OnDisable exception too — and the entity is still switched off");
                t.False(thrower.IsActive, "the switch went through");

                var log = Log(asm); log.Clear();
                Vortex.Scene.SetActive(rt.HandleForEntity(sw), false);
                t.True(log.Contains("Switcher.OnDisable active=True"), "inside its own OnDisable the entity is still on: " + Joined(log));
                t.False(sw.IsActive, "a SetActive(true) on itself from OnDisable is ignored: the switch-off wins");
                t.True(ConsoleService.Instance.Entries.Any(x => x.Level == LogLevel.Warning && x.Message.Contains("'Switcher' is being switched on or off right now")), "… with a warning");
            }
            finally { rt.End(); }
        }
    }
}
