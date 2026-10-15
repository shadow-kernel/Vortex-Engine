using System;
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
