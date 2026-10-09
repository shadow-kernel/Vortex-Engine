using System;
using System.Collections.Generic;
using Editor.Core.AI;
using Editor.Core.Services.AI;
using Vortex;

namespace VortexTests
{
    /// <summary>The behaviour tree runtime (#111): asset round trip, composite / decorator semantics, conditional aborts,
    /// task lifecycle (OnEnter / OnExit / Aborted), the looping root and the built-in blackboard tasks.</summary>
    public static class BehaviorTreeTests
    {
        // ---- test tasks: scripted outcomes, call counts ----
        public sealed class Succeeds : BtTask { public static int Ticks; public override BtStatus OnTick(float dt) { Ticks++; return BtStatus.Success; } }
        public sealed class Fails : BtTask { public static int Ticks; public override BtStatus OnTick(float dt) { Ticks++; return BtStatus.Failure; } }
        /// <summary>Runs for <c>ticks</c> ticks, then Success; counts enters / exits / aborts.</summary>
        public sealed class RunsFor : BtTask
        {
            public static int Enters, Exits, Aborts;
            private int _left;
            public override void OnEnter() { Enters++; _left = ParamInt("ticks", 2); }
            public override BtStatus OnTick(float dt) { return --_left <= 0 ? BtStatus.Success : BtStatus.Running; }
            public override void OnExit() { Exits++; if (Aborted) Aborts++; }
        }
        /// <summary>Condition: blackboard "flag" is true.</summary>
        public sealed class FlagSet : BtTask { public override BtStatus OnTick(float dt) { return Blackboard.GetBool("flag") ? BtStatus.Success : BtStatus.Failure; } }

        private static void Register()
        {
            BehaviorTreeService.RegisterTaskType(typeof(Succeeds));
            BehaviorTreeService.RegisterTaskType(typeof(Fails));
            BehaviorTreeService.RegisterTaskType(typeof(RunsFor));
            BehaviorTreeService.RegisterTaskType(typeof(FlagSet));
            Succeeds.Ticks = Fails.Ticks = 0; RunsFor.Enters = RunsFor.Exits = RunsFor.Aborts = 0;
        }

        private static BtNodeData N(BtNodeKind kind, string task = "", params BtNodeData[] children)
        {
            var n = new BtNodeData { Kind = kind, Task = task };
            n.Children.AddRange(children);
            return n;
        }

        private static BehaviorTreeRunner Runner(BtNodeData root, Action<string> log = null)
        {
            var asset = new BehaviorTreeAsset { Name = "t", Root = root };
            var r = BehaviorTreeRunner.Create(asset, "test.vbt", 0, BehaviorTreeService.ResolveTask, log ?? (s => { }));
            return r;
        }

        [Test]
        public static void AssetRoundTripsAndNormalizes(TestContext t)
        {
            var a = BehaviorTreeAsset.NewDefault("Monster");
            a.Blackboard["speed"] = "3.5";
            a.Root.Children.Add(N(BtNodeKind.Sequence, "", N(BtNodeKind.Condition, "FlagSet"), N(BtNodeKind.Task, "Succeeds")));
            a.Root.Children[0].Children[0].Abort = BtAbortMode.LowerPriority;
            a.Root.Children[0].Children[1].Params["key"] = "x";
            string json = a.ToJson();
            var b = BehaviorTreeAsset.FromJson(json);
            t.True(b != null && b.Name == "Monster" && b.Blackboard["speed"] == "3.5", "name + blackboard survive");
            t.Equal(BtNodeKind.Sequence, b.Root.Children[0].Kind, "node kinds survive");
            t.Equal(BtAbortMode.LowerPriority, b.Root.Children[0].Children[0].Abort, "abort mode survives");
            t.Equal("x", b.Root.Children[0].Children[1].Params["key"], "params survive");
            t.True(json.Contains("\"kind\": \"sequence\""), "enums serialize camelCase by name");
            // ids are unique after Normalize, decorators keep one child, tasks none
            var dup = N(BtNodeKind.Inverter, "", N(BtNodeKind.Task, "Succeeds"), N(BtNodeKind.Task, "Fails"));
            dup.Id = b.Root.Id; dup.Children[0].Children.Add(N(BtNodeKind.Task, "Succeeds"));
            b.Root.Children.Add(dup);
            b.Normalize();
            var ids = new HashSet<string>();
            foreach (var n in b.AllNodes()) t.True(ids.Add(n.Id), "ids are unique after Normalize (" + n.Id + ")");
            t.Equal(1, dup.Children.Count, "a decorator keeps one child");
            t.Equal(0, dup.Children[0].Children.Count, "a task keeps no children");
            t.True(b.ParentOf(dup.Id) == b.Root && b.Find(dup.Id) == dup, "Find / ParentOf");
        }

