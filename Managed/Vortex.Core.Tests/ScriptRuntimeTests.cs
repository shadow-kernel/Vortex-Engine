using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>The scripting runtime with real compiled behaviours (no window, no GPU): every Script component of an
    /// entity runs its own behaviour (#408).</summary>
    public static class ScriptRuntimeTests
    {
        // Two behaviours that log what happens to them into Probe.Log (read back by reflection: the classes live in the
        // runtime-compiled script assembly, not in this one).
        private const string Scripts = @"
using System.Collections.Generic;
using Vortex;

public static class Probe { public static List<string> Log = new List<string>(); }

public class Mover : VortexBehaviour
{
    public float Speed = 1f;
    public int Updates, LateUpdates;
    public override void Start()
    {
        var hp = Scene.GetBehaviour<Health>(EntityId);   // a sibling script, by type
        Probe.Log.Add(""Mover.Start speed="" + Speed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "" sees Health="" + (hp != null ? hp.Hp.ToString() : ""none""));
    }
    public override void Update(float dt) { Updates++; }
    public override void LateUpdate(float dt) { LateUpdates++; }
    public override void OnMessage(string message, object arg) { Probe.Log.Add(""Mover.OnMessage "" + message); }
    public override void OnDestroy() { Probe.Log.Add(""Mover.OnDestroy""); }
}

public class Health : VortexBehaviour
{
    public int Hp = 100;
    public int Updates;
    public override void Start() { Probe.Log.Add(""Health.Start hp="" + Hp); }
    public override void Update(float dt) { Updates++; }
    public override void OnMessage(string message, object arg) { Probe.Log.Add(""Health.OnMessage "" + message); }
    public override void OnDestroy() { Probe.Log.Add(""Health.OnDestroy""); }
}

public class Loot : VortexBehaviour
{
    public string Drop = ""coin"";
    public override void Start() { Probe.Log.Add(""Loot.Start drop="" + Drop); }
    public override void OnDestroy() { Probe.Log.Add(""Loot.OnDestroy""); }
}

public class Caller : VortexBehaviour
{
    public override void Update(float dt) { if (Scene.Find(""Npc"") != 0) SendMessage(Scene.Find(""Npc""), ""ping""); }
}

public class SaveProbe : VortexBehaviour
{
    public override void Start()
    {
        Probe.Log.Add(""slot="" + Save.CurrentSlot + "" credits="" + Save.GetInt(""credits"", 0));
        Save.SetInt(""credits"", 100);
    }
}
";

        private static Assembly Compile(TestContext t)
        {
            var file = t.Write("Scripts/Probe.cs", Scripts);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the test scripts do not compile: " + log);
            return asm;
        }

        private static List<string> Log(Assembly asm) => (List<string>)asm.GetType("Probe").GetField("Log").GetValue(null);
        private static object Field(object o, string name) => o.GetType().GetField(name).GetValue(o);

        private static Script AddScript(GameEntity e, string cls, string field = null, string value = null)
        {
            var sc = new Script(e, "Assets/Scripts/" + cls + ".cs");
            if (field != null) sc.SetFieldValue(field, value);
            e.AddComponentDirect(sc);
            return sc;
        }

        [Test]
        public static void EveryScriptComponentRunsItsOwnBehaviour(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Multi" };
            var npc = new GameEntity(scene, "Npc");
            var mover = AddScript(npc, "Mover", "Speed", "2.5");
            var health = AddScript(npc, "Health", "Hp", "40");
            var single = new GameEntity(scene, "Caller");
            AddScript(single, "Caller");
            scene.Entities.Add(npc);
            scene.Entities.Add(single);

            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                var log = Log(asm);
                var behaviours = rt.BehavioursOf(npc);
                t.Equal(2, behaviours.Count, "two Script components, two behaviours");
                t.Equal("Mover", behaviours[0].GetType().Name, "in component order (1)");
                t.Equal("Health", behaviours[1].GetType().Name, "in component order (2)");
                t.True(log.Contains("Health.Start hp=40"), "the second script's Start ran with ITS field override: " + string.Join(" | ", log));
                t.True(log.Any(l => l.StartsWith("Mover.Start speed=2.5", StringComparison.Ordinal)), "the first script's Start ran with its own override: " + string.Join(" | ", log));
                t.True(log.Contains("Mover.Start speed=2.5 sees Health=40"), "Scene.GetBehaviour<T> finds the sibling by type: " + string.Join(" | ", log));
                t.Equal(behaviours[0].EntityId, behaviours[1].EntityId, "both scripts share the entity's handle");
                t.Equal(rt.HandleForEntity(npc), behaviours[0].EntityId, "… which is the entity's canonical handle");
                t.Equal(1, rt.BehavioursOf(single).Count, "a single-script entity runs one behaviour, as before");
                t.True(rt.BehavioursOf(single)[0].EntityId != behaviours[0].EntityId, "its own handle");

                rt.Update(0.016f);
                rt.Update(0.016f);
                t.Equal(2, (int)Field(behaviours[0], "Updates"), "Mover ticks");
                t.Equal(2, (int)Field(behaviours[1], "Updates"), "Health ticks too (it used to never run)");
                t.Equal(2, (int)Field(behaviours[0], "LateUpdates"), "LateUpdate too");
                t.Equal(2, log.Count(l => l == "Mover.OnMessage ping"), "SendMessage reaches the first script");
                t.Equal(2, log.Count(l => l == "Health.OnMessage ping"), "… and the second (one message per tick)");

                // attach_script while playing: a field change reaches ITS behaviour only (#408), a new script starts
                health.SetFieldValue("Hp", "7");
                t.True(rt.ApplyFieldsToLiveBehaviour(npc, health), "Health is live");
                t.Equal(7, (int)Field(behaviours[1], "Hp"), "Health took its new value");
                t.Equal(2.5f, (float)Field(behaviours[0], "Speed"), "Mover kept its own");
                var loot = AddScript(npc, "Loot", "Drop", "gem");
                t.False(rt.ApplyFieldsToLiveBehaviour(npc, loot), "a script added during play has no behaviour yet");
                t.True(rt.AttachAtRuntime(npc, loot), "… and starts next to the running ones");
                t.True(log.Contains("Loot.Start drop=gem"), "Loot.Start ran with its field");
                t.Equal(3, rt.BehavioursOf(npc).Count, "three behaviours now");
                t.Equal(behaviours[0].EntityId, rt.BehavioursOf(npc)[2].EntityId, "the late script shares the handle");
                t.False(rt.AttachAtRuntime(npc, loot), "a running script is not started twice");
                t.False(rt.AttachAtRuntime(npc), "nothing left to start");

                // Scene.Destroy: every behaviour of the entity gets OnDestroy, exactly once
                Vortex.Scene.Destroy(behaviours[0].EntityId);
                t.Equal(1, log.Count(l => l == "Mover.OnDestroy"), "Mover.OnDestroy");
                t.Equal(1, log.Count(l => l == "Health.OnDestroy"), "Health.OnDestroy");
                t.Equal(1, log.Count(l => l == "Loot.OnDestroy"), "Loot.OnDestroy");
                t.Equal(0, rt.BehavioursOf(npc).Count, "nothing runs on the destroyed entity");
                rt.Update(0.016f);
                t.Equal(2, (int)Field(behaviours[1], "Updates"), "a destroyed behaviour no longer ticks");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void AScriptAddedDuringPlayStartsNextToARunningOne(TestContext t)
        {
            // the MCP attach_script path in play mode (#408): the entity already runs a behaviour of another class
            var asm = Compile(t);
            var scene = new Scene { Name = "Late" };
            var npc = new GameEntity(scene, "Npc");
            var mover = AddScript(npc, "Mover", "Speed", "3");
            scene.Entities.Add(npc);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                var health = AddScript(npc, "Health", "Hp", "55");
                // what attach_script does: apply to the script's own live behaviour, else start it
                bool live = rt.ApplyFieldsToLiveBehaviour(npc, health) || rt.AttachAtRuntime(npc, health);
                t.True(live, "the new script runs");
                t.True(Log(asm).Contains("Health.Start hp=55"), "with its own field value: " + string.Join(" | ", Log(asm)));
                t.Equal(3f, (float)Field(rt.LiveBehaviourOf(npc, mover), "Speed"), "the running Mover kept its value");
                t.Equal("Health", rt.LiveBehaviourOf(npc, health)?.GetType().Name, "the component maps to its behaviour");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void EveryPlaySessionReadsTheSaveFromDisk(TestContext t)
        {
            // #452: Save's cache and slot used to outlive editor play sessions — a save edited or deleted between two runs
            // was ignored and written back over by the next Flush
            var asm = Compile(t);
            var scene = new Scene { Name = "Saves" };
            var gm = new GameEntity(scene, "GameManager");
            AddScript(gm, "SaveProbe");
            scene.Entities.Add(gm);
            string slot0 = Vortex.Save.SlotPath(0), slot2 = Vortex.Save.SlotPath(2);
            string appData = Environment.GetEnvironmentVariable("VORTEX_APPDATA_DIR");
            t.True(!string.IsNullOrEmpty(appData) && slot0.StartsWith(appData, StringComparison.Ordinal), "saves of automated runs stay in their app-data folder: " + slot0);
            foreach (var f in new[] { slot0, slot2 }) if (File.Exists(f)) File.Delete(f);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.BeginPlaySession(scene, asm);                          // play 1 starts from nothing …
                t.Equal("slot=0 credits=0", Log(asm).LastOrDefault(), "a new game");
                rt.End();                                                 // … and Stop writes credits = 100
                t.True(File.ReadAllText(slot0).Contains("credits\ti:100"), "Stop flushed the save");

                File.WriteAllText(slot0, "credits\ti:5\n");               // edited on disk between the runs
                rt.BeginPlaySession(scene, asm);
                t.Equal("slot=0 credits=5", Log(asm).LastOrDefault(), "play 2 reads the edited file, not play 1's cache");
                Vortex.Save.UseSlot(2);
                rt.Begin(scene, asm);                                     // a scene switch inside the session …
                t.Equal("slot=2 credits=0", Log(asm).LastOrDefault(), "… keeps the slot the game chose");
                rt.End();

                File.Delete(slot0);                                       // deleted between the runs ("new game")
                rt.BeginPlaySession(scene, asm);
                t.Equal("slot=0 credits=0", Log(asm).LastOrDefault(), "play 3 starts on slot 0 and sees the file is gone");
                t.Equal(0, Vortex.Save.CurrentSlot, "slot 0 at the start of a session");
            }
            finally
            {
                rt.End();
                foreach (var f in new[] { slot0, slot2 }) try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }
}
