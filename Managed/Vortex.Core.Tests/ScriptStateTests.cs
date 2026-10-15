using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Editor.Core.Claude;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>#457: get_script_state / set_script_state — a running behaviour's public fields and properties and a script
    /// class's statics read as JSON values (the game's own members, not VortexBehaviour's), dotted paths into objects, depth
    /// summaries, a throwing getter shown as its error; writes converted to the member's type and checked all before any is
    /// set; statics start fresh with the next compile, so a write never outlives the play session.</summary>
    public static class ScriptStateTests
    {
        private const string Scripts = @"
using System.Collections.Generic;
using Vortex;

public enum Stance { Standing, Crouched, Prone }

public class Inventory { public int Ammo = 30; public List<string> Items = new List<string> { ""key"", ""map"" }; public Inventory Spare; }

public static class Game
{
    public static int Act = 1;
    public static string Beat { get; set; } = ""intro"";
    public const int MaxActs = 7;
    public static readonly int Seed = 42;
}

public class Mission : VortexBehaviour
{
    public static Mission Instance;
    public int Credits = 100;
    public float Speed = 1.5f;
    public Vector3 Spawn = new Vector3(1, 2, 3);
    public Stance Pose = Stance.Crouched;
    public int[] Scores = { 3, 1, 2 };
    public Inventory Bag = new Inventory();
    public Health Target;
    public readonly int Fixed = 5;
    public int Rank { get; set; } = 3;
    public int Doubled => Credits * 2;
    public int Broken { get { throw new System.InvalidOperationException(""not yet""); } }
    public override void Start() { Instance = this; }
}

public class Health : VortexBehaviour { public int Hp = 80; }
";

        private static Assembly Compile(TestContext t, string name = "Probe")
        {
            var file = t.Write("Scripts/" + name + ".cs", Scripts);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the test scripts do not compile: " + log);
            return asm;
        }

        private static JsonElement J(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

        private static readonly JsonSerializerOptions Relaxed = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>The value as the tools' JSON writes it (no escaping of quotes and angle brackets, like ToolJson).</summary>
        private static string Text(object v) => JsonSerializer.Serialize(v, Relaxed);

        private static string Error(Action f)
        {
            try { f(); } catch (ScriptState.Error e) { return e.Message; }
            return null;
        }

        [Test]
        public static void ARunningBehavioursStateReadsAsJson(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "State" };
            var gm = new GameEntity(scene, "GameManager");
            gm.AddComponentDirect(new Script(gm, "Assets/Scripts/Mission.cs"));
            var npc = new GameEntity(scene, "Npc");
            npc.AddComponentDirect(new Script(npc, "Assets/Scripts/Health.cs"));
            scene.Entities.Add(gm);
            scene.Entities.Add(npc);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                var mission = rt.BehavioursOf(gm)[0];
                var health = rt.BehavioursOf(npc)[0];
                mission.GetType().GetField("Target").SetValue(mission, health);
                var state = ScriptState.Read(mission, mission.GetType(), false, null, 2);
                t.True(state.Keys.Take(4).SequenceEqual(new[] { "Credits", "Speed", "Spawn", "Pose" }), "the game's own members in declaration order: " + string.Join(", ", state.Keys));
                t.False(state.ContainsKey("Position") || state.ContainsKey("EntityId") || state.ContainsKey("Instance"), "no engine members, no statics: " + string.Join(", ", state.Keys));
                t.Equal("100", Text(state["Credits"]), "an int");
                t.Equal("1.5", Text(state["Speed"]), "a float");
                t.Equal("[1,2,3]", Text(state["Spawn"]), "a Vector3 as [x, y, z]");
                t.Equal("\"Crouched\"", Text(state["Pose"]), "an enum by name");
                t.Equal("[3,1,2]", Text(state["Scores"]), "an array");
                t.Equal("{\"Ammo\":30,\"Items\":[\"key\",\"map\"],\"Spare\":null}", Text(state["Bag"]), "an object as its members");
                t.Equal("\"Health on 'Npc'\"", Text(state["Target"]), "another behaviour by name and entity");
                t.Equal("3", Text(state["Rank"]), "a property");
                t.Equal("200", Text(state["Doubled"]), "a computed property");
                t.True(Text(state["Broken"]).Contains("InvalidOperationException: not yet"), "a getter that throws shows its error: " + Text(state["Broken"]));

                var shallow = ScriptState.Read(mission, mission.GetType(), false, null, 0);
                t.Equal("\"<Inventory>\"", Text(shallow["Bag"]), "depth 0: objects as a summary");
                t.Equal("[3,1,2]", Text(shallow["Scores"]), "… while a list of numbers is still listed");

                var picked = ScriptState.Read(mission, mission.GetType(), false, new[] { "credits", "Bag.Ammo", "Bag.Items" }, 2);
                t.Equal("{\"credits\":100,\"Bag.Ammo\":30,\"Bag.Items\":[\"key\",\"map\"]}", Text(picked), "picked members and dotted paths (names in any case)");
                string unknown = Error(() => ScriptState.Read(mission, mission.GetType(), false, new[] { "Credit" }, 2));
                t.True(unknown != null && unknown.Contains("Credits, Speed, Spawn"), "an unknown member lists the real ones: " + unknown);
                string inNull = Error(() => ScriptState.Read(mission, mission.GetType(), false, new[] { "Bag.Spare.Ammo" }, 2));
                t.True(inNull != null && inNull.Contains("Bag.Spare is null"), "a path through null says so: " + inNull);

                // the statics of a script class — and a singleton reached through its Instance
                var game = ScriptState.FindClass(asm, "game");
                var statics = ScriptState.Read(null, game, true, null, 2);
                t.Equal("{\"Act\":1,\"Seed\":42,\"Beat\":\"intro\"}", Text(statics), "static fields and properties, no constants");
                var single = ScriptState.Read(null, ScriptState.FindClass(asm, "Mission"), true, new[] { "Instance.Credits" }, 2);
                t.Equal("{\"Instance.Credits\":100}", Text(single), "through the singleton");
                string missing = Error(() => ScriptState.FindClass(asm, "Missions"));
                t.True(missing != null && missing.Contains("Similar: Mission"), "a wrong class name suggests the real one: " + missing);
            }
            finally { rt.End(); }
        }

        [Test]
        public static void WritesAreConvertedCheckedFirstAndLiveOnly(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Write" };
            var gm = new GameEntity(scene, "GameManager");
            var script = new Script(gm, "Assets/Scripts/Mission.cs");
            script.SetFieldValue("Credits", "250");
            gm.AddComponentDirect(script);
            scene.Entities.Add(gm);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                var m = rt.BehavioursOf(gm)[0];
                object F(string n) => m.GetType().GetField(n)?.GetValue(m) ?? m.GetType().GetProperty(n).GetValue(m);
                var set = ScriptState.Write(m, m.GetType(), false, J("{\"Credits\": 5000, \"speed\": 2, \"Spawn\": [0, 1, 4], \"Pose\": \"prone\", \"Scores\": [9], \"Bag.Ammo\": 7, \"Rank\": 4}"));
                t.Equal(5000, (int)F("Credits"), "an int");
                t.Equal(2f, (float)F("Speed"), "a float (by its name in any case)");
                t.Equal("Prone", F("Pose").ToString(), "an enum by name");
                t.Equal(9, ((int[])F("Scores"))[0], "an array");
                t.Equal(4, (int)F("Rank"), "a property");
                t.Equal(7, (int)F("Bag").GetType().GetField("Ammo").GetValue(F("Bag")), "inside an object");
                t.Equal("{\"Credits\":5000,\"speed\":2,\"Spawn\":[0,1,4],\"Pose\":\"Prone\",\"Scores\":[9],\"Bag.Ammo\":7,\"Rank\":4}", Text(set), "the values read back");
                t.Equal("250", script.GetFieldValue("Credits"), "the Script component's stored value is untouched — live only");

                // one bad value: nothing is set
                string bad = Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Credits\": 1, \"Speed\": \"fast\"}")));
                t.True(bad != null && bad.Contains("Speed expects a number"), "the bad value is named: " + bad);
                t.Equal(5000, (int)F("Credits"), "… and the good one before it was not set");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Credits\": 2.5}"))).Contains("whole number"), "an int takes whole numbers only");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Fixed\": 1}"))).Contains("readonly"), "a readonly field");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Doubled\": 1}"))).Contains("no public setter"), "a get-only property");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Spawn.X\": 1}"))).Contains("set Spawn as a whole"), "inside a Vector3: set it whole");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Pose\": \"Flying\"}"))).Contains("Standing, Crouched, Prone"), "an enum lists its names");

                // statics: set while playing, fresh again in the next compile (every Play compiles the scripts anew)
                var game = ScriptState.FindClass(asm, "Game");
                ScriptState.Write(null, game, true, J("{\"Act\": 4, \"Beat\": \"heist\"}"));
                t.Equal("{\"Act\":4,\"Seed\":42,\"Beat\":\"heist\"}", Text(ScriptState.Read(null, game, true, null, 1)), "the statics took the values");
                t.True(Error(() => ScriptState.Write(null, game, true, J("{\"Seed\": 1}"))).Contains("readonly"), "a static readonly field");
                var next = Compile(t, "Probe2");
                t.Equal("{\"Act\":1,\"Seed\":42,\"Beat\":\"intro\"}", Text(ScriptState.Read(null, ScriptState.FindClass(next, "Game"), true, null, 1)), "the next session's compile starts from the initial values");
            }
            finally { rt.End(); }
        }
    }
}
