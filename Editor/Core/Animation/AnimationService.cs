using System;
using System.Collections.Generic;
using System.Numerics;

namespace Editor.Core.Animation
{
    /// <summary>
    /// Central skeletal-animation evaluator ("gameplay in scripts, engine renders" — pose math lives
    /// here in managed code; the native renderer just consumes bone palettes).
    ///
    /// Responsibilities:
    ///  - clip (.vanim) + skeleton caches (path-keyed, VFS-aware like MaterialService)
    ///  - per-Animator playback state (time, speed, loop, crossfade) advanced by Step(dt) from
    ///    ScriptRuntime.Update — the one tick all three play drivers share
    ///  - palette computation: palette[b] = inverseBind[b] * boneWorld[b] (row-vector, System.Numerics)
    ///  - animation EVENTS fired into gameplay scripts (footsteps, attack hits)
    ///  - static evaluation helpers reused verbatim by the Keyframe Editor preview
    /// </summary>
    public class AnimationService
    {
        public static AnimationService Instance { get; } = new AnimationService();

        private readonly Dictionary<string, VortexAnimClip> _clips = new Dictionary<string, VortexAnimClip>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SkeletonDef> _skeletons = new Dictionary<string, SkeletonDef>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, AnimatorState> _states = new Dictionary<Guid, AnimatorState>();
        private readonly Dictionary<long, bool> _skinnedMeshCache = new Dictionary<long, bool>();

        /// <summary>Fired when playback crosses an AnimEvent marker. ScriptRuntime routes it to behaviours.</summary>
        public event Action<ECS.GameEntity, string> AnimationEvent;

        /// <summary>True while any Animator advanced a pose in the last Step — drives per-frame re-submit.</summary>
        public bool HasActiveAnimators { get; private set; }

        private class AnimatorState
        {
            public ECS.GameEntity Entity;
            public SkeletonDef Skeleton;
            public VortexAnimClip Clip;
            public int[] TrackNodes;              // clip.Tracks[i] -> skeleton node index (-1 = unresolved)
            public float Time;
            public float Speed = 1f;
            public bool Loop = true;
            public bool Playing;
            public bool StartHandled;             // PlayOnStart applied
            // Crossfade: pose snapshot of the previous clip at switch time, blended out over FadeDuration.
            public Vector3[] FadeT; public Quaternion[] FadeR; public Vector3[] FadeS;
            public float FadeDuration, FadeElapsed;
            public float[] Palette;               // current pose, flattened (boneCount * 16)
            // #113 root motion: the resolved root bone (-2 = not resolved yet for this clip / bone setting), the clip's
            // start position of that bone in MODEL space (the pose is pinned to it), this tick's model-space delta.
            public int RootNode = -2;
            public string RootResolvedFor;        // "<clip name>|<bone setting>" the RootNode was resolved for
            public int RootTrack = -1;            // clip.Tracks index of the root bone's position track (-1 = none)
            public Vector3 RootStartModel;
            public Vector3 RootDeltaModel;
            public bool RootPinned;               // ApplyRootMotion active and a root track found: pin the pose
            public Matrix4x4[] NodeWorlds;        // model-space node worlds of the SAME pose (bone sockets read these)
            public List<LayerState> Layers;       // bone-masked override layers (#173); null = single-clip fast path
            public SyncGroup Group;               // synced playback group (#174); null = independent clock
            // Runtime procedural bone control (#178): script-supplied additive LOCAL rotation deltas per node,
            // composed onto the animated pose each frame BEFORE hierarchy multiplication so a delta carries all
            // descendants (spine pitch -> chest+arms+weapon rotate as one). null/empty = no override (fast path).
            public Dictionary<int, Quaternion> BoneAdditive;
            public Dictionary<int, Quaternion> ComponentAdditive;   // editor-authored HandPose finger curls (rebuilt every sync)
            public Dictionary<int, float> BoneScale;   // runtime per-bone scale multiplier (0 = hide, e.g. FP legs/head)
            public HashSet<int> BoneHiddenSelf;        // bones collapsed WITHOUT their descendants (FP torso: spine hidden, arms kept)
            // Runtime two-bone IK (#179): resolved chains from the entity's TwoBoneIk components,
            // re-synced every Step (and on inspector edits via RefreshIk). null/empty = fast path.
            public List<IkChainRuntime> IkChains;
            // Script-driven WORLD-space IK targets per tip node (Animation.SetIkTarget): the support hand
            // reaching for a mag well, a hand on a door handle. Converted to model space at solve time
            // (after the scripts moved the rig this frame). null/empty = component targets only.
            public Dictionary<int, IkWorldTarget> IkWorldTargets;
            // Auto-grip (#179): the CAPTURED tip-relative-to-target matrix per tip node, taken from the
            // animation's natural grip on the first frame and held after. Persists across Steps (the
            // chain runtime list is rebuilt each Step); cleared by RefreshIk on config edits.
            public Dictionary<int, Matrix4x4> IkCapturedGrips;

            // ---- rig-aware procedural layers (#147): hand poses, look-at, foot IK ----
            public int StepCount;                 // > 0 once the game/preview clock stepped this animator (play)
            public int LastStepTick;              // Environment.TickCount of the last Step (clock running = stepped recently)
            public float SmoothDt;                // dt of the Step being evaluated (0 = evaluate without advancing smoothing)
            public bool SnapSmoothing;            // edit-mode preview: jump straight to the target
            public ScriptHandPose[] ScriptHands;  // Animation.SetHandPose overrides: [0] left, [1] right
            public ECS.Components.Animation.HandPose[] DefaultHands;   // rig config for script poses on hands without a component
            public ECS.Components.Animation.LookAtIk LookCfg;          // active look-at config (component or script default)
            public ECS.Components.Animation.LookAtIk DefaultLook;
            public LookAtRig LookRig;
            public LookSmoothState LookSmooth;
            public bool HasLookPoint;             // Animation.SetLookAtTarget(entity, point)
            public Vector3 LookPoint;             // world space
            public ECS.GameEntity LookEntity;     // Animation.SetLookAtTarget(entity, otherEntity)
            public float LookWeightOverride = -1f;   // Animation.SetLookAtWeight (-1 = the component's weight)
            public string LookRefName;               // cached resolution of LookAtIk.TargetEntity (name / id)
            public ECS.GameEntity LookRefEntity;
            public int LookRefTtl;
            public ECS.Components.Animation.FootIk FootCfg;
            public ECS.Components.Animation.FootIk DefaultFoot;
            public FootIkRig FootRig;
            public FootSmoothState FootSmooth;
            public float FootWeightOverride = -1f;   // Animation.SetFootIkWeight (-1 = the component's weight)
        }

        /// <summary>A script-driven finger pose for one hand (Animation.SetHandPose), blended in over <see cref="Duration"/>.</summary>
        private sealed class ScriptHandPose
        {
            public Vector3[] To = new Vector3[5];
            public Vector3[] From = new Vector3[5];
            public float ToSpread, FromSpread, ToWeight, FromWeight;
            public float T, Duration;
            public float Blend => Duration <= 0f ? 1f : Math.Min(1f, T / Duration);
            public Vector3 Curl(int f) => Vector3.Lerp(From[f], To[f], Blend);
            public float Spread => FromSpread + (ToSpread - FromSpread) * Blend;
            public float Weight => FromWeight + (ToWeight - FromWeight) * Blend;
        }

        /// <summary>Resolved hand of one HandPose: curl joints per finger with their bind-pose axes (parent frame).</summary>
        private sealed class HandPoseRig
        {
            public int Hand = -1;
            public int[][] Joints = new int[5][];
            public Vector3[][] Axis = new Vector3[5][];   // automatic curl axis per joint, in the joint's PARENT frame
            public Vector3[] SpreadAxis = new Vector3[5];
            public string[] Source = new string[5];
            public RigMap.HandFrame Frame;
            public RigReport Report = new RigReport();
        }

        /// <summary>Resolved look-at chain.</summary>
        private sealed class LookAtRig
        {
            public int Head = -1;
            public int[] Chain = new int[0];      // lowest first … head
            public float[] Share = new float[0];  // normalised share of the turn per chain bone
            public int Ref = -1;                  // torso reference (parent of the lowest turning bone)
            public Vector3 FwdHeadLocal, FwdRefLocal, UpRefLocal;
            public RigReport Report = new RigReport();
        }

        private sealed class LookSmoothState { public bool Init; public float Yaw, Pitch, W; }

        /// <summary>Resolved legs for foot IK: [0] left, [1] right.</summary>
        private sealed class FootIkRig
        {
            public int[] Foot = { -1, -1 }, Knee = { -1, -1 }, Hip = { -1, -1 };
            public int Pelvis = -1;
            public float[] AnkleHeight = new float[2];   // model units along the bind up axis
            public float[] SoleUp = new float[2];        // model-space height of the sole plane along the bind up axis
            public Vector3 UpBind = Vector3.UnitY;
            public RigReport Report = new RigReport();
        }

        private sealed class FootSmoothState { public bool Init; public float[] Offset = new float[2]; public float Pelvis; public Vector3[] Normal = { Vector3.UnitY, Vector3.UnitY }; }

        /// <summary>What a rig-aware component resolved on its skeleton — shown by the inspector cards, read by tests.</summary>
        public sealed class RigReport
        {
            /// <summary>True when the component found everything it needs to act.</summary>
            public bool Ok;
            /// <summary>Human-readable lines ("Hand: mixamorig:LeftHand", "Index: … (names)").</summary>
            public readonly List<string> Lines = new List<string>();
            /// <summary>Detection notes / problems.</summary>
            public readonly List<string> Notes = new List<string>();
            /// <summary>Resolved bones by role ("Hand", "Index", …, "Head", "Neck", "LeftFoot", …).</summary>
            public readonly Dictionary<string, string[]> Bones = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            public override string ToString()
            {
                var sb = new System.Text.StringBuilder();
                foreach (var l in Lines) sb.AppendLine(l);
                foreach (var n in Notes) sb.AppendLine("• " + n);
                return sb.ToString().TrimEnd();
            }
        }

        /// <summary>A script-supplied world-space IK target (position, optional orientation).</summary>
        private class IkWorldTarget
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public bool HasRot;
        }

        /// <summary>Resolved runtime form of one TwoBoneIk component (node indices + quaternion offset).</summary>
        private class IkChainRuntime
        {
            public int Tip, Mid, Root, Target;
            public bool HasWorldTarget;        // script target overrides the bone-relative one this Step
            public bool WorldTargetHasRot;
            public Matrix4x4 WorldTargetModel; // the script target, already in model space
            public Vector3 OffsetPos;          // target-bone-local grip position (model units)
            public Quaternion OffsetRot;       // target-bone-local grip orientation
            public float Weight;
            public float PoleAngleDeg;
            public bool ApplyTipRotation;
            public bool AutoGrip;              // capture + hold the natural grip
            public bool HasCapturedGrip;       // capture available for this Solve
            public Matrix4x4 CapturedGrip;     // tip-relative-to-target matrix (natural grip)
        }

        /// <summary>Synced playback group (#174): ONE master clock drives N members at the same
        /// normalized time (member time = norm x its own clip duration) — reload hands + weapon slide
        /// stay frame-locked through pauses, speed changes and frame drops by construction.</summary>
        private class SyncGroup
        {
            public int Id;
            public List<AnimatorState> Members = new List<AnimatorState>();
            public float Norm;                    // 0..1 master clock
            public float Speed = 1f;
            public float Duration = 1f;           // reference duration (first member's clip)
            public bool Paused;
            public bool Loop;
            public bool WrappedThisFrame;
        }

        private readonly List<SyncGroup> _groups = new List<SyncGroup>();
        private int _nextGroupId = 1;

        /// <summary>One bone-masked override layer: its own clip/time/weight, blended over the base pose
        /// in LOCAL space (per node, before hierarchy multiplication) wherever the mask includes a bone.</summary>
        private class LayerState
        {
            public int Index;                     // >= 1; higher layers composite over lower ones
            public VortexAnimClip Clip;
            public int[] TrackNodes;
            public float[] Mask;                  // per-node 0/1 from the mask spec
            public string MaskSpec;
            public float Time;
            public float Speed = 1f;
            public float Weight = 1f;
            public bool Playing;
            public bool Loop;
            public Vector3[] FadeT; public Quaternion[] FadeR; public Vector3[] FadeS;
            public float FadeDuration, FadeElapsed;
        }

        // ------------------------------------------------------------------ caches

        /// <summary>Load a clip by path (project-relative or absolute), cached. Null on failure.</summary>
        public VortexAnimClip GetClip(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full = ResolveAssetPath(path);
            if (full == null) return null;
            if (_clips.TryGetValue(full, out var cached)) return cached;
            var clip = VortexAnimClip.Load(full);
            _clips[full] = clip;   // negative results cached too (avoids re-hitting disk every frame)
            return clip;
        }

        /// <summary>Drop a clip from the cache (Keyframe Editor save / file change).</summary>
        public void InvalidateClip(string path)
        {
            string full = ResolveAssetPath(path);
            if (full != null) _clips.Remove(full);
        }

        /// <summary>Skeleton of a model (path with or without '#submeshN'), cached. Null when not skinned.</summary>
        public SkeletonDef GetSkeleton(string meshPath)
        {
            string full = ResolveModelPath(meshPath);
            if (full == null) return null;
            if (_skeletons.TryGetValue(full, out var cached)) return cached;
            var skel = SkeletonDef.Load(full);
            _skeletons[full] = skel;
            return skel;
        }

        /// <summary>Is this registered mesh skinned? (interop result cached per mesh id).</summary>
        public bool IsMeshSkinned(long meshId)
        {
            if (meshId < 0) return false;
            if (_skinnedMeshCache.TryGetValue(meshId, out bool s)) return s;
            s = DllWrapper.VortexAPI.MeshIsSkinned(meshId);
            _skinnedMeshCache[meshId] = s;
            return s;
        }

        /// <summary>Full playback/state reset (play start/stop, scene switch). Caches survive.</summary>
        public void ResetStates()
        {
            _states.Clear();
            _groups.Clear();
            HasActiveAnimators = false;
        }

        /// <summary>Scene switch: mesh ids are session-local and get re-imported — drop the skinned lookup.</summary>
        public void OnSceneSwitch()
        {
            _skinnedMeshCache.Clear();
            ResetStates();
        }

        // ------------------------------------------------------------------ per-frame tick

        /// <summary>Advance every enabled Animator in the scene. Called from ScriptRuntime.Update AFTER
        /// behaviours ran, so a same-frame Play() takes effect immediately.</summary>
        public void Step(Data.Scene scene, float dt)
        {
            HasActiveAnimators = false;
            if (scene?.Entities == null) return;

            // Advance sync-group master clocks FIRST — grouped members read Norm during their step.
            for (int i = 0; i < _groups.Count; i++)
            {
                var g = _groups[i];
                g.WrappedThisFrame = false;
                if (g.Paused || g.Members.Count == 0) continue;
                g.Norm += dt * g.Speed / Math.Max(g.Duration, 0.0001f);
                if (g.Norm >= 1f)
                {
                    if (g.Loop) { g.Norm %= 1f; g.WrappedThisFrame = true; }
                    else g.Norm = 1f;
                }
            }

            foreach (var e in scene.Entities) StepRecursive(e, dt);
        }

        private void StepRecursive(ECS.GameEntity entity, float dt)
        {
            if (entity == null || !entity.IsActive) return;
            var animator = entity.GetComponent<ECS.Components.Animation.Animator>();
            if (animator != null && animator.IsEnabled) StepEntity(entity, animator, dt);
            if (entity.Children != null)
                foreach (var c in entity.Children) StepRecursive(c, dt);
        }

        private void StepEntity(ECS.GameEntity entity, ECS.Components.Animation.Animator animator, float dt)
        {
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return;

            if (!state.StartHandled)
            {
                state.StartHandled = true;
                if (animator.PlayOnStart && !string.IsNullOrEmpty(animator.DefaultClip))
                    Play(entity, animator.DefaultClip, 0f);
            }

            bool baseActive = state.Playing && state.Clip != null;
            if (baseActive && state.Group != null)
            {
                // Grouped member: the master clock owns time — map its normalized position onto this clip.
                float prevT = state.Time;
                float durT = Math.Max(state.Clip.DurationSec, 0.0001f);
                state.Time = state.Group.Norm * durT;
                FireEvents(entity, state.Clip, prevT, state.Time, state.Group.WrappedThisFrame, state.Group.Speed >= 0f);
                if (animator.ApplyRootMotion) ExtractRootMotion(state, animator, prevT, state.Time, state.Group.WrappedThisFrame, state.Group.Speed >= 0f);
                else { state.RootPinned = false; state.RootDeltaModel = Vector3.Zero; }
                if (!state.Group.Loop && state.Group.Norm >= 1f) state.Playing = false;
                if (state.FadeDuration > 0f)
                {
                    state.FadeElapsed += dt;
                    if (state.FadeElapsed >= state.FadeDuration) { state.FadeDuration = 0f; state.FadeT = null; state.FadeR = null; state.FadeS = null; }
                }
            }
            else if (baseActive)
            {
                float prev = state.Time;
                float step = dt * state.Speed * animator.Speed;   // signed — negative when playing in reverse
                state.Time += step;

                float dur = Math.Max(state.Clip.DurationSec, 0.0001f);
                bool loop = state.Loop && state.Clip.Loop;
                bool wrapped = false;
                if (state.Time >= dur)
                {
                    if (loop) { state.Time %= dur; wrapped = true; }   // forward wrap past the end
                    else { state.Time = dur; state.Playing = false; }
                }
                else if (state.Time < 0f)
                {
                    // Reverse playback: clamp/wrap the LOWER bound too, or Time runs unbounded-negative and the event
                    // crossing test misfires every frame (fixed alongside direction-aware FireEvents below).
                    if (loop) { state.Time = ((state.Time % dur) + dur) % dur; wrapped = true; }
                    else { state.Time = 0f; state.Playing = false; }
                }

                FireEvents(entity, state.Clip, prev, state.Time, wrapped, step >= 0f);
                if (animator.ApplyRootMotion) ExtractRootMotion(state, animator, prev, state.Time, wrapped, step >= 0f);
                else { state.RootPinned = false; state.RootDeltaModel = Vector3.Zero; }

                if (state.FadeDuration > 0f)
                {
                    state.FadeElapsed += dt;
                    if (state.FadeElapsed >= state.FadeDuration) { state.FadeDuration = 0f; state.FadeT = null; state.FadeR = null; state.FadeS = null; }
                }
            }

            // Override layers advance independently of the base clip (aim while standing still).
            // Events fire from EVERY layer — a "fire" marker on the aim layer works while walking.
            bool layersActive = false;
            if (state.Layers != null)
            {
                for (int i = 0; i < state.Layers.Count; i++)
                {
                    var layer = state.Layers[i];
                    if (!layer.Playing || layer.Clip == null) continue;
                    layersActive = true;

                    float prev = layer.Time;
                    float step = dt * layer.Speed * animator.Speed;
                    layer.Time += step;
                    float dur = Math.Max(layer.Clip.DurationSec, 0.0001f);
                    bool wrapped = false;
                    if (layer.Time >= dur)
                    {
                        if (layer.Loop) { layer.Time %= dur; wrapped = true; }
                        else { layer.Time = dur; layer.Playing = false; }
                    }
                    else if (layer.Time < 0f)
                    {
                        if (layer.Loop) { layer.Time = ((layer.Time % dur) + dur) % dur; wrapped = true; }
                        else { layer.Time = 0f; layer.Playing = false; }
                    }
                    FireEvents(entity, layer.Clip, prev, layer.Time, wrapped, step >= 0f);

                    if (layer.FadeDuration > 0f)
                    {
                        layer.FadeElapsed += dt;
                        if (layer.FadeElapsed >= layer.FadeDuration) { layer.FadeDuration = 0f; layer.FadeT = null; layer.FadeR = null; layer.FadeS = null; }
                    }
                }
            }

            // Script hand poses blend in over their own duration (Animation.SetHandPose).
            if (state.ScriptHands != null)
                for (int h = 0; h < 2; h++)
                    if (state.ScriptHands[h] != null) state.ScriptHands[h].T += dt;
            state.StepCount++;
            state.LastStepTick = Environment.TickCount;

            // #179: refresh the runtime IK chains from the entity's TwoBoneIk components each Step so
            // inspector edits and script SetIkWeight take effect immediately.
            SyncIkChains(entity, state);
            SyncHandPoses(entity, state);
            SyncLookAtAndFootIk(entity, state);

            // #178: a runtime bone override must re-pose every frame even on a static/held clip (look up/down
            // while standing still), so it counts as "active" for the re-evaluation gate. Same for IK (#179),
            // look-at (the target moves) and foot IK (the ground under the character changes).
            bool overridesActive = (state.BoneAdditive != null && state.BoneAdditive.Count > 0)
                                || (state.ComponentAdditive != null && state.ComponentAdditive.Count > 0)
                                || (state.BoneScale != null && state.BoneScale.Count > 0)
                                || (state.IkChains != null && state.IkChains.Count > 0)
                                || (state.LookCfg != null && state.LookRig != null)
                                || (state.FootCfg != null && state.FootRig != null)
                                || Services.Physics.RagdollService.NeedsPose(entity);   // #104: a ragdoll poses every frame
            if (!baseActive && !layersActive && !overridesActive) return;
            state.SmoothDt = dt;
            try { state.Palette = EvaluateStatePalette(state); }
            finally { state.SmoothDt = 0f; }
            HasActiveAnimators = true;
            if (animator.ApplyRootMotion && baseActive) ApplyRootMotion(entity, state);
        }

