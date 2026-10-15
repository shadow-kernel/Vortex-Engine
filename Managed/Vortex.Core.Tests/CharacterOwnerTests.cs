using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Editor.Core.AI;
using Editor.Core.Data;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>#406 review, round two: which entity a script character belongs to when code other than the entity's own
    /// callback moves it, through the real script runtime — a shotgun's Update calling the guard's own Step, a squad
    /// manager stepping its guards, a behaviour tree task moving its agent, a UI action, an area event handler. A capsule
    /// that once belonged to its entity keeps it; custom ids still name no entity.</summary>
    public static class CharacterOwnerTests
    {
        private const float Dt = 1f / 60f;

        // Guard.Mode bits: 1 = moves itself every Update, 2 = moves once in Start, 4 = handles its Areas.Entered,
        // 8 = switches to the "jolt" controller in Start.
        internal const string Scripts = @"
using Vortex;

public static class OwnerProbe { public static Vector3 TaskFeet; public static int TaskTicks; }

public class Guard : VortexBehaviour
{
    public int Mode;
    public Vector3 Feet;
    public override void Start()
    {
        if ((Mode & 8) != 0) Physics.SetCharacterController(""jolt"");
        Feet = Position;
        if ((Mode & 2) != 0) Step(new Vector3(0f, -0.01f, 0f));
        if ((Mode & 4) != 0) Areas.Entered += Entered;
    }
    public override void Update(float dt) { if ((Mode & 1) != 0) Step(new Vector3(0f, -0.1f, 0f)); }
    public void Step(Vector3 move) { Feet = Physics.MoveCharacter(Feet, 0.35f, 1.8f, move, EntityId); }
    public void Hop() { Step(new Vector3(0.1f, -0.01f, 0f)); }
    private void Entered(long entity, AreaInfo area) { if (entity == EntityId) Step(new Vector3(0.1f, -0.01f, 0f)); }
}

public class Shotgun : VortexBehaviour
{
    public int FireAt = 5;
    public Vector3 Knocked;
    public long RayBefore = -2, RayAfter = -2;
    private int _frame;
    public override void Update(float dt)
    {
        if (++_frame != FireAt) return;
        long id = Scene.Find(""Guard"");
        var g = Scene.GetBehaviour<Guard>(id);
        RaycastHit h;
        RayBefore = Physics.Raycast(new Vector3(-3f, 1f, 0f), new Vector3(1f, 0f, 0f), 10f, out h) ? h.EntityId : -1;
        g.Step(new Vector3(0.3f, 0f, 0f));   // the knock-back: the guard's own method, run by the shotgun's Update
        Knocked = g.Feet;
        RayAfter = Physics.Raycast(new Vector3(-3f, 1f, 0f), new Vector3(1f, 0f, 0f), 10f, out h) ? h.EntityId : -1;
        Scene.Destroy(id);
    }
}

public class SquadManager : VortexBehaviour
{
    public int NameOwner;
    public Vector3 Feet;
    private bool _init;
    public override void Update(float dt)
    {
        long id = Scene.Find(""Guard"");
        if (id == 0) return;
        var step = new Vector3(0.02f, -0.1f, 0f);
        if (NameOwner == 0) { Scene.GetBehaviour<Guard>(id).Step(step); return; }
        if (!_init) { Feet = Scene.WorldPositionOf(id); _init = true; }
        Feet = Physics.MoveCharacter(Feet, 0.35f, 1.8f, step, id, id);
    }
}

public class WalkAgent : BtTask
{
    private Vector3 _feet;
    private bool _init;
    public override BtStatus OnTick(float dt)
    {
        if (!_init) { _feet = Scene.WorldPositionOf(Agent); _init = true; }
        _feet = Physics.MoveCharacter(_feet, 0.35f, 1.8f, new Vector3(0.02f, -0.1f, 0f), Agent);
        OwnerProbe.TaskFeet = _feet; OwnerProbe.TaskTicks++;
        return BtStatus.Running;
    }
}
";

        internal static Assembly Compile(TestContext t)
        {
            var file = t.Write("Scripts/Owners2.cs", Scripts);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the test scripts do not compile: " + log);
            return asm;
        }

        internal static GameEntity Floor(Scene s)
            => PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));

        /// <summary>The guard: a capsule collider around it (what makes it shootable) and the Guard script.</summary>
        internal static GameEntity Guard(Scene s, int mode, bool collider = true)
            => PhysicsRaycastTests.Add(s, null, "Guard", new Vector3(0, 0, 0), e =>
            {
                if (collider) e.AddComponentDirect(new CapsuleCollider(e) { Radius = 0.35f, Height = 1.8f, Center = new Vector3(0, 0.9f, 0) });
                Attach(e, "Guard", "Mode", mode.ToString());
            });

        internal static void Attach(GameEntity e, string cls, string field = null, string value = null)
        {
            var sc = new Script(e, "Assets/Scripts/" + cls + ".cs");
            if (field != null) sc.SetFieldValue(field, value);
            e.AddComponentDirect(sc);
        }

        internal static object Field(object o, string name) => o.GetType().GetField(name).GetValue(o);

        private static List<long> OwnedBy(GameEntity e)
        {
            var ids = new List<long>();
            CollisionService.CharacterIdsOwnedBy(o => o == e, ids);
            return ids;
        }

        /// <summary>The reviewer's repro: the shotgun's Update knocks the guard back through the guard's own Step (its
        /// EntityId, but the shotgun's callback), then destroys it. The capsule keeps its entity: the knock-back is not
        /// shoved by the guard's own collider, and Destroy takes the capsule along.</summary>
        [Test]
        public static void AnotherScriptsCallKeepsTheCharactersEntity(TestContext t)
        {
            var asm = Compile(t);
            var s = new Scene { Name = "Shotgun" };
            Floor(s);
            var guard = Guard(s, 1);
            var shooter = PhysicsRaycastTests.Add(s, null, "Shooter", new Vector3(-5f, 0, 0), e => Attach(e, "Shotgun"));
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(s, asm);
                long gid = rt.BehavioursOf(guard)[0].EntityId;
                for (int i = 0; i < 8; i++) rt.Update(Dt);
                var knocked = (Vortex.Vector3)Field(rt.BehavioursOf(shooter)[0], "Knocked");
                t.True(Math.Abs(knocked.X - 0.3f) < 1e-3f && Math.Abs(knocked.Y) < 0.02f,
                       "the knock-back moves the guard 0.3 m, its own collider no obstacle (" + knocked.X + ", " + knocked.Y + ")");
                t.False(CollisionService.HasCharacter(gid), "Destroy took the guard's capsule along");
            }
            finally { rt.End(); }
        }

        /// <summary>A squad manager steps its guard: through the guard's own Step after the guard once moved itself (the
        /// capsule keeps its entity), or with the guard named as the owner (Physics.MoveCharacter(…, id, ownerEntity)) when
        /// no code of the guard ever moves it.</summary>
        [Test]
        public static void AManagerSteppingAGuardMovesTheGuardsOwnCapsule(TestContext t)
        {
            var asm = Compile(t);
            foreach (var named in new[] { false, true })
            {
                var s = new Scene { Name = "Squad" + named };
                Floor(s);
                var guard = Guard(s, named ? 0 : 2);   // the guard moves once in Start, or never
                var manager = PhysicsRaycastTests.Add(s, null, "Manager", new Vector3(-5f, 0, 0), e => Attach(e, "SquadManager", "NameOwner", named ? "1" : "0"));
                var rt = ScriptRuntime.Instance;
                string how = named ? "named owner" : "the guard's own Step";
                try
                {
                    rt.Begin(s, asm);
                    long gid = rt.BehavioursOf(guard)[0].EntityId;
                    for (int i = 0; i < 20; i++) rt.Update(Dt);
                    var feet = (Vortex.Vector3)Field(named ? rt.BehavioursOf(manager)[0] : rt.BehavioursOf(guard)[0], "Feet");
                    t.True(Math.Abs(feet.X - 0.4f) < 0.01f && Math.Abs(feet.Y) < 0.02f,
                           how + ": the guard walks 0.4 m inside its own collider (" + feet.X + ", " + feet.Y + ")");
                    var owned = OwnedBy(guard);
                    t.True(owned.Count == 1 && owned[0] == gid, how + ": the capsule belongs to the guard");
                    Vortex.Scene.Destroy(gid);
                    t.False(CollisionService.HasCharacter(gid), how + ": Destroy takes it along");
                }
                finally { rt.End(); }
            }
        }

        /// <summary>A behaviour tree task moves its agent with Physics.MoveCharacter(…, Agent): the tree ticks on the agent's
        /// behalf, so the capsule is the agent's — its own collider no obstacle.</summary>
        [Test]
        public static void ABehaviourTreeTaskMovesItsAgentsOwnCapsule(TestContext t)
        {
            var asm = Compile(t);
            var tree = new BehaviorTreeAsset { Name = "Walker", Root = new BtNodeData { Kind = BtNodeKind.Sequence, Name = "Root" } };
            tree.Root.Children.Add(new BtNodeData { Kind = BtNodeKind.Task, Task = "WalkAgent" });
            tree.Normalize();
            var path = t.Write("Assets/AI/Walker.vbt", tree.ToJson());
            var s = new Scene { Name = "Tree", IsActive = true };   // the playing scene: trees of an inactive one do not tick
            Floor(s);
            var walker = PhysicsRaycastTests.Add(s, null, "Walker", new Vector3(0, 0, 0), e =>
            {
                e.AddComponentDirect(new CapsuleCollider(e) { Radius = 0.35f, Height = 1.8f, Center = new Vector3(0, 0.9f, 0) });
                e.AddComponentDirect(new BehaviorTreeAgent(e) { TreePath = path, RunOnStart = true });
            });
            var probe = asm.GetType("OwnerProbe");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(s, asm);
                for (int i = 0; i < 20; i++) rt.Update(Dt);
                int ticks = (int)probe.GetField("TaskTicks").GetValue(null);
                var feet = (Vortex.Vector3)probe.GetField("TaskFeet").GetValue(null);
                t.True(ticks >= 20, "the task ran every tick (" + ticks + ")");
                t.True(Math.Abs(feet.X - 0.4f) < 0.01f && Math.Abs(feet.Y) < 0.02f,
                       "the agent walks 0.4 m inside its own collider (" + feet.X + ", " + feet.Y + ")");
                t.Equal(1, OwnedBy(walker).Count, "the capsule belongs to the agent");
            }
            finally { rt.End(); }
        }

        /// <summary>A UI button action on a scene behaviour and a handler of the entity's Areas.Entered event are that
        /// entity's code too: the guard's Hop / Entered move its own capsule.</summary>
        [Test]
        public static void UiActionsAndAreaHandlersActForTheirEntity(TestContext t)
        {
            var asm = Compile(t);
            foreach (var viaArea in new[] { false, true })
            {
                var s = new Scene { Name = viaArea ? "Area" : "Ui" };
                Floor(s);
                var guard = Guard(s, viaArea ? 4 : 0);
                var rt = ScriptRuntime.Instance;
                string how = viaArea ? "Areas.Entered handler" : "UI action";
                try
                {
                    rt.Begin(s, asm);
                    long gid = rt.BehavioursOf(guard)[0].EntityId;
                    var info = new Vortex.AreaInfo(gid, "Zone", new string[0], 0, new Vortex.Vector3(0f, 0f, 0f), new Dictionary<string, string>());
                    for (int i = 0; i < 4; i++)
                    {
                        if (viaArea) rt.RaiseAreaEvent(guard, 0, info);
                        else rt.InvokeUiActions(new List<Editor.UI.Vui.UiAction> { new Editor.UI.Vui.UiAction { Screen = "Hud.vui", Action = "Hop" } });
                    }
                    var feet = (Vortex.Vector3)Field(rt.BehavioursOf(guard)[0], "Feet");
                    t.True(Math.Abs(feet.X - 0.4f) < 0.01f && Math.Abs(feet.Y) < 0.05f,
                           how + ": the guard moved 0.4 m inside its own collider (" + feet.X + ", " + feet.Y + ")");
                    var owned = OwnedBy(guard);
                    t.True(owned.Count == 1 && owned[0] == gid, how + ": the capsule belongs to the guard");
                }
                finally { rt.End(); }
            }
        }
    }
}
