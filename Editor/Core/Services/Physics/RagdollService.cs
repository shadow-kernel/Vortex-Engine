using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Editor.Core.Animation;
using Editor.DllWrapper;
using Editor.ECS;
using RagdollComponent = Editor.ECS.Components.Physics.Ragdoll;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Editor.Core.Services.Physics
{
    /// <summary>
    /// Ragdolls (GitHub #104). A ragdoll is built when it is activated, from two things only: the character's skeleton
    /// (the humanoid bones are found by <see cref="RigMap"/> on any rig — Mixamo, Unreal, Rigify, Unity …) and its
    /// CURRENT animated pose. Each major bone gets a Jolt body placed exactly where the animation has it — pelvis and
    /// chest as capsules across the hips / shoulders, spine, a sphere for the head, capsules for upper and lower arms
    /// and legs — so the hand-over never pops, and the bodies inherit the pose's velocity (a running character keeps
    /// running into the fall). Parts are joined by swing-twist joints, elbows and knees by hinges limited to their
    /// anatomical range (measured from the bend they have at activation).
    ///
    /// The bodies live on the DEBRIS layer: they collide with the level and with props, never with each other or with
    /// characters (a body never blocks the player), and the scripts' Physics.Raycast (Jolt) still hits them and reports
    /// the character's entity. <see cref="PhysicsService"/> routes impulses aimed at a ragdolled entity to the nearest
    /// part, so the weapon code that shoves props shoves bodies too.
    ///
    /// Every frame <see cref="ProcessPose"/> (called by AnimationService right before the skinning palette) replaces the
    /// animated pose with the simulated one: simulated bones take their body's pose (interpolated between the last two
    /// physics steps), every other bone keeps the local transform it had at activation relative to its parent (fingers
    /// keep their grip, the neck follows the chest). Bone sockets read the same pose, so a gun in a hand falls with it.
    /// </summary>
    public static class RagdollService
    {
        // ================================================================ definition (bind-pose analysis)

        public enum JointKind { None, Ball, Hinge }

        /// <summary>One body part of the humanoid template, resolved to skeleton nodes.</summary>
        public sealed class PartDef
        {
            public string Role;                 // "Pelvis", "Spine", "Chest", "Head", "UpperArm.L", "LowerArm.L", ...
            public int Node;                    // the skeleton node this body drives
            public int Parent = -1;             // parent part index (-1 = the root part)
            public int End = -1;                // node the capsule reaches (the next joint); -1 = derived
            public int LateralA = -1, LateralB = -1;   // pelvis / chest: capsule across these two joints
            public float Radius;                // fraction of the character's height
            public float Extend;                // stretch the capsule past End by this fraction (the hand on a forearm)
            public float MassShare;
            public bool Sphere;                 // head
            public JointKind Joint;
            public float Swing, TwistMin, TwistMax;   // ball joint limits (degrees)
            public float FlexMin, FlexMax;            // hinge limits measured from a straight limb (degrees)
            public bool FlexForward;                  // hinge flexes toward the body front (elbow) or the back (knee)
        }

        /// <summary>The ragdoll layout of one skeleton (cached per skeleton).</summary>
        public sealed class Definition
        {
            public SkeletonDef Skeleton;
            public readonly List<PartDef> Parts = new List<PartDef>();
            /// <summary>Bind-pose stature in model units (feet to the top of the head).</summary>
            public float HeightModel;
            public int Pelvis = -1;
            public readonly AnimationService.RigReport Report = new AnimationService.RigReport();
            public bool Ok => Report.Ok;
        }

        private static readonly ConditionalWeakTable<SkeletonDef, Definition> _defs = new ConditionalWeakTable<SkeletonDef, Definition>();

        /// <summary>The ragdoll layout the skeleton resolves to (cached). Check <see cref="Definition.Ok"/> and the
        /// report's notes when a rig is not recognised.</summary>
        public static Definition Describe(SkeletonDef skel)
        {
            if (skel == null) return null;
            return _defs.GetValue(skel, BuildDefinition);
        }

        /// <summary>Inspector / test readout: the parts an entity's skeleton resolves to.</summary>
        public static AnimationService.RigReport DescribeEntity(GameEntity entity)
        {
            var skel = entity != null ? AnimationService.Instance.SkeletonOf(entity) : null;
            if (skel == null)
            {
                var r = new AnimationService.RigReport();
                r.Notes.Add("No skeleton: the entity needs an Animator and a skinned (rigged) mesh.");
                return r;
            }
            return Describe(skel).Report;
        }

        private static Definition BuildDefinition(SkeletonDef skel)
        {
            var def = new Definition { Skeleton = skel };
            var rep = def.Report;
            var si = RigMap.Info(skel);
            if (si == null) { rep.Notes.Add("No skeleton."); return def; }
            var notes = new List<string>();

            int footL = RigMap.FindFoot(si, -1), footR = RigMap.FindFoot(si, 1);
            int shinL = -1, thighL = -1, shinR = -1, thighR = -1;
            bool legL = footL >= 0 && RigMap.ResolveLimb(si, footL, out shinL, out thighL);
            bool legR = footR >= 0 && RigMap.ResolveLimb(si, footR, out shinR, out thighR);
            if (!legL || !legR)
            {
                rep.Notes.Add("Legs not found: a ragdoll needs a left and a right foot bone with knee and hip joints above them.");
                return def;
            }
            int pelvis = RigMap.Lca(si, thighL, thighR);
            if (pelvis < 0) { rep.Notes.Add("No common pelvis bone above both legs."); return def; }
            def.Pelvis = pelvis;

            int handL = RigMap.FindHand(si, -1, notes), handR = RigMap.FindHand(si, 1, notes);
            int foreL = -1, upperL = -1, foreR = -1, upperR = -1;
            bool armL = handL >= 0 && RigMap.ResolveLimb(si, handL, out foreL, out upperL);
            bool armR = handR >= 0 && RigMap.ResolveLimb(si, handR, out foreR, out upperR);
            bool arms = armL && armR;
            int chest = arms ? RigMap.Lca(si, upperL, upperR) : -1;
            if (chest >= 0 && !RigMap.IsAncestor(si, pelvis, chest)) chest = -1;
            if (chest < 0) arms = false;

            int head = RigMap.FindHead(si, notes);
            if (head >= 0 && chest >= 0 && !RigMap.IsAncestor(si, chest, head)) head = -1;

            // the spine body: the first moving bone above the pelvis on the way to the chest
            int spine = -1;
            if (chest >= 0)
            {
                for (int p = RigMap.Parent(si, chest); p >= 0 && p != pelvis; p = RigMap.Parent(si, p))
                    if (si.Effective[p] && !RigMap.IsHelper(si, p)) spine = p;
            }

            var up = RigMap.ModelFrame(si).Up;
            float feet = Math.Min(Vector3.Dot(RigMap.Pos(si, footL), up), Vector3.Dot(RigMap.Pos(si, footR), up));
            float top = head >= 0 ? Vector3.Dot(RigMap.Pos(si, head), up)
                      : chest >= 0 ? Vector3.Dot(RigMap.Pos(si, chest), up) : Vector3.Dot(RigMap.Pos(si, pelvis), up);
            // ankle-to-head-joint is ~84% of the stature (chest ~72%, pelvis ~53%)
            float frac = head >= 0 ? 0.84f : chest >= 0 ? 0.70f : 0.50f;
            def.HeightModel = Math.Max(1e-4f, (top - feet) / frac);

            var parts = def.Parts;
            parts.Add(new PartDef { Role = "Pelvis", Node = pelvis, LateralA = thighL, LateralB = thighR, Radius = 0.085f, MassShare = 0.15f });
            int torso = 0;
            if (spine >= 0)
            {
                parts.Add(new PartDef { Role = "Spine", Node = spine, End = chest >= 0 ? chest : -1, Parent = 0, Radius = 0.075f, MassShare = 0.11f,
                    Joint = JointKind.Ball, Swing = 25f, TwistMin = -20f, TwistMax = 20f });
                torso = parts.Count - 1;
            }
            int chestPart = -1;
            if (chest >= 0)
            {
                bool direct = spine < 0;
                parts.Add(new PartDef { Role = "Chest", Node = chest, LateralA = upperL, LateralB = upperR, Parent = torso, Radius = 0.085f, MassShare = 0.17f,
                    Joint = JointKind.Ball, Swing = direct ? 35f : 25f, TwistMin = direct ? -30f : -20f, TwistMax = direct ? 30f : 20f });
                chestPart = parts.Count - 1;
                torso = chestPart;
            }
            if (head >= 0 && chestPart >= 0)
            {
                int headEnd = RigMap.MainChild(si, head);
                parts.Add(new PartDef { Role = "Head", Node = head, End = headEnd, Parent = chestPart, Sphere = true, Radius = 0.062f, MassShare = 0.07f,
                    Joint = JointKind.Ball, Swing = 40f, TwistMin = -45f, TwistMax = 45f });
            }
            if (arms)
            {
                AddArm(parts, "L", upperL, foreL, handL, chestPart);
                AddArm(parts, "R", upperR, foreR, handR, chestPart);
            }
            AddLeg(parts, "L", thighL, shinL, footL);
            AddLeg(parts, "R", thighR, shinR, footR);

            float sum = 0f;
            foreach (var p in parts) sum += p.MassShare;
            foreach (var p in parts) p.MassShare /= Math.Max(sum, 1e-4f);

            rep.Ok = true;
            rep.Lines.Add("Pelvis: " + RigMap.NameOf(si, pelvis) + (spine >= 0 ? "   Spine: " + RigMap.NameOf(si, spine) : ""));
            if (chest >= 0) rep.Lines.Add("Chest: " + RigMap.NameOf(si, chest) + (head >= 0 ? "   Head: " + RigMap.NameOf(si, head) : ""));
            if (arms) rep.Lines.Add("Arms: " + RigMap.NameOf(si, upperL) + " / " + RigMap.NameOf(si, foreL) + ",  " + RigMap.NameOf(si, upperR) + " / " + RigMap.NameOf(si, foreR));
            rep.Lines.Add("Legs: " + RigMap.NameOf(si, thighL) + " / " + RigMap.NameOf(si, shinL) + ",  " + RigMap.NameOf(si, thighR) + " / " + RigMap.NameOf(si, shinR));
            rep.Lines.Add(parts.Count + " bodies, " + (parts.Count - 1) + " joints");
            if (!arms) rep.Notes.Add("No arms found (hands with an elbow and a shoulder above them) — the arms follow the chest rigidly.");
            if (head < 0) rep.Notes.Add("No head bone found — the head follows the chest rigidly.");
            foreach (var n in notes) rep.Notes.Add(n);
            rep.Bones["Pelvis"] = new[] { RigMap.NameOf(si, pelvis) };
            foreach (var p in parts) rep.Bones[p.Role] = new[] { RigMap.NameOf(si, p.Node) };
            return def;
        }

        private static void AddArm(List<PartDef> parts, string side, int upper, int fore, int hand, int chestPart)
        {
            parts.Add(new PartDef { Role = "UpperArm." + side, Node = upper, End = fore, Parent = chestPart, Radius = 0.034f, MassShare = 0.03f,
                Joint = JointKind.Ball, Swing = 80f, TwistMin = -60f, TwistMax = 60f });
            parts.Add(new PartDef { Role = "LowerArm." + side, Node = fore, End = hand, Extend = 0.35f, Parent = parts.Count - 1, Radius = 0.029f, MassShare = 0.025f,
                Joint = JointKind.Hinge, FlexMin = -5f, FlexMax = 145f, FlexForward = true });
        }

        private static void AddLeg(List<PartDef> parts, string side, int thigh, int shin, int foot)
        {
            parts.Add(new PartDef { Role = "UpperLeg." + side, Node = thigh, End = shin, Parent = 0, Radius = 0.052f, MassShare = 0.11f,
                Joint = JointKind.Ball, Swing = 60f, TwistMin = -25f, TwistMax = 25f });
            parts.Add(new PartDef { Role = "LowerLeg." + side, Node = shin, End = foot, Parent = parts.Count - 1, Radius = 0.04f, MassShare = 0.055f,
                Joint = JointKind.Hinge, FlexMin = -3f, FlexMax = 150f, FlexForward = false });
        }

        // ================================================================ runtime state

        private sealed class Part
        {
            public PartDef Def;
            public uint Body, Joint;
            public Matrix4x4 BodyToNode;        // node world (with scale) = BodyToNode * body pose
            public Vector3 PrevPos, LastPos;
            public Quaternion PrevRot, LastRot;
        }

        private sealed class Instance
        {
            public GameEntity Entity, MeshEntity;
            public Definition Def;
            public Part[] Parts;
            public int[] PartOfNode;            // node -> part index, -1 = follows its parent
            public Matrix4x4[] FrozenLocal;     // node local (model space) at activation
            public float BlendTime;
            public long StartTicks;
            public bool CollidersRemoved;
            public bool ClipStopped;            // the animation was frozen once the simulation took over (no more clip events)
        }

        /// <summary>The last two evaluated poses of an entity that carries a Ragdoll component (velocity at activation).</summary>
        private sealed class History
        {
            public Matrix4x4[] Worlds, PrevWorlds;
            public Matrix4x4 Mesh, PrevMesh;
            public long Ticks, PrevTicks;
        }

        private static readonly Dictionary<GameEntity, Instance> _active = new Dictionary<GameEntity, Instance>();
        private static readonly Dictionary<GameEntity, History> _history = new Dictionary<GameEntity, History>();
        private static readonly HashSet<GameEntity> _startHandled = new HashSet<GameEntity>();
        private static readonly HashSet<GameEntity> _warned = new HashSet<GameEntity>();
        private static readonly RagdollComponent _defaults = new RagdollComponent();
        private static readonly float[] _p3 = new float[3], _q4 = new float[4], _a3 = new float[3], _n3 = new float[3];

        /// <summary>Number of active ragdolls (tests, stats).</summary>
        public static int ActiveCount => _active.Count;

        public static bool IsActive(GameEntity entity) => entity != null && _active.ContainsKey(entity);

        /// <summary>Must the entity's animator evaluate this frame even without a playing clip? (an active ragdoll, or
        /// one that starts at play start)</summary>
        public static bool NeedsPose(GameEntity entity)
        {
            if (entity == null) return false;
            if (_active.ContainsKey(entity)) return true;
            if (!PhysicsService.IsBuilt) return false;
            var c = entity.GetComponent<RagdollComponent>();
            return c != null && c.IsEnabled && c.ActivateOnStart && !_startHandled.Contains(entity);
        }

        /// <summary>Is any of the entity's ancestors (or the entity) an active ragdoll? Its pose comes from physics.</summary>
        public static bool IsActiveOrChildOf(GameEntity entity)
        {
            for (var e = entity; e != null; e = e.Parent) if (_active.ContainsKey(e)) return true;
            return false;
        }

        // ================================================================ activation

        /// <summary>Turn an animated character into a ragdoll at its current pose. <paramref name="impulse"/> (N·s,
        /// world space) is applied to the body part nearest <paramref name="point"/> — the shot that killed it. Returns
        /// false (and logs why) when the entity has no recognisable skeleton or physics is not running. Calling it on
        /// an active ragdoll just applies the impulse.</summary>
        public static bool Activate(GameEntity entity, Vector3? impulse = null, Vector3? point = null)
        {
            if (entity == null) return false;
            if (_active.TryGetValue(entity, out var existing))
            {
                if (impulse.HasValue) ApplyImpulse(existing, impulse.Value, point ?? PelvisPosition(existing));
                return true;
            }
            if (!PhysicsService.IsBuilt) { WarnOnce(entity, "physics is not running (play mode in a Jolt-enabled build)"); return false; }

            var anim = AnimationService.Instance;
            var skel = anim.SkeletonOf(entity);
            if (skel == null || !skel.IsValid) { WarnOnce(entity, "no skeleton — the entity needs an Animator and a rigged mesh"); return false; }
            var def = Describe(skel);
            if (!def.Ok) { WarnOnce(entity, "the skeleton is not a recognisable humanoid: " + string.Join(" ", def.Report.Notes)); return false; }

            _history.TryGetValue(entity, out var hist);
            var worlds = hist?.Worlds ?? anim.CurrentNodeWorlds(entity) ?? skel.BindNodeWorlds();
            if (worlds == null || worlds.Length != skel.Nodes.Length) worlds = skel.BindNodeWorlds();
            var meshEntity = anim.FindSkinnedMeshEntity(entity) ?? entity;
            var mesh = BoneSocketService.EntityWorld(meshEntity);
            var settings = entity.GetComponent<RagdollComponent>() ?? _defaults;

            var inst = new Instance
            {
                Entity = entity, MeshEntity = meshEntity, Def = def,
                Parts = new Part[def.Parts.Count],
                PartOfNode = new int[skel.Nodes.Length],
                FrozenLocal = new Matrix4x4[skel.Nodes.Length],
                BlendTime = settings.BlendTime,
                StartTicks = Stopwatch.GetTimestamp(),
            };
            for (int i = 0; i < inst.PartOfNode.Length; i++) inst.PartOfNode[i] = -1;
            for (int i = 0; i < skel.Nodes.Length; i++)
            {
                int par = skel.Nodes[i].Parent;
                Matrix4x4 local = worlds[i];
                if (par >= 0 && par < i && Matrix4x4.Invert(worlds[par], out var invPar)) local = worlds[i] * invPar;
                inst.FrozenLocal[i] = IsFinite(local) ? local : Matrix4x4.Identity;
            }

            float scale = ModelToWorldScale(mesh);
            float height = def.HeightModel * scale;              // metres
            if (!(height > 0.2f) || height > 50f) height = 1.8f;
            var fwd = BodyForward(skel, worlds, mesh, def.Pelvis);
            ulong entityId = unchecked((ulong)entity.EntityId);

            bool ok = true;
            for (int k = 0; k < def.Parts.Count && ok; k++)
            {
                var pd = def.Parts[k];
                var part = new Part { Def = pd };
                inst.Parts[k] = part;
                inst.PartOfNode[pd.Node] = k;
                ok = CreateBody(inst, part, worlds, mesh, height, settings, entityId);
            }
            for (int k = 1; k < def.Parts.Count && ok; k++)
                ok = CreateJoint(inst, inst.Parts[k], worlds, mesh, fwd, settings);
            if (!ok)
            {
                DestroyBodies(inst);
                WarnOnce(entity, "the physics bodies could not be created");
                return false;
            }

            SeedVelocities(inst, worlds, mesh, hist);
            if (impulse.HasValue) ApplyImpulse(inst, impulse.Value, point ?? PelvisPosition(inst));

            if (settings.DisableColliders)
            {
                try { PhysicsService.RemoveEntity(entity); } catch { }
                try { CollisionService.RemoveEntityShapes(entity, true); } catch { }
                inst.CollidersRemoved = true;
            }
            // a character controller stops being one: its capsule must not keep blocking the spot it died on
            try
            {
                long handle = Editor.Scripting.ScriptRuntime.Instance?.HandleForEntity(entity) ?? 0;
                if (handle != 0) CollisionService.RemoveCharacter(handle);
            }
            catch { }

            _active[entity] = inst;
            if (inst.BlendTime <= 0f) StopClip(inst);
            SceneRenderService.RuntimeDirty = true;
            return true;
        }

        /// <summary>Freeze the character's clip once the simulation owns the pose: a dead body must not keep firing the
        /// running clip's footstep events. With a blend time the clip plays on until the hand-over is complete.</summary>
        private static void StopClip(Instance inst)
        {
            if (inst.ClipStopped) return;
            inst.ClipStopped = true;
            try { AnimationService.Instance.Stop(inst.Entity); } catch { }
        }

        /// <summary>Hand the character back to its animation: the bodies are destroyed and the entity's own colliders
        /// come back. The entity stays where it is — move it to <see cref="PelvisPosition(GameEntity)"/> first for a
        /// get-up.</summary>
        public static void Deactivate(GameEntity entity)
        {
            if (entity == null || !_active.TryGetValue(entity, out var inst)) return;
            _active.Remove(entity);
            DestroyBodies(inst);
            if (inst.CollidersRemoved && PhysicsService.IsBuilt)
            {
                try { CollisionService.AddEntityShapes(entity); } catch { }
                try { PhysicsService.AddEntity(entity); } catch { }
            }
            SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>World position of the ragdoll's pelvis (the entity's position when it is not a ragdoll).</summary>
        public static Vector3 PelvisPosition(GameEntity entity)
        {
            if (entity != null && _active.TryGetValue(entity, out var inst)) return PelvisPosition(inst);
            return entity != null ? BoneSocketService.EntityWorld(entity).Translation : Vector3.Zero;
        }

        private static Vector3 PelvisPosition(Instance inst) => inst.Parts.Length > 0 ? inst.Parts[0].LastPos : Vector3.Zero;

        /// <summary>Shove the body part nearest <paramref name="point"/> (a shot, an explosion, a kick).</summary>
        public static bool AddImpulseAtPoint(GameEntity entity, Vector3 impulse, Vector3 point)
        {
            if (entity == null || !_active.TryGetValue(entity, out var inst)) return false;
            ApplyImpulse(inst, impulse, point);
            return true;
        }

        /// <summary>Push the whole body (every part, by mass share): explosions, a vehicle.</summary>
        public static bool AddImpulse(GameEntity entity, Vector3 impulse)
        {
            if (entity == null || !_active.TryGetValue(entity, out var inst)) return false;
            foreach (var p in inst.Parts)
            {
                if (p.Body == 0) continue;
                Fill(_a3, impulse * p.Def.MassShare);
                try { VortexAPI.PhysicsSetActive(p.Body, 1); VortexAPI.PhysicsAddImpulse(p.Body, _a3); } catch { }
            }
            return true;
        }

        private static void ApplyImpulse(Instance inst, Vector3 impulse, Vector3 point)
        {
            Part best = null; float bestD = float.MaxValue;
            foreach (var p in inst.Parts)
            {
                if (p.Body == 0) continue;
                float d = (p.LastPos - point).LengthSquared();
                if (d < bestD) { bestD = d; best = p; }
            }
            if (best == null) return;
            Fill(_a3, impulse); Fill(_p3, point);
            try { VortexAPI.PhysicsSetActive(best.Body, 1); VortexAPI.PhysicsAddImpulseAtPoint(best.Body, _a3, _p3); } catch { }
        }

        // ================================================================ building

        private static bool CreateBody(Instance inst, Part part, Matrix4x4[] worlds, Matrix4x4 mesh, float height, RagdollComponent s, ulong entityId)
        {
            var pd = part.Def;
            Matrix4x4 W(int node) => worlds[node] * mesh;
            Vector3 P(int node) => W(node).Translation;

            float r = Math.Max(0.015f, pd.Radius * height * s.Thickness);
            Vector3 center, axisY, axisX;
            float half = 0f;
            int shape;
            var nodeW = W(pd.Node);
            if (pd.LateralA >= 0 && pd.LateralB >= 0)
            {
                Vector3 a = P(pd.LateralA), b = P(pd.LateralB);
                Vector3 mid = (a + b) * 0.5f;
                center = Vector3.Lerp(mid, nodeW.Translation, pd.Role == "Chest" ? 0.45f : 0.5f);
                axisY = SafeNormalize(b - a, Row(nodeW, 0));
                // "up" of a lateral body: from the pelvis toward the chest, or from the chest toward the neck
                Vector3 upRef = center - P(inst.Def.Pelvis);
                if (pd.Role == "Pelvis") upRef = P(pd.Node) - mid;
                if (upRef.LengthSquared() < 1e-8f) upRef = Row(nodeW, 1);
                axisX = upRef;
                half = Math.Max(0.02f, (b - a).Length() * 0.5f * 0.8f - r * 0.5f);
                shape = PhysicsService.ShapeCapsule;
            }
            else if (pd.Sphere)
            {
                var hp = P(pd.Node);
                if (pd.End >= 0)
                {
                    var ep = P(pd.End);
                    center = (hp + ep) * 0.5f;
                    r = Clamp(0.5f * (ep - hp).Length(), 0.04f * height, 0.075f * height) * s.Thickness;
                }
                else
                {
                    int par = inst.Def.Skeleton.Nodes[pd.Node].Parent;
                    var dir = par >= 0 ? SafeNormalize(hp - P(par), Vector3.UnitY) : Vector3.UnitY;
                    center = hp + dir * (0.06f * height);
                }
                axisY = SafeNormalize(Row(nodeW, 1), Vector3.UnitY);
                axisX = Row(nodeW, 0);
                shape = PhysicsService.ShapeSphere;
            }
            else
            {
                Vector3 a = nodeW.Translation;
                Vector3 b = pd.End >= 0 ? P(pd.End) : a + SafeNormalize(Row(nodeW, 1), Vector3.UnitY) * (r * 3f);
                if (pd.Extend > 0f) b += (b - a) * pd.Extend;
                float len = (b - a).Length();
                if (len < 1e-4f) { b = a + SafeNormalize(Row(nodeW, 1), Vector3.UnitY) * (r * 2f); len = (b - a).Length(); }
                center = (a + b) * 0.5f;
                axisY = (b - a) / len;
                axisX = Row(nodeW, 0);
                half = Math.Max(0.01f, len * 0.5f - r);
                shape = PhysicsService.ShapeCapsule;
            }

            var rot = Basis(axisY, axisX);
            var bodyPose = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(center);
            if (!Matrix4x4.Invert(bodyPose, out var invBody)) return false;
            part.BodyToNode = nodeW * invBody;
            part.PrevPos = part.LastPos = center;
            part.PrevRot = part.LastRot = rot;

            float mass = Math.Max(0.2f, s.Mass * pd.MassShare);
            _n3[0] = r; _n3[1] = half; _n3[2] = 0f;
            Fill(_p3, center); Fill(_q4, rot);
            try
            {
                part.Body = VortexAPI.PhysicsCreateBody(entityId, shape, _n3, _p3, _q4, PhysicsService.MotionDynamic, mass,
                    s.Friction, 0f, 0.05f, s.Damping, 0, PhysicsService.LayerDebris, 0u, 1f);
            }
            catch { part.Body = 0; }
            return part.Body != 0;
        }

        private static bool CreateJoint(Instance inst, Part part, Matrix4x4[] worlds, Matrix4x4 mesh, Vector3 forward, RagdollComponent s)
        {
            var pd = part.Def;
            if (pd.Parent < 0 || pd.Joint == JointKind.None) return true;
            var parent = inst.Parts[pd.Parent];
            Vector3 P(int node) => (worlds[node] * mesh).Translation;
            var pivot = P(pd.Node);
            var parentNode = parent.Def.Node;

            // the segment this part continues from (parent joint -> this joint) and its own direction
            Vector3 dParent = SafeNormalize(pivot - P(parentNode), Vector3.UnitY);
            Vector3 dSelf;
            if (pd.Sphere) dSelf = SafeNormalize(part.LastPos - pivot, dParent);
            else if (pd.LateralA >= 0) dSelf = SafeNormalize(pivot - P(parentNode), Vector3.UnitY);
            else dSelf = SafeNormalize(pd.End >= 0 ? P(pd.End) - pivot : Vector3.Transform(Vector3.UnitY, part.LastRot), dParent);

            uint joint = 0;
            try
            {
                if (pd.Joint == JointKind.Ball)
                {
                    Fill(_p3, pivot); Fill(_a3, dSelf);
                    joint = VortexAPI.PhysicsCreateBallJoint(part.Body, parent.Body, _p3, _a3, pd.Swing, pd.TwistMin, pd.TwistMax, 1, 0f);
                }
                else
                {
                    // hinge: bend in the plane the limb is bent in now; a straight limb bends toward the body front
                    // (elbow) or back (knee). Limits are relative to the pose at creation (= angle 0).
                    Vector3 axis; float phi;
                    var bendAxis = Vector3.Cross(dParent, dSelf);
                    float bend = (float)Math.Atan2(bendAxis.Length(), Vector3.Dot(dParent, dSelf));
                    if (bend > 12f * Deg && bendAxis.LengthSquared() > 1e-8f)
                    {
                        axis = Vector3.Normalize(bendAxis);
                        phi = bend;
                    }
                    else
                    {
                        var m = RigMap.Perp(pd.FlexForward ? forward : -forward, dParent);
                        if (m.LengthSquared() < 1e-8f) m = RigMap.Perp(Vector3.UnitY, dParent);
                        axis = SafeNormalize(Vector3.Cross(dParent, SafeNormalize(m, Vector3.UnitX)), Vector3.UnitX);
                        phi = (float)Math.Atan2(Vector3.Dot(Vector3.Cross(dParent, dSelf), axis), Vector3.Dot(dParent, dSelf));
                    }
                    float phiDeg = phi / Deg;
                    float min = Clamp(pd.FlexMin - phiDeg, -180f, 0f), max = Clamp(pd.FlexMax - phiDeg, 0f, 180f);
                    var normal = SafeNormalize(RigMap.Perp(dSelf, axis), RigMap.Perp(dParent, axis));
                    Fill(_p3, pivot); Fill(_a3, axis); Fill(_n3, normal);
                    joint = VortexAPI.PhysicsCreateHinge(part.Body, parent.Body, _p3, _a3, _n3, min, max, 1, 0f, s.JointFriction, 0f);
                }
            }
            catch { joint = 0; }
            part.Joint = joint;
            return joint != 0;
        }

        /// <summary>Linear velocity of every part from the last two evaluated poses (world space).</summary>
        private static void SeedVelocities(Instance inst, Matrix4x4[] worlds, Matrix4x4 mesh, History hist)
        {
            if (hist == null || hist.PrevWorlds == null || hist.PrevWorlds.Length != worlds.Length || hist.Ticks <= hist.PrevTicks) return;
            float dt = (float)((hist.Ticks - hist.PrevTicks) / (double)Stopwatch.Frequency);
            if (dt < 0.002f || dt > 0.25f) return;
            foreach (var p in inst.Parts)
            {
                if (p.Body == 0) continue;
                var now = (worlds[p.Def.Node] * mesh).Translation;
                var before = (hist.PrevWorlds[p.Def.Node] * hist.PrevMesh).Translation;
                var v = (now - before) / dt;
                float sp = v.Length();
                if (!(sp > 0.01f)) continue;
                if (sp > 20f) v *= 20f / sp;
                Fill(_a3, v);
                try { VortexAPI.PhysicsSetLinearVelocity(p.Body, _a3); } catch { }
            }
        }

        private static void DestroyBodies(Instance inst)
        {
            if (inst?.Parts == null) return;
            bool live = PhysicsService.IsBuilt;
            for (int k = inst.Parts.Length - 1; k >= 0; k--)
            {
                var p = inst.Parts[k];
                if (p == null) continue;
                if (live && p.Joint != 0) { try { VortexAPI.PhysicsDestroyConstraint(p.Joint); } catch { } }
                if (live && p.Body != 0) { try { VortexAPI.PhysicsDestroyBody(p.Body); } catch { } }
                p.Joint = 0; p.Body = 0;
            }
        }

        // ================================================================ per frame (PhysicsService / AnimationService)

        /// <summary>PhysicsService, before the last of several steps in one frame: the pose to interpolate from.</summary>
        internal static void SnapshotPrevious()
        {
            foreach (var inst in _active.Values)
                foreach (var p in inst.Parts)
                {
                    p.PrevPos = p.LastPos; p.PrevRot = p.LastRot;
                    if (p.Body != 0 && ReadPose(p.Body, out var pos, out var rot)) { p.PrevPos = pos; p.PrevRot = rot; }
                }
        }

        /// <summary>PhysicsService, after the frame's steps: read every part's new pose.</summary>
        internal static void Readback(bool previousSnapshotted)
        {
            if (_active.Count == 0) return;
            foreach (var inst in _active.Values)
                foreach (var p in inst.Parts)
                {
                    if (!previousSnapshotted) { p.PrevPos = p.LastPos; p.PrevRot = p.LastRot; }
                    if (p.Body != 0 && ReadPose(p.Body, out var pos, out var rot)) { p.LastPos = pos; p.LastRot = rot; }
                }
            SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>The world is gone (play stopped, scene switch): forget every ragdoll — their bodies went with it.</summary>
        internal static void OnWorldCleared()
        {
            _active.Clear(); _history.Clear(); _startHandled.Clear(); _warned.Clear();
        }

        /// <summary>AnimationService, right before the skinning palette: an active ragdoll's simulated pose replaces
        /// the animated one; a character that merely CAN ragdoll records its pose (velocity at activation) and starts
        /// falling here when its component says Activate On Start.</summary>
        public static Matrix4x4[] ProcessPose(GameEntity entity, SkeletonDef skel, Matrix4x4[] animWorlds)
        {
            if (entity == null || skel == null || animWorlds == null) return animWorlds;
            if (_active.TryGetValue(entity, out var inst)) return Compose(inst, skel, animWorlds);

            var comp = entity.GetComponent<RagdollComponent>();
            if (comp == null || !comp.IsEnabled || !PhysicsService.IsBuilt) return animWorlds;
            Record(entity, animWorlds);
            if (comp.ActivateOnStart && _startHandled.Add(entity) && Activate(entity) && _active.TryGetValue(entity, out inst))
                return Compose(inst, skel, animWorlds);
            return animWorlds;
        }

        private static void Record(GameEntity entity, Matrix4x4[] worlds)
        {
            if (!_history.TryGetValue(entity, out var h)) _history[entity] = h = new History();
            var meshEntity = AnimationService.Instance.FindSkinnedMeshEntity(entity) ?? entity;
            h.PrevWorlds = h.Worlds; h.PrevMesh = h.Mesh; h.PrevTicks = h.Ticks;
            h.Worlds = worlds; h.Mesh = BoneSocketService.EntityWorld(meshEntity); h.Ticks = Stopwatch.GetTimestamp();
        }

        private static Matrix4x4[] Compose(Instance inst, SkeletonDef skel, Matrix4x4[] animWorlds)
        {
            int n = skel.Nodes.Length;
            if (inst.PartOfNode.Length != n || animWorlds.Length != n) return animWorlds;
            var mesh = BoneSocketService.EntityWorld(inst.MeshEntity);
            if (!Matrix4x4.Invert(mesh, out var inv)) return animWorlds;
            float alpha = PhysicsService.InterpolationAlpha;
            var outW = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
            {
                int k = inst.PartOfNode[i];
                if (k >= 0)
                {
                    var p = inst.Parts[k];
                    var pos = Vector3.Lerp(p.PrevPos, p.LastPos, alpha);
                    var rot = Quaternion.Slerp(p.PrevRot, p.LastRot, alpha);
                    var w = p.BodyToNode * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos) * inv;
                    outW[i] = IsFinite(w) ? w : animWorlds[i];
                }
                else
                {
                    int par = skel.Nodes[i].Parent;
                    outW[i] = par >= 0 && par < i ? inst.FrozenLocal[i] * outW[par] : animWorlds[i];
                }
            }
            if (inst.BlendTime > 0f)
            {
                float t = (float)((Stopwatch.GetTimestamp() - inst.StartTicks) / (double)Stopwatch.Frequency) / inst.BlendTime;
                if (t < 1f)
                {
                    float wgt = t * t * (3f - 2f * t);
                    for (int i = 0; i < n; i++) outW[i] = BlendMatrix(animWorlds[i], outW[i], wgt);
                }
                else StopClip(inst);
            }
            return outW;
        }

        // ================================================================ helpers

        private const float Deg = (float)(Math.PI / 180.0);

        private static bool ReadPose(uint body, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.Identity;
            try
            {
                if (VortexAPI.PhysicsGetBodyTransform(body, _p3, _q4) == 0) return false;
            }
            catch { return false; }
            pos = new Vector3(_p3[0], _p3[1], _p3[2]);
            rot = new Quaternion(_q4[0], _q4[1], _q4[2], _q4[3]);
            return !(float.IsNaN(pos.X) || float.IsNaN(rot.W));
        }

        /// <summary>Body forward (world) at activation: the bind-pose forward carried along by the pelvis' current rotation.</summary>
        private static Vector3 BodyForward(SkeletonDef skel, Matrix4x4[] worlds, Matrix4x4 mesh, int pelvis)
        {
            var si = RigMap.Info(skel);
            var frame = RigMap.ModelFrame(si);
            Vector3 fwdModel = frame.Forward;
            if (pelvis >= 0 && Matrix4x4.Invert(si.Bind[pelvis], out var invBind))
                fwdModel = Vector3.TransformNormal(frame.Forward, invBind * worlds[pelvis]);
            var f = Vector3.TransformNormal(fwdModel, mesh);
            f.Y = 0f;   // the body front, level
            return SafeNormalize(f, Vector3.UnitZ);
        }

        private static float ModelToWorldScale(Matrix4x4 mesh)
        {
            float sx = new Vector3(mesh.M11, mesh.M12, mesh.M13).Length();
            float sy = new Vector3(mesh.M21, mesh.M22, mesh.M23).Length();
            float sz = new Vector3(mesh.M31, mesh.M32, mesh.M33).Length();
            float s = (sx + sy + sz) / 3f;
            return s > 1e-8f ? s : 1f;
        }

        private static Vector3 Row(Matrix4x4 m, int row)
            => row == 0 ? new Vector3(m.M11, m.M12, m.M13) : row == 1 ? new Vector3(m.M21, m.M22, m.M23) : new Vector3(m.M31, m.M32, m.M33);

        /// <summary>Rotation whose local +Y is <paramref name="y"/> and whose local +X leans toward <paramref name="xHint"/>.</summary>
        private static Quaternion Basis(Vector3 y, Vector3 xHint)
        {
            y = SafeNormalize(y, Vector3.UnitY);
            var x = RigMap.Perp(xHint, y);
            if (x.LengthSquared() < 1e-8f) x = RigMap.Perp(Math.Abs(y.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ, y);
            x = Vector3.Normalize(x);
            var z = Vector3.Cross(x, y);
            var m = new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        }

        private static Matrix4x4 BlendMatrix(Matrix4x4 a, Matrix4x4 b, float t)
        {
            if (!Matrix4x4.Decompose(a, out var sa, out var ra, out var ta) || !Matrix4x4.Decompose(b, out var sb, out var rb, out var tb)) return t < 0.5f ? a : b;
            return Matrix4x4.CreateScale(Vector3.Lerp(sa, sb, t)) * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra, rb, t))
                 * Matrix4x4.CreateTranslation(Vector3.Lerp(ta, tb, t));
        }

        private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float l = v.Length();
            return l > 1e-6f && !float.IsNaN(l) ? v / l : fallback;
        }

        private static bool IsFinite(Matrix4x4 m)
            => !(float.IsNaN(m.M11) || float.IsNaN(m.M22) || float.IsNaN(m.M33) || float.IsNaN(m.M41) || float.IsNaN(m.M42) || float.IsNaN(m.M43)
                 || float.IsInfinity(m.M41) || float.IsInfinity(m.M42) || float.IsInfinity(m.M43));

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static void Fill(float[] dst, Vector3 v) { dst[0] = v.X; dst[1] = v.Y; dst[2] = v.Z; }
        private static void Fill(float[] dst, Quaternion q) { dst[0] = q.X; dst[1] = q.Y; dst[2] = q.Z; dst[3] = q.W; }

        private static void WarnOnce(GameEntity e, string why)
        {
            if (e == null || !_warned.Add(e)) return;
            try { ConsoleService.Instance?.LogWarning("Ragdoll on '" + (e.Name ?? "?") + "': " + why + "."); } catch { }
        }
    }
}