        [Test]
        public static void SelectorAndSequenceSemantics(TestContext t)
        {
            Register();
            // Selector: first success wins, later children untouched
            var sel = Runner(N(BtNodeKind.Selector, "", N(BtNodeKind.Task, "Fails"), N(BtNodeKind.Task, "Succeeds"), N(BtNodeKind.Task, "Succeeds")));
            sel.Tick(0.016f);
            t.Equal(BtStatus.Success, sel.LastStatus, "selector succeeds");
            t.Equal(1, Fails.Ticks, "first child ticked");
            t.Equal(1, Succeeds.Ticks, "the third child was never reached");
            // Sequence: stops at the first failure
            Register();
            var seq = Runner(N(BtNodeKind.Sequence, "", N(BtNodeKind.Task, "Succeeds"), N(BtNodeKind.Task, "Fails"), N(BtNodeKind.Task, "Succeeds")));
            seq.Tick(0.016f);
            t.Equal(BtStatus.Failure, seq.LastStatus, "sequence fails");
            t.Equal(1, Succeeds.Ticks, "the third child was never reached after the failure");
            // Running resumes where it was: a 3-tick task in a sequence keeps the earlier siblings from re-running
            Register();
            var run = N(BtNodeKind.Task, "RunsFor"); run.Params["ticks"] = "3";
            var seq2 = Runner(N(BtNodeKind.Sequence, "", N(BtNodeKind.Task, "Succeeds"), run, N(BtNodeKind.Task, "Succeeds")));
            seq2.Tick(0.016f); t.Equal(BtStatus.Running, seq2.LastStatus, "running after tick 1");
            seq2.Tick(0.016f); t.Equal(BtStatus.Running, seq2.LastStatus, "running after tick 2");
            seq2.Tick(0.016f); t.Equal(BtStatus.Success, seq2.LastStatus, "done after tick 3");
            t.Equal(2, Succeeds.Ticks, "the first sibling ran once, the last once");
            t.Equal(1, RunsFor.Enters, "OnEnter once");
            t.Equal(1, RunsFor.Exits, "OnExit once");
            t.Equal(0, RunsFor.Aborts, "not aborted");
            t.Equal("RunsFor", seq2.ActiveTaskLabel == "" ? "RunsFor" : "RunsFor", "label query does not throw");
        }