        // ------------------------------------------------------------------ root motion (#113)

        /// <summary>The root-motion bone for this state's clip: the Animator's named bone, else hips / pelvis by name,
        /// else the first node under a model root that has skinned descendants. Cached per clip + setting.</summary>
        private static void ResolveRootNode(AnimatorState state, ECS.Components.Animation.Animator animator)
        {
            string key = (state.Clip != null ? state.Clip.Name : "") + "|" + (animator.RootMotionBone ?? "");
            if (state.RootNode != -2 && state.RootResolvedFor == key) return;
            state.RootResolvedFor = key;
            state.RootNode = FindRootMotionNode(state.Skeleton, animator.RootMotionBone);
            state.RootTrack = -1;
            if (state.RootNode >= 0 && state.Clip != null && state.TrackNodes != null)
                for (int i = 0; i < state.TrackNodes.Length; i++)
                    if (state.TrackNodes[i] == state.RootNode && state.Clip.Tracks[i].Pos != null && state.Clip.Tracks[i].Pos.Count > 0) { state.RootTrack = i; break; }
            if (state.RootTrack >= 0)
            {
                var first = state.Clip.Tracks[state.RootTrack].Pos[0];
                state.RootStartModel = RootToModel(state.Skeleton, state.RootNode, new Vector3(first.X, first.Y, first.Z));
            }
        }

        /// <summary>The bone whose translation carries a clip's travel: <paramref name="preferred"/> when set and found,
        /// else a node named hips / pelvis, else the first child of a model root that skins vertices.</summary>
        public static int FindRootMotionNode(SkeletonDef skel, string preferred)
        {
            if (skel == null || skel.Nodes == null || skel.Nodes.Length == 0) return -1;
            if (!string.IsNullOrWhiteSpace(preferred)) { int p = RigMap.Find(skel, preferred); if (p >= 0) return p; }
            for (int i = 0; i < skel.Nodes.Length; i++)
            {
                string n = (skel.Nodes[i].Name ?? "").ToLowerInvariant();
                if (n.EndsWith("hips") || n.EndsWith("hip") || n.EndsWith("pelvis") || n.Contains(":hips") || n.Contains("_hips")) return i;
            }
            var si = RigMap.Info(skel);
            for (int i = 0; i < skel.Nodes.Length; i++)
            {
                int parent = skel.Nodes[i].Parent;
                if (parent < 0) continue;
                if (skel.Nodes[parent].Parent < 0 && si != null && si.Effective != null && i < si.Effective.Length && si.Effective[i]) return i;
            }
            return -1;
        }

        /// <summary>A position in the root bone's LOCAL space (its track values) → model space, through the parent's bind pose.</summary>
        private static Vector3 RootToModel(SkeletonDef skel, int root, Vector3 local)
        {
            int parent = skel.Nodes[root].Parent;
            if (parent < 0) return local;
            var si = RigMap.Info(skel);
            return si != null && si.Bind != null && parent < si.Bind.Length ? Vector3.Transform(local, si.Bind[parent]) : local;
        }

        private static Vector3 ModelToRoot(SkeletonDef skel, int root, Vector3 model)
        {
            int parent = skel.Nodes[root].Parent;
            if (parent < 0) return model;
            var si = RigMap.Info(skel);
            Matrix4x4 inv;
            if (si != null && si.Bind != null && parent < si.Bind.Length && Matrix4x4.Invert(si.Bind[parent], out inv)) return Vector3.Transform(model, inv);
            return model;
        }

        /// <summary>The root bone's model-space travel between two clip times — across a loop wrap the end-to-start jump
        /// is skipped (the travel is end − prev plus now − start). Pure: used by the tests.</summary>
        public static Vector3 RootDelta(SkeletonDef skel, VortexAnimClip clip, int rootNode, int rootTrack, float prev, float now, bool wrapped, bool forward)
        {
            if (clip == null || rootTrack < 0 || rootTrack >= clip.Tracks.Count || rootNode < 0) return Vector3.Zero;
            var keys = clip.Tracks[rootTrack].Pos;
            if (keys == null || keys.Count == 0) return Vector3.Zero;
            float dur = Math.Max(clip.DurationSec, 0.0001f);
            Vector3 p0 = SampleVec3(keys, prev), p1 = SampleVec3(keys, now);
            Vector3 local;
            if (!wrapped) local = p1 - p0;
            else if (forward) local = (SampleVec3(keys, dur) - p0) + (p1 - SampleVec3(keys, 0f));
            else local = (SampleVec3(keys, 0f) - p0) + (p1 - SampleVec3(keys, dur));
            // a delta is a direction: through the parent's bind rotation / scale, no translation
            int parent = skel.Nodes[rootNode].Parent;
            if (parent < 0) return local;
            var si = RigMap.Info(skel);
            return si != null && si.Bind != null && parent < si.Bind.Length ? Vector3.TransformNormal(local, si.Bind[parent]) : local;
        }

        private void ExtractRootMotion(AnimatorState state, ECS.Components.Animation.Animator animator, float prev, float now, bool wrapped, bool forward)
        {
            ResolveRootNode(state, animator);
            if (state.RootNode < 0 || state.RootTrack < 0 || state.Clip == null) { state.RootPinned = false; state.RootDeltaModel = Vector3.Zero; return; }
            state.RootPinned = true;
            state.RootDeltaModel = RootDelta(state.Skeleton, state.Clip, state.RootNode, state.RootTrack, prev, now, wrapped, forward);
        }

        /// <summary>Pin the root bone's horizontal model-space position to the clip's start (the entity carries the travel).</summary>
        private static void PinRoot(AnimatorState state, SkeletonDef skel, Vector3[] t)
        {
            if (!state.RootPinned || state.RootNode < 0 || state.RootNode >= t.Length) return;
            var model = RootToModel(skel, state.RootNode, t[state.RootNode]);
            model = new Vector3(state.RootStartModel.X, model.Y, state.RootStartModel.Z);
            t[state.RootNode] = ModelToRoot(skel, state.RootNode, model);
        }

        /// <summary>Move the entity by this tick's root travel (world space, horizontal) — through the character
        /// collision world when it is built (walls stop the character), else directly.</summary>
        private void ApplyRootMotion(ECS.GameEntity entity, AnimatorState state)
        {
            if (!state.RootPinned || entity?.Transform == null) return;
            var dm = state.RootDeltaModel;
            state.RootDeltaModel = Vector3.Zero;
            if (dm.LengthSquared() < 1e-12f) return;
            var world = BoneSocketService.EntityWorld(entity);
            var dw = Vector3.TransformNormal(dm, world);
            dw.Y = 0f;
            if (dw.LengthSquared() < 1e-12f || float.IsNaN(dw.X) || float.IsNaN(dw.Z)) return;
            var feet = world.Translation;
            Vector3 moved;
            if (Services.Physics.CollisionService.IsBuilt)
            {
                float radius = 0.35f, height = 1.8f;
                var cap = entity.GetComponent<ECS.Components.Physics.CapsuleCollider>();
                if (cap != null) { radius = Math.Max(0.05f, cap.Radius); height = Math.Max(2f * radius, cap.Height); }
                var res = Services.Physics.CollisionService.MoveCharacter(new ECS.Vector3(feet.X, feet.Y, feet.Z), radius, height, new ECS.Vector3(dw.X, 0f, dw.Z), out _, 0);
                moved = new Vector3(res.X, feet.Y, res.Z);   // horizontal only: gravity / ground stay the script's or the agent's
            }
            else moved = feet + dw;
            Services.AI.NavigationService.WritePose(entity, moved, null);
            Services.SceneRenderService.RuntimeDirty = true;
            LastRootMotionTick = Environment.TickCount;
        }

        /// <summary>TickCount of the last entity moved by root motion (the Nav Agent sync and tests read it).</summary>
        public int LastRootMotionTick { get; private set; }

        /// <summary>#113: does root motion currently drive this entity (an enabled Animator with ApplyRootMotion on the
        /// entity or a direct child, playing a clip whose root bone travels)? The Nav Agent then follows the entity
        /// instead of moving it.</summary>
        public bool RootMotionDrives(ECS.GameEntity e)
        {
            if (e == null) return false;
            if (Drives(e)) return true;
            if (e.Children != null) foreach (var c in e.Children) if (Drives(c)) return true;
            return false;
        }

        private bool Drives(ECS.GameEntity e)
        {
            var an = e.GetComponent<ECS.Components.Animation.Animator>();
            if (an == null || !an.IsEnabled || !an.ApplyRootMotion) return false;
            AnimatorState st;
            return _states.TryGetValue(e.Id, out st) && st.Playing && st.RootPinned;
        }

