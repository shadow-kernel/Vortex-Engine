using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Editor.Core.Data;
using Editor.Core.Input;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>#457: the simulated input the editor's send_input and the player's --input file play into the game — one
    /// parser for both formats, keys and mouse buttons OR-ed with the real ones in HostInput, a press and release inside one
    /// frame still one GetKeyDown, mouse movement that adds up exactly, the wheel, the UI cursor, and the window counting as
    /// focused only while something plays. A compiled behaviour reads it all through the public Input API.</summary>
    public static class PlayTestInputTests
    {
        private const string Probe = @"
using Vortex;

public class InputProbe : VortexBehaviour
{
    public int WTicks, SpaceDowns, SpaceUps, FireDowns, FireTicks, Ticks;
    public float LookX, LookY, Scroll, Walked;
    public override void Update(float dt)
    {
        Ticks++;
        if (Input.GetKey(""W"")) { WTicks++; Walked += 2f * dt; }
        if (Input.GetKeyDown(""Space"")) SpaceDowns++;
        if (Input.GetKeyUp(""Space"")) SpaceUps++;
        if (Input.GetKeyDown(""LButton"")) FireDowns++;
        if (Input.GetKey(""LButton"")) FireTicks++;
        LookX += Input.MouseDeltaX; LookY += Input.MouseDeltaY;
        Scroll += Input.ScrollDelta;
    }
}
";

        private static List<InputScript.Command> Json(string json, List<string> problems = null)
        {
            using var doc = JsonDocument.Parse(json);
            return InputScript.FromJson(doc.RootElement.Clone(), problems ?? new List<string>());
        }

        [Test]
        public static void OneParserReadsThePlayerFileAndSendInputsJson(TestContext t)
        {
            var problems = new List<string>();
            var lines = InputScript.Parse(new[]
            {
                "# a player --input file",
                "1.0  press W",
                "2.0  release W        # a comment after a command",
                "2.5  hold LButton 0.6",
                "3.5  tap R",
                "4.0  look 40 -10 0.5",
                "4.5  scroll -1",
                "4.8  cursor 640 360",
                "5.0  capture fire.bmp",
                "6.0  exit",
                "0.5  tap Space",
                "",
                "7 press Shiftt",
                "x press W",
                "8 jump",
            }, problems);
            t.Equal(10, lines.Count, "ten commands read");
            t.Equal("tap", lines[0].Op, "sorted by time: the 0.5 s tap comes first");
            var hold = lines.First(c => c.Op == "hold");
            t.Equal(0x01, hold.Vk, "LButton is virtual key 1");
            t.Equal(0.6, hold.Seconds, "the hold's seconds");
            var look = lines.First(c => c.Op == "look");
            t.True(look.X == 40 && look.Y == -10 && look.Seconds == 0.5, "look dx dy seconds");
            t.Equal(-1.0, lines.First(c => c.Op == "scroll").X, "scroll notches");
            t.Equal("fire.bmp", lines.First(c => c.Op == "capture").Text, "capture's file");
            t.Equal(3, problems.Count, "three lines could not be read: " + string.Join(" | ", problems));
            t.True(problems[0].StartsWith("line 13") && problems[0].Contains("unknown key 'Shiftt'") && problems[0].Contains("LeftShift"), "an unknown key is named with the valid ones: " + problems[0]);
            t.True(problems[2].Contains("unknown command 'jump'"), "an unknown command: " + problems[2]);

            // send_input's actions: the same commands as JSON
            var json = Json("[{\"t\": 0, \"press\": \"W\"}, {\"t\": 2, \"release\": \"W\"}, {\"t\": 2.1, \"tap\": \"LButton\"}, {\"t\": 2.5, \"look\": [40, -10, 0.5]}," +
                            " {\"t\": 3, \"hold\": [\"LeftShift\", 0.8]}, {\"t\": 4, \"scroll\": 1}, {\"cursor\": [100, 50]}]");
            t.Equal(7, json.Count, "seven actions");
            t.True(json[0].Op == "press" && json[1].Op == "cursor", "t defaults to 0, and actions of the same time keep their order");
            t.Equal(0xA0, json.First(c => c.Op == "hold").Vk, "LeftShift");
            t.Equal(3.8, json.First(c => c.Op == "hold").End, "a hold ends after its seconds");
            var bad = new List<string>();
            Json("[{\"t\": 1, \"press\": \"W\", \"release\": \"W\"}, {\"t\": -1, \"tap\": \"E\"}, {\"look\": [1]}, {\"t\": 2, \"capture\": \"x.bmp\"}, {\"t\": 3}, \"tap W\", {\"tap\": \"Nope\"}]", bad);
            t.Equal(7, bad.Count, "every bad action is reported: " + string.Join(" | ", bad));
            t.True(bad[0].Contains("one command per action"), "two commands in one action");
            t.True(bad[3].Contains("only works in the standalone player"), "capture is the player's");
            t.True(bad[6].Contains("unknown key 'Nope'"), "an unknown key");
        }

        [Test]
        public static void SimulatedKeysReachTheGameThroughTheInputApi(TestContext t)
        {
            var file = t.Write("Scripts/InputProbe.cs", Probe);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the probe does not compile: " + log);
            var scene = new Scene { Name = "Input" };
            var player = new GameEntity(scene, "Player");
            player.AddComponentDirect(new Script(player, "Assets/Scripts/InputProbe.cs"));
            scene.Entities.Add(player);
            var rt = ScriptRuntime.Instance;
            var sim = HostInput.Simulated;
            var oldKeys = HostInput.KeyDown;
            var oldFocus = HostInput.WindowFocused;
            try
            {
                HostInput.KeyDown = vk => false;           // nobody at the keyboard …
                HostInput.WindowFocused = () => false;     // … and another app in front (the terminal running Claude Code)
                sim.Clear();
                rt.Begin(scene, asm);
                var probe = rt.BehavioursOf(player)[0];
                int F(string n) => (int)probe.GetType().GetField(n).GetValue(probe);
                float Fl(string n) => (float)probe.GetType().GetField(n).GetValue(probe);
                const double dt = 1.0 / 60;
                void Run(double seconds)
                {
                    for (double s = 0; s < seconds - 1e-9; s += dt)
                    {
                        // what the editor's play tick does before the scripts run
                        var (dx, dy, scroll) = sim.Tick(dt);
                        Vortex.Input.MouseDeltaX = dx; Vortex.Input.MouseDeltaY = dy; Vortex.Input.ScrollDelta = scroll;
                        rt.Update((float)dt);
                    }
                }

                t.False(Vortex.Input.WindowFocused, "unfocused before anything plays");
                sim.Enqueue(Json("[{\"t\": 0, \"press\": \"W\"}, {\"t\": 2, \"release\": \"W\"}, {\"t\": 2.1, \"tap\": \"LButton\"}," +
                                 " {\"t\": 0.5, \"press\": \"Space\"}, {\"t\": 0.5, \"release\": \"Space\"}, {\"t\": 1, \"look\": [40, -10, 0.5]}, {\"t\": 1.2, \"scroll\": -1}]"));
                t.True(Vortex.Input.WindowFocused, "while input plays, the game counts as focused");
                Run(3);
                t.True(Math.Abs(F("WTicks") - 120) <= 1, "W held for 2 s of 60 Hz ticks: " + F("WTicks"));
                t.True(Math.Abs(Fl("Walked") - 4f) < 0.05f, "the probe walked 2 s at 2 m/s: " + Fl("Walked"));
                t.Equal(1, F("SpaceDowns"), "a press and release in the same frame is one GetKeyDown");
                t.Equal(1, F("SpaceUps"), "… and one GetKeyUp the frame after");
                t.Equal(1, F("FireDowns"), "the LButton tap fires once");
                t.True(F("FireTicks") >= 7 && F("FireTicks") <= 8, "a tap holds 0.12 s: " + F("FireTicks") + " ticks");
                t.True(Math.Abs(Fl("LookX") - 40f) < 1e-3f && Math.Abs(Fl("LookY") + 10f) < 1e-3f, "the mouse movement adds up exactly: " + Fl("LookX") + ", " + Fl("LookY"));
                t.Equal(-1f, Fl("Scroll"), "one wheel notch down");
                t.False(sim.Active, "everything played");
                t.False(Vortex.Input.WindowFocused, "unfocused again once it is over");
                t.False(HostInput.IsKeyDown(0x57), "W is up");

                // the real keyboard still works next to it (OR), and Clear (Stop) releases what is held
                HostInput.WindowFocused = () => true;
                HostInput.KeyDown = vk => vk == 0x44;      // D held for real
                sim.Enqueue(Json("[{\"press\": \"W\"}]"));
                Run(dt);
                t.True(HostInput.IsKeyDown(0x44) && HostInput.IsKeyDown(0x57), "the real D and the simulated W at once");
                sim.Clear();
                t.False(HostInput.IsKeyDown(0x57) || sim.Active, "Clear releases the simulated keys");
                t.True(HostInput.IsKeyDown(0x44), "… not the real ones");
            }
            finally
            {
                rt.End();
                sim.Clear();
                HostInput.KeyDown = oldKeys;
                HostInput.WindowFocused = oldFocus;
            }
        }

        [Test]
        public static void InputRunsOnTheGameClockAndTheCursorStaysUntilTheMouseMoves(TestContext t)
        {
            var sim = new SimulatedInput();
            double end = sim.Enqueue(Json("[{\"t\": 0.5, \"hold\": [\"E\", 0.25]}, {\"t\": 1, \"cursor\": [320, 180]}, {\"t\": 1, \"tap\": \"LButton\"}]"));
            t.Equal(1.12, Math.Round(end, 2), "the sequence is over when the last tap is released");
            sim.Advance(0.4);
            t.False(sim.IsDown('E'), "nothing before its time");
            sim.Advance(0.5);
            t.True(sim.IsDown('E') && sim.PressedThisTick.Contains('E'), "E goes down at 0.5 s");
            sim.Advance(0.6);
            t.True(sim.IsDown('E') && sim.PressedThisTick.Count == 0, "held, no new press");
            sim.Advance(0.8);
            t.False(sim.IsDown('E'), "released after 0.25 s");
            sim.Advance(1.0);
            t.True(sim.HasCursor && sim.CursorX == 320 && sim.CursorY == 180 && sim.IsDown(0x01), "cursor placed, left button down: a UI click there");
            sim.Advance(1.2);
            t.False(sim.Active, "over");
            t.True(sim.HasCursor, "the cursor stays where it was put after the sequence");
            sim.ReleaseCursor();
            t.False(sim.HasCursor, "until the real mouse moves");
            sim.Advance(1.0);
            t.Equal(1.2, sim.Now, "the clock never runs backwards");

            // a later call queues from the clock as it is then; Cancel drops what is left, keeping the clock
            sim.Enqueue(Json("[{\"press\": \"W\"}, {\"t\": 5, \"release\": \"W\"}]"));
            sim.Advance(1.3);
            t.True(sim.IsDown('W') && sim.Active, "W held");
            sim.Cancel();
            t.False(sim.IsDown('W') || sim.Active, "cancel releases it and drops the release still queued");
            t.Equal(1.3, sim.Now, "the clock stays");

            // an idle tick does no work
            var idle = new SimulatedInput();
            var r = idle.Advance(0.016);
            t.True(r.dx == 0 && r.dy == 0 && r.scroll == 0 && !idle.Active, "idle");
        }
    }
}
