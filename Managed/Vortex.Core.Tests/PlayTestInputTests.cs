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

        /// <summary>What the editor's play tick does before the scripts run: advance the simulated input, hand its mouse movement and
        /// wheel to Input, then the scripts' tick.</summary>
        private static void Tick(ScriptRuntime rt, double dt)
        {
            var (dx, dy, scroll) = HostInput.Simulated.Tick(dt);
            Vortex.Input.MouseDeltaX = 0f; Vortex.Input.MouseDeltaY = 0f; Vortex.Input.ScrollDelta = 0f;   // nothing from the physical mouse
            Vortex.Input.SetSimulated(dx, dy, scroll);
            rt.Update((float)dt);
        }

        private static System.Reflection.Assembly CompileProbe(TestContext t)
        {
            var file = t.Write("Scripts/InputProbe.cs", Probe);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the probe does not compile: " + log);
            return asm;
        }

        private static (ScriptRuntime rt, Vortex.VortexBehaviour probe, GameEntity player) StartProbe(TestContext t, System.Reflection.Assembly asm = null)
        {
            asm ??= CompileProbe(t);
            var scene = new Scene { Name = "Input" };
            var player = new GameEntity(scene, "Player");
            player.AddComponentDirect(new Script(player, "Assets/Scripts/InputProbe.cs"));
            scene.Entities.Add(player);
            var rt = ScriptRuntime.Instance;
            rt.Begin(scene, asm);
            return (rt, rt.BehavioursOf(player)[0], player);
        }

        [Test]
        public static void SimulatedKeysReachTheGameThroughTheInputApi(TestContext t)
        {
            var sim = HostInput.Simulated;
            var oldKeys = HostInput.KeyDown;
            var oldFocus = HostInput.WindowFocused;
            ScriptRuntime rt = null;
            try
            {
                HostInput.KeyDown = vk => false;           // nobody at the keyboard …
                HostInput.WindowFocused = () => false;     // … and another app in front (the terminal running Claude Code)
                sim.Clear();
                var (r, probe, _) = StartProbe(t);
                rt = r;
                int F(string n) => (int)probe.GetType().GetField(n).GetValue(probe);
                float Fl(string n) => (float)probe.GetType().GetField(n).GetValue(probe);
                const double dt = 1.0 / 60;
                void Run(double seconds) { for (double s = 0; s < seconds - 1e-9; s += dt) Tick(rt, dt); }

                sim.Enqueue(Json("[{\"t\": 0, \"press\": \"W\"}, {\"t\": 2, \"release\": \"W\"}, {\"t\": 2.1, \"tap\": \"LButton\"}," +
                                 " {\"t\": 0.5, \"press\": \"Space\"}, {\"t\": 0.5, \"release\": \"Space\"}, {\"t\": 1, \"look\": [40, -10, 0.5]}, {\"t\": 1.2, \"scroll\": -1}]"));
                Run(3);
                t.False(Vortex.Input.WindowFocused, "the window never counted as focused — only the simulated input passes the gate");
                t.True(Math.Abs(F("WTicks") - 120) <= 1, "W held for 2 s of 60 Hz ticks: " + F("WTicks"));
                t.True(Math.Abs(Fl("Walked") - 4f) < 0.05f, "the probe walked 2 s at 2 m/s: " + Fl("Walked"));
                t.Equal(1, F("SpaceDowns"), "a press and release in the same frame is one GetKeyDown");
                t.Equal(1, F("SpaceUps"), "… and one GetKeyUp the frame after");
                t.Equal(1, F("FireDowns"), "the LButton tap fires once");
                t.True(F("FireTicks") >= 7 && F("FireTicks") <= 8, "a tap holds 0.12 s: " + F("FireTicks") + " ticks");
                t.True(Math.Abs(Fl("LookX") - 40f) < 1e-3f && Math.Abs(Fl("LookY") + 10f) < 1e-3f, "the mouse movement adds up exactly: " + Fl("LookX") + ", " + Fl("LookY"));
                t.Equal(-1f, Fl("Scroll"), "one wheel notch down");
                t.False(sim.Active, "everything played");
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
                rt?.End();
                sim.Clear();
                Vortex.Input.SetSimulated(0f, 0f, 0f);
                HostInput.KeyDown = oldKeys;
                HostInput.WindowFocused = oldFocus;
            }
        }

        [Test]
        public static void ALoneLookOrScrollReachesAnUnfocusedGame(TestContext t)
        {
            // #457 review: the simulation counted as playing only while something was left to play, so the tick that emitted the
            // last (or only) mouse movement or wheel notch found the window "unfocused" and Input dropped it
            var sim = HostInput.Simulated;
            var oldKeys = HostInput.KeyDown;
            var oldFocus = HostInput.WindowFocused;
            ScriptRuntime rt = null;
            try
            {
                HostInput.KeyDown = vk => false;
                HostInput.WindowFocused = () => false;
                var asm = CompileProbe(t);
                foreach (var (label, json) in new[]
                {
                    ("a lone scroll", "[{\"t\": 0, \"scroll\": -1}]"),
                    ("a lone one-frame look", "[{\"t\": 0, \"look\": [40, -10]}]"),
                    ("a lone 0.5 s look", "[{\"t\": 0, \"look\": [40, -10, 0.5]}]"),
                })
                foreach (double dt in new[] { 1.0 / 60, 0.033, 0.1 })
                {
                    sim.Clear();
                    var (r, probe, _) = StartProbe(t, asm);
                    rt = r;
                    float Fl(string n) => (float)probe.GetType().GetField(n).GetValue(probe);
                    sim.Enqueue(Json(json));
                    for (int i = 0; i < 40; i++) Tick(rt, dt);
                    bool scroll = label.Contains("scroll");
                    if (scroll) t.Equal(-1f, Fl("Scroll"), label + " at dt " + dt);
                    else t.True(Math.Abs(Fl("LookX") - 40f) < 1e-3f && Math.Abs(Fl("LookY") + 10f) < 1e-3f, label + " at dt " + dt + ": " + Fl("LookX") + ", " + Fl("LookY"));
                    rt.End();
                }
            }
            finally
            {
                rt?.End();
                sim.Clear();
                Vortex.Input.SetSimulated(0f, 0f, 0f);
                HostInput.KeyDown = oldKeys;
                HostInput.WindowFocused = oldFocus;
            }
        }

        [Test]
        public static void OnlySimulatedInputPassesAnUnfocusedWindow(TestContext t)
        {
            // #457 review: while simulated input played the whole game counted as focused, so a physical pad (or a global key
            // state, as on Windows) drove the game with the editor in the background
            var sim = HostInput.Simulated;
            var oldKeys = HostInput.KeyDown;
            var oldFocus = HostInput.WindowFocused;
            var oldPad = HostInput.Gamepad;
            ScriptRuntime rt = null;
            try
            {
                HostInput.KeyDown = vk => vk == 'D';       // D held on the real keyboard (a global key state)
                HostInput.Gamepad = () => new GamepadState { Connected = true, Buttons = 0x1000, LeftX = 1f };   // a pad: A held, stick right
                HostInput.WindowFocused = () => false;
                sim.Clear();
                var (r, probe, _) = StartProbe(t);
                rt = r;
                sim.Enqueue(Json("[{\"press\": \"W\"}, {\"t\": 1, \"release\": \"W\"}]"));
                Tick(rt, 1.0 / 60);
                t.True(Vortex.Input.GetKey("W"), "the simulated W reaches the unfocused game");
                t.False(Vortex.Input.GetKey("D"), "the physical D does not");
                t.False(Vortex.Input.GamepadConnected || Vortex.Input.GetGamepadButton("A") || Vortex.Input.LeftStickX != 0f, "neither does the pad");
                HostInput.WindowFocused = () => true;
                Tick(rt, 1.0 / 60);
                t.True(Vortex.Input.GetKey("W") && Vortex.Input.GetKey("D") && Vortex.Input.GetGamepadButton("A"), "in front, everything counts");
            }
            finally
            {
                rt?.End();
                sim.Clear();
                Vortex.Input.SetSimulated(0f, 0f, 0f);
                HostInput.KeyDown = oldKeys;
                HostInput.WindowFocused = oldFocus;
                HostInput.Gamepad = oldPad;
            }
        }

        [Test]
        public static void OldPlayerScriptsKeepTheirDefaults(TestContext t)
        {
            // #457 review: the player's parser read a missing number as 0 — "hold LButton" was a 0.05 s hold, "look 40" moved x only
            var problems = new List<string>();
            var warnings = new List<string>();
            var lines = InputScript.Parse(new[] { "4.0 hold LButton", "4.2 look 40", "4.4 look 25 -5", "4.6 hold R 0.3", "5 look" }, problems, warnings);
            t.Equal(4, lines.Count, "four commands kept");
            var hold = lines.First(c => c.Op == "hold" && c.Vk == 0x01);
            t.Equal(InputScript.MinHoldSeconds, hold.Seconds, "a hold without seconds is the shortest hold");
            var look = lines.First(c => c.Op == "look" && c.X == 40);
            t.True(look.Y == 0 && look.Seconds == 0, "a look without dy moves along x, in one frame");
            t.Equal(2, warnings.Count, "both said: " + string.Join(" | ", warnings));
            t.True(warnings[0].Contains("hold without seconds") && warnings[1].Contains("look without dy"), "what was assumed");
            t.True(problems.Count == 1 && problems[0].Contains("dx"), "a look without any number is still skipped: " + string.Join(" | ", problems));
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