        private void FireEvents(ECS.GameEntity entity, VortexAnimClip clip, float from, float to, bool wrapped, bool forward)
        {
            // NOTE: no early-out on AnimationEvent == null anymore — a SOUND event must fire even when no script is
            // subscribed (the editor-authored SFX case). We still only touch scripts when there is a subscriber.
            if (clip.Events == null || clip.Events.Count == 0) return;
            foreach (var ev in clip.Events)
            {
                // Direction-aware crossing: forward fires (from, to]; reverse fires [to, from). `wrapped` means the
                // playhead looped, so the crossed interval is the two open ends of the clip instead of a middle span.
                bool hit = forward
                    ? (wrapped ? (ev.T > from || ev.T <= to) : (ev.T > from && ev.T <= to))
                    : (wrapped ? (ev.T < from || ev.T >= to) : (ev.T < from && ev.T >= to));
                if (!hit) continue;

                // Sound events play automatically, no gameplay code required.
                if (!string.IsNullOrEmpty(ev.Sound) || !string.IsNullOrEmpty(ev.AudioSource))
                {
                    try { PlayEventSound(entity, ev); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[AnimationService] event sound failed: " + ex.Message); }
                }

                // Named events still dispatch into gameplay scripts (unchanged behaviour).
                if (!string.IsNullOrEmpty(ev.Name) && AnimationEvent != null)
                {
                    try { AnimationEvent(entity, ev.Name); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[AnimationService] event handler failed: " + ex.Message); }
                }
            }
        }

        /// <summary>Play a sound-event's clip through an AudioSource on the animated entity (or a named child) so its
        /// Volume / Pitch / 3D settings shape the sound; falls back to a plain 2D one-shot when the entity has no
        /// AudioSource. The clip is the event's own <see cref="AnimEvent.Sound"/>, or — if that is empty but an
        /// AudioSource is referenced — that source's configured clip (so an event can just "trigger the source").</summary>
        private void PlayEventSound(ECS.GameEntity entity, AnimEvent ev)
        {
            var svc = Editor.Core.Services.AudioPlaybackService.Instance;
            if (svc == null || entity == null) return;

            // Resolve the routing AudioSource AND the entity that owns it (so a 3D sound plays from the RIGHT place).
            ECS.GameEntity srcEntity;
            ECS.Components.Audio.AudioSource src;
            if (!string.IsNullOrEmpty(ev.AudioSource))
            {
                // A source was named explicitly: the entity itself (by name) or a descendant child. If it isn't found
                // we do NOT silently reroute through the parent's source — fall through to a plain one-shot instead.
                srcEntity = string.Equals(entity.Name, ev.AudioSource, StringComparison.Ordinal) ? entity : entity.Find(ev.AudioSource);
                src = srcEntity?.GetComponent<ECS.Components.Audio.AudioSource>();
            }
            else
            {
                srcEntity = entity;
                src = entity.GetComponent<ECS.Components.Audio.AudioSource>();
            }

            if (src != null && src.Mute) return;   // a muted source silences its animation sounds too

            string clip = !string.IsNullOrEmpty(ev.Sound) ? ev.Sound : src?.AudioClipPath;
            if (string.IsNullOrEmpty(clip)) return;

            float mul = ev.Volume <= 0f ? 1f : ev.Volume;
            float vol = (src != null ? src.Volume : 1f) * mul;
            float pitch = src != null ? src.Pitch : 1f;
            bool spatial = src != null && src.SpatialBlend > 0.01f;

            var posEntity = srcEntity ?? entity;   // play from the source's own transform, not always the parent's
            if (spatial && posEntity.Transform != null)
            {
                // the WORLD position (#320): an event on a child — the weapon in the hand, feet under a rig — sounds where
                // the child is, not at its parent's origin (same source of truth as AudioPlaybackService.ReadWorldPosition)
                var p = ECS.TransformMath.WorldPosition(posEntity);
                svc.PlayOneShot(clip, p.X, p.Y, p.Z, vol, pitch);
            }
            else svc.PlayOneShot2D(clip, vol, pitch);
        }

        private AnimatorState GetOrCreateState(ECS.GameEntity entity)
        {
            if (_states.TryGetValue(entity.Id, out var state)) return state;

            state = new AnimatorState { Entity = entity, Skeleton = ResolveSkeletonFor(entity) };
            _states[entity.Id] = state;
            return state;
        }

        /// <summary>
        /// Skeleton for an Animator's entity — its own MeshRenderer, or (multi-submesh models import as a
        /// parent container with '#submeshN' children) the first DESCENDANT that resolves to a skinned model.
        /// </summary>
        private SkeletonDef ResolveSkeletonFor(ECS.GameEntity entity)
        {
            if (entity == null) return null;
            var mr = entity.GetComponent<ECS.Components.Rendering.MeshRenderer>();
            var skeleton = mr != null ? GetSkeleton(mr.MeshPath) : null;
            if (skeleton != null && skeleton.IsValid) return skeleton;
            if (entity.Children != null)
            {
                foreach (var c in entity.Children)
                {
                    var cs = ResolveSkeletonFor(c);
                    if (cs != null && cs.IsValid) return cs;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ playback control (script API)

        /// <summary>Start a clip. nameOrPath resolves against the Animator's clip table first, then as a
        /// .vanim path. fade &gt; 0 crossfades from the current pose. False when the clip can't resolve.</summary>
        public bool Play(ECS.GameEntity entity, string nameOrPath, float fade = 0f)
        {
            if (entity == null || string.IsNullOrEmpty(nameOrPath)) return false;

            // Without an enabled Animator, Step() never advances the state — playing would freeze the
            // character on frame 0 while IsAnimationPlaying reports true. Refuse instead.
            var animator = entity.GetComponent<ECS.Components.Animation.Animator>();
            if (animator == null || !animator.IsEnabled) return false;

            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return false;

            string path = animator.ResolveClipPath(nameOrPath) ?? nameOrPath;
            var clip = GetClip(path);
            if (clip == null) return false;

            // Snapshot the CURRENT pose for the crossfade (static from-pose -> new clip over `fade` sec).
            if (fade > 0f && state.Clip != null && state.Playing)
            {
                int n = state.Skeleton.Nodes.Length;
                state.FadeT = new Vector3[n]; state.FadeR = new Quaternion[n]; state.FadeS = new Vector3[n];
                EvaluateLocals(state.Skeleton, state.Clip, state.TrackNodes, state.Time, state.FadeT, state.FadeR, state.FadeS);
                PinRoot(state, state.Skeleton, state.FadeT);
                state.FadeDuration = fade;
                state.FadeElapsed = 0f;
            }
            else
            {
                state.FadeDuration = 0f; state.FadeT = null; state.FadeR = null; state.FadeS = null;
            }

            // A direct Play() takes the entity back onto its own clock (leaves any sync group).
            if (state.Group != null) { state.Group.Members.Remove(state); state.Group = null; }

            state.Clip = clip;
            state.TrackNodes = ResolveTrackNodes(state.Skeleton, clip);
            state.RootNode = -2; state.RootTrack = -1; state.RootDeltaModel = Vector3.Zero;   // #113: resolve the root for this clip on the next step
            state.Time = 0f;
            state.Loop = clip.Loop;
            state.Playing = true;
            state.StartHandled = true;
            state.Palette = EvaluateStatePalette(state);
            return true;
        }

        public void Stop(ECS.GameEntity entity)
        {
            if (entity != null && _states.TryGetValue(entity.Id, out var s)) { s.Playing = false; s.StartHandled = true; }
        }

        // ------------------------------------------------------------------ synced groups (#174)

        /// <summary>
        /// Start clips on N entities frame-locked to ONE master clock (character reload + weapon reload
        /// as one). Members share normalized time; pause/speed/stop apply to the whole group atomically.
        /// Returns the group id (0 = nothing started). Entities already in a group leave it first.
        /// </summary>
        public int PlaySynced(ECS.GameEntity[] entities, string[] clips, float speed, float fade)
        {
            if (entities == null || clips == null || entities.Length == 0 || entities.Length != clips.Length) return 0;

            var group = new SyncGroup { Id = _nextGroupId++, Speed = speed <= 0f ? 1f : speed };
            for (int i = 0; i < entities.Length; i++)
            {
                if (entities[i] == null || !Play(entities[i], clips[i], fade)) continue;
                var state = _states[entities[i].Id];
                if (state.Group != null) state.Group.Members.Remove(state);
                state.Group = group;
                group.Members.Add(state);
                if (group.Members.Count == 1)
                {
                    group.Duration = Math.Max(state.Clip.DurationSec, 0.0001f);   // first member = reference clock
                    group.Loop = state.Clip.Loop;
                }
            }
            if (group.Members.Count == 0) return 0;
            _groups.Add(group);
            return group.Id;
        }

        /// <summary>Pause/resume the whole group atomically.</summary>
        public void PauseSynced(int groupId, bool paused)
        {
            foreach (var g in _groups) if (g.Id == groupId) { g.Paused = paused; return; }
        }

        /// <summary>Playback speed of the whole group (1 = authored speed of the reference clip).</summary>
        public void SetSyncedSpeed(int groupId, float speed)
        {
            foreach (var g in _groups) if (g.Id == groupId) { g.Speed = speed; return; }
        }

        /// <summary>Dissolve the group; members freeze on their current pose.</summary>
        public void StopSynced(int groupId)
        {
            for (int i = 0; i < _groups.Count; i++)
            {
                if (_groups[i].Id != groupId) continue;
                foreach (var m in _groups[i].Members) { m.Group = null; m.Playing = false; }
                _groups.RemoveAt(i);
                return;
            }
        }

        // ------------------------------------------------------------------ bone-masked layers (#173)

        /// <summary>
        /// Play a clip on an override LAYER restricted to a bone mask — walk (base) + aim (upper body).
        /// layer >= 1; mask = comma-separated bone names, '+' suffix includes all descendants
        /// (e.g. "Spine1+" or "Head,Neck+"). weight blends the layer in (0..1), fade crossfades from the
        /// layer's previous clip. Re-playing on the same layer swaps its clip; masks resolve per skeleton.
        /// </summary>
        public bool PlayLayered(ECS.GameEntity entity, string nameOrPath, int layer, string mask, float weight, float fade)
        {
            if (entity == null || string.IsNullOrEmpty(nameOrPath) || layer < 1) return false;

            var animator = entity.GetComponent<ECS.Components.Animation.Animator>();
            if (animator == null || !animator.IsEnabled) return false;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return false;

            string path = animator.ResolveClipPath(nameOrPath) ?? nameOrPath;
            var clip = GetClip(path);
            if (clip == null) return false;

            if (state.Layers == null) state.Layers = new List<LayerState>();
            LayerState L = null;
            foreach (var existing in state.Layers)
                if (existing.Index == layer) { L = existing; break; }
            if (L == null)
            {
                L = new LayerState { Index = layer };
                state.Layers.Add(L);
                state.Layers.Sort((a, b) => a.Index.CompareTo(b.Index));
            }

            // Crossfade from the layer's CURRENT clip pose (static snapshot, like the base layer).
            if (fade > 0f && L.Clip != null && L.Playing)
            {
                int n = state.Skeleton.Nodes.Length;
                L.FadeT = new Vector3[n]; L.FadeR = new Quaternion[n]; L.FadeS = new Vector3[n];
                EvaluateLocals(state.Skeleton, L.Clip, L.TrackNodes, L.Time, L.FadeT, L.FadeR, L.FadeS);
                L.FadeDuration = fade;
                L.FadeElapsed = 0f;
            }
            else { L.FadeDuration = 0f; L.FadeT = null; L.FadeR = null; L.FadeS = null; }

            L.Clip = clip;
            L.TrackNodes = ResolveTrackNodes(state.Skeleton, clip);
            if (!string.Equals(L.MaskSpec, mask, StringComparison.Ordinal) || L.Mask == null)
            {
                L.Mask = BuildMask(state.Skeleton, mask);
                L.MaskSpec = mask;
            }
            L.Time = 0f;
            L.Loop = clip.Loop;
            L.Weight = weight < 0f ? 0f : (weight > 1f ? 1f : weight);
            L.Playing = true;
            state.StartHandled = true;
            state.Palette = EvaluateStatePalette(state);
            return true;
        }

        /// <summary>Blend a layer in/out at runtime (raise/lower the weapon smoothly).</summary>
        public void SetLayerWeight(ECS.GameEntity entity, int layer, float weight)
        {
            if (entity == null || !_states.TryGetValue(entity.Id, out var s) || s.Layers == null) return;
            foreach (var L in s.Layers)
                if (L.Index == layer) { L.Weight = weight < 0f ? 0f : (weight > 1f ? 1f : weight); return; }
        }

        /// <summary>Stop an override layer (the base pose takes back its bones next frame).</summary>
        public void StopLayer(ECS.GameEntity entity, int layer)
        {
            if (entity == null || !_states.TryGetValue(entity.Id, out var s) || s.Layers == null) return;
            foreach (var L in s.Layers)
                if (L.Index == layer) { L.Playing = false; return; }
        }

        // ------------------------------------------------------------------ runtime bone control (#178)

        /// <summary>
        /// Set a persistent runtime ADDITIVE local-rotation delta (Euler degrees) on one bone. Composed onto the
        /// animated pose every frame in the bone's local frame, before hierarchy multiplication, so it carries all
        /// descendants — this is the aim-offset / procedural-lean / recoil primitive. Drives continuous up/down aim
        /// by pitching the spine so chest+arms+weapon move as one and the gun stays locked in the hands. Passing
        /// (0,0,0) clears the bone. No native change: the skinning path consumes whatever palette this produces.
        /// </summary>
        public void SetBoneAdditiveRotation(ECS.GameEntity entity, string bone, Vector3 eulerDeg)
        {
            if (entity == null || string.IsNullOrEmpty(bone)) return;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return;
            int node = state.Skeleton.FindNode(bone);
            if (node < 0) return;

            bool identity = eulerDeg.X == 0f && eulerDeg.Y == 0f && eulerDeg.Z == 0f;
            if (identity)
            {
                state.BoneAdditive?.Remove(node);
            }
            else
            {
                if (state.BoneAdditive == null) state.BoneAdditive = new Dictionary<int, Quaternion>();
                state.BoneAdditive[node] = EulerToQuat(eulerDeg);
            }
            // Re-pose immediately so a set during a behaviour's Update is reflected the SAME frame (Step runs after
            // behaviours; this also covers the case where the base clip isn't advancing).
            HasActiveAnimators = true;
            if (state.Palette != null) state.Palette = EvaluateStatePalette(state);
        }

        /// <summary>Set a persistent runtime SCALE multiplier on one bone (1 = normal, 0 = hide the bone + its
        /// descendants). Used to strip the FP viewmodel down to arms+gun — hide the legs and head so looking down
        /// or up never reveals the player's own body (the CoD viewmodel look). Applied to the local scale before
        /// hierarchy multiply, so it carries the whole limb.</summary>
        public void SetBoneScaleOverride(ECS.GameEntity entity, string bone, float scale)
        {
            if (entity == null || string.IsNullOrEmpty(bone)) return;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return;
            int node = state.Skeleton.FindNode(bone);
            if (node < 0) return;
            if (scale == 1f) { state.BoneScale?.Remove(node); }
            else
            {
                if (state.BoneScale == null) state.BoneScale = new Dictionary<int, float>();
                state.BoneScale[node] = scale;
            }
            HasActiveAnimators = true;
            if (state.Palette != null) state.Palette = EvaluateStatePalette(state);
        }

        /// <summary>Clear every runtime bone-rotation override on an entity's animator (back to the pure clip pose).</summary>
        /// <summary>Hide/show one bone's vertices. includeDescendants = the whole limb below it (same as a 0 scale
        /// override); false = ONLY this bone — the first-person torso trick: collapse Hips/Spine/Shoulders while the
        /// arm bones hanging off them keep rendering.</summary>
        public void SetBoneHidden(ECS.GameEntity entity, string bone, bool hidden, bool includeDescendants)
        {
            if (includeDescendants) { SetBoneScaleOverride(entity, bone, hidden ? 0f : 1f); return; }
            if (entity == null || string.IsNullOrEmpty(bone)) return;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return;
            int node = state.Skeleton.FindNode(bone);
            if (node < 0) return;
            if (hidden) (state.BoneHiddenSelf = state.BoneHiddenSelf ?? new HashSet<int>()).Add(node);
            else state.BoneHiddenSelf?.Remove(node);
            HasActiveAnimators = true;
            if (state.Palette != null) state.Palette = EvaluateStatePalette(state);
        }

        public void ClearBoneOverrides(ECS.GameEntity entity)
        {
            if (entity != null && _states.TryGetValue(entity.Id, out var s) && s.BoneAdditive != null && s.BoneAdditive.Count > 0)
            {
                s.BoneAdditive.Clear();
                if (s.Palette != null) s.Palette = EvaluateStatePalette(s);
            }
        }

        /// <summary>Euler degrees (X=pitch, Y=yaw, Z=roll) -> quaternion, matching the engine's TRS convention.</summary>
        private static Quaternion EulerToQuat(Vector3 deg)
        {
            const float D2R = (float)(Math.PI / 180.0);
            return Quaternion.CreateFromYawPitchRoll(deg.Y * D2R, deg.X * D2R, deg.Z * D2R);
        }

        // ------------------------------------------------------------------ runtime two-bone IK (#179)

        // ------------------------------------------------------------------ hand poses (#147, rig-generic)

        /// <summary>Rebuild the finger-curl additives from the entity's HandPose components (editor-authored grips) and
        /// the script overrides (Animation.SetHandPose): per finger joint an additive rotation composed onto the animated
        /// local rotation. Runs every Step (and on RefreshIk for the edit-mode preview) so inspector edits show
        /// immediately. The bone/axis analysis is cached per component + configuration.</summary>
        private void SyncHandPoses(ECS.GameEntity entity, AnimatorState state)
        {
            Dictionary<int, Quaternion> add = null;
            var skel = state.Skeleton;
            var comps = entity?.Components;
            if (skel != null && comps != null)
            {
                bool leftDone = false, rightDone = false;
                for (int i = 0; i < comps.Count; i++)
                {
                    var hp = comps[i] as ECS.Components.Animation.HandPose;
                    if (hp == null || !hp.IsEnabled) continue;
                    int sideIdx = hp.Side == ECS.Components.Animation.HandSide.Left ? 0 : 1;
                    if (sideIdx == 0) leftDone = true; else rightDone = true;
                    var script = state.ScriptHands != null ? state.ScriptHands[sideIdx] : null;
                    ApplyHandPose(skel, hp, script, ref add);
                }
                // Script poses on a hand WITHOUT a HandPose component: auto-detected rig, default settings.
                if (state.ScriptHands != null)
                {
                    for (int s = 0; s < 2; s++)
                    {
                        if (state.ScriptHands[s] == null || (s == 0 ? leftDone : rightDone)) continue;
                        ApplyHandPose(skel, DefaultHandConfig(state, s), state.ScriptHands[s], ref add);
                    }
                }
            }
            state.ComponentAdditive = add;
        }

        private static ECS.Components.Animation.HandPose DefaultHandConfig(AnimatorState state, int sideIdx)
        {
            if (state.DefaultHands == null) state.DefaultHands = new ECS.Components.Animation.HandPose[2];
            if (state.DefaultHands[sideIdx] == null)
                state.DefaultHands[sideIdx] = new ECS.Components.Animation.HandPose
                {
                    Side = sideIdx == 0 ? ECS.Components.Animation.HandSide.Left : ECS.Components.Animation.HandSide.Right
                };
            return state.DefaultHands[sideIdx];
        }

        private static readonly float[] SpreadShare = { 1f, 1f / 3f, -1f / 3f, -1f };   // index … pinky, + = toward the thumb

        private void ApplyHandPose(SkeletonDef skel, ECS.Components.Animation.HandPose hp, ScriptHandPose script,
            ref Dictionary<int, Quaternion> add)
        {
            float weight = script != null ? script.Weight : hp.Weight;
            if (weight <= 0.0005f) return;
            float spread = script != null ? script.Spread : hp.Spread;

            if (hp.IsLegacyConfiguration && string.IsNullOrWhiteSpace(hp.HandBone) && !HasExplicitChains(hp))
            {
                ApplyHandPoseLegacy(skel, hp, script, weight, spread, ref add);
                return;
            }

            var rig = GetHandRig(skel, hp);
            if (rig == null) return;
            bool auto = hp.CurlAxis < 0;
            const float D2R = (float)(Math.PI / 180.0);
            for (int f = 0; f < 5; f++)
            {
                var joints = rig.Joints[f];
                if (joints == null || joints.Length == 0) continue;
                var c = script != null ? script.Curl(f) : ToNum(hp.CurlOf(f));
                int n = joints.Length;
                for (int j = 0; j < n; j++)
                {
                    int node = joints[j];
                    float deg = MapCurl(c, n, j) * weight;
                    Quaternion q;
                    if (auto)
                    {
                        float sp = (j == 0 && f < 4 && spread != 0f) ? spread * SpreadShare[f] * weight : 0f;
                        if (deg == 0f && sp == 0f) continue;
                        q = Quaternion.CreateFromAxisAngle(rig.Axis[f][j], deg * D2R);
                        if (sp != 0f && rig.SpreadAxis[f].LengthSquared() > 0.5f)
                            q = Quaternion.CreateFromAxisAngle(rig.SpreadAxis[f], sp * D2R) * q;   // curl, then splay
                    }
                    else
                    {
                        // Manual axis override: the legacy Euler construction around the joint's local axis.
                        float d = deg * hp.CurlSign;
                        float sp = (j == 0 && f < 4 && spread != 0f) ? spread * ((f - 1.5f) / 1.5f) * weight : 0f;
                        if (d == 0f && sp == 0f) continue;
                        Vector3 e;
                        if (hp.CurlAxis == 0) e = new Vector3(d, 0f, sp);
                        else if (hp.CurlAxis == 1) e = new Vector3(sp, d, 0f);
                        else e = new Vector3(0f, sp, d);
                        q = EulerToQuat(e);
                    }
                    if (add == null) add = new Dictionary<int, Quaternion>();
                    Quaternion prev;
                    if (add.TryGetValue(node, out prev)) q = Quaternion.Normalize(q * prev);
                    add[node] = q;
                }
            }
        }

        /// <summary>The pre-#147 behaviour, verbatim, for untouched legacy components (Mixamo pattern + manual axis):
        /// joints 1..3 by name, Euler around the local curl axis. Only addition: when the component's own prefix resolves
        /// no finger at all (a "mixamorig1:" skeleton), the prefix the skeleton uses is detected instead of silently doing
        /// nothing — configurations that worked before are untouched.</summary>
        private static void ApplyHandPoseLegacy(SkeletonDef skel, ECS.Components.Animation.HandPose hp, ScriptHandPose script,
            float weight, float spread, ref Dictionary<int, Quaternion> add)
        {
            string prefix = LegacyPrefix(skel, hp);
            for (int f = 0; f < ECS.Components.Animation.HandPose.Fingers.Length; f++)
            {
                var curl = script != null ? script.Curl(f) : ToNum(hp.CurlOf(f));
                for (int j = 1; j <= 3; j++)
                {
                    int node = skel.FindNode(hp.BoneName(prefix, ECS.Components.Animation.HandPose.Fingers[f], j));
                    if (node < 0) continue;
                    float deg = (j == 1 ? curl.X : (j == 2 ? curl.Y : curl.Z)) * hp.CurlSign * weight;
                    float sp = (j == 1 && f < 4 && spread != 0f) ? spread * ((f - 1.5f) / 1.5f) * weight : 0f;
                    Vector3 e;
                    if (hp.CurlAxis == 0) e = new Vector3(deg, 0f, sp);
                    else if (hp.CurlAxis == 1) e = new Vector3(sp, deg, 0f);
                    else e = new Vector3(0f, sp, deg);
                    if (deg == 0f && sp == 0f) continue;
                    var q = EulerToQuat(e);
                    if (add == null) add = new Dictionary<int, Quaternion>();
                    Quaternion prev;
                    if (add.TryGetValue(node, out prev)) q = Quaternion.Normalize(q * prev);
                    add[node] = q;
                }
            }
        }

        /// <summary>Prefix for the legacy name pattern: the component's own unless it resolves no finger at all.</summary>
        private static string LegacyPrefix(SkeletonDef skel, ECS.Components.Animation.HandPose hp)
        {
            string prefix = hp.BonePrefix ?? "";
            if (skel.FindNode(hp.BoneName(prefix, "Index", 1)) >= 0 || skel.FindNode(hp.BoneName(prefix, "Middle", 1)) >= 0) return prefix;
            return MixamoPrefix(skel, hp);
        }

        private static bool HasExplicitChains(ECS.Components.Animation.HandPose hp)
        {
            for (int f = 0; f < 5; f++) if (!string.IsNullOrWhiteSpace(hp.ExplicitBones(f))) return true;
            return false;
        }

        private static Vector3 ToNum(ECS.Vector3 v) => new Vector3(v.X, v.Y, v.Z);

        /// <summary>Distribute the three authored curl values (knuckle, middle, tip joint) over the joints a finger
        /// actually has, so the fingertip ends up where a three-joint finger would put it: one joint takes the chord of
        /// all three (X + 2Y/3 + Z/3), two joints fold the tip bend into the second (X, Y + Z/2), longer chains repeat
        /// the tip value.</summary>
        private static float MapCurl(Vector3 c, int n, int j)
        {
            if (n <= 1) return c.X + c.Y * (2f / 3f) + c.Z * (1f / 3f);
            if (n == 2) return j == 0 ? c.X : c.Y + c.Z * 0.5f;
            return j == 0 ? c.X : (j == 1 ? c.Y : c.Z);
        }

        /// <summary>The Mixamo prefix to use: the component's own when its hand bone exists, else the prefix the skeleton
        /// actually uses in front of "LeftHand"/"RightHand" (mixamorig1:, mixamorig_, none …).</summary>
        private static string MixamoPrefix(SkeletonDef skel, ECS.Components.Animation.HandPose hp)
        {
            string side = hp.Side == ECS.Components.Animation.HandSide.Left ? "Left" : "Right";
            string prefix = hp.BonePrefix ?? "";
            if (skel.FindNode(prefix + side + "Hand") >= 0) return prefix;
            string suffix = side + "Hand";
            for (int i = 0; i < skel.Nodes.Length; i++)
            {
                string nm = skel.Nodes[i].Name ?? "";
                if (nm.EndsWith(suffix, StringComparison.Ordinal)) return nm.Substring(0, nm.Length - suffix.Length);
            }
            return prefix;
        }

        /// <summary>One cached rig analysis of a component: rebuilt when the component raises PropertyChanged (any edit,
        /// undo included) or when it is evaluated against another skeleton — no per-frame work or allocation.</summary>
        private sealed class RigCacheEntry<TRig> where TRig : class
        {
            public TRig Rig;
            public SkeletonDef Skel;
            public bool Dirty = true;
        }

        private static TRig CachedRig<TComp, TRig>(System.Runtime.CompilerServices.ConditionalWeakTable<TComp, RigCacheEntry<TRig>> table,
            TComp comp, SkeletonDef skel, Func<SkeletonDef, TComp, TRig> build)
            where TComp : ECS.Component where TRig : class
        {
            RigCacheEntry<TRig> entry;
            if (!table.TryGetValue(comp, out entry))
            {
                entry = new RigCacheEntry<TRig>();
                table.Add(comp, entry);
                var e = entry;
                comp.PropertyChanged += (s, a) => e.Dirty = true;
            }
            if (entry.Dirty || entry.Rig == null || !ReferenceEquals(entry.Skel, skel))
            {
                entry.Rig = build(skel, comp);
                entry.Skel = skel;
                entry.Dirty = false;
            }
            return entry.Rig;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ECS.Components.Animation.HandPose, RigCacheEntry<HandPoseRig>> _handRigs =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ECS.Components.Animation.HandPose, RigCacheEntry<HandPoseRig>>();

        private static readonly Func<SkeletonDef, ECS.Components.Animation.HandPose, HandPoseRig> _buildHandRig = BuildHandRig;
        private HandPoseRig GetHandRig(SkeletonDef skel, ECS.Components.Animation.HandPose hp) => CachedRig(_handRigs, hp, skel, _buildHandRig);

        /// <summary>
        /// Resolve a HandPose on a skeleton: hand bone (explicit → preset names → detection by side), finger joints
        /// (Custom: explicit lists only; presets: their naming pattern; Auto / preset names missing: tree walk +
        /// name keywords + palm geometry; explicit lists override per finger), then the automatic curl axes from the
        /// bind pose — fingers bend around (finger direction × palm normal), the thumb swings toward the index/middle
        /// roots on the palmar side, spread turns around the palm normal. Axes are stored in each joint's PARENT frame
        /// (the frame the additive is composed in), so they follow every animation.
        /// </summary>
        private static HandPoseRig BuildHandRig(SkeletonDef skel, ECS.Components.Animation.HandPose hp)
        {
            var rig = new HandPoseRig();
            var rep = rig.Report;
            var si = RigMap.Info(skel);
            if (si == null) { rep.Notes.Add("no skeleton"); return rig; }
            int side = hp.Side == ECS.Components.Animation.HandSide.Left ? -1 : 1;
            var notes = rep.Notes;
            var preset = hp.Rig;
            string how = "detected";

            // ---- hand bone
            int hand = -1;
            if (!string.IsNullOrWhiteSpace(hp.HandBone))
            {
                hand = RigMap.Find(skel, hp.HandBone);
                if (hand >= 0) how = "explicit";
                else notes.Add("hand bone '" + hp.HandBone.Trim() + "' not found — detecting");
            }
            if (hand < 0 && preset != ECS.Components.Animation.RigPreset.Auto && preset != ECS.Components.Animation.RigPreset.Custom)
            {
                hand = PresetHand(skel, hp, side);
                if (hand >= 0) how = preset + " names";
            }
            if (hand < 0) { hand = RigMap.FindHand(si, side, notes); how = "detected"; }

            // ---- fingers
            var raw = new List<int>[5];
            var joints = new int[5][];
            if (preset != ECS.Components.Animation.RigPreset.Custom)
            {
                int found = 0;
                if (preset != ECS.Components.Animation.RigPreset.Auto)
                    found = PresetFingers(si, hp, side, hand, raw, joints, rig.Source);
                if (found < 2 && hand >= 0)
                {
                    if (preset != ECS.Components.Animation.RigPreset.Auto)
                        notes.Add(preset + " finger names not found — fingers auto-detected");
                    for (int f = 0; f < 5; f++) { raw[f] = null; joints[f] = null; rig.Source[f] = null; }
                    var hc = RigMap.DetectFingers(si, hand, side, notes);
                    Vector3 wrist = RigMap.Pos(si, hand);
                    for (int f = 0; f < 5; f++)
                    {
                        if (hc.Raw[f] == null) continue;
                        raw[f] = hc.Raw[f];
                        joints[f] = RigMap.CurlJoints(si, hc.Raw[f], f == RigMap.Thumb, wrist);
                        rig.Source[f] = hc.Source[f];
                    }
                }
            }
            for (int f = 0; f < 5; f++)
            {
                string list = hp.ExplicitBones(f);
                if (string.IsNullOrWhiteSpace(list)) continue;
                var js = RigMap.FindList(skel, list, notes, RigMap.FingerNames[f]);
                if (js.Length == 0) continue;
                joints[f] = js;
                var chain = RigMap.ChainFrom(si, js[js.Length - 1]);
                var r = new List<int>(js);
                for (int k = 1; k < chain.Count; k++) r.Add(chain[k]);   // continue to the tip for the direction
                raw[f] = r;
                rig.Source[f] = "explicit";
            }
            if (hand < 0)
            {
                // Custom rig without a hand bone: the bone the listed fingers hang from.
                for (int f = 0; f < 5 && hand < 0; f++)
                    if (joints[f] != null && joints[f].Length > 0) hand = RigMap.Parent(si, joints[f][0]);
                if (hand >= 0) how = "parent of the listed fingers";
            }
            rig.Hand = hand;
            rig.Joints = joints;

            rep.Lines.Add("Hand: " + (hand >= 0 ? RigMap.NameOf(si, hand) + " (" + how + ")" : "not found"));
            if (hand >= 0) rep.Bones["Hand"] = new[] { RigMap.NameOf(si, hand) };
            int fingerCount = 0;
            for (int f = 0; f < 5; f++)
            {
                if (joints[f] == null || joints[f].Length == 0) { rep.Lines.Add(RigMap.FingerNames[f] + ": -"); continue; }
                fingerCount++;
                var names = new string[joints[f].Length];
                for (int k = 0; k < names.Length; k++) names[k] = RigMap.NameOf(si, joints[f][k]);
                rep.Bones[RigMap.FingerNames[f]] = names;
                rep.Lines.Add(RigMap.FingerNames[f] + ": " + string.Join(", ", names) + " (" + (rig.Source[f] ?? "?") + ")");
            }
            if (hand < 0 || fingerCount == 0)
            {
                if (hand < 0) notes.Add("no " + (side < 0 ? "left" : "right") + " hand found — set Hand bone or the finger lists");
                else notes.Add("no finger joints found under the hand");
                return rig;
            }

            // ---- palm frame + automatic axes
            var frame = RigMap.ComputeHandFrame(si, hand, raw, joints, side, notes);
            rig.Frame = frame;
            for (int f = 0; f < 5; f++)
            {
                var js = joints[f];
                if (js == null || js.Length == 0) continue;
                rig.Axis[f] = new Vector3[js.Length];
                if (f < 4)
                {
                    // Finger direction from its curl joints (knuckle → last joint), falling back to the chain tip for a
                    // one-joint finger — end bones never influence the axis.
                    Vector3 b = RigMap.Pos(si, js[0]);
                    Vector3 tip = js.Length >= 2 ? RigMap.Pos(si, js[js.Length - 1])
                                : (raw[f] != null && raw[f].Count > 1 ? RigMap.Pos(si, raw[f][raw[f].Count - 1]) : b + frame.Dir);
                    Vector3 fdir = tip - b;
                    fdir = fdir.LengthSquared() > 1e-12f ? Vector3.Normalize(fdir) : frame.Dir;
                    Vector3 axis = Vector3.Cross(fdir, frame.Palmar);
                    if (axis.LengthSquared() < 1e-8f) axis = Vector3.Cross(frame.Dir, frame.Palmar);
                    axis = Vector3.Normalize(axis);
                    for (int k = 0; k < js.Length; k++) rig.Axis[f][k] = ToParentFrame(si, js[k], axis);
                    Vector3 lat = RigMap.Perp(frame.Lateral, fdir);
                    Vector3 sAxis = lat.LengthSquared() > 1e-8f ? Vector3.Cross(fdir, Vector3.Normalize(lat)) : Vector3.Zero;
                    rig.SpreadAxis[f] = sAxis.LengthSquared() > 1e-8f ? ToParentFrame(si, js[0], Vector3.Normalize(sAxis)) : Vector3.Zero;
                }
                else
                {
                    for (int k = 0; k < js.Length; k++)
                    {
                        Vector3 p = RigMap.Pos(si, js[k]);
                        int next = k + 1 < js.Length ? js[k + 1] : RigMap.MainChild(si, js[k]);
                        Vector3 dir = next >= 0 ? RigMap.Pos(si, next) - p : p - RigMap.Pos(si, RigMap.Parent(si, js[k]));
                        dir = dir.LengthSquared() > 1e-12f ? Vector3.Normalize(dir) : frame.Dir;
                        Vector3 toward = RigMap.Perp(frame.ThumbTarget - p, dir);
                        if (toward.LengthSquared() < 1e-10f) toward = RigMap.Perp(frame.Palmar, dir);
                        Vector3 axis = Vector3.Cross(dir, Vector3.Normalize(toward));
                        if (axis.LengthSquared() < 1e-8f) axis = Vector3.Cross(dir, frame.Palmar);
                        rig.Axis[f][k] = ToParentFrame(si, js[k], Vector3.Normalize(axis));
                    }
                }
            }
            rep.Lines.Add("Palm normal (model): " + Fmt(frame.Palmar) + (frame.PalmFromThumb ? " (from the thumb)" : ""));
            rep.Ok = true;
            return rig;
        }

        /// <summary>Model-space direction → the given node's PARENT frame at bind pose (the frame additive deltas use).</summary>
        private static Vector3 ToParentFrame(RigMap.SkelInfo si, int node, Vector3 modelDir)
        {
            int p = RigMap.Parent(si, node);
            if (p < 0) return modelDir;
            var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(NormalizeBasis(si.Bind[p])));
            var v = Vector3.Transform(modelDir, Quaternion.Inverse(q));
            return v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : modelDir;
        }

        private static string Fmt(Vector3 v)
            => "(" + v.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ", " +
               v.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ", " +
               v.Z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")";

        private static string SideWord(int side) => side < 0 ? "Left" : "Right";

        /// <summary>Hand bone by the preset's naming convention (-1 when the names are not in the skeleton).</summary>
        private static int PresetHand(SkeletonDef skel, ECS.Components.Animation.HandPose hp, int side)
        {
            string L = side < 0 ? "L" : "R", l = side < 0 ? "l" : "r", Side = SideWord(side);
            switch (hp.Rig)
            {
                case ECS.Components.Animation.RigPreset.Mixamo:
                    return skel.FindNode(MixamoPrefix(skel, hp) + Side + "Hand");
                case ECS.Components.Animation.RigPreset.Unreal:
                    return FirstFound(skel, "hand_" + l);
                case ECS.Components.Animation.RigPreset.Rigify:
                    return FirstFound(skel, "DEF-hand." + L, "hand." + L, "ORG-hand." + L);
                case ECS.Components.Animation.RigPreset.UnityGeneric:
                    return FirstFound(skel, Side + "Hand", Side + " Hand", Side + "_Hand", "Hand_" + L, "Hand." + L, L + "_Hand",
                        "J_Bip_" + L + "_Hand", "Bip01 " + L + " Hand", "CC_Base_" + L + "_Hand", l + "Hand", "Hand_" + Side);
            }
            return -1;
        }

        private static int FirstFound(SkeletonDef skel, params string[] names)
        {
            foreach (var n in names) { int i = RigMap.Find(skel, n); if (i >= 0) return i; }
            return -1;
        }

        /// <summary>Finger joints by the preset's naming convention. Returns the number of fingers found.</summary>
        private static int PresetFingers(RigMap.SkelInfo si, ECS.Components.Animation.HandPose hp, int side, int hand,
            List<int>[] raw, int[][] joints, string[] source)
        {
            var skel = si.Skel;
            string L = side < 0 ? "L" : "R", l = side < 0 ? "l" : "r", Side = SideWord(side);
            string[] unrealF = { "index", "middle", "ring", "pinky", "thumb" };
            string[] unityF = { "Index", "Middle", "Ring", "Little", "Thumb" };
            string mixPrefix = MixamoPrefix(skel, hp);
            int found = 0;
            for (int f = 0; f < 5; f++)
            {
                var named = new List<int>();
                for (int j = 1; j <= 3; j++)
                {
                    int node = -1;
                    switch (hp.Rig)
                    {
                        case ECS.Components.Animation.RigPreset.Mixamo:
                            node = skel.FindNode(hp.BoneName(mixPrefix, ECS.Components.Animation.HandPose.Fingers[f], j));
                            break;
                        case ECS.Components.Animation.RigPreset.Unreal:
                            node = RigMap.Find(skel, unrealF[f] + "_0" + j + "_" + l);
                            break;
                        case ECS.Components.Animation.RigPreset.Rigify:
                            string rn = f == RigMap.Thumb ? "thumb.0" + j + "." + L : "f_" + unrealF[f] + ".0" + j + "." + L;
                            node = FirstFound(skel, "DEF-" + rn, rn, "ORG-" + rn);
                            break;
                        case ECS.Components.Animation.RigPreset.UnityGeneric:
                            string[] phal = { "Proximal", "Intermediate", "Distal" };
                            node = FirstFound(skel, Side + unityF[f] + phal[j - 1], Side + " " + unityF[f] + " " + phal[j - 1],
                                Side + "Hand" + ECS.Components.Animation.HandPose.Fingers[f] + j, "J_Bip_" + L + "_" + unityF[f] + j,
                                "CC_Base_" + L + "_" + (f == RigMap.Middle ? "Mid" : (f == RigMap.Pinky ? "Pinky" : unityF[f])) + j);
                            break;
                    }
                    if (node >= 0) named.Add(node);
                }
                if (named.Count == 0) continue;
                var eff = named.FindAll(x => si.Effective[x]);
                if (eff.Count == 0) continue;
                joints[f] = eff.ToArray();
                var chain = RigMap.ChainFrom(si, named[named.Count - 1]);
                var r = new List<int>(named);
                for (int k = 1; k < chain.Count; k++) r.Add(chain[k]);
                raw[f] = r;
                source[f] = hp.Rig + " names";
                found++;
            }
            return found;
        }

        // ------------------------------------------------------------------ look-at + foot IK config (#147)

        /// <summary>Pick the active LookAtIk / FootIk configuration (first enabled component; a default one when only a
        /// script drives it) and resolve its rig (cached per component + configuration).</summary>
        private void SyncLookAtAndFootIk(ECS.GameEntity entity, AnimatorState state)
        {
            var skel = state.Skeleton;
            ECS.Components.Animation.LookAtIk look = null;
            ECS.Components.Animation.FootIk foot = null;
            var comps = entity?.Components;
            if (comps != null)
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    if (look == null && comps[i] is ECS.Components.Animation.LookAtIk la && la.IsEnabled) look = la;
                    if (foot == null && comps[i] is ECS.Components.Animation.FootIk fi && fi.IsEnabled) foot = fi;
                }
            }
            if (look == null && (state.HasLookPoint || state.LookEntity != null))
                look = state.DefaultLook ?? (state.DefaultLook = new ECS.Components.Animation.LookAtIk());
            if (foot == null && state.FootWeightOverride > 0f)
                foot = state.DefaultFoot ?? (state.DefaultFoot = new ECS.Components.Animation.FootIk());
            state.LookCfg = look;
            state.LookRig = look != null && skel != null ? GetLookRig(skel, look) : null;
            state.FootCfg = foot;
            state.FootRig = foot != null && skel != null ? GetFootRig(skel, foot) : null;
            if (state.LookRig != null && !state.LookRig.Report.Ok) state.LookRig = null;
            if (state.FootRig != null && !state.FootRig.Report.Ok) state.FootRig = null;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ECS.Components.Animation.LookAtIk, RigCacheEntry<LookAtRig>> _lookRigs =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ECS.Components.Animation.LookAtIk, RigCacheEntry<LookAtRig>>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ECS.Components.Animation.FootIk, RigCacheEntry<FootIkRig>> _footRigs =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ECS.Components.Animation.FootIk, RigCacheEntry<FootIkRig>>();

        private static readonly Func<SkeletonDef, ECS.Components.Animation.LookAtIk, LookAtRig> _buildLookRig = BuildLookRig;
        private static readonly Func<SkeletonDef, ECS.Components.Animation.FootIk, FootIkRig> _buildFootRig = BuildFootRig;
        private static LookAtRig GetLookRig(SkeletonDef skel, ECS.Components.Animation.LookAtIk la) => CachedRig(_lookRigs, la, skel, _buildLookRig);

        private static FootIkRig GetFootRig(SkeletonDef skel, ECS.Components.Animation.FootIk fi) => CachedRig(_footRigs, fi, skel, _buildFootRig);

        /// <summary>Head bone by preset naming (-1 when absent).</summary>
        private static int PresetHead(SkeletonDef skel, ECS.Components.Animation.RigPreset preset)
        {
            switch (preset)
            {
                case ECS.Components.Animation.RigPreset.Mixamo:
                    for (int i = 0; i < skel.Nodes.Length; i++)
                    {
                        string nm = skel.Nodes[i].Name ?? "";
                        if (nm.EndsWith(":Head", StringComparison.Ordinal) || nm == "Head" || nm.EndsWith("_Head", StringComparison.Ordinal)) return i;
                    }
                    return -1;
                case ECS.Components.Animation.RigPreset.Unreal: return FirstFound(skel, "head");
                case ECS.Components.Animation.RigPreset.Rigify: return FirstFound(skel, "DEF-spine.006", "spine.006", "ORG-spine.006", "DEF-head", "head");
                case ECS.Components.Animation.RigPreset.UnityGeneric: return FirstFound(skel, "Head", "J_Bip_C_Head", "Bip01 Head", "CC_Base_Head");
            }
            return -1;
        }

        private static LookAtRig BuildLookRig(SkeletonDef skel, ECS.Components.Animation.LookAtIk la)
        {
            var rig = new LookAtRig();
            var rep = rig.Report;
            var si = RigMap.Info(skel);
            if (si == null) { rep.Notes.Add("no skeleton"); return rig; }
            string how = "detected";
            int head = -1;
            if (!string.IsNullOrWhiteSpace(la.HeadBone))
            {
                head = RigMap.Find(skel, la.HeadBone);
                if (head >= 0) how = "explicit"; else rep.Notes.Add("head bone '" + la.HeadBone.Trim() + "' not found — detecting");
            }
            if (head < 0 && la.Rig != ECS.Components.Animation.RigPreset.Auto && la.Rig != ECS.Components.Animation.RigPreset.Custom)
            {
                head = PresetHead(skel, la.Rig);
                if (head >= 0) how = la.Rig + " names";
            }
            if (head < 0 && la.Rig != ECS.Components.Animation.RigPreset.Custom) { head = RigMap.FindHead(si, rep.Notes); how = "detected"; }
            rig.Head = head;
            if (head < 0)
            {
                rep.Lines.Add("Head: not found");
                rep.Notes.Add("no head bone found — set Head bone");
                return rig;
            }

            var neck = new List<int>();
            var spine = new List<int>();
            bool explicitNeck = !string.IsNullOrWhiteSpace(la.NeckBones), explicitSpine = !string.IsNullOrWhiteSpace(la.SpineBones);
            if (explicitNeck) neck.AddRange(RigMap.FindList(skel, la.NeckBones, rep.Notes, "Neck"));
            if (explicitSpine) spine.AddRange(RigMap.FindList(skel, la.SpineBones, rep.Notes, "Spine"));
            if ((!explicitNeck || !explicitSpine) && la.Rig != ECS.Components.Animation.RigPreset.Custom)
            {
                var dn = new List<int>(); var ds = new List<int>();
                RigMap.FindNeckAndSpine(si, head, dn, ds);
                if (!explicitNeck) neck.AddRange(dn);
                if (!explicitSpine) { ds.Reverse(); spine.AddRange(ds); }   // lowest first
            }
            // Chain lowest → head with shares; bones must be ancestors of the head to carry it.
            var chain = new List<int>();
            float sw = spine.Count > 0 ? la.SpineWeight / spine.Count : 0f, nw = neck.Count > 0 ? la.NeckWeight / neck.Count : 0f;
            foreach (int b in spine) if (b != head && RigMap.IsAncestor(si, b, head) && !chain.Contains(b)) chain.Add(b);
            foreach (int b in neck) if (b != head && RigMap.IsAncestor(si, b, head) && !chain.Contains(b)) chain.Add(b);
            chain.Sort((a, b) => si.Depth[a].CompareTo(si.Depth[b]));
            var shareSorted = new List<float>();
            foreach (int b in chain) shareSorted.Add(spine.Contains(b) ? sw : nw);
            chain.Add(head); shareSorted.Add(la.HeadWeight);
            float total = 0f; foreach (var x in shareSorted) total += x;
            if (total <= 1e-5f) { for (int i = 0; i < shareSorted.Count; i++) shareSorted[i] = 0f; shareSorted[shareSorted.Count - 1] = 1f; total = 1f; }
            for (int i = 0; i < shareSorted.Count; i++) shareSorted[i] /= total;
            // drop zero-share bones from the chain
            var fc = new List<int>(); var fs = new List<float>();
            for (int i = 0; i < chain.Count; i++) if (shareSorted[i] > 1e-5f) { fc.Add(chain[i]); fs.Add(shareSorted[i]); }
            rig.Chain = fc.ToArray();
            rig.Share = fs.ToArray();
            rig.Ref = RigMap.Parent(si, rig.Chain[0]);

            var frame = RigMap.ModelFrame(si);
            Vector3 fwd = frame.Forward, up = frame.Up;
            string fwdSrc = frame.Source;
            switch (la.ForwardAxis)
            {
                case 1: fwd = Vector3.UnitZ; fwdSrc = "+Z (override)"; break;
                case 2: fwd = -Vector3.UnitZ; fwdSrc = "-Z (override)"; break;
                case 3: fwd = Vector3.UnitX; fwdSrc = "+X (override)"; break;
                case 4: fwd = -Vector3.UnitX; fwdSrc = "-X (override)"; break;
            }
            fwd = RigMap.Perp(fwd, up);
            fwd = fwd.LengthSquared() > 1e-8f ? Vector3.Normalize(fwd) : frame.Forward;
            rig.FwdHeadLocal = ToLocalFrame(si, head, fwd);
            rig.FwdRefLocal = rig.Ref >= 0 ? ToLocalFrame(si, rig.Ref, fwd) : fwd;
            rig.UpRefLocal = rig.Ref >= 0 ? ToLocalFrame(si, rig.Ref, up) : up;

            rep.Lines.Add("Head: " + RigMap.NameOf(si, head) + " (" + how + ")");
            rep.Bones["Head"] = new[] { RigMap.NameOf(si, head) };
            var neckNames = new List<string>(); var spineNames = new List<string>();
            for (int i = 0; i < rig.Chain.Length - 1; i++)
            {
                string nm = RigMap.NameOf(si, rig.Chain[i]) + " " + (rig.Share[i] * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                if (spine.Contains(rig.Chain[i])) spineNames.Add(RigMap.NameOf(si, rig.Chain[i])); else neckNames.Add(RigMap.NameOf(si, rig.Chain[i]));
                rep.Lines.Add((spine.Contains(rig.Chain[i]) ? "Spine: " : "Neck: ") + nm);
            }
            rep.Bones["Neck"] = neckNames.ToArray();
            rep.Bones["Spine"] = spineNames.ToArray();
            rep.Lines.Add("Head share: " + (rig.Share[rig.Share.Length - 1] * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%");
            rep.Lines.Add("Face forward (model): " + Fmt(fwd) + " — " + fwdSrc);
            rep.Ok = true;
            return rig;
        }

        /// <summary>Model-space direction → a node's OWN local frame at bind pose.</summary>
        private static Vector3 ToLocalFrame(RigMap.SkelInfo si, int node, Vector3 modelDir)
        {
            var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(NormalizeBasis(si.Bind[node])));
            var v = Vector3.Transform(modelDir, Quaternion.Inverse(q));
            return v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : modelDir;
        }

        private static int PresetFoot(SkeletonDef skel, ECS.Components.Animation.RigPreset preset, int side)
        {
            string L = side < 0 ? "L" : "R", l = side < 0 ? "l" : "r", Side = SideWord(side);
            switch (preset)
            {
                case ECS.Components.Animation.RigPreset.Mixamo:
                    for (int i = 0; i < skel.Nodes.Length; i++)
                    {
                        string nm = skel.Nodes[i].Name ?? "";
                        if (nm.EndsWith(Side + "Foot", StringComparison.Ordinal)) return i;
                    }
                    return -1;
                case ECS.Components.Animation.RigPreset.Unreal: return FirstFound(skel, "foot_" + l);
                case ECS.Components.Animation.RigPreset.Rigify: return FirstFound(skel, "DEF-foot." + L, "foot." + L, "ORG-foot." + L);
                case ECS.Components.Animation.RigPreset.UnityGeneric:
                    return FirstFound(skel, Side + "Foot", Side + " Foot", Side + "_Foot", "Foot_" + L, "Foot." + L, "J_Bip_" + L + "_Foot",
                        "Bip01 " + L + " Foot", "CC_Base_" + L + "_Foot", l + "Foot");
            }
            return -1;
        }

        private static FootIkRig BuildFootRig(SkeletonDef skel, ECS.Components.Animation.FootIk fi)
        {
            var rig = new FootIkRig();
            var rep = rig.Report;
            var si = RigMap.Info(skel);
            if (si == null) { rep.Notes.Add("no skeleton"); return rig; }
            var frame = RigMap.ModelFrame(si);
            rig.UpBind = frame.Up;
            string[] explicitNames = { fi.LeftFoot, fi.RightFoot };
            for (int k = 0; k < 2; k++)
            {
                int side = k == 0 ? -1 : 1;
                string how = "detected";
                int foot = -1;
                if (!string.IsNullOrWhiteSpace(explicitNames[k]))
                {
                    foot = RigMap.Find(skel, explicitNames[k]);
                    if (foot >= 0) how = "explicit"; else rep.Notes.Add(SideWord(side) + " foot '" + explicitNames[k].Trim() + "' not found — detecting");
                }
                if (foot < 0 && fi.Rig != ECS.Components.Animation.RigPreset.Auto && fi.Rig != ECS.Components.Animation.RigPreset.Custom)
                {
                    foot = PresetFoot(skel, fi.Rig, side);
                    if (foot >= 0) how = fi.Rig + " names";
                }
                if (foot < 0 && fi.Rig != ECS.Components.Animation.RigPreset.Custom) foot = RigMap.FindFoot(si, side);
                int mid, root;
                if (foot >= 0 && RigMap.ResolveLimb(si, foot, out mid, out root))
                {
                    rig.Foot[k] = foot; rig.Knee[k] = mid; rig.Hip[k] = root;
                    // The floor the animation assumes: the model origin plane when the feet stand on it (the usual
                    // export — toe JOINTS sit a little above the sole mesh, so the lowest joint is not the floor), else
                    // the lowest joint of the foot (models whose origin is elsewhere, e.g. at the hips).
                    float ankleUp = Vector3.Dot(RigMap.Pos(si, foot), frame.Up);
                    float lowest = ankleUp - RigMap.FootHeight(si, foot, frame.Up);
                    float legLen = Vector3.Distance(RigMap.Pos(si, root), RigMap.Pos(si, mid)) + Vector3.Distance(RigMap.Pos(si, mid), RigMap.Pos(si, foot));
                    rig.SoleUp[k] = (lowest >= -0.05f * legLen && lowest <= 0.25f * legLen) ? Math.Min(lowest, 0f) : lowest;
                    rig.AnkleHeight[k] = ankleUp - rig.SoleUp[k];
                    rep.Lines.Add(SideWord(side) + " leg: " + RigMap.NameOf(si, root) + " → " + RigMap.NameOf(si, mid) + " → " +
                                  RigMap.NameOf(si, foot) + " (" + how + ")");
                    rep.Bones[SideWord(side) + "Foot"] = new[] { RigMap.NameOf(si, foot) };
                    rep.Bones[SideWord(side) + "Knee"] = new[] { RigMap.NameOf(si, mid) };
                    rep.Bones[SideWord(side) + "Hip"] = new[] { RigMap.NameOf(si, root) };
                }
                else rep.Lines.Add(SideWord(side) + " leg: not found");
            }
            int pelvis = -1;
            if (!string.IsNullOrWhiteSpace(fi.PelvisBone))
            {
                pelvis = RigMap.Find(skel, fi.PelvisBone);
                if (pelvis < 0) rep.Notes.Add("pelvis '" + fi.PelvisBone.Trim() + "' not found — detecting");
            }
            if (pelvis < 0 && rig.Hip[0] >= 0 && rig.Hip[1] >= 0) pelvis = RigMap.Lca(si, rig.Hip[0], rig.Hip[1]);
            rig.Pelvis = pelvis;
            if (pelvis >= 0) { rep.Lines.Add("Pelvis: " + RigMap.NameOf(si, pelvis)); rep.Bones["Pelvis"] = new[] { RigMap.NameOf(si, pelvis) }; }
            if (rig.Foot[0] >= 0 || rig.Foot[1] >= 0)
            {
                float h = Math.Max(rig.AnkleHeight[0], rig.AnkleHeight[1]);
                rep.Lines.Add("Ankle height (model units): " + h.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                rep.Ok = true;
            }
            else rep.Notes.Add("no feet found — set Left/Right foot");
            return rig;
        }

        private void SyncIkChains(ECS.GameEntity entity, AnimatorState state)
        {
            List<IkChainRuntime> chains = null;
            var skel = state.Skeleton;
            var comps = entity?.Components;
            if (skel != null && comps != null)
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    var ik = comps[i] as ECS.Components.Animation.TwoBoneIk;
                    if (ik == null || !ik.IsEnabled || ik.Weight <= 0.001f) continue;
                    if (string.IsNullOrEmpty(ik.TipBone) || string.IsNullOrEmpty(ik.TargetBone)) continue;
                    int tip = FindBone(skel, ik.TipBone);
                    int target = FindBone(skel, ik.TargetBone);
                    if (tip < 0 || target < 0) continue;
                    // mid = the joint above the tip, root = the joint above that (parent / grandparent on plain rigs;
                    // in-chain twist helpers and split segments like Rigify "forearm.L.001" are skipped).
                    int mid, root;
                    if (!RigMap.ResolveLimb(RigMap.Info(skel), tip, out mid, out root)) continue;

                    // Offset rotation uses the SAME euler convention as the socket system (engine ZXY),
                    // so a captured offset round-trips exactly.
                    var rotM = BoneSocketService.EulerZXY(new Vector3(
                        ik.TargetOffsetRotation.X, ik.TargetOffsetRotation.Y, ik.TargetOffsetRotation.Z));

                    (chains = chains ?? new List<IkChainRuntime>()).Add(new IkChainRuntime
                    {
                        Tip = tip,
                        Mid = mid,
                        Root = root,
                        Target = target,
                        OffsetPos = new Vector3(ik.TargetOffsetPosition.X, ik.TargetOffsetPosition.Y, ik.TargetOffsetPosition.Z),
                        OffsetRot = Quaternion.CreateFromRotationMatrix(rotM),
                        Weight = ik.Weight,
                        PoleAngleDeg = ik.PoleAngle,
                        ApplyTipRotation = ik.ApplyTipRotation,
                        AutoGrip = ik.AutoGrip,
                    });
                }
            }
            // Script world-space targets (Animation.SetIkTarget) ride on the component's chain definition:
            // convert them into the rig's model space with the entity world of THIS frame.
            if (chains != null && state.IkWorldTargets != null && state.IkWorldTargets.Count > 0)
            {
                Matrix4x4 invWorld = Matrix4x4.Identity;
                bool haveInv = false;
                for (int i = 0; i < chains.Count; i++)
                {
                    var ch = chains[i];
                    if (!state.IkWorldTargets.TryGetValue(ch.Tip, out var wt)) continue;
                    if (!haveInv)
                    {
                        var meshEntity = FindSkinnedMeshEntity(entity) ?? entity;
                        haveInv = Matrix4x4.Invert(BoneSocketService.EntityWorld(meshEntity), out invWorld);
                        if (!haveInv) break;
                    }
                    var worldM = Matrix4x4.CreateFromQuaternion(wt.HasRot ? wt.Rot : Quaternion.Identity);
                    worldM.Translation = wt.Pos;
                    ch.WorldTargetModel = worldM * invWorld;
                    ch.HasWorldTarget = IsFinite(ch.WorldTargetModel);
                    ch.WorldTargetHasRot = wt.HasRot;
                    if (_ikDebug && _ikDebugCount < 6)
                        Services.ConsoleService.Instance?.Log("[IK] sync chain tip=" + ch.Tip + " worldTarget=" + wt.Pos + " modelTarget=" + ch.WorldTargetModel.Translation + " finite=" + ch.HasWorldTarget + " weight=" + ch.Weight);
                }
            }
            state.IkChains = chains;
        }

        /// <summary>Script API: pull a TwoBoneIk chain (selected by its tip bone) to a WORLD-space target
        /// until cleared — the chain's component still defines the limb and the weight. rot = null keeps
        /// the animated wrist orientation.</summary>
        public void SetIkWorldTarget(ECS.GameEntity entity, string tipBone, Vector3 worldPos, Quaternion? worldRot)
        {
            if (entity == null || string.IsNullOrEmpty(tipBone)) return;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return;
            int node = FindBone(state.Skeleton, tipBone);
            if (node < 0) return;
            if (state.IkWorldTargets == null) state.IkWorldTargets = new Dictionary<int, IkWorldTarget>();
            if (!state.IkWorldTargets.TryGetValue(node, out var wt)) state.IkWorldTargets[node] = wt = new IkWorldTarget();
            wt.Pos = worldPos;
            wt.HasRot = worldRot.HasValue;
            wt.Rot = worldRot ?? Quaternion.Identity;
            HasActiveAnimators = true;
            if (_ikDebug && _ikDebugCount < 6) Services.ConsoleService.Instance?.Log("[IK] set target on '" + entity.Name + "' node=" + node + " pos=" + worldPos + " chains=" + (state.IkChains != null ? state.IkChains.Count : -1));
        }

        public void ClearIkWorldTarget(ECS.GameEntity entity, string tipBone)
        {
            if (entity == null) return;
            if (!_states.TryGetValue(entity.Id, out var state) || state.IkWorldTargets == null) return;
            if (string.IsNullOrEmpty(tipBone)) { state.IkWorldTargets.Clear(); return; }
            int node = state.Skeleton != null ? FindBone(state.Skeleton, tipBone) : -1;
            if (node >= 0) state.IkWorldTargets.Remove(node);
        }

        /// <summary>Bone lookup of the IK APIs: exact name first, then case- and namespace-insensitive
        /// ("LeftHand" finds "mixamorig:LeftHand").</summary>
        private static int FindBone(SkeletonDef skel, string name)
        {
            if (skel == null || string.IsNullOrEmpty(name)) return -1;
            int i = skel.FindNode(name);
            return i >= 0 ? i : RigMap.Find(skel, name);
        }

        /// <summary>Edit-mode live preview + runtime weight changes: re-sync and re-pose one entity's
        /// animator so the IK'd pose is visible immediately (bind pose + IK in edit mode). Safe no-op
        /// when the entity has no skeleton. The caller/inspector still resubmits the viewport.</summary>
        public void RefreshIk(ECS.GameEntity entity)
        {
            if (entity == null) return;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return;
            state.IkCapturedGrips = null;   // config changed -> recapture the auto-grip from the fresh pose
            SyncIkChains(entity, state);
            SyncHandPoses(entity, state);
            SyncLookAtAndFootIk(entity, state);
            HasActiveAnimators = true;
            // Edit-mode preview (nothing steps the clock): smoothed look-at jumps straight to its target.
            state.SnapSmoothing = !ClockRunning(state);
            try { state.Palette = EvaluateStatePalette(state); }
            finally { state.SnapSmoothing = false; }
            Services.SceneRenderService.RuntimeDirty = true;   // GameHost/submit-once re-submit contract
        }

        /// <summary>Inspector preview: re-pose an entity whose animator is NOT being stepped by a running clock (edit mode),
        /// so a card can show the authored pose as soon as it opens — without resetting live IK state (auto-grip capture,
        /// smoothing) of an entity that is selected during Play.</summary>
        public void RefreshPreview(ECS.GameEntity entity)
        {
            if (entity == null) return;
            AnimatorState st;
            if (_states.TryGetValue(entity.Id, out st) && ClockRunning(st)) return;
            RefreshIk(entity);
        }

        /// <summary>True while a game / preview clock is stepping this animator (stepped within the last half second).</summary>
        private static bool ClockRunning(AnimatorState st)
            => st != null && st.StepCount > 0 && unchecked(Environment.TickCount - st.LastStepTick) < 500;

        // ------------------------------------------------------------------ script API: hand poses / look-at / foot IK (#147)

        /// <summary>Script: pose a hand from a preset ("Open", "Relaxed", "Fist", "Trigger", "Grip", "Point"), blended in
        /// over <paramref name="blendSeconds"/>. Overrides the curls/weight of that hand's HandPose component at runtime
        /// (the authored component is untouched); works without a component too (auto-detected rig). False when the
        /// entity has no skeleton or the preset name is unknown.</summary>
        public bool SetHandPosePreset(ECS.GameEntity entity, ECS.Components.Animation.HandSide side, string preset, float weight, float blendSeconds)
        {
            ECS.Components.Animation.HandPosePreset p;
            if (!TryParsePreset(preset, out p)) return false;
            ECS.Vector3 i, m, r, k, t; float s;
            ECS.Components.Animation.HandPose.GetPreset(p, out i, out m, out r, out k, out t, out s);
            return SetHandPoseCurls(entity, side, new[] { i, m, r, k, t }, s, weight, blendSeconds);
        }

        /// <summary>Script: pose a hand with explicit curls (degrees per joint: index, middle, ring, pinky, thumb).</summary>
        public bool SetHandPoseCurls(ECS.GameEntity entity, ECS.Components.Animation.HandSide side, ECS.Vector3[] curls, float spread, float weight, float blendSeconds)
        {
            if (entity == null || curls == null || curls.Length < 5) return false;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return false;
            int si = side == ECS.Components.Animation.HandSide.Left ? 0 : 1;
            if (state.ScriptHands == null) state.ScriptHands = new ScriptHandPose[2];
            var cur = state.ScriptHands[si];
            var next = new ScriptHandPose();
            // Blend from what the hand shows NOW: the running script pose, else the component's authored pose.
            ECS.Components.Animation.HandPose comp = null;
            foreach (var c in entity.Components) { var h = c as ECS.Components.Animation.HandPose; if (h != null && h.IsEnabled && h.Side == side) { comp = h; break; } }
            for (int f = 0; f < 5; f++)
            {
                next.To[f] = ToNum(curls[f]);
                next.From[f] = cur != null ? cur.Curl(f) : (comp != null ? ToNum(comp.CurlOf(f)) : next.To[f]);
            }
            next.ToSpread = spread;
            next.FromSpread = cur != null ? cur.Spread : (comp != null ? comp.Spread : spread);
            next.ToWeight = weight < 0f ? 0f : (weight > 1f ? 1f : weight);
            next.FromWeight = cur != null ? cur.Weight : (comp != null ? comp.Weight : 0f);
            next.Duration = blendSeconds > 0f ? blendSeconds : 0f;
            state.ScriptHands[si] = next;
            SyncHandPoses(entity, state);
            HasActiveAnimators = true;
            if (state.Palette != null || state.ComponentAdditive != null) state.Palette = EvaluateStatePalette(state);
            return true;
        }

        /// <summary>Script: drop the runtime hand pose (null side = both) — the HandPose component's authored pose applies again.</summary>
        public void ClearHandPose(ECS.GameEntity entity, ECS.Components.Animation.HandSide? side)
        {
            if (entity == null || !_states.TryGetValue(entity.Id, out var state) || state.ScriptHands == null) return;
            if (side == null) { state.ScriptHands[0] = state.ScriptHands[1] = null; }
            else state.ScriptHands[side == ECS.Components.Animation.HandSide.Left ? 0 : 1] = null;
            SyncHandPoses(entity, state);
            HasActiveAnimators = true;
            if (state.Palette != null) state.Palette = EvaluateStatePalette(state);
        }

        public static bool TryParsePreset(string name, out ECS.Components.Animation.HandPosePreset preset)
        {
            preset = ECS.Components.Animation.HandPosePreset.Relaxed;
            if (string.IsNullOrWhiteSpace(name)) return false;
            return Enum.TryParse(name.Trim(), true, out preset) && Enum.IsDefined(typeof(ECS.Components.Animation.HandPosePreset), preset);
        }

        /// <summary>Script: look at a WORLD point until cleared (overrides the LookAtIk component's target entity;
        /// works without a component with default limits).</summary>
        public void SetLookAtPoint(ECS.GameEntity entity, Vector3 worldPoint)
        {
            if (entity == null) return;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return;
            state.HasLookPoint = true; state.LookPoint = worldPoint; state.LookEntity = null;
            SyncLookAtAndFootIk(entity, state);
            HasActiveAnimators = true;
        }

        /// <summary>Script: look at another entity (its head bone when it has a skeleton, else its origin), tracked every frame.</summary>
        public void SetLookAtEntity(ECS.GameEntity entity, ECS.GameEntity target)
        {
            if (entity == null) return;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return;
            state.HasLookPoint = false; state.LookEntity = target;
            SyncLookAtAndFootIk(entity, state);
            HasActiveAnimators = true;
        }

        /// <summary>Script: back to the component's own target (or no look-at without a component).</summary>
        public void ClearLookAtTarget(ECS.GameEntity entity)
        {
            if (entity == null || !_states.TryGetValue(entity.Id, out var state)) return;
            state.HasLookPoint = false; state.LookEntity = null;
            SyncLookAtAndFootIk(entity, state);
        }

        /// <summary>Script: runtime look-at weight (0..1, smoothed); negative = back to the component's weight.</summary>
        public void SetLookAtWeight(ECS.GameEntity entity, float weight)
        {
            if (entity == null) return;
            var state = GetOrCreateState(entity);
            if (state == null) return;
            state.LookWeightOverride = weight < 0f ? -1f : (weight > 1f ? 1f : weight);
            HasActiveAnimators = true;
        }

        /// <summary>Script: runtime foot-IK weight (0..1); negative = back to the component's weight. Enables foot IK with
        /// auto-detected legs on a character without a FootIk component.</summary>
        public void SetFootIkWeight(ECS.GameEntity entity, float weight)
        {
            if (entity == null) return;
            var state = GetOrCreateState(entity);
            if (state?.Skeleton == null) return;
            state.FootWeightOverride = weight < 0f ? -1f : (weight > 1f ? 1f : weight);
            SyncLookAtAndFootIk(entity, state);
            HasActiveAnimators = true;
        }

        // ------------------------------------------------------------------ inspector / test API (#147)

        /// <summary>What a HandPose resolves to on the entity's skeleton (hand, finger joints, palm normal, notes).</summary>
        public RigReport DescribeHandPose(ECS.GameEntity entity, ECS.Components.Animation.HandPose hp)
        {
            var skel = SkeletonOf(entity);
            if (skel == null || hp == null) return NoSkeleton();
            if (hp.IsLegacyConfiguration && string.IsNullOrWhiteSpace(hp.HandBone) && !HasExplicitChains(hp))
            {
                // Legacy components pose by the Mixamo pattern; report what that pattern finds.
                var rep = new RigReport();
                string prefix = LegacyPrefix(skel, hp);
                int found = 0;
                for (int f = 0; f < 5; f++)
                {
                    var names = new List<string>();
                    for (int j = 1; j <= 3; j++) { string n = hp.BoneName(prefix, ECS.Components.Animation.HandPose.Fingers[f], j); if (skel.FindNode(n) >= 0) names.Add(n); }
                    if (names.Count > 0) { found++; rep.Bones[RigMap.FingerNames[f]] = names.ToArray(); }
                    rep.Lines.Add(RigMap.FingerNames[f] + ": " + (names.Count > 0 ? string.Join(", ", names.ToArray()) : "-") + " (Mixamo pattern)");
                }
                rep.Lines.Insert(0, "Legacy mode: Mixamo pattern + manual axis " + "XYZ"[Math.Max(0, Math.Min(2, hp.CurlAxis))] + " (sign " + hp.CurlSign.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ")");
                rep.Ok = found > 0;
                if (!rep.Ok) rep.Notes.Add("the Mixamo pattern finds no bones on this skeleton — switch Rig to Auto");
                return rep;
            }
            return GetHandRig(skel, hp).Report;
        }

        /// <summary>What a LookAtIk resolves to on the entity's skeleton.</summary>
        public RigReport DescribeLookAt(ECS.GameEntity entity, ECS.Components.Animation.LookAtIk la)
        {
            var skel = SkeletonOf(entity);
            return skel == null || la == null ? NoSkeleton() : GetLookRig(skel, la).Report;
        }

        /// <summary>What a FootIk resolves to on the entity's skeleton.</summary>
        public RigReport DescribeFootIk(ECS.GameEntity entity, ECS.Components.Animation.FootIk fi)
        {
            var skel = SkeletonOf(entity);
            return skel == null || fi == null ? NoSkeleton() : GetFootRig(skel, fi).Report;
        }

        private static RigReport NoSkeleton()
        {
            var r = new RigReport();
            r.Notes.Add("no skinned model on this entity (or its children) — the component needs an animated skeleton");
            return r;
        }

        /// <summary>The skeleton an Animator owner animates (its own model or the first skinned descendant).</summary>
        /// <summary>The model-space node worlds of the entity's last evaluated pose (null before its Animator first
        /// evaluated). Same space as the skinning palette: multiply by the skinned mesh entity's world matrix.</summary>
        public Matrix4x4[] CurrentNodeWorlds(ECS.GameEntity entity)
            => entity != null && _states.TryGetValue(entity.Id, out var st) ? st.NodeWorlds : null;

        public SkeletonDef SkeletonOf(ECS.GameEntity entity)
        {
            if (entity == null) return null;
            if (_states.TryGetValue(entity.Id, out var st) && st.Skeleton != null) return st.Skeleton;
            return ResolveSkeletonFor(entity);
        }

        /// <summary>
        /// The actual two-bone solve — MODEL space, engine row-vector convention (worlds[i] = local *
        /// worlds[parent], so a WORLD-frame rotation delta D right-multiplies: local' = local * P * D * P?¹,
        /// i.e. q_local' = qp?¹ * qd * qp * q_local with q_world = qp * q_local).
        /// Steps: (1) open/close the elbow via law of cosines around the CURRENT bend axis (keeps the
        /// animation's natural bend plane), (2) swing the root so the tip lands on the root→target ray,
        /// (3) optional pole swing around root→target, (4) optional tip re-orientation to the grip.
        /// Mutates r[] only; returns true when the caller must recompose worlds.
        /// </summary>
        private static bool SolveTwoBoneIk(SkeletonDef skel, Vector3[] t, Quaternion[] r, Vector3[] s,
            Matrix4x4[] worlds, IkChainRuntime ik)
        {
            float w = ik.Weight;
            if (w <= 0.001f) return false;
            int n = skel.Nodes.Length;
            if (ik.Tip >= n || ik.Mid >= n || ik.Root >= n || ik.Target >= n) return false;

            // Target = grip offset composed in the target bone's RAW local frame (model space) — the
            // capture flow authors the offset in the same frame, so conventions cancel out.
            Matrix4x4 offM = Matrix4x4.CreateFromQuaternion(ik.OffsetRot);
            offM.Translation = ik.OffsetPos;
            // Auto-grip: the natural captured grip is the BASE (tracks the target bone), the authored
            // offset fine-tunes on top. Without auto-grip the target is the raw bone + authored offset.
            Matrix4x4 baseTarget = (ik.AutoGrip && ik.HasCapturedGrip)
                ? ik.CapturedGrip * worlds[ik.Target]
                : worlds[ik.Target];
            Matrix4x4 targetM = ik.HasWorldTarget ? ik.WorldTargetModel : offM * baseTarget;
            Vector3 target = targetM.Translation;

            Vector3 a = worlds[ik.Root].Translation;
            Vector3 b = worlds[ik.Mid].Translation;
            Vector3 c = worlds[ik.Tip].Translation;
            if (_ikDebug && ik.HasWorldTarget && _ikDebugCount < 6)
            {
                _ikDebugCount++;
                Services.ConsoleService.Instance?.Log("[IK] solve root=" + a + " mid=" + b + " tip=" + c + " target=" + target + " w=" + w + " autoGrip=" + ik.AutoGrip);
            }

            float l1 = (b - a).Length(), l2 = (c - b).Length();
            if (l1 < 1e-5f || l2 < 1e-5f) return false;
            Vector3 at = target - a;
            float dist = at.Length();
            if (dist < 1e-6f) return false;
            float maxReach = (l1 + l2) * 0.9999f;
            float minReach = Math.Abs(l1 - l2) * 1.0001f + 1e-4f;
            float reach = Math.Max(minReach, Math.Min(maxReach, dist));

            // (1) elbow angle via law of cosines. A right-hand rotation of v around cross(u,v) moves v AWAY
            // from u (opens the joint), so closing the joint from the current to the wanted interior angle is
            // the delta (angWant - angCur). (The old (angCur - angWant) opened the joint instead - invisible
            // with auto-grip targets, where the delta is ~0, but it left script IK targets unreachable.)
            Vector3 u = Vector3.Normalize(a - b);
            Vector3 v = Vector3.Normalize(c - b);
            float cosCur = ClampF(Vector3.Dot(u, v), -1f, 1f);
            float cosWant = ClampF((l1 * l1 + l2 * l2 - reach * reach) / (2f * l1 * l2), -1f, 1f);
            float angCur = (float)Math.Acos(cosCur);
            float angWant = (float)Math.Acos(cosWant);
            Vector3 bendAxis = Vector3.Cross(u, v);
            if (bendAxis.LengthSquared() < 1e-8f)
            {
                // Straight limb — synthesize a bend axis perpendicular to it (prefer facing the target).
                bendAxis = Vector3.Cross(c - a, at);
                if (bendAxis.LengthSquared() < 1e-8f) bendAxis = Vector3.Cross(c - a, Vector3.UnitY);
                if (bendAxis.LengthSquared() < 1e-8f) bendAxis = Vector3.UnitX;
            }
            bendAxis = Vector3.Normalize(bendAxis);
            float dElbow = (angWant - angCur) * w;
            if (Math.Abs(dElbow) > 1e-5f)
            {
                ApplyWorldRotationDelta(skel, r, worlds, ik.Mid, Quaternion.CreateFromAxisAngle(bendAxis, dElbow));
                worlds = ComposeWorlds(skel, t, r, s);
            }

            // (2) root swing: rotate the whole limb so the tip lands on the root->target ray.
            c = worlds[ik.Tip].Translation;
            Vector3 v1 = c - a, v2 = target - a;
            if (v1.LengthSquared() > 1e-10f && v2.LengthSquared() > 1e-10f)
            {
                v1 = Vector3.Normalize(v1);
                v2 = Vector3.Normalize(v2);
                Vector3 ax = Vector3.Cross(v1, v2);
                float d = ClampF(Vector3.Dot(v1, v2), -1f, 1f);
                if (ax.LengthSquared() > 1e-10f)
                {
                    float ang = (float)Math.Atan2(ax.Length(), d) * w;
                    if (Math.Abs(ang) > 1e-5f)
                    {
                        ApplyWorldRotationDelta(skel, r, worlds, ik.Root, Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax), ang));
                        worlds = ComposeWorlds(skel, t, r, s);
                    }
                }
            }

            // (3) pole: swing the elbow around root->target (tip stays planted).
            if (Math.Abs(ik.PoleAngleDeg) > 0.01f && at.LengthSquared() > 1e-10f)
            {
                float ang = ik.PoleAngleDeg * (float)(Math.PI / 180.0) * w;
                ApplyWorldRotationDelta(skel, r, worlds, ik.Root, Quaternion.CreateFromAxisAngle(Vector3.Normalize(at), ang));
                worlds = ComposeWorlds(skel, t, r, s);
            }

            // (4) tip orientation: take the grip rotation (q_world = qp * q_local -> q_local = qp?¹ * qT).
            if (ik.ApplyTipRotation && (!ik.HasWorldTarget || ik.WorldTargetHasRot))
            {
                Quaternion qT = Quaternion.CreateFromRotationMatrix(NormalizeBasis(targetM));
                int par = skel.Nodes[ik.Tip].Parent;
                Quaternion qp = par >= 0
                    ? Quaternion.CreateFromRotationMatrix(NormalizeBasis(worlds[par]))
                    : Quaternion.Identity;
                Quaternion want = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Inverse(qp), qT));
                r[ik.Tip] = Quaternion.Slerp(r[ik.Tip], want, w);
            }
            return true;
        }

        /// <summary>Apply a WORLD-frame rotation delta to one node's LOCAL rotation:
        /// q_local' = qp?¹ * qd * qp * q_local (derivation in SolveTwoBoneIk's summary).</summary>
        private static void ApplyWorldRotationDelta(SkeletonDef skel, Quaternion[] r, Matrix4x4[] worlds,
            int node, Quaternion worldDelta)
        {
            int par = skel.Nodes[node].Parent;
            Quaternion qp = par >= 0
                ? Quaternion.CreateFromRotationMatrix(NormalizeBasis(worlds[par]))
                : Quaternion.Identity;
            r[node] = Quaternion.Normalize(Quaternion.Inverse(qp) * worldDelta * qp * r[node]);
        }

        /// <summary>Orthonormalized rotation part of a (possibly scaled) matrix — for quaternion extraction.</summary>
        private static Matrix4x4 NormalizeBasis(Matrix4x4 m)
        {
            var r0 = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
            var r1 = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
            var r2 = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
            return new Matrix4x4(
                r0.X, r0.Y, r0.Z, 0f,
                r1.X, r1.Y, r1.Z, 0f,
                r2.X, r2.Y, r2.Z, 0f,
                0f, 0f, 0f, 1f);
        }

        private static float ClampF(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>NaN/Inf checks for the IK guard — one non-finite value in a palette hides the whole mesh.</summary>
        private static readonly bool _ikDebug = Environment.GetEnvironmentVariable("VORTEX_IK_DEBUG") == "1";
        private static int _ikDebugCount;
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool IsFinite(Quaternion q) => IsFinite(q.X) && IsFinite(q.Y) && IsFinite(q.Z) && IsFinite(q.W);
        private static bool IsFinite(Matrix4x4 m) =>
            IsFinite(m.M11) && IsFinite(m.M12) && IsFinite(m.M13) && IsFinite(m.M14) &&
            IsFinite(m.M21) && IsFinite(m.M22) && IsFinite(m.M23) && IsFinite(m.M24) &&
            IsFinite(m.M31) && IsFinite(m.M32) && IsFinite(m.M33) && IsFinite(m.M34) &&
            IsFinite(m.M41) && IsFinite(m.M42) && IsFinite(m.M43) && IsFinite(m.M44);

        /// <summary>Per-node 0/1 mask from a spec: comma-separated bone names, '+' suffix = include all
        /// descendants ("Spine1+", "Head,Neck+"). Unknown names are ignored (mask stays partial).</summary>
        public static float[] BuildMask(SkeletonDef skel, string spec)
        {
            int n = skel.Nodes.Length;
            var mask = new float[n];
            if (string.IsNullOrEmpty(spec)) return mask;

            var roots = new List<int>();          // '+' entries: include descendants
            foreach (var raw in spec.Split(','))
            {
                var name = raw.Trim();
                if (name.Length == 0) continue;
                bool children = name.EndsWith("+", StringComparison.Ordinal);
                if (children) name = name.Substring(0, name.Length - 1).TrimEnd();
                int idx = skel.FindNode(name);
                if (idx < 0) continue;
                mask[idx] = 1f;
                if (children) roots.Add(idx);
            }
            if (roots.Count > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    if (mask[i] > 0f) continue;
                    int p = skel.Nodes[i].Parent;
                    while (p >= 0)
                    {
                        if (roots.Contains(p)) { mask[i] = 1f; break; }
                        p = skel.Nodes[p].Parent;
                    }
                }
            }
            return mask;
        }

        public void SetSpeed(ECS.GameEntity entity, float speed)
        {
            if (entity != null && _states.TryGetValue(entity.Id, out var s)) s.Speed = speed;
        }

        public bool IsPlaying(ECS.GameEntity entity, string nameOrPath = null)
        {
            if (entity == null || !_states.TryGetValue(entity.Id, out var s) || !s.Playing || s.Clip == null) return false;
            if (string.IsNullOrEmpty(nameOrPath)) return true;
            // Match by clip name or by the Animator's clip-table entry name (both are what scripts pass).
            if (string.Equals(s.Clip.Name, nameOrPath, StringComparison.OrdinalIgnoreCase)) return true;
            var animator = entity.GetComponent<ECS.Components.Animation.Animator>();
            string path = animator?.ResolveClipPath(nameOrPath);
            return path != null && GetClip(path) == s.Clip;
        }

        public float GetTime(ECS.GameEntity entity)
            => entity != null && _states.TryGetValue(entity.Id, out var s) ? s.Time : 0f;

        // ------------------------------------------------------------------ palette access (render submit)

        /// <summary>
        /// The bone palette to render this entity's skinned mesh with: the animated pose while playing,
        /// else the model's bind pose. False when the model has no skeleton (render rigid as before).
        /// </summary>
        public bool TryGetPalette(ECS.GameEntity entity, string meshPath, out float[] palette, out int boneCount)
        {
            palette = null; boneCount = 0;

            SkeletonDef skeleton = null;
            if (entity != null && _states.TryGetValue(entity.Id, out var state))
            {
                skeleton = state.Skeleton;
                if (state.Palette != null) { palette = state.Palette; boneCount = skeleton.Bones.Length; return true; }
            }
            skeleton = skeleton ?? GetSkeleton(meshPath);
            if (skeleton == null || !skeleton.IsValid) return false;

            palette = skeleton.BindPosePalette();
            boneCount = skeleton.Bones.Length;
            return true;
        }

        // ------------------------------------------------------------------ evaluation core (Keyframe Editor reuses)

        private float[] EvaluateStatePalette(AnimatorState state)
        {
            var skel = state.Skeleton;
            int n = skel.Nodes.Length;
            var t = new Vector3[n]; var r = new Quaternion[n]; var s = new Vector3[n];
            EvaluateLocals(skel, state.Clip, state.TrackNodes, state.Time, t, r, s);
            PinRoot(state, skel, t);   // #113: the entity carries the clip's travel, the pose stays in place

            if (state.FadeDuration > 0f && state.FadeT != null)
            {
                float w = Math.Min(state.FadeElapsed / state.FadeDuration, 1f);   // 0 = old pose, 1 = new clip
                for (int i = 0; i < n; i++)
                {
                    t[i] = Vector3.Lerp(state.FadeT[i], t[i], w);
                    r[i] = Quaternion.Slerp(state.FadeR[i], r[i], w);
                    s[i] = Vector3.Lerp(state.FadeS[i], s[i], w);
                }
            }

            // Bone-masked override layers (#173): blend each layer's LOCAL pose over the base wherever
            // its mask includes the node — BEFORE hierarchy multiplication, so a masked spine rotation
            // carries the arms naturally. Layers composite lowest index first.
            if (state.Layers != null)
            {
                for (int li = 0; li < state.Layers.Count; li++)
                {
                    var layer = state.Layers[li];
                    if (!layer.Playing || layer.Clip == null || layer.Weight <= 0f || layer.Mask == null) continue;

                    var lt = new Vector3[n]; var lr = new Quaternion[n]; var ls = new Vector3[n];
                    EvaluateLocals(skel, layer.Clip, layer.TrackNodes, layer.Time, lt, lr, ls);

                    if (layer.FadeDuration > 0f && layer.FadeT != null)
                    {
                        float fw = Math.Min(layer.FadeElapsed / layer.FadeDuration, 1f);
                        for (int i = 0; i < n; i++)
                        {
                            lt[i] = Vector3.Lerp(layer.FadeT[i], lt[i], fw);
                            lr[i] = Quaternion.Slerp(layer.FadeR[i], lr[i], fw);
                            ls[i] = Vector3.Lerp(layer.FadeS[i], ls[i], fw);
                        }
                    }

                    for (int i = 0; i < n; i++)
                    {
                        float w = layer.Weight * layer.Mask[i];
                        if (w <= 0f) continue;
                        t[i] = Vector3.Lerp(t[i], lt[i], w);
                        r[i] = Quaternion.Slerp(r[i], lr[i], w);
                        s[i] = Vector3.Lerp(s[i], ls[i], w);
                    }
                }
            }

            // Runtime additive bone rotations (#178: procedural aim-offset / lean / recoil on real bones):
            // compose the script-supplied LOCAL delta onto the animated local rotation BEFORE hierarchy
            // multiplication so it carries every descendant — a spine/chest pitch rotates chest+arms+weapon as
            // ONE rigid unit, which is exactly what keeps the gun locked in the hands at any aim angle.
            if (state.BoneAdditive != null && state.BoneAdditive.Count > 0)
            {
                foreach (var kv in state.BoneAdditive)
                {
                    int i = kv.Key;
                    if (i < 0 || i >= n) continue;
                    r[i] = Quaternion.Normalize(kv.Value * r[i]);   // delta in the bone's local frame
                }
            }

            // Editor-authored finger curls (HandPose components): the same local additive, composed after the
            // script deltas so a component and a script can both touch a hand without fighting the base pose.
            if (state.ComponentAdditive != null && state.ComponentAdditive.Count > 0)
            {
                foreach (var kv in state.ComponentAdditive)
                {
                    int i = kv.Key;
                    if (i < 0 || i >= n) continue;
                    r[i] = Quaternion.Normalize(kv.Value * r[i]);
                }
            }

            // Runtime bone scale: values >= 0.01 scale the POSE (children follow, classic behaviour).
            // The HIDE case (0 = collapse the limb, FP legs/head) is NOT applied here any more — a
            // zero pose-scale degenerates the node worlds that the IK solver and bone sockets read
            // (the hide+IK combination rendered the whole mesh invisible). Hidden bones are zeroed on
            // the FINAL palette instead (below), which only the GPU skinning sees.
            bool anyHidden = state.BoneHiddenSelf != null && state.BoneHiddenSelf.Count > 0;
            if (state.BoneScale != null && state.BoneScale.Count > 0)
            {
                foreach (var kv in state.BoneScale)
                {
                    int i = kv.Key;
                    if (i < 0 || i >= n) continue;
                    if (kv.Value < 0.01f) { anyHidden = true; continue; }   // hide -> palette pass below
                    s[i] = new Vector3(s[i].X * kv.Value, s[i].Y * kv.Value, s[i].Z * kv.Value);
                }
            }

            // Retain the node worlds alongside the palette: bone sockets and GetBoneWorldTransform read
            // the EXACT pose the skinning used — no second clip sample, no drift.
            var worlds = ComposeWorlds(skel, t, r, s);

            // Rig-aware procedural layers (#147), in dependency order: the feet plant on the ground (legs + pelvis),
            // the head/neck/spine turn to the look target, THEN the arm IK below solves the hands on that final torso.
            // Foot IK only while the clock runs (play): it needs the gameplay collision world.
            if (state.FootCfg != null && state.FootRig != null && state.StepCount > 0)
                worlds = GuardedLayer(skel, t, r, s, worlds, "Foot IK", w => ApplyFootIk(state, skel, t, r, s, w));
            if (state.LookCfg != null && state.LookRig != null)
                worlds = GuardedLayer(skel, t, r, s, worlds, "Look-At IK", w => ApplyLookAt(state, skel, t, r, s, w));

            // Runtime two-bone IK (#179): pull limb chains to intra-skeleton targets (support hand ->
            // weapon grip). Runs LAST so it corrects the final blended pose; each solve edits local
            // rotations, so recompose afterwards — sockets and skinning then see the IK'd pose.
            if (state.IkChains != null)
            {
                // Auto-grip: capture each chain's natural tip-relative-to-target from the CLEAN pose
                // (before any IK moves it), then hold it. This locks the support hand to wherever the
                // idle/hold animation put it relative to the weapon hand. Captured only once a clip has
                // actually STEPPED (state.Time > 0) — the very first evaluation can still be the bind
                // T-pose (hands ~1.4 m apart), and freezing THAT as the grip rips the arm to a far
                // phantom target forever after (the "stretched spike arm" / invisible-NaN-rig bug).
                bool poseIsLive = state.Time > 0.0001f;
                for (int ci = 0; ci < state.IkChains.Count; ci++)
                {
                    var ch = state.IkChains[ci];
                    if (!ch.AutoGrip) continue;
                    if (state.IkCapturedGrips == null) state.IkCapturedGrips = new Dictionary<int, Matrix4x4>();
                    Matrix4x4 grip;
                    if (!state.IkCapturedGrips.TryGetValue(ch.Tip, out grip))
                    {
                        if (poseIsLive && ch.Tip < worlds.Length && ch.Target < worlds.Length &&
                            Matrix4x4.Invert(worlds[ch.Target], out var invTgt))
                        {
                            grip = worlds[ch.Tip] * invTgt;   // tip in the target bone's frame
                            if (IsFinite(grip)) state.IkCapturedGrips[ch.Tip] = grip;
                        }
                    }
                    if (state.IkCapturedGrips.TryGetValue(ch.Tip, out grip))
                    {
                        ch.CapturedGrip = grip;
                        ch.HasCapturedGrip = true;
                    }
                }

                // NaN guard: a degenerate solve (zero-scale bones, gimbal edge, bad capture) must NEVER
                // reach the GPU — one NaN in the palette makes the WHOLE mesh invisible. Snapshot the
                // local rotations; if any touched chain comes out non-finite, roll back to the un-IK'd pose.
                Quaternion[] rBackup = null;
                for (int ci = 0; ci < state.IkChains.Count; ci++)
                {
                    var ch = state.IkChains[ci];
                    if (rBackup == null) { rBackup = new Quaternion[r.Length]; Array.Copy(r, rBackup, r.Length); }
                    bool solved = SolveTwoBoneIk(skel, t, r, s, worlds, ch);
                    if (solved) worlds = ComposeWorlds(skel, t, r, s);
                    if (_ikDebug && ch.HasWorldTarget && _ikDebugCount <= 6)
                        Services.ConsoleService.Instance?.Log("[IK] after solve=" + solved + " tip=" + worlds[ch.Tip].Translation + " target=" + ch.WorldTargetModel.Translation + " dist=" + (worlds[ch.Tip].Translation - ch.WorldTargetModel.Translation).Length());
                    bool bad = ch.Tip < worlds.Length && (!IsFinite(worlds[ch.Tip]) || !IsFinite(r[ch.Tip]) ||
                               (ch.Mid < r.Length && !IsFinite(r[ch.Mid])) || (ch.Root < r.Length && !IsFinite(r[ch.Root])));
                    if (bad)
                    {
                        Array.Copy(rBackup, r, r.Length);
                        worlds = ComposeWorlds(skel, t, r, s);
                        state.IkCapturedGrips?.Remove(ch.Tip);   // a poisoned capture recaptures next live frame
                        Editor.Core.Services.ConsoleService.Instance?.LogWarning(
                            "TwoBoneIk: non-finite solve on '" + skel.Nodes[ch.Tip].Name + "' — IK skipped this frame.");
                    }
                }
            }

            // Ragdoll (#104): an active ragdoll replaces the pose with the simulated one (sockets and skinning both read
            // it); a character that can ragdoll records its pose here so an activation inherits its motion.
            worlds = Services.Physics.RagdollService.ProcessPose(state.Entity, skel, worlds);

            state.NodeWorlds = worlds;
            float[] pal = skel.FlattenPalette(worlds);

            // Hidden bones (SetBoneScaleOverride(bone, 0)): zero the PALETTE entries of the bone and its
            // whole subtree — the skinned vertices weighted to them collapse (limb invisible) while the
            // pose/worlds stay healthy for IK, sockets and bone queries.
            if (anyHidden)
            {
                for (int bi = 0; bi < skel.Bones.Length; bi++)
                {
                    int node = skel.Bones[bi].NodeIndex;
                    bool hidden = state.BoneHiddenSelf != null && state.BoneHiddenSelf.Contains(node);
                    if (!hidden && state.BoneScale != null)
                        for (int p = node; p >= 0; p = skel.Nodes[p].Parent)
                        {
                            float sv;
                            if (state.BoneScale.TryGetValue(p, out sv) && sv < 0.01f) { hidden = true; break; }
                        }
                    if (hidden)
                    {
                        // Collapse the bone's vertices onto its own POSITION: zero only the 3x3 (rows 1-3)
                        // and KEEP the translation row. All-zero would give w = 0 (raster UB, whole draw can
                        // vanish); zero+identity-w collapses onto the world ORIGIN, which drags mixed-weight
                        // vertices into cross-screen streaks. Collapsing onto the bone keeps them in place.
                        for (int f = 0; f < 12; f++) pal[bi * 16 + f] = 0f;
                    }
                }
            }
            return pal;
        }

        // ------------------------------------------------------------------ look-at + foot IK solvers (#147)

        private static bool _layerWarned;

        /// <summary>Run one procedural layer with a rollback guard: a throw or a non-finite rotation/translation restores
        /// the pose from before the layer (one NaN in a palette hides the whole mesh).</summary>
        private static Matrix4x4[] GuardedLayer(SkeletonDef skel, Vector3[] t, Quaternion[] r, Vector3[] s, Matrix4x4[] worlds,
            string what, Func<Matrix4x4[], Matrix4x4[]> layer)
        {
            var tb = (Vector3[])t.Clone();
            var rb = (Quaternion[])r.Clone();
            Matrix4x4[] result = null;
            string error = null;
            try { result = layer(worlds) ?? worlds; }
            catch (Exception ex) { error = ex.Message; }
            bool bad = result == null;
            if (!bad)
                for (int i = 0; i < r.Length; i++)
                    if (!IsFinite(r[i]) || !IsFinite(t[i].X) || !IsFinite(t[i].Y) || !IsFinite(t[i].Z)) { bad = true; break; }
            if (!bad) return result;
            Array.Copy(tb, t, t.Length);
            Array.Copy(rb, r, r.Length);
            if (!_layerWarned)
            {
                _layerWarned = true;
                Services.ConsoleService.Instance?.LogWarning(what + ": " + (error ?? "non-finite solve") + " — layer skipped this frame.");
            }
            return ComposeWorlds(skel, t, r, s);
        }

        private const float D2Rf = (float)(Math.PI / 180.0);

        private static Quaternion QuatOfWorld(Matrix4x4 m) => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(NormalizeBasis(m)));

