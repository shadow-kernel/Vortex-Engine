using System.Runtime.Serialization;

namespace Editor.ECS.Components.Rendering
{
    /// <summary>
    /// Kamera-Projektion
    /// </summary>
    public enum CameraProjection
    {
        Perspective,
        Orthographic
    }

    /// <summary>
    /// Clear-Flags für die Kamera
    /// </summary>
    public enum CameraClearFlags
    {
        Skybox,
        SolidColor,
        DepthOnly,
        Nothing
    }

    /// <summary>
    /// Kamera-Komponente für Rendering-Viewports.
    /// </summary>
    [DataContract(Name = "Camera", Namespace = "")]
    public class Camera : Component
    {
        private CameraProjection _projection = CameraProjection.Perspective;
        private CameraClearFlags _clearFlags = CameraClearFlags.Skybox;
        private float _fieldOfView = 60f;
        private float _orthographicSize = 5f;
        private float _nearClip = 0.1f;
        private float _farClip = 1000f;
        private bool _isMainCamera;
        private int _depth;
        private float _backgroundR;
        private float _backgroundG;
        private float _backgroundB = 0.3f;
        private int _cullingMask = -1; // All layers

        public override string DisplayName => "Camera";
        public override string IconCode => "\uE722";
        public override string IconColor => "#569CD6";

        /// <summary>
        /// Projektionsart (Perspektive/Orthografisch)
        /// </summary>
        [DataMember(Name = "projection", Order = 10)]
        public CameraProjection Projection
        {
            get => _projection;
            set => SetProperty(ref _projection, value, nameof(Projection));
        }

        /// <summary>
        /// Clear-Flags
        /// </summary>
        [DataMember(Name = "clearFlags", Order = 11)]
        public CameraClearFlags ClearFlags
        {
            get => _clearFlags;
            set => SetProperty(ref _clearFlags, value, nameof(ClearFlags));
        }

        /// <summary>
        /// Sichtfeld in Grad (für Perspektive)
        /// </summary>
        [DataMember(Name = "fov", Order = 12)]
        public float FieldOfView
        {
            get => _fieldOfView;
            set => SetProperty(ref _fieldOfView, value, nameof(FieldOfView));
        }

        /// <summary>
        /// Orthografische Größe (für Orthografisch)
        /// </summary>
        [DataMember(Name = "orthoSize", Order = 13)]
        public float OrthographicSize
        {
            get => _orthographicSize;
            set => SetProperty(ref _orthographicSize, value, nameof(OrthographicSize));
        }

        /// <summary>
        /// Nahe Clipping-Ebene
        /// </summary>
        [DataMember(Name = "nearClip", Order = 14)]
        public float NearClip
        {
            get => _nearClip;
            set => SetProperty(ref _nearClip, value, nameof(NearClip));
        }

        /// <summary>
        /// Ferne Clipping-Ebene
        /// </summary>
        [DataMember(Name = "farClip", Order = 15)]
        public float FarClip
        {
            get => _farClip;
            set => SetProperty(ref _farClip, value, nameof(FarClip));
        }

        /// <summary>
        /// Ob dies die Hauptkamera ist
        /// </summary>
        [DataMember(Name = "isMainCamera", Order = 16)]
        public bool IsMainCamera
        {
            get => _isMainCamera;
            set => SetProperty(ref _isMainCamera, value, nameof(IsMainCamera));
        }

        /// <summary>
        /// Render-Tiefe (niedrigere Werte rendern zuerst)
        /// </summary>
        [DataMember(Name = "depth", Order = 17)]
        public int Depth
        {
            get => _depth;
            set => SetProperty(ref _depth, value, nameof(Depth));
        }

        /// <summary>
        /// Hintergrundfarbe Rot
        /// </summary>
        [DataMember(Name = "bgR", Order = 18)]
        public float BackgroundR
        {
            get => _backgroundR;
            set => SetProperty(ref _backgroundR, value, nameof(BackgroundR));
        }

        /// <summary>
        /// Hintergrundfarbe Grün
        /// </summary>
        [DataMember(Name = "bgG", Order = 19)]
        public float BackgroundG
        {
            get => _backgroundG;
            set => SetProperty(ref _backgroundG, value, nameof(BackgroundG));
        }

        /// <summary>
        /// Hintergrundfarbe Blau
        /// </summary>
        [DataMember(Name = "bgB", Order = 20)]
        public float BackgroundB
        {
            get => _backgroundB;
            set => SetProperty(ref _backgroundB, value, nameof(BackgroundB));
        }

        /// <summary>
        /// Culling-Maske für Layer
        /// </summary>
        [DataMember(Name = "cullingMask", Order = 21)]
        public int CullingMask
        {
            get => _cullingMask;
            set => SetProperty(ref _cullingMask, value, nameof(CullingMask));
        }

        public Camera() : base() { }
        public Camera(GameEntity entity) : base(entity) { }
        public Camera(GameEntity entity, bool isMainCamera) : base(entity)
        {
            IsMainCamera = isMainCamera;
        }
    }
}
