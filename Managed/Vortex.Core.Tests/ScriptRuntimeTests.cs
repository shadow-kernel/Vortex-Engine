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
    /// entity runs its own behaviour (#408), every play session reads the saves from disk (#452), the UI works in points
    /// on every host (#454), a NaN never reaches a transform (#455).</summary>
    public static class ScriptRuntimeTests
    {
        // Behaviours that log what happens to them into Probe.Log (read back by reflection: the classes live in the
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

public class NanWriter : VortexBehaviour
{
    public Vector3 Moved;
    public override void Update(float dt)
    {
        float zero = 0f;
        var p = Position; p.X = zero / zero; Position = p;            // a 0/0 in game math
        Rotation = new Vector3(float.NaN, 0f, 0f);
        Scale = new Vector3(1f, 1f / zero, 1f);
        Scene.SetWorldPositionOf(EntityId, new Vector3(float.PositiveInfinity, 1f, 1f));
        Moved = Physics.MoveCharacter(Position, 0.3f, 1.8f, new Vector3(zero / zero, 0f, 0f), EntityId);
    }
}

public class Counter : VortexBehaviour
{
    public int Starts, Updates, LateUpdates;
    public override void Start() { Starts++; }
    public override void Update(float dt) { Updates++; }
    public override void LateUpdate(float dt) { LateUpdates++; }
}

public class SelfDestructAI : VortexBehaviour
{
    public override void Update(float dt) { Scene.Destroy(EntityId); }
    public override void OnDestroy() { Probe.Log.Add(""AI.OnDestroy""); }
}

public class Killer : VortexBehaviour
{
    public override void Start() { Scene.Destroy(Scene.Find(""Victim"")); }
}

public class Witness : VortexBehaviour
{
    public override void OnDestroy()
    {
        Probe.Log.Add(""Witness sees Health="" + (Scene.GetBehaviour<Health>(EntityId) != null) + "" Loot="" + (Scene.GetBehaviour<Loot>(EntityId) != null));
    }
}

public class UiProbe : VortexBehaviour
{
    public override void Start()
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Probe.Log.Add(""ui "" + UI.Width.ToString(ci) + ""x"" + UI.Height.ToString(ci) + "" @"" + UI.Scale.ToString(ci));
    }
}

public class ContactZone : VortexBehaviour
{
    public override void OnTriggerEnter(TriggerHit other) { }
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

        private static List<string> WarningsAbout(string text) =>
            Editor.Core.Services.ConsoleService.Instance.Entries
                .Where(e => e.Level == Editor.Core.Services.LogLevel.Warning && e.Message.Contains(text)).Select(e => e.Message).ToList();

        private static bool Same(Vector3 a, float x, float y, float z) => a.X == x && a.Y == y && a.Z == z;

        [Test]
        public static void NonFiniteTransformWritesAreIgnored(TestContext t)
        {
            // #455: a NaN / infinity used to go straight into SyncToEngine -> the native quaternion and matrix math
            ScriptRuntime.ResetNonFiniteWarnings();
            var scene = new Scene { Name = "NaN" };
            var crate = new GameEntity(scene, "NanCrate");
            var tr = crate.Transform;
            tr.LocalPosition = new Vector3(1, 2, 3); tr.LocalRotation = new Vector3(0, 90, 0); tr.LocalScale = new Vector3(2, 2, 2);
            tr.LocalPosition = new Vector3(float.NaN, 0, 0);
            tr.LocalRotation = new Vector3(0, float.PositiveInfinity, 0);
            tr.LocalScale = new Vector3(1, float.NegativeInfinity, 1);
            tr.LocalEulerAngles = new Vector3(float.NaN, 5, 5);
            tr.SetLocalPositionFromEngine(new Vector3(9, float.NaN, 9));
            t.True(Same(tr.LocalPosition, 1, 2, 3), "the position keeps its last valid value (not the origin): " + tr.LocalPosition.X);
            t.True(Same(tr.LocalRotation, 0, 90, 0), "the rotation too");
            t.True(Same(tr.LocalScale, 2, 2, 2), "the scale too");
            var warned = WarningsAbout("'NanCrate'");
            t.Equal(3, warned.Count, "one warning per property: " + string.Join(" | ", warned));
            t.True(warned.Any(w => w.Contains("position (NaN, 0, 0)")) && warned.Any(w => w.Contains("rotation")) && warned.Any(w => w.Contains("scale")),
                "the warnings name the property and the value: " + string.Join(" | ", warned));
            tr.LocalPosition = new Vector3(float.NaN, float.NaN, float.NaN);
            t.Equal(3, WarningsAbout("'NanCrate'").Count, "no flood: once per entity and property");
            tr.LocalPosition = new Vector3(4, 5, 6);
            t.True(Same(tr.LocalPosition, 4, 5, 6), "finite values are taken as always");
            ScriptRuntime.ResetNonFiniteWarnings();   // a new play session
            tr.LocalPosition = new Vector3(0, float.NaN, 0);
            t.Equal(4, WarningsAbout("'NanCrate'").Count, "the next play session warns again");
        }