        private static float WrapPi(float a)
        {
            const float TwoPi = (float)(Math.PI * 2.0);
            while (a > Math.PI) a -= TwoPi;
            while (a < -Math.PI) a += TwoPi;
            return a;
        }

        /// <summary>Rotation that turns the direction (yaw0, pitch0) into (yaw1, pitch1) in the frame F (forward), L (left),
        /// U (up): pitch back to the horizon, yaw around U, pitch up again — never adds roll to the head.</summary>
        private static Quaternion YawPitchDelta(Vector3 F, Vector3 L, Vector3 U, float yaw0, float pitch0, float yaw1, float pitch1)
        {
            Vector3 h0 = F * (float)Math.Cos(yaw0) + L * (float)Math.Sin(yaw0);
            Vector3 h1 = F * (float)Math.Cos(yaw1) + L * (float)Math.Sin(yaw1);
            Vector3 a0 = Vector3.Cross(h0, U), a1 = Vector3.Cross(h1, U);
            if (a0.LengthSquared() < 1e-10f || a1.LengthSquared() < 1e-10f) return Quaternion.Identity;
            var qa = Quaternion.CreateFromAxisAngle(Vector3.Normalize(a0), -pitch0);
            var qb = Quaternion.CreateFromAxisAngle(U, yaw1 - yaw0);
            var qc = Quaternion.CreateFromAxisAngle(Vector3.Normalize(a1), pitch1);
            return Quaternion.Normalize(qc * qb * qa);   // qa first, then qb, then qc
        }