        [Test]
        public static void DecoratorsAndParallel(TestContext t)
        {
            Register();
            var inv = Runner(N(BtNodeKind.Inverter, "", N(BtNodeKind.Task, "Fails")));
            inv.Tick(0.016f); t.Equal(BtStatus.Success, inv.LastStatus, "inverter flips failure");
            var succ = Runner(N(BtNodeKind.Succeeder, "", N(BtNodeKind.Task, "Fails")));
            succ.Tick(0.016f); t.Equal(BtStatus.Success, succ.LastStatus, "succeeder");
            // Repeat 3 times: the child runs three ticks (one per tick, never twice in a tick)
            Register();
            var rep = N(BtNodeKind.Repeat, "", N(BtNodeKind.Task, "Succeeds")); rep.Params["count"] = "3";
            var r = Runner(rep);
            r.Tick(0.016f); r.Tick(0.016f);
            t.Equal(BtStatus.Running, r.LastStatus, "repeat is running after two iterations");
            r.Tick(0.016f);
            t.Equal(BtStatus.Success, r.LastStatus, "repeat finished after the third");
            t.Equal(3, Succeeds.Ticks, "child ran three times");
            // Repeat until failure
            Register();
            var untilFail = N(BtNodeKind.Repeat, "", N(BtNodeKind.Task, "Fails")); untilFail.Params["untilFailure"] = "true";
            var uf = Runner(untilFail); uf.Tick(0.016f);
            t.Equal(BtStatus.Success, uf.LastStatus, "until-failure succeeds when the child fails");
            // Cooldown: second run within the window fails
            Register();
            var cd = N(BtNodeKind.Cooldown, "", N(BtNodeKind.Task, "Succeeds")); cd.Params["seconds"] = "1";
            var c = Runner(N(BtNodeKind.Selector, "", cd, N(BtNodeKind.Task, "Fails")));
            c.Tick(0.1f); t.Equal(BtStatus.Success, c.LastStatus, "first run passes the cooldown");
            c.Tick(0.1f); t.Equal(BtStatus.Failure, c.LastStatus, "within the cooldown the node fails");
            c.Tick(1.0f); t.Equal(BtStatus.Success, c.LastStatus, "after the cooldown it runs again");
            t.Equal(2, Succeeds.Ticks, "child ran twice");
            // Parallel: fails as soon as one child fails, the running one is aborted
            Register();
            var longRun = N(BtNodeKind.Task, "RunsFor"); longRun.Params["ticks"] = "5";
            var par = Runner(N(BtNodeKind.Parallel, "", longRun, N(BtNodeKind.Task, "Fails")));
            par.Tick(0.016f);
            t.Equal(BtStatus.Failure, par.LastStatus, "parallel fails with the failing child");
            t.Equal(1, RunsFor.Aborts, "the running child was aborted (OnExit with Aborted)");
        }

        [Test]
        public static void ConditionalAbortsAndLifecycle(TestContext t)
        {
            Register();
            // Selector [ Condition(flag, abort lower) -> RunsFor(10) ] [ RunsFor(10) ]
            var guardedTask = N(BtNodeKind.Task, "RunsFor"); guardedTask.Params["ticks"] = "10";
            var guard = N(BtNodeKind.Condition, "FlagSet", guardedTask); guard.Abort = BtAbortMode.LowerPriority;
            var fallback = N(BtNodeKind.Task, "RunsFor"); fallback.Params["ticks"] = "10";
            var r = Runner(N(BtNodeKind.Selector, "", guard, fallback));
            r.Tick(0.016f); r.Tick(0.016f);
            t.Equal(BtStatus.Running, r.LastStatus, "the fallback runs while the flag is off");
            t.Equal(1, RunsFor.Enters, "one task entered (the fallback)");
            r.Blackboard.Set("flag", true);
            r.Tick(0.016f);
            t.Equal(1, RunsFor.Aborts, "the fallback was aborted when the higher-priority condition passed");
            t.Equal(2, RunsFor.Enters, "the guarded task entered");
            t.Equal(BtStatus.Running, r.LastStatus, "the guarded task runs now");
            t.Equal("RunsFor", r.ActiveTaskLabel, "active task label");
            // abort SELF: the guarded task stops when its own condition fails
            Register();
            var selfTask = N(BtNodeKind.Task, "RunsFor"); selfTask.Params["ticks"] = "10";
            var selfGuard = N(BtNodeKind.Condition, "FlagSet", selfTask); selfGuard.Abort = BtAbortMode.Self;
            var s = Runner(N(BtNodeKind.Selector, "", selfGuard, N(BtNodeKind.Task, "Succeeds")));
            s.Blackboard.Set("flag", true);
            s.Tick(0.016f); t.Equal(BtStatus.Running, s.LastStatus, "guarded task runs");
            s.Blackboard.Set("flag", false);
            s.Tick(0.016f);
            t.Equal(1, RunsFor.Aborts, "self-abort when the condition turned false");
            t.Equal(BtStatus.Success, s.LastStatus, "the selector moved on to the next child in the same tick");
            // Stop(): running tasks get OnExit with Aborted
            Register();
            var longTask = N(BtNodeKind.Task, "RunsFor"); longTask.Params["ticks"] = "10";
            var st = Runner(longTask);
            st.Tick(0.016f);
            st.Stop();
            t.Equal(1, RunsFor.Aborts, "Stop aborts the running task");
            st.Tick(0.016f);
            t.Equal(1, RunsFor.Enters, "a stopped runner no longer ticks");
            // the root loops: a finished tree restarts on the next tick
            Register();
            var loop = Runner(N(BtNodeKind.Task, "Succeeds"));
            loop.Tick(0.016f); loop.Tick(0.016f); loop.Tick(0.016f);
            t.Equal(3, Succeeds.Ticks, "the tree ran three times over three ticks");
        }

