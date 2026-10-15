using System.Runtime.Serialization;

namespace Editor.ECS.Components
{
    /// <summary>
    /// Transform-Komponente f�r Position, Rotation und Skalierung.
    /// Dies ist ein reines Daten-Modell f�r den Editor.
    /// Die eigentliche Engine-Logik wird in C++ implementiert.
    /// </summary>
    [DataContract(Name = "Transform", Namespace = "")]
    public class Transform : Component
    {
        private Vector3 _localPosition;
        private Vector3 _localRotation;
        private Vector3 _localScale = Vector3.One;

        public override string DisplayName => "Transform";
        public override string IconCode => "\uE81E";
        public override string IconColor => "#4EC9B0";

        /// <summary>Bumped whenever a transform of a STATIC entity changes (#364 A); the scene submit compares it with
        /// the version it last sent the static set for.</summary>
        public static int StaticVersion;

        #region Serialized Properties

        /// <summary>
        /// Lokale Position relativ zum Parent
        /// </summary>
        [DataMember(Name = "localPosition", Order = 10)]
        public Vector3 LocalPosition
        {
            get => _localPosition;
            set { if (Finite(value, 1, "position") && SetProperty(ref _localPosition, value, nameof(LocalPosition))) SyncToEngine(); }
        }

        /// <summary>
        /// Lokale Rotation relativ zum Parent (Euler-Winkel in Grad)
        /// </summary>
        [DataMember(Name = "localRotation", Order = 11)]
        public Vector3 LocalRotation
        {
            get => _localRotation;
            set { if (Finite(value, 2, "rotation") && SetProperty(ref _localRotation, value, nameof(LocalRotation))) SyncToEngine(); }
        }

        /// <summary>
        /// Lokale Skalierung relativ zum Parent
        /// </summary>
        [DataMember(Name = "localScale", Order = 12)]
        public Vector3 LocalScale
        {
            get => _localScale;
            set { if (Finite(value, 4, "scale") && SetProperty(ref _localScale, value, nameof(LocalScale))) SyncToEngine(); }
        }

        #endregion

        #region Editor Helper Properties

        /// <summary>
        /// Lokale Rotation als Euler-Winkel (in Grad) - F�r Inspector-Anzeige
        /// </summary>
        [IgnoreDataMember]
        public Vector3 LocalEulerAngles
        {
            get => _localRotation;
            set { if (Finite(value, 2, "rotation") && SetProperty(ref _localRotation, value, nameof(LocalEulerAngles))) SyncToEngine(); }
        }

        #endregion

        #region Non-finite guard (#455)

        /// <summary>Bumped when a play session starts (ScriptRuntime.BeginPlaySession), so each session warns afresh.</summary>
        internal static int NonFiniteWarningEpoch;
        private int _nonFiniteWarned, _nonFiniteEpoch;   // bits 1 position, 2 rotation, 4 scale — warned in that epoch