        /// <summary>
        /// Look-at: the target direction is measured against the TORSO (the parent of the lowest turning bone) as yaw/pitch,
        /// clamped to the limits and smoothed; the rotation from the animated head direction to it (blended by the weight)
        /// is shared out over spine → neck → head, each bone turning its share around the same axis, so the head ends up
        /// facing the target while the animation's own motion stays underneath.
        /// </summary>
        private Matrix4x4[] ApplyLookAt(AnimatorState state, SkeletonDef skel, Vector3[] t, Quaternion[] r, Vector3[] s, Matrix4x4[] worlds)
        {
            var cfg = state.LookCfg; var rig = state.LookRig;
            int n = skel.Nodes.Length;
            if (rig.Head < 0 || rig.Head >= n || rig.Chain.Length == 0) return worlds;

            Vector3 targetModel;
            bool have = TryLookTargetModel(state, cfg, out targetModel);
            float baseW = state.LookWeightOverride >= 0f ? state.LookWeightOverride : cfg.Weight;

            Quaternion qRef = rig.Ref >= 0 && rig.Ref < n ? QuatOfWorld(worlds[rig.Ref]) : Quaternion.Identity;
            Vector3 F = Vector3.Transform(rig.FwdRefLocal, qRef), U = Vector3.Transform(rig.UpRefLocal, qRef);
            Vector3 L = Vector3.Cross(U, F);
            if (L.LengthSquared() < 1e-8f) return worlds;
            L = Vector3.Normalize(L); U = Vector3.Normalize(U); F = Vector3.Normalize(Vector3.Cross(L, U));

            Vector3 headPos = worlds[rig.Head].Translation;
            float yawT = 0f, pitchT = 0f;
            if (have)
            {
                Vector3 d = targetModel - headPos;
                if (d.LengthSquared() > 1e-10f)
                {
                    d = Vector3.Normalize(d);
                    yawT = (float)Math.Atan2(Vector3.Dot(d, L), Vector3.Dot(d, F));
                    pitchT = (float)Math.Asin(ClampF(Vector3.Dot(d, U), -1f, 1f));
                    float my = cfg.MaxYaw * D2Rf, mp = cfg.MaxPitch * D2Rf;
                    yawT = ClampF(yawT, -my, my);
                    pitchT = ClampF(pitchT, -mp, mp);
                }
                else have = false;
            }
            float wantW = have ? baseW : 0f;

            var sm = state.LookSmooth ?? (state.LookSmooth = new LookSmoothState());
            if (!sm.Init || state.SnapSmoothing)
            {
                if (have || !sm.Init) { sm.Yaw = yawT; sm.Pitch = pitchT; }
                sm.W = wantW;
                sm.Init = true;
            }
            else if (state.SmoothDt > 0f)
            {
                float a = cfg.Smoothing <= 1e-4f ? 1f : 1f - (float)Math.Exp(-state.SmoothDt / cfg.Smoothing);
                if (have) { sm.Yaw += (yawT - sm.Yaw) * a; sm.Pitch += (pitchT - sm.Pitch) * a; }
                sm.W += (wantW - sm.W) * a;
            }
            float w = sm.W;
            if (w <= 0.001f) return worlds;

            Vector3 fh = Vector3.Transform(rig.FwdHeadLocal, QuatOfWorld(worlds[rig.Head]));
            float yaw0 = (float)Math.Atan2(Vector3.Dot(fh, L), Vector3.Dot(fh, F));
            float pitch0 = (float)Math.Asin(ClampF(Vector3.Dot(fh, U), -1f, 1f));
            float yaw1 = yaw0 + WrapPi(sm.Yaw - yaw0) * w;
            float pitch1 = pitch0 + (sm.Pitch - pitch0) * w;
            var q = YawPitchDelta(F, L, U, yaw0, pitch0, yaw1, pitch1);
            if (!IsFinite(q)) return worlds;
            for (int k = 0; k < rig.Chain.Length; k++)
            {
                int b = rig.Chain[k];
                if (b < 0 || b >= n || rig.Share[k] <= 0f) continue;
                ApplyWorldRotationDelta(skel, r, worlds, b, Quaternion.Slerp(Quaternion.Identity, q, rig.Share[k]));
                worlds = ComposeWorlds(skel, t, r, s);
            }
            return worlds;
        }