        [Test]
        public static void AScriptWritingNaNKeepsTheGameRunning(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "NaNPlay" };
            var hero = new GameEntity(scene, "NanHero");
            hero.Transform.LocalPosition = new Vector3(4, 5, 6);
            AddScript(hero, "NanWriter");
            scene.Entities.Add(hero);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.BeginPlaySession(scene, asm);
                rt.Update(0.016f);
                int firstFrame = WarningsAbout("NanHero").Count;
                rt.Update(0.016f);
                var tr = hero.Transform;
                t.True(Same(tr.LocalPosition, 4, 5, 6) && Same(tr.LocalRotation, 0, 0, 0) && Same(tr.LocalScale, 1, 1, 1),
                    "the entity keeps its transform: " + tr.LocalPosition.X + "," + tr.LocalPosition.Y + "," + tr.LocalPosition.Z);
                var moved = (Vortex.Vector3)Field(rt.BehavioursOf(hero)[0], "Moved");
                t.True(float.IsFinite(moved.X) && float.IsFinite(moved.Y) && float.IsFinite(moved.Z), "MoveCharacter with a NaN move answers a finite position: " + moved);
                var warned = WarningsAbout("NanHero");
                t.True(warned.Count >= 3 && warned.All(w => w.Contains("NanWriter.Update")), "the warnings name the entity and the script: " + string.Join(" | ", warned));
                t.True(warned.Any(w => w.StartsWith("Physics.MoveCharacter", StringComparison.Ordinal)), "MoveCharacter says so too");
                t.Equal(firstFrame, warned.Count, "nothing more on the second frame");
            }
            finally { rt.End(); }
        }

        /// <summary>An editor viewport as the Avalonia shell reports it: logical size, device pixels per unit.</summary>
        private sealed class FakeViewport : Editor.Core.Viewport.IViewportHost
        {
            public double Width { get; set; }
            public double Height { get; set; }
            public double Scaling { get; set; }
            public bool IsWindowActive => true;
            public void SetCursorHidden(bool hidden) { }
            public void WarpCursorToCenter() { }
            public void FocusViewport() { }
            public void ClearFocus() { }
            public void ShowToast(string message) { }
        }

        [Test]
        public static void UiSizeAndMouseAreInPointsOnEveryHost(TestContext t)
        {
            // #454: one unit for UI.Width/Height/MouseX/Y in editor play and the player — the window's points — plus the
            // device pixels per point. A Retina play view 756 x 472 points (1512 x 944 pixels), the mouse at (100, 50):
            var retina = new FakeViewport { Width = 756, Height = 472, Scaling = 2 };
            var f = Editor.Core.Viewport.EditorViewportSession.UiFrame(retina, 100, 50);
            t.True(f.W == 756f && f.H == 472f, "the editor feeds the viewport's logical size, not its render pixels: " + f.W + " x " + f.H);
            t.True(f.MouseX == 100f && f.MouseY == 50f, "the pointer stays in points");
            t.Equal(2f, f.Scale, "two device pixels per point");
            t.Equal(1f, Editor.Core.Viewport.EditorViewportSession.UiFrame(new FakeViewport { Width = 800, Height = 600, Scaling = 0 }, 0, 0).Scale, "an unknown scaling counts as 1");

            // what the scripts read, from either host
            var rt = ScriptRuntime.Instance;
            var oldHost = Vortex.UI.Host;
            try
            {
                Vortex.UI.Host = rt;
                rt.SetUIFrame(f.W, f.H, f.MouseX, f.MouseY, false, false, f.Scale);   // the editor's play view
                t.True(Vortex.UI.Width == 756f && Vortex.UI.Height == 472f && Vortex.UI.MouseX == 100f && Vortex.UI.MouseY == 50f, "UI reads points");
                t.Equal(1512f, Vortex.UI.Width * Vortex.UI.Scale, "the drawable's pixel width is Width * Scale");
                rt.SetUIFrame(756, 472, 100, 50, false, false, 2f);                   // the player: window points + SDL's density
                t.Equal(756f, Vortex.UI.Width, "the player reports the same width for the same window");
                t.Equal(2f, Vortex.UI.Scale, "and the same scale");
                rt.SetUIFrame(1280, 720, 0, 0, false, false, float.NaN);
                t.Equal(1f, Vortex.UI.Scale, "a bad density reads as 1");
            }
            finally { Vortex.UI.Host = oldHost; rt.SetUIFrame(0, 0, 0, 0, false, false, 1f); }
        }

        // ---------------------------------------------------------------- review follow-ups of #408 / #452 / #454 / #455

        private static List<string> InfosAbout(string text) =>
            Editor.Core.Services.ConsoleService.Instance.Entries
                .Where(e => e.Level == Editor.Core.Services.LogLevel.Info && e.Message.Contains(text)).Select(e => e.Message).ToList();

        private static GameEntity Entity(Scene scene, string name, params string[] scripts)
        {
            var e = new GameEntity(scene, name);
            foreach (var s in scripts) AddScript(e, s);
            scene.Entities.Add(e);
            return e;
        }

        [Test]
        public static void ADestroyInsideUpdateDoesNotCostOthersTheirFrame(TestContext t)
        {
            // an NPC [Health, Loot, AI] whose AI destroys the entity in Update: removing its three behaviours from the tick
            // list mid-loop shifted it, so the next three behaviours skipped the frame
            var asm = Compile(t);
            var scene = new Scene { Name = "Destroy" };
            var first = Entity(scene, "First", "Counter");
            var npc = Entity(scene, "Npc", "Health", "Loot", "SelfDestructAI");
            var after = new[] { Entity(scene, "A", "Counter"), Entity(scene, "B", "Counter"), Entity(scene, "C", "Counter"), Entity(scene, "D", "Counter") };
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.BeginPlaySession(scene, asm);
                var counters = new[] { first }.Concat(after).Select(e => rt.BehavioursOf(e)[0]).ToList();
                rt.Update(0.016f);
                foreach (var c in counters)
                {
                    t.Equal(1, (int)Field(c, "Updates"), "every other behaviour got its Update in the frame of the destroy");
                    t.Equal(1, (int)Field(c, "LateUpdates"), "… and its LateUpdate");
                }
                var log = Log(asm);
                t.Equal(1, log.Count(l => l == "Health.OnDestroy"), "Health.OnDestroy once");
                t.Equal(1, log.Count(l => l == "Loot.OnDestroy"), "Loot.OnDestroy once");
                t.Equal(1, log.Count(l => l == "AI.OnDestroy"), "AI.OnDestroy once");
                rt.Update(0.016f);
                foreach (var c in counters) t.Equal(2, (int)Field(c, "Updates"), "and the next frame as well");
                t.Equal(0, rt.BehavioursOf(npc).Count, "the NPC runs nothing any more");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void AStartThatDestroysAnotherScriptedEntityDoesNotThrow(TestContext t)
        {
            // Begin's start loop walked a snapshot count by index; the destroy shrank the list and the catch re-read
            // _behaviours[i] past its end, so an ArgumentOutOfRangeException escaped Begin
            var asm = Compile(t);
            var scene = new Scene { Name = "Killer" };
            Entity(scene, "Hitman", "Killer");
            Entity(scene, "Victim", "Health", "Loot");
            var witness = Entity(scene, "Bystander", "Counter");
            var rt = ScriptRuntime.Instance;
            try
            {
                Exception thrown = null;
                try { rt.BeginPlaySession(scene, asm); } catch (Exception ex) { thrown = ex; }
                t.True(thrown == null, "Begin does not throw: " + thrown);
                var log = Log(asm);
                t.False(log.Any(l => l.StartsWith("Health.Start", StringComparison.Ordinal) || l.StartsWith("Loot.Start", StringComparison.Ordinal)), "the victim's scripts never start after their OnDestroy");
                t.Equal(1, log.Count(l => l == "Health.OnDestroy"), "the victim's Health got OnDestroy once");
                t.Equal(1, log.Count(l => l == "Loot.OnDestroy"), "and its Loot");
                t.Equal(1, (int)Field(rt.BehavioursOf(witness)[0], "Starts"), "the bystander started once");
                rt.Update(0.016f);
                t.Equal(1, (int)Field(rt.BehavioursOf(witness)[0], "Updates"), "and ticks");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void ADisabledScriptDoesNotRun(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Disabled" };
            var e = new GameEntity(scene, "Npc");
            var mover = AddScript(e, "Mover"); mover.IsEnabled = false;   // switched off in the Inspector
            AddScript(e, "Health");
            scene.Entities.Add(e);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.BeginPlaySession(scene, asm);
                t.Equal(1, rt.BehavioursOf(e).Count, "only the enabled script runs");
                t.Equal("Health", rt.BehavioursOf(e)[0].GetType().Name, "… Health");
                t.False(Log(asm).Any(l => l.StartsWith("Mover.Start", StringComparison.Ordinal)), "the disabled one never starts");
                t.False(rt.AttachAtRuntime(e, mover), "attaching it live does not start it either");
                mover.IsEnabled = true;
                t.True(rt.AttachAtRuntime(e, mover), "once switched on, it starts");
                t.Equal(2, rt.BehavioursOf(e).Count, "two behaviours now");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void AReplacedScriptComponentKeepsItsRunningBehaviour(TestContext t)
        {
            // attach_script after an undo / a prefab revert: the Script component is a NEW object for the same class — the
            // running behaviour is re-linked to it, not joined by a second copy
            var asm = Compile(t);
            var scene = new Scene { Name = "Relink" };
            var e = new GameEntity(scene, "Npc");
            var old = AddScript(e, "Mover", "Speed", "2");
            scene.Entities.Add(e);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.BeginPlaySession(scene, asm);
                var running = rt.BehavioursOf(e)[0];
                e.Components.Remove(old);
                var fresh = AddScript(e, "Mover", "Speed", "9");
                bool live = rt.ApplyFieldsToLiveBehaviour(e, fresh) || rt.AttachAtRuntime(e, fresh);   // what attach_script does
                t.True(live, "the script counts as running");
                t.Equal(1, rt.BehavioursOf(e).Count, "no second Mover");
                t.True(ReferenceEquals(running, rt.LiveBehaviourOf(e, fresh)), "the running one now belongs to the new component");
                t.Equal(9f, (float)Field(running, "Speed"), "and took its field values");
                t.Equal(1, Log(asm).Count(l => l.StartsWith("Mover.Start", StringComparison.Ordinal)), "started once");
                t.False(rt.AttachAtRuntime(e, fresh), "nothing to start");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void SeveralScriptsAndDuplicatesAreReportedAtPlayStart(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Report" };
            Entity(scene, "ReportNpc", "Mover", "Health");
            Entity(scene, "ReportDup", "Loot", "Loot");   // an old scene: the same script dropped twice
            Entity(scene, "ReportSingle", "Health");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.BeginPlaySession(scene, asm);
                t.Equal(1, InfosAbout("'ReportNpc' runs 2 scripts — Mover, Health").Count, "one line for the entity with two scripts");
                t.Equal(0, InfosAbout("'ReportSingle'").Count, "nothing for a single script");
                t.Equal(1, WarningsAbout("'ReportDup' carries the same script more than once (Loot ×2)").Count, "a warning for the duplicate");
                rt.Begin(scene, asm);   // a scene switch / hot reload inside the session
                t.Equal(1, WarningsAbout("'ReportDup' carries").Count, "the warning comes once per session");
                rt.BeginPlaySession(scene, asm);
                t.Equal(2, WarningsAbout("'ReportDup' carries").Count, "and again in the next session");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void GetBehaviourSkipsASiblingThatWasDestroyed(TestContext t)
        {
            // OnDestroy runs in component order; a later script asking for an earlier sibling must not get the dead one
            var asm = Compile(t);
            var scene = new Scene { Name = "Siblings" };
            var e = Entity(scene, "Npc", "Health", "Witness", "Loot");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.BeginPlaySession(scene, asm);
                Vortex.Scene.Destroy(rt.BehavioursOf(e)[0].EntityId);
                t.True(Log(asm).Contains("Witness sees Health=False Loot=True"), "Health (destroyed first) is gone, Loot (still running) is found: " + string.Join(" | ", Log(asm)));
            }
            finally { rt.End(); }
        }

        [Test]
        public static void UiFrameIsValidInStart(TestContext t)
        {
            // the hosts feed the UI frame before BeginPlaySession; nothing in Begin may reset it
            var asm = Compile(t);
            var scene = new Scene { Name = "UiStart" };
            Entity(scene, "Hud", "UiProbe");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.SetUIFrame(800, 600, 1, 2, false, false, 2f);
                rt.BeginPlaySession(scene, asm);
                t.Equal("ui 800x600 @2", Log(asm).LastOrDefault(), "Start() reads the frame");
            }
            finally { rt.End(); rt.SetUIFrame(0, 0, 0, 0, false, false, 1f); }
        }

        [Test]
        public static void ContactScriptsAreRecognised(TestContext t)
        {
            // the Collision Editor's script section acts on the script that reacts to contact, never on e.g. Health
            var asm = Compile(t);
            t.True(ScriptRuntime.HandlesContact(asm.GetType("ContactZone")), "an OnTriggerEnter override");
            t.False(ScriptRuntime.HandlesContact(asm.GetType("Health")), "Health does not react to contact");
            t.False(ScriptRuntime.HandlesContact(null), "no type");
        }

        [Test]
        public static void NonFiniteWarningsAreCappedPerSession(TestContext t)
        {
            // a spawner yielding a NaN per spawn: the first few are shown, the rest become one line when play stops
            var asm = Compile(t);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.BeginPlaySession(new Scene { Name = "Cap" }, asm);
                var scene = new Scene { Name = "Spawns" };
                for (int i = 0; i < ScriptRuntime.NonFiniteWarningCap + 5; i++)
                    new GameEntity(scene, "CapCrate" + i).Transform.LocalPosition = new Vector3(float.NaN, 0, 0);
                t.Equal(ScriptRuntime.NonFiniteWarningCap, WarningsAbout("for 'CapCrate").Count, "only the first ones are shown");
                rt.End();
                t.Equal(1, WarningsAbout("…and 5 more non-finite").Count, "then one line counts the rest");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void ANewSessionDoesNotWriteTheLastSessionsLeftovers(TestContext t)
        {
            // if Stop's flush failed, the next Play used to write the stale cache over a file the user edited meanwhile
            var asm = Compile(t);
            var scene = new Scene { Name = "Leftovers" };
            string slot0 = Vortex.Save.SlotPath(0);
            Directory.CreateDirectory(Path.GetDirectoryName(slot0));
            File.WriteAllText(slot0, "credits\ti:1\n");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.BeginPlaySession(scene, asm);
                Vortex.Save.SetInt("credits", 100);              // pending — as if Stop's write had failed
                File.WriteAllText(slot0, "credits\ti:5\n");      // the user edits the save between the runs
                rt.BeginPlaySession(scene, asm);
                t.Equal("credits\ti:5\n", File.ReadAllText(slot0), "the edited file is left alone");
                t.Equal(5, Vortex.Save.GetInt("credits"), "and is what the new session reads");
            }
            finally { rt.End(); try { File.Delete(slot0); } catch { } }
        }
    }
}