        /// <summary>A NaN or an infinity never becomes a transform value (#455): SyncToEngine would hand it to the engine's
        /// native quaternion / matrix math, which can crash the editor or the player — one 0/0 in a game script was enough.
        /// Such a write is ignored as a whole (the entity keeps its last valid value; never a jump to the origin), and one
        /// warning per entity and property and play session names the entity and the script that wrote it — the first
        /// few of a session; the rest are counted into one "…and N more" line when play stops.</summary>
        private bool Finite(Vector3 v, int bit, string property)
        {
            if (float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)) return true;
            if (_nonFiniteEpoch != NonFiniteWarningEpoch) { _nonFiniteEpoch = NonFiniteWarningEpoch; _nonFiniteWarned = 0; }
            if ((_nonFiniteWarned & bit) != 0) return false;
            _nonFiniteWarned |= bit;
            if (!Editor.Scripting.ScriptRuntime.TakeNonFiniteWarning()) return false;   // over the session's cap: counted only
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                string writer = Editor.Scripting.ScriptRuntime.ScriptFrameOfCaller();
                Editor.Core.Services.ConsoleService.Instance.LogWarning("Transform: ignored a non-finite " + property + " ("
                    + v.X.ToString(ci) + ", " + v.Y.ToString(ci) + ", " + v.Z.ToString(ci) + ") for "
                    + (Entity != null ? "'" + Entity.Name + "'" : "an entity") + (writer != null ? ", written by " + writer : "")
                    + " — it keeps its last valid " + property + ". (Shown once per entity and property.)");
            }
            catch { }
            return false;
        }

        #endregion

        #region Engine Sync

        /// <summary>
        /// Pushes this transform to the engine-side entity so the engine transform stays
        /// authoritative/live (single source of truth). No-op until the owning entity has been
        /// created in the engine — the create path already seeds the transform, and every later
        /// edit flows through here. This is the bridge gameplay/physics/networking will read from.
        /// </summary>
        internal void SyncToEngine()
        {
            // the scene renderer updates this entity's subtree on its next submit (#361)
            RenderChanges.TransformChanged(Entity);
            // a STATIC entity moved (#364 A): the renderer's retained static set must be re-sent. Dynamic entities
            // (Animator / Rigidbody / … on them or an ancestor) are re-sent every frame anyway and stay silent here.
            if (Entity == null || !Entity.RenderDynamic) StaticVersion++;
            var owner = Entity;
            if (owner == null) return;

            long engineId = owner.EntityId;
            if (!Editor.Utilities.ID.IsValid(engineId)) return;

            Editor.DllWrapper.VortexAPI.SetEntityTransform(engineId, _localPosition, _localRotation, _localScale);

            // Draw matrices are baked into the render queue at SubmitScene time — flag the change so the
            // submit-once GameHost loop re-submits and script-moved entities visibly move in shipped games.
            Editor.Core.Services.SceneRenderService.RuntimeDirty = true;
        }

        /// <summary>
        /// Sets local position for DISPLAY only — updates the value + notifies the inspector but does
        /// NOT push back to the engine. Used during play, when the engine runtime (physics) owns the
        /// transform and we mirror its result back without creating a write-back feedback loop.
        /// </summary>
        public void SetLocalPositionFromEngine(Vector3 position)
        {
            if (Finite(position, 1, "position") && SetProperty(ref _localPosition, position, nameof(LocalPosition)))
                RenderChanges.TransformChanged(Entity);   // #361
        }

        #endregion

        #region World matrix (#361)

        // The world matrix, cached: valid while this transform's local values, its parent and the parent's world matrix are
        // the ones it was computed from — compared up the chain on each read (the values themselves, not a counter: undo
        // writes the fields directly), so a change anywhere above is seen without an invalidation walk that could miss one.
        private float[] _world;
        private int _worldVersion, _worldParentVersion;
        private Vector3 _worldPos, _worldRot, _worldScale;
        private Transform _worldParent;
        [System.ThreadStatic] private static float[] t_local;   // scratch, per thread: a reader off the main thread never shares it

        /// <summary>The world matrix (row-major 4x4, row 3 = translation; this transform × its parents' — the matrix the
        /// renderer draws with), recomputed only after this transform or an ancestor changed. Owned by the transform: read
        /// it, do not keep or change it.</summary>
        internal float[] WorldMatrix { get { EnsureWorld(); return _world; } }

        /// <summary>Counts up whenever <see cref="WorldMatrix"/> was recomputed: the scene renderer sends a moved instance when
        /// this differs from the version it sent last.</summary>
        internal int WorldVersion { get { EnsureWorld(); return _worldVersion; } }

        private void EnsureWorld()
        {
            // like SceneRenderService.BuildWorldMatrixInto: a parent without a Transform ends the chain
            var parent = Entity?.Parent?.Transform;
            int parentVersion = 0;
            if (parent != null) { parent.EnsureWorld(); parentVersion = parent._worldVersion; }
            if (_world != null && ReferenceEquals(_worldParent, parent) && _worldParentVersion == parentVersion
                && Same(_worldPos, _localPosition) && Same(_worldRot, _localRotation) && Same(_worldScale, _localScale)) return;
            if (_world == null) _world = new float[16];
            var local = t_local ??= new float[16];
            Editor.Core.Services.SceneRenderService.LocalMatrixInto(this, local);
            if (parent != null) Editor.Core.Services.SceneRenderService.MultiplyInto(local, parent._world, _world);
            else System.Array.Copy(local, _world, 16);
            _worldPos = _localPosition; _worldRot = _localRotation; _worldScale = _localScale;
            _worldParent = parent;
            _worldParentVersion = parentVersion;
            _worldVersion++;
        }

        // float.Equals: a NaN equals itself, so it could never force a recompute every read (the setters refuse them anyway)
        private static bool Same(Vector3 a, Vector3 b) =>
            a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.Z.Equals(b.Z);

        #endregion

        #region Constructors

        public Transform() : base() { }
        public Transform(GameEntity entity) : base(entity) { }

        #endregion

        #region Editor Methods

        /// <summary>
        /// Setzt Position, Rotation und Skalierung auf Standardwerte zur�ck.
        /// Nur f�r Editor-Verwendung.
        /// </summary>
        public void Reset()
        {
            LocalPosition = Vector3.Zero;
            LocalRotation = Vector3.Zero;
            LocalScale = Vector3.One;
        }

        #endregion
    }
}