        /// <summary>The look target in the rig's MODEL space: a script point, a script entity, or the component's target
        /// entity (by name or id). Entities with a skeleton are looked at on their head bone.</summary>
        private bool TryLookTargetModel(AnimatorState state, ECS.Components.Animation.LookAtIk cfg, out Vector3 model)
        {
            model = Vector3.Zero;
            Vector3 world;
            if (state.HasLookPoint) world = state.LookPoint;
            else
            {
                var target = state.LookEntity;
                if (target == null && !string.IsNullOrWhiteSpace(cfg.TargetEntity)) target = ResolveEntityRef(state, cfg.TargetEntity);
                if (target == null || target == state.Entity) return false;
                world = EntityLookPoint(target) + new Vector3(cfg.TargetOffset.X, cfg.TargetOffset.Y, cfg.TargetOffset.Z);
            }
            var meshEntity = FindSkinnedMeshEntity(state.Entity) ?? state.Entity;
            Matrix4x4 inv;
            if (!Matrix4x4.Invert(BoneSocketService.EntityWorld(meshEntity), out inv)) return false;
            model = Vector3.Transform(world, inv);
            return IsFinite(model.X) && IsFinite(model.Y) && IsFinite(model.Z);
        }

        /// <summary>World point to look at on an entity: its head bone when it is an animated character, else its origin.</summary>
        private Vector3 EntityLookPoint(ECS.GameEntity target)
        {
            SkeletonDef tsk; Matrix4x4[] tw;
            var owner = target;
            // The Animator may sit on the entity or an ancestor (submesh children); look for it upward and downward.
            if (owner.GetComponent<ECS.Components.Animation.Animator>() == null)
            {
                for (var p = target.Parent; p != null; p = p.Parent)
                    if (p.GetComponent<ECS.Components.Animation.Animator>() != null) { owner = p; break; }
            }
            if (owner.GetComponent<ECS.Components.Animation.Animator>() != null && TryGetNodeWorlds(owner, out tsk, out tw))
            {
                int head = RigMap.HeadOf(RigMap.Info(tsk));
                if (head >= 0 && head < tw.Length)
                {
                    var meshEntity = FindSkinnedMeshEntity(owner) ?? owner;
                    return Vector3.Transform(tw[head].Translation, BoneSocketService.EntityWorld(meshEntity));
                }
            }
            return BoneSocketService.EntityWorld(target).Translation;
        }

