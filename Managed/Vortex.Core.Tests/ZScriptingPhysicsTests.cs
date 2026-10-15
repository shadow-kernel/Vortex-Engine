using System;
using System.Linq;
using System.Reflection;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Physics;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>The scripting fundamentals against the real Jolt world (#481, #425): a force applied in FixedUpdate gives the
    /// same result at 30, 60 and 144 fps, and the time scale slows the bodies down. Sorts after ZJoltPhysicsTests (it needs
    /// the native library); skipped without an engine library with Jolt.</summary>
    public static class ZScriptingPhysicsTests
    {
        private const string Scripts = @"
using Vortex;

public class Pusher : VortexBehaviour
{
    public float Force = 1000f;
    public override void FixedUpdate(float fixedDt) { AddForce(new Vector3(Force, 0f, 0f)); }
}

public class Idle : VortexBehaviour { }

public static class BodyLog { public static System.Collections.Generic.List<string> Log = new System.Collections.Generic.List<string>(); }

public static class SpringLog { public static System.Collections.Generic.List<float> Y = new System.Collections.Generic.List<float>(); }

// a damped spring around y = 100 driven from FixedUpdate: k = 400 N/m, c = 4 N·s/m on 1 kg (ω = 20 rad/s, ζ = 0.1)
public class Spring : VortexBehaviour
{
    public float K = 400f, C = 4f, Rest = 100f;
    public override void FixedUpdate(float fixedDt)
    {
        float y = Position.Y, v = Physics.GetVelocity(EntityId).Y;
        SpringLog.Y.Add(y);
        AddForce(new Vector3(0f, -K * (y - Rest) - C * v, 0f));
    }
}

public class BodyProbe : VortexBehaviour
{
    public override void OnEnable() { BodyLog.Log.Add(""OnEnable body="" + Physics.HasRigidbody(EntityId) + "" mass="" + Physics.GetMass(EntityId)); }
    public override void Start() { BodyLog.Log.Add(""Start body="" + Physics.HasRigidbody(EntityId)); }
    public override void OnDisable() { BodyLog.Log.Add(""OnDisable body="" + Physics.HasRigidbody(EntityId)); }
}
";

        internal static bool Jolt()
        {
            try { Editor.Core.Native.NativeLoader.Register(); } catch { }
            if (PhysicsNative.Probed && !PhysicsNative.Available) PhysicsNative.ResetProbe();
            if (PhysicsNative.Available) return true;
            Console.WriteLine("    (skipped: no native physics — " + PhysicsNative.Reason + ")");
            return false;
        }

        private static Assembly Compile(TestContext t)
        {
            var file = t.Write("Scripts/PhysicsProbe.cs", Scripts);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the test scripts do not compile: " + log);
            return asm;
        }

        /// <summary>A 1 m box body far from anything, no drag, gravity off unless asked.</summary>
        internal static GameEntity Body(Scene s, string name, float mass, bool gravity, string script = null)
        {
            var e = new GameEntity(s, name);
            e.Transform.LocalPosition = new Vector3(0f, 100f, 0f);
            e.AddComponentDirect(new BoxCollider(e));
            e.AddComponentDirect(new Rigidbody(e) { Mass = mass, Drag = 0f, AngularDrag = 0f, UseGravity = gravity });
            if (script != null) e.AddComponentDirect(new Script(e, "Assets/Scripts/" + script + ".cs"));
            s.Entities.Add(e);
            return e;
        }

        /// <summary>#481 acceptance: AddForce(1000 N) in FixedUpdate on a 1000 kg body reaches the same velocity after 2 s
        /// (±1 %) at 30, 60 and 144 fps — 2 m/s.</summary>
        [Test]
        public static void AForceInFixedUpdateIsFrameRateIndependent(TestContext t)
        {
            if (!Jolt()) return;
            var asm = Compile(t);
            var rt = ScriptRuntime.Instance;
            var speeds = new float[3];
            int[] fps = { 30, 60, 144 };
            try
            {
                for (int k = 0; k < fps.Length; k++)
                {
                    var s = new Scene { Name = "Push" + fps[k] };
                    var crate = Body(s, "Crate", 1000f, gravity: false, script: "Pusher");
                    PhysicsRaycastTests.AsLoaded(s);
                    GameTime.ResetSession(1f / 60f);
                    rt.Begin(s, asm);
                    t.True(PhysicsService.IsBuilt && PhysicsService.HasRigidbody(crate), "the crate is a dynamic body");
                    for (int i = 0; i < 2 * fps[k]; i++) rt.Update(1f / fps[k]);
                    speeds[k] = PhysicsService.GetVelocity(crate).X;
                    rt.End();
                }
                Console.WriteLine("        velocity after 2 s: 30 fps " + speeds[0].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ", 60 fps " + speeds[1].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ", 144 fps " + speeds[2].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + " m/s");
                foreach (var (v, f) in new[] { (speeds[0], 30), (speeds[1], 60), (speeds[2], 144) })
                    t.True(Math.Abs(v - 2f) <= 0.02f, f + " fps: 2 m/s after 2 s (" + v.ToString("0.0000") + ")");
                t.True(Math.Abs(speeds[0] - speeds[2]) <= 0.02f, "30 and 144 fps agree within 1 % (" + speeds[0] + " / " + speeds[2] + ")");
            }
            finally { rt.End(); GameTime.ResetSession(1f / 60f); PhysicsNative.Shutdown(); }
        }

        /// <summary>#481 review: FixedUpdate reads the body where the simulation has it. A damped spring (k = 400, c = 4, m = 1,
        /// 1 m out) driven from FixedUpdate settles as the maths says — e^(-ζωt) = 0.018 m at 2 s — at 30, 60 and 144 fps, with
        /// and without interpolation, and every FixedUpdate sees the same positions at every frame rate. (When FixedUpdate
        /// read the interpolated pose — a step behind, the same for all steps of a frame — the spring diverged: 28–32 m.)</summary>
        [Test]
        public static void ASpringInFixedUpdateSettlesAtEveryFrameRate(TestContext t)
        {
            if (!Jolt()) return;
            var asm = Compile(t);
            var rt = ScriptRuntime.Instance;
            var ys = (System.Collections.Generic.List<float>)asm.GetType("SpringLog").GetField("Y").GetValue(null);
            int[] fps = { 30, 60, 144 };
            try
            {
                foreach (bool interpolate in new[] { true, false })
                {
                    var runs = new System.Collections.Generic.List<float[]>();
                    foreach (int f in fps)
                    {
                        PhysicsService.Interpolate = interpolate;
                        var s = new Scene { Name = "Spring" + f };
                        var bob = Body(s, "Bob", 1f, gravity: false, script: "Spring");
                        bob.Transform.LocalPosition = new Vector3(0f, 101f, 0f);   // 1 m from rest
                        PhysicsRaycastTests.AsLoaded(s);
                        GameTime.ResetSession(1f / 60f);
                        ys.Clear();
                        rt.Begin(s, asm);
                        float late = 0f;
                        for (int i = 0; i < 3 * f; i++)
                        {
                            rt.Update(1f / f);
                            if (i >= 2 * f) late = Math.Max(late, Math.Abs(bob.Transform.LocalPosition.Y - 100f));
                        }
                        rt.End();
                        string tag = f + " fps, interpolation " + (interpolate ? "on" : "off");
                        Console.WriteLine("        " + tag + ": " + ys.Count + " steps, |y - rest| at 2..3 s at most " + late.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + " m");
                        t.True(ys.Count >= 178 && ys.Count <= 181, tag + ": 180 steps in 3 s (" + ys.Count + ")");
                        t.True(late < 0.05f, tag + ": the spring settled like the maths says (≈0.018 m at 2 s; " + late + ")");
                        runs.Add(ys.ToArray());
                    }
                    int n = runs.Min(r => r.Length);
                    float worst = 0f;
                    for (int k = 0; k < n; k++) worst = Math.Max(worst, Math.Max(Math.Abs(runs[0][k] - runs[1][k]), Math.Abs(runs[2][k] - runs[1][k])));
                    t.True(worst < 1e-4f, "interpolation " + (interpolate ? "on" : "off") + ": every FixedUpdate sees the same positions at 30, 60 and 144 fps (worst difference " + worst + " m)");
                }
            }
            finally { PhysicsService.Interpolate = true; rt.End(); GameTime.ResetSession(1f / 60f); PhysicsNative.Shutdown(); }
        }

        /// <summary>#422 acceptance: a Rigidbody child under an inactive area root has no body in Start; Scene.SetActive(root,
        /// true) creates it before the child's OnEnable (HasRigidbody true, the mass is there); SetActive(root, false) calls
        /// OnDisable while the body still exists, and removes it afterwards.</summary>
        [Test]
        public static void OnEnableSeesTheBodyAndOnDisableStillDoes(TestContext t)
        {
            if (!Jolt()) return;
            var asm = Compile(t);
            var rt = ScriptRuntime.Instance;
            var log = (System.Collections.Generic.List<string>)asm.GetType("BodyLog").GetField("Log").GetValue(null);
            try
            {
                var s = new Scene { Name = "Area" };
                var area = new GameEntity(s, "Area");
                s.Entities.Add(area);
                area.SetActiveFlagOnly(false);
                var car = new GameEntity(s, "Car");
                car.Parent = area; area.Children.Add(car);
                car.Transform.LocalPosition = new Vector3(0f, 100f, 0f);
                car.AddComponentDirect(new BoxCollider(car));
                car.AddComponentDirect(new Rigidbody(car) { Mass = 5f, UseGravity = false });
                car.AddComponentDirect(new Script(car, "Assets/Scripts/BodyProbe.cs"));
                PhysicsRaycastTests.AsLoaded(s);
                log.Clear();
                GameTime.ResetSession(1f / 60f);
                rt.Begin(s, asm);
                t.Equal("Start body=False", string.Join(" | ", log), "under the inactive root: Start, no body yet, no OnEnable");
                log.Clear();
                Vortex.Scene.SetActive(rt.HandleForEntity(area), true);
                t.Equal("OnEnable body=True mass=5", string.Join(" | ", log), "SetActive(root, true): OnEnable with the body in place");
                log.Clear();
                Vortex.Scene.SetActive(rt.HandleForEntity(area), false);
                t.Equal("OnDisable body=True", string.Join(" | ", log), "SetActive(root, false): OnDisable while the body still exists");
                t.False(PhysicsService.HasRigidbody(car), "… and it is gone afterwards");
            }
            finally { rt.End(); GameTime.ResetSession(1f / 60f); PhysicsNative.Shutdown(); }
        }

        /// <summary>#421 acceptance: a scene entity with only a Collider becomes a dynamic body after Physics.AddRigidbody(id, 5):
        /// HasRigidbody, GetMass 5, it falls; its child keeps its own body (the same id); RemoveRigidbody makes it static again;
        /// an entity with neither collider nor mesh is refused with a warning and left as it was; Stop takes the Rigidbody off.</summary>
        [Test]
        public static void ARigidbodyAddedWhilePlayingMakesAStaticPropFall(TestContext t)
        {
            if (!Jolt()) return;
            var asm = Compile(t);
            var rt = ScriptRuntime.Instance;
            try
            {
                var s = new Scene { Name = "Props" };
                var post = new GameEntity(s, "LampPost");
                post.Transform.LocalPosition = new Vector3(0f, 100f, 0f);
                post.AddComponentDirect(new BoxCollider(post));
                post.AddComponentDirect(new Script(post, "Assets/Scripts/Idle.cs"));
                s.Entities.Add(post);
                var bulb = new GameEntity(s, "Bulb");
                bulb.Transform.LocalPosition = new Vector3(0f, 2f, 0f);
                bulb.AddComponentDirect(new SphereCollider(bulb) { Radius = 0.2f });
                bulb.AddComponentDirect(new Rigidbody(bulb) { Mass = 0.5f, BodyType = RigidbodyType.Kinematic });
                bulb.Parent = post; post.Children.Add(bulb);
                var ghost = new GameEntity(s, "Ghost");   // no collider, no mesh
                ghost.Transform.LocalPosition = new Vector3(10f, 100f, 0f);
                s.Entities.Add(ghost);
                PhysicsRaycastTests.AsLoaded(s);
                GameTime.ResetSession(1f / 60f);
                rt.Begin(s, asm);
                long id = rt.HandleForEntity(post);
                t.True(PhysicsService.HasBody(post) && !PhysicsService.HasRigidbody(post), "authored: a static body");
                uint bulbBody = PhysicsService.GetBodyId(bulb);
                t.True(bulbBody != 0, "the child has its own body");

                t.True(Vortex.Physics.AddRigidbody(id, 5f), "AddRigidbody(id, 5) succeeds");
                t.True(Vortex.Physics.HasRigidbody(id), "HasRigidbody is true at once");
                t.True(Math.Abs(Vortex.Physics.GetMass(id) - 5f) < 1e-3f, "GetMass is 5 (" + Vortex.Physics.GetMass(id) + ")");
                t.Equal(bulbBody, PhysicsService.GetBodyId(bulb), "the child keeps its body (same id)");
                for (int i = 0; i < 30; i++) rt.Update(1f / 60f);
                t.True(post.Transform.LocalPosition.Y < 99.5f, "it falls (y " + post.Transform.LocalPosition.Y + ")");

                t.True(Vortex.Physics.RemoveRigidbody(id), "RemoveRigidbody");
                t.True(PhysicsService.HasBody(post) && !PhysicsService.HasRigidbody(post), "a static body again");
                t.False(Vortex.Physics.RemoveRigidbody(id), "nothing left to remove");

                long gid = rt.HandleForEntity(ghost);
                t.False(Vortex.Physics.AddRigidbody(gid, 2f), "no collider and no mesh: refused");
                t.True(ConsoleService.Instance.Entries.Any(x => x.Level == LogLevel.Warning && x.Message.Contains("'Ghost'") && x.Message.Contains("no enabled collider")), "… with a warning");
                t.True(ghost.GetComponent<Rigidbody>() == null, "… and the entity left as it was");

                t.True(Vortex.Scene.AddComponent(gid, "SphereCollider") && Vortex.Physics.AddRigidbody(gid, 2f), "a collider first, then the Rigidbody: a body");
                t.True(Math.Abs(PhysicsService.GetMass(ghost) - 2f) < 1e-3f, "with its mass");
                rt.End();
                t.True(post.GetComponent<Rigidbody>() == null && ghost.GetComponent<Rigidbody>() == null && ghost.GetComponent<SphereCollider>() == null,
                       "play end: the runtime components are gone");
            }
            finally { rt.End(); GameTime.ResetSession(1f / 60f); PhysicsNative.Shutdown(); }
        }

        /// <summary>#425: a falling body at time scale 0.5 falls half as fast per real second, and stands still at 0.</summary>
        [Test]
        public static void TheTimeScaleSlowsTheBodies(TestContext t)
        {
            if (!Jolt()) return;
            var asm = Compile(t);
            var rt = ScriptRuntime.Instance;
            try
            {
                var s = new Scene { Name = "Fall" };
                var ball = Body(s, "Ball", 1f, gravity: true, script: "Idle");
                PhysicsRaycastTests.AsLoaded(s);
                GameTime.ResetSession(1f / 60f);
                rt.Begin(s, asm);
                Vortex.Time.TimeScale = 0.5f;
                for (int i = 0; i < 60; i++) rt.Update(1f / 60f);   // one real second = half a second of game time
                float v = PhysicsService.GetVelocity(ball).Y;
                t.True(Math.Abs(v + 9.81f * 0.5f) < 0.1f, "after a real second at 0.5 the body falls at 4.9 m/s (" + v + ")");
                Vortex.Time.TimeScale = 0f;
                float y0 = ball.Transform.LocalPosition.Y;
                for (int i = 0; i < 30; i++) rt.Update(1f / 60f);
                t.True(Math.Abs(PhysicsService.GetVelocity(ball).Y - v) < 1e-4f, "paused: the velocity holds");
                t.True(Math.Abs(ball.Transform.LocalPosition.Y - y0) < 1e-3f, "paused: the body stands still (" + y0 + " → " + ball.Transform.LocalPosition.Y + ")");
            }
            finally { rt.End(); GameTime.ResetSession(1f / 60f); PhysicsNative.Shutdown(); }
        }
    }
}
