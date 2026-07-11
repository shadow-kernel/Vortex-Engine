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
            public Matrix4x4[] NodeWorlds;        // model-space node worlds of the SAME pose (bone sockets read these)
            public List<LayerState> Layers;       // bone-masked override layers (#173); null = single-clip fast path
            public SyncGroup Group;               // synced playback group (#174); null = independent clock
            // Runtime procedural bone control (#178): script-supplied additive LOCAL rotation deltas per node,
            // composed onto the animated pose each frame BEFORE hierarchy multiplication so a delta carries all
            // descendants (spine pitch -> chest+arms+weapon rotate as one). null/empty = no override (fast path).
            public Dictionary<int, Quaternion> BoneAdditive;
            public Dictionary<int, float> BoneScale;   // runtime per-bone scale multiplier (0 = hide, e.g. FP legs/head)
            // Runtime two-bone IK (#179): resolved chains from the entity's TwoBoneIk components,
            // re-synced every Step (and on inspector edits via RefreshIk). null/empty = fast path.
            public List<IkChainRuntime> IkChains;
            // Auto-grip (#179): the CAPTURED tip-relative-to-target matrix per tip node, taken from the
            // animation's natural grip on the first frame and held after. Persists across Steps (the
            // chain runtime list is rebuilt each Step); cleared by RefreshIk on config edits.
            public Dictionary<int, Matrix4x4> IkCapturedGrips;
        }

        /// <summary>Resolved runtime form of one TwoBoneIk component (node indices + quaternion offset).</summary>
        private class IkChainRuntime
        {
            public int Tip, Mid, Root, Target;
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

            // #179: refresh the runtime IK chains from the entity's TwoBoneIk components each Step so
            // inspector edits and script SetIkWeight take effect immediately.
            SyncIkChains(entity, state);

            // #178: a runtime bone override must re-pose every frame even on a static/held clip (look up/down
            // while standing still), so it counts as "active" for the re-evaluation gate. Same for IK (#179).
            bool overridesActive = (state.BoneAdditive != null && state.BoneAdditive.Count > 0)
                                || (state.BoneScale != null && state.BoneScale.Count > 0)
                                || (state.IkChains != null && state.IkChains.Count > 0);
            if (!baseActive && !layersActive && !overridesActive) return;
            state.Palette = EvaluateStatePalette(state);
            HasActiveAnimators = true;
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
                var p = posEntity.Transform.LocalPosition;   // world pos source-of-truth (see AudioPlaybackService.ReadWorldPosition)
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

        /// <summary>Rebuild the state's IK chain list from the entity's TwoBoneIk components. Called per
        /// Step and from <see cref="RefreshIk"/>, so inspector edits and script SetIkWeight apply the
        /// same frame. Chains resolve tip -> (mid = parent, root = grandparent); invalid ones drop out.</summary>
        private static void SyncIkChains(ECS.GameEntity entity, AnimatorState state)
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
                    int tip = skel.FindNode(ik.TipBone);
                    int target = skel.FindNode(ik.TargetBone);
                    if (tip < 0 || target < 0) continue;
                    int mid = skel.Nodes[tip].Parent;
                    int root = mid >= 0 ? skel.Nodes[mid].Parent : -1;
                    if (root < 0) continue;

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
            state.IkChains = chains;
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
            HasActiveAnimators = true;
            state.Palette = EvaluateStatePalette(state);
            Services.SceneRenderService.RuntimeDirty = true;   // GameHost/submit-once re-submit contract
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
            Matrix4x4 targetM = offM * baseTarget;
            Vector3 target = targetM.Translation;

            Vector3 a = worlds[ik.Root].Translation;
            Vector3 b = worlds[ik.Mid].Translation;
            Vector3 c = worlds[ik.Tip].Translation;

            float l1 = (b - a).Length(), l2 = (c - b).Length();
            if (l1 < 1e-5f || l2 < 1e-5f) return false;
            Vector3 at = target - a;
            float dist = at.Length();
            if (dist < 1e-6f) return false;
            float maxReach = (l1 + l2) * 0.9999f;
            float minReach = Math.Abs(l1 - l2) * 1.0001f + 1e-4f;
            float reach = Math.Max(minReach, Math.Min(maxReach, dist));

            // (1) elbow angle via law of cosines. +angle around cross(u,v) moves v TOWARD u (closes the
            // joint), so the delta from current->wanted interior angle is (angCur - angWant).
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
            float dElbow = (angCur - angWant) * w;
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
            if (ik.ApplyTipRotation)
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

            // Runtime bone scale: values >= 0.01 scale the POSE (children follow, classic behaviour).
            // The HIDE case (0 = collapse the limb, FP legs/head) is NOT applied here any more — a
            // zero pose-scale degenerates the node worlds that the IK solver and bone sockets read
            // (the hide+IK combination rendered the whole mesh invisible). Hidden bones are zeroed on
            // the FINAL palette instead (below), which only the GPU skinning sees.
            bool anyHidden = false;
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
                    if (SolveTwoBoneIk(skel, t, r, s, worlds, ch))
                        worlds = ComposeWorlds(skel, t, r, s);
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
                    bool hidden = false;
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

            int p = 0;
            int channelCount = (int)flat[p++];
            for (int c = 0; c < channelCount && p < flat.Length; c++)
            {
                int nodeIndex = (int)flat[p++];
                int posCount = (int)flat[p++];
                int rotCount = (int)flat[p++];
                int scaleCount = (int)flat[p++];

                string bone = (nodeIndex >= 0 && nodeIndex < nodes.Length) ? nodes[nodeIndex].Name : null;
                var track = bone != null ? new AnimTrack { Bone = bone } : null;

                for (int k = 0; k < posCount; k++, p += 4)
                    track?.Pos.Add(new AnimKeyVec3 { T = flat[p], X = flat[p + 1], Y = flat[p + 2], Z = flat[p + 3] });
                for (int k = 0; k < rotCount; k++, p += 5)
                    track?.Rot.Add(new AnimKeyQuat { T = flat[p], X = flat[p + 1], Y = flat[p + 2], Z = flat[p + 3], W = flat[p + 4] });
                for (int k = 0; k < scaleCount; k++, p += 4)
                    track?.Scale.Add(new AnimKeyVec3 { T = flat[p], X = flat[p + 1], Y = flat[p + 2], Z = flat[p + 3] });

                if (track != null) clip.Tracks.Add(track);
            }
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
                if (clip == null || clip.Tracks.Count == 0) continue;

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
