using System.Runtime.Serialization;

namespace Editor.ECS.Components
{
    /// <summary>
    /// Transform-Komponente für Position, Rotation und Skalierung.
    /// Dies ist ein reines Daten-Modell für den Editor.
    /// Die eigentliche Engine-Logik wird in C++ implementiert.
    /// </summary>
    [DataContract(Name = "Transform", Namespace = "")]
    public class Transform : Component
    {
        private Vector3 _localPosition;
        private Quaternion _localRotation = Quaternion.Identity;
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
            set => SetProperty(ref _localPosition, value, nameof(LocalPosition));
        }

        /// <summary>
        /// Lokale Rotation relativ zum Parent (Quaternion)
        /// </summary>
        [DataMember(Name = "localRotation", Order = 11)]
        public Quaternion LocalRotation
        {
            get => _localRotation;
            set => SetProperty(ref _localRotation, value, nameof(LocalRotation));
        }

        /// <summary>
        /// Lokale Skalierung relativ zum Parent
        /// </summary>
        [DataMember(Name = "localScale", Order = 12)]
        public Vector3 LocalScale
        {
            get => _localScale;
            set => SetProperty(ref _localScale, value, nameof(LocalScale));
        }

        #endregion

        #region Editor Helper Properties

        /// <summary>
        /// Lokale Rotation als Euler-Winkel (in Grad) - Für Inspector-Anzeige
        /// </summary>
        [IgnoreDataMember]
        public Vector3 LocalEulerAngles
        {
            get => _localRotation.EulerAngles;
            set => LocalRotation = Quaternion.Euler(value);
        }

        #endregion

        #region Constructors

        public Transform() : base() { }
        public Transform(GameEntity entity) : base(entity) { }

        #endregion

        #region Editor Methods

        /// <summary>
        /// Setzt Position, Rotation und Skalierung auf Standardwerte zurück.
        /// Nur für Editor-Verwendung.
        /// </summary>
        public void Reset()
        {
            LocalPosition = Vector3.Zero;
            LocalRotation = Quaternion.Identity;
            LocalScale = Vector3.One;
        }

        #endregion
    }
}