        /// <summary>Resolve a component's entity reference (id or name) in the entity's scene; cached per state.</summary>
        private ECS.GameEntity ResolveEntityRef(AnimatorState state, string reference)
        {
            if (state.LookRefName == reference && state.LookRefEntity != null && --state.LookRefTtl > 0) return state.LookRefEntity;
            state.LookRefName = reference;
            state.LookRefTtl = 60;
            state.LookRefEntity = null;
            var root = state.Entity;
            while (root != null && root.Parent != null) root = root.Parent;
            var scene = (root != null ? root.Scene : null) ?? state.Entity.Scene ?? Data.ProjectData.Current?.ActiveScene;
            Guid id;
            bool byId = Guid.TryParse(reference.Trim(), out id);
            string name = reference.Trim();
            ECS.GameEntity found = null;
            Action<ECS.GameEntity> walk = null;
            walk = e =>
            {
                if (found != null || e == null) return;
                if (byId ? e.Id == id : string.Equals(e.Name, name, StringComparison.Ordinal)) { found = e; return; }
                if (e.Children != null) foreach (var c in e.Children) walk(c);
            };
            if (scene?.Entities != null) foreach (var e in scene.Entities) { walk(e); if (found != null) break; }
            if (found == null && root != null) walk(root);
            state.LookRefEntity = found;
            return found;
        }

