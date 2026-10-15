using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// <summary>#457: get_script_state / set_script_state. Reading runs no game code unless a path names it: fields and
    /// auto-properties are read directly, a computed property is listed (a getter that calls itself is refused even when
    /// named — its stack overflow used to take the editor down), lazy sequences are not run. Objects shared or in a cycle are
    /// shown once, a read is capped. Writes convert JSON to the member's type (Color / Quaternion / structs / [Flags] /
    /// a byte enum in range), write a struct back to its member, are all checked before any is set, never go into .NET's or
    /// the engine's other objects, and statics start fresh with the next compile.</summary>
    public static class ScriptStateTests
    {
        private const string Scripts = @"
using System;
using System.Collections.Generic;
using System.Linq;
using Vortex;

public enum Stance { Standing, Crouched, Prone }
public enum Small : byte { A = 1, B = 2 }
[Flags] public enum Perks { None = 0, Fast = 1, Strong = 2 }
public struct Stats { public int Hp; public float Armor; }
public class Inventory { public int Ammo = 30; public List<string> Items = new List<string> { ""key"", ""map"" }; public Inventory Spare; }
public class Node { public int Id; public Node Next; public List<Node> Links = new List<Node>(); }
public class Lazy { public int Made = 1; }

public static class Game
{
    public static int Act = 1;
    public static string Beat { get; set; } = ""intro"";
    public const int MaxActs = 7;
    public static readonly int Seed = 42;
    public static System.IO.FileInfo Log;
    public static int Lazies;
    public static List<Node> Nodes = new List<Node>();
    public static List<List<List<int>>> Cube;
}

public class Mission : VortexBehaviour
{
    public static Mission Instance;
    static Lazy _lazy;
    public static Lazy LazyOne { get { Game.Lazies++; return _lazy ?? (_lazy = new Lazy()); } }   // reading it makes one
    public int Credits = 100;
    public float Speed = 1.5f;
    public Vector3 Spawn = new Vector3(1, 2, 3);
    public Color Tint = new Color(1, 0, 0, 1);
    public Quaternion Facing = new Quaternion(0, 0, 0, 1);
    public Stance Pose = Stance.Crouched;
    public Small Size = Small.A;
    public Perks Perks = Perks.Fast;
    public Stats Stats = new Stats { Hp = 50, Armor = 0.5f };
    public int[] Scores = { 3, 1, 2 };
    public Inventory Bag = new Inventory();
    public Inventory SameBag;
    public Health Target;
    public Node Route;
    public IEnumerable<int> Pending = Enumerable.Range(0, 3).Select(i => { Game.Lazies++; return i; });
    public readonly int Fixed = 5;
    public int Rank { get; set; } = 3;
    public int Doubled => Credits * 2;
    public int Broken { get { throw new InvalidOperationException(""not yet""); } }
    public float Loop { get { return Loop; } }                                   // a typo the game never reads
    public float Echo { get { return Speed; } set { Echo = value; } }            // a setter that calls itself
    public override void Start()
    {
        Instance = this;
        SameBag = Bag;
        var a = new Node { Id = 1 }; var b = new Node { Id = 2, Next = a }; a.Next = b;   // a cycle
        Route = a;
    }
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

        private static (ScriptRuntime rt, Vortex.VortexBehaviour mission) Begin(Assembly asm, string name)
        {
            var scene = new Scene { Name = name };
            var gm = new GameEntity(scene, "GameManager");
            gm.AddComponentDirect(new Script(gm, "Assets/Scripts/Mission.cs"));
            var npc = new GameEntity(scene, "Npc");
            npc.AddComponentDirect(new Script(npc, "Assets/Scripts/Health.cs"));
            scene.Entities.Add(gm);
            scene.Entities.Add(npc);
            var rt = ScriptRuntime.Instance;
            rt.Begin(scene, asm);
            var mission = rt.BehavioursOf(gm)[0];
            mission.GetType().GetField("Target").SetValue(mission, rt.BehavioursOf(npc)[0]);
            return (rt, mission);
        }

        [Test]
        public static void ReadingRunsNoGameCodeUnlessNamed(TestContext t)
        {
            var asm = Compile(t);
            var (rt, m) = Begin(asm, "Read");
            var game = asm.GetType("Game");
            int Lazies() => (int)game.GetField("Lazies").GetValue(null);
            try
            {
                var snap = ScriptState.Read(m, m.GetType(), false, null, 2);
                var s = snap.State;
                t.True(s.Keys.Take(4).SequenceEqual(new[] { "Credits", "Speed", "Spawn", "Tint" }), "the game's own members in declaration order: " + string.Join(", ", s.Keys));
                t.False(s.ContainsKey("Position") || s.ContainsKey("EntityId") || s.ContainsKey("Instance"), "no engine members, no statics");
                t.Equal("100", Text(s["Credits"]), "an int");
                t.Equal("[1,2,3]", Text(s["Spawn"]), "a Vector3 as [x, y, z]");
                t.Equal("[1,0,0,1]", Text(s["Tint"]), "a Color as [r, g, b, a]");
                t.Equal("[0,0,0,1]", Text(s["Facing"]), "a Quaternion as [x, y, z, w]");
                t.Equal("\"Crouched\"", Text(s["Pose"]), "an enum by name");
                t.Equal("\"Fast\"", Text(s["Perks"]), "a flags enum");
                t.Equal("{\"Hp\":50,\"Armor\":0.5}", Text(s["Stats"]), "a struct of the game as its fields");
                t.Equal("{\"Ammo\":30,\"Items\":[\"key\",\"map\"],\"Spare\":null}", Text(s["Bag"]), "an object as its members");
                t.Equal("\"<Inventory, shown above>\"", Text(s["SameBag"]), "the same object a second time is not expanded again");
                t.True(Text(s["Route"]).Contains("<cycle: Node>"), "a cycle stops: " + Text(s["Route"]));
                t.Equal("\"Health on 'Npc'\"", Text(s["Target"]), "another behaviour by name and entity");
                t.Equal("3", Text(s["Rank"]), "an auto-property through its backing field");
                t.True(Text(s["Pending"]).StartsWith("\"<", StringComparison.Ordinal) && Lazies() == 0, "a lazy sequence is not run: " + Text(s["Pending"]));
                t.True(snap.Computed.SequenceEqual(new[] { "Doubled", "Broken", "Loop", "Echo" }), "getters of their own are listed, not run: " + string.Join(", ", snap.Computed));
                t.False(s.ContainsKey("Loop"), "the getter that calls itself was not run — reading all of the state used to overflow the editor's stack");

                // named in fields, a getter runs — except one that calls itself, which would never come back
                var named = ScriptState.Read(m, m.GetType(), false, new[] { "Doubled", "Broken", "Loop" }, 2).State;
                t.Equal("200", Text(named["Doubled"]), "a computed property, named");
                t.True(Text(named["Broken"]).Contains("InvalidOperationException: not yet"), "a throwing getter shows its error");
                t.True(Text(named["Loop"]).Contains("calls itself"), "a getter that calls itself is refused: " + Text(named["Loop"]));

                // statics: a lazy singleton is not created by a read; named, it is
                var statics = ScriptState.Read(null, asm.GetType("Mission"), true, null, 2);
                t.True(statics.Computed.Contains("LazyOne") && Lazies() == 0, "LazyOne listed, not run");
                t.Equal("\"Mission on 'GameManager'\"", Text(statics.State["Instance"]), "the singleton by name");
                var made = ScriptState.Read(null, asm.GetType("Mission"), true, new[] { "LazyOne.Made", "Instance.Credits" }, 2).State;
                t.True(Text(made["LazyOne.Made"]) == "1" && Lazies() == 1, "named, the getter runs once");
                t.Equal("100", Text(made["Instance.Credits"]), "through the singleton");
                var gameState = ScriptState.Read(null, game, true, null, 2).State;
                t.Equal("1", Text(gameState["Act"]), "a static field");
                t.Equal("\"intro\"", Text(gameState["Beat"]), "a static auto-property");
                t.False(gameState.ContainsKey("MaxActs"), "no constants");

                // .NET's objects are not opened, and a path does not go into them (#457 review: a FileInfo's setter writes the disk)
                game.GetField("Log").SetValue(null, new FileInfo(t.Write("log.txt", "x")));
                t.Equal("\"<FileInfo>\"", Text(ScriptState.Read(null, game, true, new[] { "Log" }, 2).State["Log"]), "a FileInfo is not expanded");
                string into = Error(() => ScriptState.Read(null, game, true, new[] { "Log.Length" }, 2));
                t.True(into != null && into.Contains("only goes into the game's own"), "a path into it is refused: " + into);

                string unknown = Error(() => ScriptState.Read(m, m.GetType(), false, new[] { "Credit" }, 2));
                t.True(unknown != null && unknown.Contains("Credits, Speed, Spawn"), "an unknown member lists the real ones: " + unknown);
                string inNull = Error(() => ScriptState.Read(m, m.GetType(), false, new[] { "Bag.Spare.Ammo" }, 2));
                t.True(inNull != null && inNull.Contains("Bag.Spare is null"), "a path through null says so: " + inNull);
                string missing = Error(() => ScriptState.FindClass(asm, "Missions"));
                t.True(missing != null && missing.Contains("Similar: Mission"), "a wrong class name suggests the real one: " + missing);
            }
            finally { rt.End(); }
        }

        [Test]
        public static void AGraphIsShownOnceAndAReadIsCapped(TestContext t)
        {
            var asm = Compile(t);
            var game = asm.GetType("Game");
            var nodeType = asm.GetType("Node");
            // the reviewer's waypoints: 64 nodes, each linked to the next 16 — depth 6 used to take 0.8 s and 16 MB
            var nodes = (System.Collections.IList)game.GetField("Nodes").GetValue(null);
            nodes.Clear();
            for (int i = 0; i < 64; i++) { var n = Activator.CreateInstance(nodeType); nodeType.GetField("Id").SetValue(n, i); nodes.Add(n); }
            for (int i = 0; i < 64; i++)
            {
                var links = (System.Collections.IList)nodeType.GetField("Links").GetValue(nodes[i]);
                for (int k = 1; k <= 16; k++) links.Add(nodes[(i + k) % 64]);
            }
            var sw = Stopwatch.StartNew();
            var snap = ScriptState.Read(null, game, true, new[] { "Nodes" }, 6);
            string json = Text(snap.State);
            t.True(sw.ElapsedMilliseconds < 500 && json.Length < 200_000, "each node once: " + sw.ElapsedMilliseconds + " ms, " + json.Length + " chars");
            t.True(json.Contains("shown above"), "the other links point at nodes already shown");
            t.False(snap.Truncated, "well within the cap");

            // 64 × 64 × 64 numbers: more than one answer holds — cut, and said so
            var cube = new List<List<List<int>>>();
            for (int a = 0; a < 64; a++) { var plane = new List<List<int>>(); for (int b = 0; b < 64; b++) plane.Add(Enumerable.Range(0, 64).ToList()); cube.Add(plane); }
            game.GetField("Cube").SetValue(null, cube);
            var big = ScriptState.Read(null, game, true, new[] { "Cube" }, 6);
            string bigJson = Text(big.State);
            t.True(big.Truncated, "truncated");
            t.True(bigJson.Length < 1_000_000 && bigJson.Contains("…"), "the rest shows as …: " + bigJson.Length + " chars");
        }

        [Test]
        public static void WritesConvertCheckFirstAndStayInTheGame(TestContext t)
        {
            var asm = Compile(t);
            var (rt, m) = Begin(asm, "Write");
            object F(string n) { var f = m.GetType().GetField(n); return f != null ? f.GetValue(m) : m.GetType().GetProperty(n).GetValue(m); }
            float C(string member, string f) => (float)F(member).GetType().GetField(f).GetValue(F(member));
            try
            {
                var set = ScriptState.Write(m, m.GetType(), false, J("{\"Credits\": 5000, \"speed\": 2, \"Spawn\": [0, 1, 4], \"Pose\": \"prone\", \"Scores\": [9], \"Bag.Ammo\": 7, \"Rank\": 4}"));
                t.Equal(5000, (int)F("Credits"), "an int");
                t.Equal(2f, (float)F("Speed"), "a float (by its name in any case)");
                t.Equal("Prone", F("Pose").ToString(), "an enum by name");
                t.Equal(9, ((int[])F("Scores"))[0], "an array");
                t.Equal(4, (int)F("Rank"), "a property");
                t.Equal(7, (int)F("Bag").GetType().GetField("Ammo").GetValue(F("Bag")), "inside an object");
                t.Equal("{\"Credits\":5000,\"speed\":2,\"Spawn\":[0,1,4],\"Pose\":\"Prone\",\"Scores\":[9],\"Bag.Ammo\":7,\"Rank\":4}", Text(set), "the values read back");

                // Color, Quaternion and the game's structs — whole, by field, or inside through a path (written back)
                ScriptState.Write(m, m.GetType(), false, J("{\"Tint\": [0, 1, 0]}"));
                t.True(C("Tint", "R") == 0f && C("Tint", "G") == 1f && C("Tint", "A") == 1f, "[r, g, b] keeps the alpha");
                ScriptState.Write(m, m.GetType(), false, J("{\"Tint.A\": 0.5, \"Facing\": [0, 0.7071, 0, 0.7071], \"Spawn.X\": 9}"));
                t.True(C("Tint", "A") == 0.5f && C("Tint", "G") == 1f, "Tint.A inside the struct, written back");
                t.True(Math.Abs(C("Facing", "Y") - 0.7071f) < 1e-6f, "a Quaternion");
                t.Equal(9f, C("Spawn", "X"), "Spawn.X inside a Vector3, written back");
                ScriptState.Write(m, m.GetType(), false, J("{\"Tint\": {\"r\": 0.25}, \"Stats\": {\"Hp\": 10}, \"Stats.Armor\": 2}"));
                t.True(C("Tint", "R") == 0.25f && C("Tint", "A") == 0.5f, "an object sets only the fields it names");
                var stats = F("Stats");
                t.True((int)stats.GetType().GetField("Hp").GetValue(stats) == 10 && (float)stats.GetType().GetField("Armor").GetValue(stats) == 2f, "a struct of the game");

                // enums: by number in range, [Flags] combinations
                ScriptState.Write(m, m.GetType(), false, J("{\"Size\": 2, \"Perks\": \"Fast, Strong\"}"));
                t.Equal("B", F("Size").ToString(), "a byte enum by number");
                t.Equal("Fast, Strong", F("Perks").ToString(), "a flags combination by name");
                ScriptState.Write(m, m.GetType(), false, J("{\"Perks\": 2}"));
                t.Equal("Strong", F("Perks").ToString(), "flags by number");
                string overflow = Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Size\": 300}")));
                t.True(overflow != null && overflow.Contains("A, B"), "a number out of the enum's range is an error that lists the names, not an OverflowException: " + overflow);
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Perks\": 4}"))).Contains("Fast, Strong"), "an undefined flag");

                // one bad value: nothing is set
                string bad = Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Credits\": 1, \"Speed\": \"fast\"}")));
                t.True(bad != null && bad.Contains("Speed expects a number"), "the bad value is named: " + bad);
                t.Equal(5000, (int)F("Credits"), "… and the good one before it was not set");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Credits\": 2.5}"))).Contains("whole number"), "an int takes whole numbers only");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Fixed\": 1}"))).Contains("readonly"), "a readonly field");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Doubled\": 1}"))).Contains("no public setter"), "a get-only property");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Echo\": 1}"))).Contains("calls itself"), "a setter that calls itself is refused");
                t.True(Error(() => ScriptState.Write(m, m.GetType(), false, J("{\"Bag\": {\"Ammo\": 1}}"))).Contains("dotted path (Bag.Member)"), "an object of a class: its members through a path");
                ScriptState.Write(m, m.GetType(), false, J("{\"Target\": null}"));
                t.Equal(null, F("Target"), "a reference set to null");

                // statics: set while playing, never into .NET's objects, fresh again in the next compile
                var game = asm.GetType("Game");
                ScriptState.Write(null, game, true, J("{\"Act\": 4, \"Beat\": \"heist\"}"));
                var after = ScriptState.Read(null, game, true, new[] { "Act", "Beat" }, 1).State;
                t.True(Text(after["Act"]) == "4" && Text(after["Beat"]) == "\"heist\"", "the statics took the values");
                t.True(Error(() => ScriptState.Write(null, game, true, J("{\"Seed\": 1}"))).Contains("readonly"), "a static readonly field");
                string file = t.Write("victim.txt", "x");
                game.GetField("Log").SetValue(null, new FileInfo(file));
                string fileInfo = Error(() => ScriptState.Write(null, game, true, J("{\"Log.IsReadOnly\": true}")));
                t.True(fileInfo != null && fileInfo.Contains("only goes into the game's own") && !new FileInfo(file).IsReadOnly,
                    "a path into a FileInfo is refused and the file stays writable (#457 review): " + fileInfo);
                var next = Compile(t, "Probe2");
                var fresh = ScriptState.Read(null, ScriptState.FindClass(next, "Game"), true, new[] { "Act", "Beat" }, 1).State;
                t.True(Text(fresh["Act"]) == "1" && Text(fresh["Beat"]) == "\"intro\"", "the next session's compile starts from the initial values");
            }
            finally { rt.End(); }
        }
    }
}
