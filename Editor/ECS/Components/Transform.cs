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

        #region Serialized Properties

        /// <summary>
        /// Lokale Position relativ zum Parent
        /// </summary>
        [DataMember(Name = "localPosition", Order = 10)]
        public Vector3 LocalPosition
        {
            get => _localPosition;
            set { if (SetProperty(ref _localPosition, value, nameof(LocalPosition))) SyncToEngine(); }
        }

        /// <summary>
        /// Lokale Rotation relativ zum Parent (Euler-Winkel in Grad)
        /// </summary>
        [DataMember(Name = "localRotation", Order = 11)]
        public Vector3 LocalRotation
        {
            get => _localRotation;
            set { if (SetProperty(ref _localRotation, value, nameof(LocalRotation))) SyncToEngine(); }
        }

        /// <summary>
        /// Lokale Skalierung relativ zum Parent
        /// </summary>
        [DataMember(Name = "localScale", Order = 12)]
        public Vector3 LocalScale
        {
            get => _localScale;
            set { if (SetProperty(ref _localScale, value, nameof(LocalScale))) SyncToEngine(); }
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
            set { if (SetProperty(ref _localRotation, value, nameof(LocalEulerAngles))) SyncToEngine(); }
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
            var owner = Entity;
            if (owner == null) return;

            long engineId = owner.EntityId;
            if (!Editor.Utilities.ID.IsValid(engineId)) return;

            Editor.DllWrapper.VortexAPI.SetEntityTransform(engineId, _localPosition, _localRotation, _localScale);
        }

        /// <summary>
        /// Sets local position for DISPLAY only — updates the value + notifies the inspector but does
        /// NOT push back to the engine. Used during play, when the engine runtime (physics) owns the
        /// transform and we mirror its result back without creating a write-back feedback loop.
        /// </summary>
        public void SetLocalPositionFromEngine(Vector3 position)
        {
            SetProperty(ref _localPosition, position, nameof(LocalPosition));
        }

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