        [Test]
        public static void BuiltinBlackboardTasksAndMissingTask(TestContext t)
        {
            var set = N(BtNodeKind.Task, "SetValue"); set.Params["key"] = "Target"; set.Params["value"] = "1,2,3";
            var has = N(BtNodeKind.Condition, "HasValue"); has.Params["key"] = "Target";
            var wait = N(BtNodeKind.Task, "Wait"); wait.Params["seconds"] = "0.5";
            var log = N(BtNodeKind.Task, "Log"); log.Params["message"] = "target at {Target}";
            var lines = new List<string>();
            var old = BehaviorTreeService.Logger; BehaviorTreeService.Logger = lines.Add;
            try
            {
                var r = Runner(N(BtNodeKind.Sequence, "", set, has, wait, log), lines.Add);
                r.Tick(0.3f);
                t.Equal(BtStatus.Running, r.LastStatus, "waiting");
                var v = r.Blackboard.GetVector3("Target");
                t.True(Math.Abs(v.X - 1f) < 1e-5f && Math.Abs(v.Z - 3f) < 1e-5f, "SetValue wrote a Vector3-parsable value (" + v.X + "," + v.Y + "," + v.Z + ")");
                r.Tick(0.3f);
                t.Equal(BtStatus.Success, r.LastStatus, "sequence done after the wait");
                t.True(lines.Exists(l => l.Contains("target at 1,2,3")), "Log resolved {Target} from the blackboard: " + string.Join(" | ", lines));
                // a task nobody has: the node fails and the runner lists it
                var missing = Runner(N(BtNodeKind.Task, "NoSuchTask"), lines.Add);
                missing.Tick(0.016f);
                t.Equal(BtStatus.Failure, missing.LastStatus, "a missing task fails");
                t.True(missing.MissingTasks.Contains("NoSuchTask"), "the runner reports the missing class");
                t.True(BehaviorTreeService.Builtins.ContainsKey("MoveTo") && BehaviorTreeService.Builtins.ContainsKey("PatrolNext"), "built-ins are discovered by name");
            }
            finally { BehaviorTreeService.Logger = old; }
            // blackboard conversions
            var bb = new Blackboard();
            bb.Set("f", "2.5"); bb.Set("b", "true"); bb.Set("i", 7); bb.Set("e", 42L);
            t.True(Math.Abs(bb.GetFloat("f") - 2.5f) < 1e-6f && bb.GetBool("b") && bb.GetInt("i") == 7 && bb.GetEntity("e") == 42L, "typed reads parse strings");
            t.Equal("2.5", bb.GetString("f"), "string read of a string stays");
            t.True(bb.GetFloat("missing", -1f) == -1f, "fallback for a missing key");
        }
    }
}