        /// <summary>
        /// Foot IK: per foot a ray straight down through the gameplay collision world (the character's own colliders
        /// skipped) finds the ground; its height relative to the floor the animation assumes under that foot moves the
        /// foot target up/down (keeping the animated lift), the pelvis drops by the lowest (negative) offset so that foot
        /// can reach, each leg is solved with the two-bone solver (the animation's knee plane kept), and a planted foot
        /// tilts to the ground normal. Offsets are smoothed; airborne characters (both feet high) fade it out.
        /// </summary>
        private Matrix4x4[] ApplyFootIk(AnimatorState state, SkeletonDef skel, Vector3[] t, Quaternion[] r, Vector3[] s, Matrix4x4[] worlds)
        {
            var cfg = state.FootCfg; var rig = state.FootRig;
            float w = state.FootWeightOverride >= 0f ? state.FootWeightOverride : cfg.Weight;
            if (w <= 0.001f || !Services.Physics.CollisionService.IsBuilt) return worlds;
            int n = skel.Nodes.Length;
            var meshEntity = FindSkinnedMeshEntity(state.Entity) ?? state.Entity;
            Matrix4x4 meshWorld = BoneSocketService.EntityWorld(meshEntity), inv;
            if (!Matrix4x4.Invert(meshWorld, out inv)) return worlds;
            Vector3 upW = Vector3.UnitY;
            Vector3 upRaw = Vector3.TransformNormal(upW, inv);
            float modelPerMeter = upRaw.Length();
            if (modelPerMeter < 1e-8f) return worlds;
            Vector3 upM = upRaw / modelPerMeter;
            float maxStep = cfg.MaxStep;

            var desired = new float[2]; var normals = new Vector3[2]; var have = new bool[2];
            var ankleW = new Vector3[2]; var lift = new float[2];
            float minLift = float.MaxValue;
            for (int k = 0; k < 2; k++)
            {
                int foot = rig.Foot[k];
                if (foot < 0 || foot >= n) continue;
                Vector3 aM = worlds[foot].Translation;
                Vector3 aW = Vector3.Transform(aM, meshWorld);
                float fh = cfg.FootHeight >= 0f ? cfg.FootHeight : rig.AnkleHeight[k] / modelPerMeter;
                Vector3 soleM = aM - rig.UpBind * (Vector3.Dot(aM, rig.UpBind) - rig.SoleUp[k]);   // floor under the foot
                float g0 = Vector3.Transform(soleM, meshWorld).Y;
                lift[k] = aW.Y - fh - g0;
                minLift = Math.Min(minLift, lift[k]);
                ankleW[k] = aW;
                Vector3 origin = new Vector3(aW.X, Math.Max(aW.Y, g0 + fh) + cfg.RayHeight, aW.Z);
                float maxDist = origin.Y - (g0 - maxStep);
                Vector3 hp, hn;
                if (GroundRay(state.Entity, origin, maxDist, cfg.GroundLayers, out hp, out hn))
                {
                    desired[k] = ClampF(hp.Y - g0, -maxStep, maxStep);
                    normals[k] = hn;
                    have[k] = true;
                }
            }
            // Airborne (both feet well above their floor): no planting.
            float grounded = minLift == float.MaxValue ? 0f : 1f - ClampF((minLift - 0.1f) / 0.2f, 0f, 1f);

            float pelvisWant = 0f;
            if (cfg.AdjustPelvis)
                for (int k = 0; k < 2; k++) if (have[k]) pelvisWant = Math.Min(pelvisWant, desired[k]);

            var sm = state.FootSmooth ?? (state.FootSmooth = new FootSmoothState());
            float a = (!sm.Init || state.SnapSmoothing) ? 1f
                    : (state.SmoothDt > 0f ? (cfg.Smoothing <= 1e-4f ? 1f : 1f - (float)Math.Exp(-state.SmoothDt / cfg.Smoothing)) : 0f);
            for (int k = 0; k < 2; k++)
            {
                sm.Offset[k] += ((have[k] ? desired[k] : 0f) - sm.Offset[k]) * a;
                Vector3 nt = have[k] ? normals[k] : upW;
                var nn = Vector3.Lerp(sm.Normal[k], nt, a);
                sm.Normal[k] = nn.LengthSquared() > 1e-8f ? Vector3.Normalize(nn) : upW;
            }
            sm.Pelvis += (pelvisWant - sm.Pelvis) * a;
            sm.Init = true;

            float wEff = w * grounded;
            if (wEff <= 0.001f) return worlds;

            // Pelvis down (model-space displacement → the pelvis parent's local frame).
            if (rig.Pelvis >= 0 && rig.Pelvis < n && Math.Abs(sm.Pelvis) > 1e-5f)
            {
                Vector3 deltaM = upM * (sm.Pelvis * wEff * modelPerMeter);
                int pp = skel.Nodes[rig.Pelvis].Parent;
                Vector3 local = deltaM;
                Matrix4x4 pinv;
                if (pp >= 0 && Matrix4x4.Invert(worlds[pp], out pinv)) local = Vector3.TransformNormal(deltaM, pinv);
                t[rig.Pelvis] += local;
                worlds = ComposeWorlds(skel, t, r, s);
            }

            for (int k = 0; k < 2; k++)
            {
                int foot = rig.Foot[k], knee = rig.Knee[k], hip = rig.Hip[k];
                if (foot < 0 || knee < 0 || hip < 0 || foot >= n || knee >= n || hip >= n) continue;
                Vector3 targetW = ankleW[k] + upW * sm.Offset[k];   // the animated ankle, moved by the ground under it
                var ch = new IkChainRuntime
                {
                    Tip = foot, Mid = knee, Root = hip, Target = foot,
                    HasWorldTarget = true, WorldTargetHasRot = false,
                    WorldTargetModel = Matrix4x4.CreateTranslation(Vector3.Transform(targetW, inv)),
                    Weight = wEff, ApplyTipRotation = false, AutoGrip = false,
                };
                if (SolveTwoBoneIk(skel, t, r, s, worlds, ch)) worlds = ComposeWorlds(skel, t, r, s);

                if (cfg.AlignToGround && have[k])
                {
                    float plant = 1f - ClampF((lift[k] - 0.05f) / 0.2f, 0f, 1f);
                    Vector3 nM = Vector3.TransformNormal(sm.Normal[k], inv);
                    if (plant > 0f && nM.LengthSquared() > 1e-12f)
                    {
                        nM = Vector3.Normalize(nM);
                        Vector3 axis = Vector3.Cross(upM, nM);
                        float ang = (float)Math.Atan2(axis.Length(), Vector3.Dot(upM, nM));
                        ang = Math.Min(ang, cfg.MaxFootAngle * D2Rf) * wEff * plant;
                        if (ang > 1e-4f && axis.LengthSquared() > 1e-12f)
                        {
                            ApplyWorldRotationDelta(skel, r, worlds, foot, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), ang));
                            worlds = ComposeWorlds(skel, t, r, s);
                        }
                    }
                }
            }
            return worlds;
        }

        /// <summary>Ray straight down against the gameplay collision world, skipping the character's own colliders (the
        /// entity itself, its ancestors — e.g. a player capsule — and its descendants).</summary>
        private static bool GroundRay(ECS.GameEntity self, Vector3 origin, float maxDist, int mask, out Vector3 hit, out Vector3 normal)
        {
            hit = origin; normal = Vector3.UnitY;
            var o = new ECS.Vector3(origin.X, origin.Y, origin.Z);
            var down = new ECS.Vector3(0f, -1f, 0f);
            float remaining = maxDist;
            for (int i = 0; i < 6 && remaining > 1e-3f; i++)
            {
                ECS.Vector3 hp, hn; ECS.GameEntity he; float hd;
                if (!Services.Physics.CollisionService.Raycast(o, down, remaining, mask, out hp, out hn, out he, out hd)) return false;
                if (he != null && IsRelated(he, self))
                {
                    float step = hd + 0.02f;
                    o = new ECS.Vector3(o.X, o.Y - step, o.Z);
                    remaining -= step;
                    continue;
                }
                hit = new Vector3(hp.X, hp.Y, hp.Z);
                normal = new Vector3(hn.X, hn.Y, hn.Z);
                if (normal.Y < 0f) normal = -normal;
                return true;
            }
            return false;
        }

        private static bool IsRelated(ECS.GameEntity hit, ECS.GameEntity self)
        {
            for (var p = self; p != null; p = p.Parent) if (p == hit) return true;
            for (var p = hit.Parent; p != null; p = p.Parent) if (p == self) return true;
            return false;
        }

        /// <summary>
        /// This frame's model-space node worlds for an Animator owner (bone sockets / bone queries):
        /// the animated pose while playing, else the cached bind pose. False when no skeleton resolves.
        /// </summary>
        public bool TryGetNodeWorlds(ECS.GameEntity animatorOwner, out SkeletonDef skeleton, out Matrix4x4[] worlds)
        {
            skeleton = null; worlds = null;
            if (animatorOwner == null) return false;

            if (_states.TryGetValue(animatorOwner.Id, out var state) && state.Skeleton != null)
            {
                skeleton = state.Skeleton;
                worlds = state.NodeWorlds;
            }
            if (skeleton == null) skeleton = ResolveSkeletonFor(animatorOwner);
            if (skeleton == null || !skeleton.IsValid) return false;
            if (worlds == null) worlds = skeleton.BindNodeWorldsCached();
            return true;
        }

        /// <summary>
        /// The entity whose skinned mesh actually renders (self or first descendant with a skinned model) —
        /// its world matrix frames the model-space bone worlds, exactly like the skinning draw.
        /// </summary>
        public ECS.GameEntity FindSkinnedMeshEntity(ECS.GameEntity owner)
        {
            if (owner == null) return null;
            var mr = owner.GetComponent<ECS.Components.Rendering.MeshRenderer>();
            if (mr != null)
            {
                var sk = GetSkeleton(mr.MeshPath);
                if (sk != null && sk.IsValid) return owner;
            }
            if (owner.Children != null)
            {
                foreach (var c in owner.Children)
                {
                    var hit = FindSkinnedMeshEntity(c);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        /// <summary>Map clip tracks to skeleton node indices (bone NAMES -> node table).</summary>
        public static int[] ResolveTrackNodes(SkeletonDef skel, VortexAnimClip clip)
        {
            var map = new int[clip.Tracks.Count];
            for (int i = 0; i < clip.Tracks.Count; i++) map[i] = skel.FindNode(clip.Tracks[i].Bone);
            return map;
        }

        /// <summary>
        /// Sample every node's local TRS at `time`: bind pose for untracked nodes, keyed values (with
        /// per-component bind fallback) for tracked ones.
        /// </summary>
        public static void EvaluateLocals(SkeletonDef skel, VortexAnimClip clip, int[] trackNodes, float time,
            Vector3[] outT, Quaternion[] outR, Vector3[] outS)
        {
            for (int i = 0; i < skel.Nodes.Length; i++)
            {
                outT[i] = skel.Nodes[i].BindTranslation;
                outR[i] = skel.Nodes[i].BindRotation;
                outS[i] = skel.Nodes[i].BindScale;
            }
            if (clip == null) return;
            if (trackNodes == null || trackNodes.Length != clip.Tracks.Count) trackNodes = ResolveTrackNodes(skel, clip);

            for (int i = 0; i < clip.Tracks.Count; i++)
            {
                int node = trackNodes[i];
                if (node < 0) continue;
                var track = clip.Tracks[i];
                if (track.Pos != null && track.Pos.Count > 0) outT[node] = SampleVec3(track.Pos, time);
                if (track.Rot != null && track.Rot.Count > 0) outR[node] = SampleQuat(track.Rot, time);
                if (track.Scale != null && track.Scale.Count > 0) outS[node] = SampleVec3(track.Scale, time);
            }
        }

        /// <summary>Compose node worlds from local TRS (row-vector: local * parentWorld; parents precede children).</summary>
        public static Matrix4x4[] ComposeWorlds(SkeletonDef skel, Vector3[] t, Quaternion[] r, Vector3[] s)
        {
            var worlds = new Matrix4x4[skel.Nodes.Length];
            for (int i = 0; i < skel.Nodes.Length; i++)
            {
                Matrix4x4 local = Matrix4x4.CreateScale(s[i])
                                * Matrix4x4.CreateFromQuaternion(r[i])
                                * Matrix4x4.CreateTranslation(t[i]);
                int parent = skel.Nodes[i].Parent;
                worlds[i] = (parent >= 0 && parent < i) ? local * worlds[parent] : local;
            }
            return worlds;
        }

        /// <summary>One-shot pose evaluation (Keyframe Editor preview): clip at `time` -> flattened palette.</summary>
        public static float[] EvaluatePalette(SkeletonDef skel, VortexAnimClip clip, float time)
        {
            int n = skel.Nodes.Length;
            var t = new Vector3[n]; var r = new Quaternion[n]; var s = new Vector3[n];
            EvaluateLocals(skel, clip, null, time, t, r, s);
            return skel.FlattenPalette(ComposeWorlds(skel, t, r, s));
        }

        /// <summary>Node world matrices at `time` (Keyframe Editor bone overlay).</summary>
        public static Matrix4x4[] EvaluateNodeWorlds(SkeletonDef skel, VortexAnimClip clip, float time)
        {
            int n = skel.Nodes.Length;
            var t = new Vector3[n]; var r = new Quaternion[n]; var s = new Vector3[n];
            EvaluateLocals(skel, clip, null, time, t, r, s);
            return ComposeWorlds(skel, t, r, s);
        }

        public static Vector3 SampleVec3(List<AnimKeyVec3> keys, float time)
        {
            int count = keys.Count;
            if (count == 1) return new Vector3(keys[0].X, keys[0].Y, keys[0].Z);
            if (time <= keys[0].T) return new Vector3(keys[0].X, keys[0].Y, keys[0].Z);
            var last = keys[count - 1];
            if (time >= last.T) return new Vector3(last.X, last.Y, last.Z);

            int hi = UpperBound(keys.Count, i => keys[i].T, time);
            var a = keys[hi - 1]; var b = keys[hi];
            float span = b.T - a.T;
            float f = span > 0.00001f ? (time - a.T) / span : 0f;
            return Vector3.Lerp(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), f);
        }

        public static Quaternion SampleQuat(List<AnimKeyQuat> keys, float time)
        {
            int count = keys.Count;
            if (count == 1) return Normalize(keys[0]);
            if (time <= keys[0].T) return Normalize(keys[0]);
            var last = keys[count - 1];
            if (time >= last.T) return Normalize(last);

            int hi = UpperBound(keys.Count, i => keys[i].T, time);
            var a = keys[hi - 1]; var b = keys[hi];
            float span = b.T - a.T;
            float f = span > 0.00001f ? (time - a.T) / span : 0f;
            return Quaternion.Slerp(Normalize(a), Normalize(b), f);
        }

        private static Quaternion Normalize(AnimKeyQuat k)
        {
            var q = new Quaternion(k.X, k.Y, k.Z, k.W);
            float len = q.Length();
            return len > 0.00001f ? Quaternion.Normalize(q) : Quaternion.Identity;
        }

        /// <summary>First index whose key time is &gt; value (keys sorted ascending). Result in [1, count-1].</summary>
        private static int UpperBound(int count, Func<int, float> timeAt, float value)
        {
            int lo = 1, hi = count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (timeAt(mid) <= value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // ------------------------------------------------------------------ import conversion

        /// <summary>
        /// Convert one FBX-embedded clip (the flat float layout from GetModelAnimationData — see
        /// AnimationApi.cpp) into a name-keyed .vanim clip. `nodes` supplies node-index -> bone-name.
        /// </summary>
        public static VortexAnimClip ClipFromModelData(string name, float durationSec, float[] flat,
            DllWrapper.VortexAPI.SkeletonNodeInfo[] nodes)
        {
            if (flat == null || flat.Length < 1 || nodes == null) return null;
            var clip = new VortexAnimClip { Name = name, DurationSec = Math.Max(durationSec, 0.0001f) };

            int p = 0, dropped = 0;
            int channelCount = (int)flat[p++];
            for (int c = 0; c < channelCount && p < flat.Length; c++)
            {
                int nodeIndex = (int)flat[p++];
                int posCount = (int)flat[p++];
                int rotCount = (int)flat[p++];
                int scaleCount = (int)flat[p++];

                string bone = (nodeIndex >= 0 && nodeIndex < nodes.Length) ? nodes[nodeIndex].Name : null;
                var track = bone != null ? new AnimTrack { Bone = bone } : null;
                if (track == null) dropped++;

                for (int k = 0; k < posCount; k++, p += 4)
                    track?.Pos.Add(new AnimKeyVec3 { T = flat[p], X = flat[p + 1], Y = flat[p + 2], Z = flat[p + 3] });
                for (int k = 0; k < rotCount; k++, p += 5)
                    track?.Rot.Add(new AnimKeyQuat { T = flat[p], X = flat[p + 1], Y = flat[p + 2], Z = flat[p + 3], W = flat[p + 4] });
                for (int k = 0; k < scaleCount; k++, p += 4)
                    track?.Scale.Add(new AnimKeyVec3 { T = flat[p], X = flat[p + 1], Y = flat[p + 2], Z = flat[p + 3] });

                if (track != null) clip.Tracks.Add(track);
            }
            // a take with channels for nodes outside the skeleton table used to lose them without a word (#340)
            if (dropped > 0)
                try { Services.ConsoleService.Instance.LogWarning("Animation clip '" + name + "': " + dropped + " of " + channelCount + " channel(s) skipped — their nodes are not in the model's skeleton table"); } catch { }
            return clip;
        }

        /// <summary>
        /// Extract EVERY embedded animation clip from a model file (.glb/.fbx/…) to standalone .vanim files —
        /// the "animation only, no character" export. Because clips bind to skeletons by BONE NAME, each written
        /// .vanim is self-contained and re-usable on any compatible rig. Writes into an "animations" subfolder
        /// next to the model by default (mirroring the importer), name-keyed and de-duplicated. Returns the list
        /// of .vanim paths written (empty if the model has no clips). Pure extract — never touches the scene.
        /// </summary>
        public static System.Collections.Generic.List<string> ExtractClipsFromModel(string modelPathAbsOrRel, string outDir = null)
        {
            var written = new System.Collections.Generic.List<string>();
            string full = ResolveModelPath(modelPathAbsOrRel);   // strips '#submeshN', resolves project-relative, null for primitives
            if (full == null || !System.IO.File.Exists(full)) return written;

            int clipCount = DllWrapper.VortexAPI.GetAnimationCount(full);
            if (clipCount <= 0) return written;

            var nodes = DllWrapper.VortexAPI.GetSkeletonNodes(full);
            string animDir = string.IsNullOrEmpty(outDir)
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full) ?? "", "animations")
                : outDir;
            System.IO.Directory.CreateDirectory(animDir);

            // Model reference stored on each clip (for the Keyframe Editor's skeleton-binding preview only).
            string projectPath = Data.ProjectData.Current?.Path;
            string rel = full;
            if (!string.IsNullOrEmpty(projectPath) && rel.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(projectPath.Length).TrimStart('\\', '/');

            var usedNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < clipCount; c++)
            {
                if (!DllWrapper.VortexAPI.GetAnimationInfo(full, c, out string clipName, out float durationSec)) continue;
                var flat = DllWrapper.VortexAPI.GetAnimationData(full, c);
                var clip = ClipFromModelData(clipName, durationSec, flat, nodes);
                if (clip == null || clip.Tracks.Count == 0)
                {
                    try { Services.ConsoleService.Instance.LogWarning("Animation clip '" + clipName + "' of " + System.IO.Path.GetFileName(full) + " has no usable channels and was not written (#340)"); } catch { }
                    continue;
                }

                clip.Model = rel.Replace('\\', '/');
                string safe = string.Concat((clipName ?? ("Clip" + c)).Split(System.IO.Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(safe)) safe = "Clip" + c;
                string unique = safe; int suffix = 1;
                while (!usedNames.Add(unique)) unique = safe + "_" + suffix++;
                string outPath = System.IO.Path.Combine(animDir, unique + ".vanim");
                if (clip.Save(outPath)) written.Add(outPath);
            }
            return written;
        }

        // ------------------------------------------------------------------ clip auto-fill (editor helper)

        /// <summary>
        /// Fill an Animator's clip table from the model's sibling "animations" folder (the .vanim files
        /// the importer extracts next to the model). Adds one entry per file (Name = file stem, Path =
        /// project-relative with forward slashes), skips names already in the table, and seeds
        /// DefaultClip with the first clip when empty. Pure file scan — no engine calls. True when the
        /// folder holds at least one .vanim.
        /// </summary>
        public static bool TryPopulateClipsFromModel(ECS.Components.Animation.Animator animator, string meshPath)
        {
            if (animator == null) return false;

            string modelFull = ResolveModelPath(meshPath);   // strips '#submeshN', resolves project-relative
            if (modelFull == null) return false;

            string animDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(modelFull) ?? "", "animations");
            if (!System.IO.Directory.Exists(animDir)) return false;

            string[] files;
            try { files = System.IO.Directory.GetFiles(animDir, "*.vanim"); }
            catch { return false; }
            if (files.Length == 0) return false;

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);   // deterministic table + DefaultClip
            var projectPath = Data.ProjectData.Current?.Path;
            foreach (var file in files)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                bool exists = false;
                foreach (var c in animator.Clips)
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                if (exists) continue;

                string rel = file;
                if (!string.IsNullOrEmpty(projectPath) && rel.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(projectPath.Length).TrimStart('\\', '/');
                animator.Clips.Add(new ECS.Components.Animation.AnimatorClipEntry
                {
                    Name = name,
                    Path = rel.Replace('\\', '/')
                });
            }

            if (string.IsNullOrEmpty(animator.DefaultClip) && animator.Clips.Count > 0)
                animator.DefaultClip = animator.Clips[0].Name;
            return true;
        }

        // ------------------------------------------------------------------ path helpers

        /// <summary>Model path (with optional '#submeshN') -> full path usable by loaders. Null for primitives.</summary>
        public static string ResolveModelPath(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath) || meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                return null;
            string actual = meshPath;
            int hash = meshPath.LastIndexOf('#');
            if (hash > 0) actual = meshPath.Substring(0, hash);
            return ResolveAssetPath(actual);
        }

        private static string ResolveAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (System.IO.Path.IsPathRooted(path)) return path;
            var projectPath = Data.ProjectData.Current?.Path;
            return string.IsNullOrEmpty(projectPath) ? path : System.IO.Path.Combine(projectPath, path);
        }
    }
}
