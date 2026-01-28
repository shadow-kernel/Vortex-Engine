using System.Runtime.Serialization;
using Editor.ECS;

namespace Editor.ECS.Components.Rendering
{
    /// <summary>
    /// Skybox-Typen für Umgebungsbeleuchtung
    /// </summary>
    public enum SkyboxType
    {
        SolidColor,
        Gradient,
        Cubemap,
        Texture
    }

    /// <summary>
    /// Skybox-Komponente für Umgebungsbeleuchtung und Hintergrund.
    /// Simuliert Image-Based Lighting (IBL) durch Ambient/Environment Light.
    /// </summary>
    [DataContract(Name = "Skybox", Namespace = "")]
    public class Skybox : Component
    {
        private SkyboxType _skyboxType = SkyboxType.Gradient;
        private float _ambientIntensity = 0.8f;  // Higher default for visible effect
        private bool _isEnabled = true;

        // Solid Color / Top Color für Gradient - brighter defaults
        private float _topColorR = 0.7f;
        private float _topColorG = 0.8f;
        private float _topColorB = 1.0f;

        // Bottom Color für Gradient
        private float _bottomColorR = 0.3f;
        private float _bottomColorG = 0.3f;
        private float _bottomColorB = 0.4f;

        // Horizon Color für Gradient
        private float _horizonColorR = 0.8f;
        private float _horizonColorG = 0.85f;
        private float _horizonColorB = 0.95f;

        // Exposure für HDR
        private float _exposure = 1.0f;

        // Cubemap-Pfad (für zukünftige Implementierung)
        private string _cubemapPath = "";

        public override string DisplayName => "Skybox";
        public override string IconCode => "\uE81A";
        public override string IconColor => "#87CEEB";

        /// <summary>
        /// Typ der Skybox
        /// </summary>
        [DataMember(Name = "skyboxType", Order = 10)]
        public SkyboxType SkyboxType
        {
            get => _skyboxType;
            set => SetProperty(ref _skyboxType, value, nameof(SkyboxType));
        }

        /// <summary>
        /// Ambient/Environment Licht-Intensität (0-2)
        /// </summary>
        [DataMember(Name = "ambientIntensity", Order = 11)]
        public float AmbientIntensity
        {
            get => _ambientIntensity;
            set => SetProperty(ref _ambientIntensity, value, nameof(AmbientIntensity));
        }

        /// <summary>
        /// Ob die Skybox aktiv ist
        /// </summary>
        [DataMember(Name = "isEnabled", Order = 12)]
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value, nameof(IsEnabled));
        }

        /// <summary>
        /// Obere/Himmel-Farbe Rot
        /// </summary>
        [DataMember(Name = "topColorR", Order = 20)]
        public float TopColorR
        {
            get => _topColorR;
            set => SetProperty(ref _topColorR, value, nameof(TopColorR));
        }

        /// <summary>
        /// Obere/Himmel-Farbe Grün
        /// </summary>
        [DataMember(Name = "topColorG", Order = 21)]
        public float TopColorG
        {
            get => _topColorG;
            set => SetProperty(ref _topColorG, value, nameof(TopColorG));
        }

        /// <summary>
        /// Obere/Himmel-Farbe Blau
        /// </summary>
        [DataMember(Name = "topColorB", Order = 22)]
        public float TopColorB
        {
            get => _topColorB;
            set => SetProperty(ref _topColorB, value, nameof(TopColorB));
        }

        /// <summary>
        /// Untere/Boden-Farbe Rot
        /// </summary>
        [DataMember(Name = "bottomColorR", Order = 30)]
        public float BottomColorR
        {
            get => _bottomColorR;
            set => SetProperty(ref _bottomColorR, value, nameof(BottomColorR));
        }

        /// <summary>
        /// Untere/Boden-Farbe Grün
        /// </summary>
        [DataMember(Name = "bottomColorG", Order = 31)]
        public float BottomColorG
        {
            get => _bottomColorG;
            set => SetProperty(ref _bottomColorG, value, nameof(BottomColorG));
        }

        /// <summary>
        /// Untere/Boden-Farbe Blau
        /// </summary>
        [DataMember(Name = "bottomColorB", Order = 32)]
        public float BottomColorB
        {
            get => _bottomColorB;
            set => SetProperty(ref _bottomColorB, value, nameof(BottomColorB));
        }

        /// <summary>
        /// Horizont-Farbe Rot
        /// </summary>
        [DataMember(Name = "horizonColorR", Order = 40)]
        public float HorizonColorR
        {
            get => _horizonColorR;
            set => SetProperty(ref _horizonColorR, value, nameof(HorizonColorR));
        }

        /// <summary>
        /// Horizont-Farbe Grün
        /// </summary>
        [DataMember(Name = "horizonColorG", Order = 41)]
        public float HorizonColorG
        {
            get => _horizonColorG;
            set => SetProperty(ref _horizonColorG, value, nameof(HorizonColorG));
        }

        /// <summary>
        /// Horizont-Farbe Blau
        /// </summary>
        [DataMember(Name = "horizonColorB", Order = 42)]
        public float HorizonColorB
        {
            get => _horizonColorB;
            set => SetProperty(ref _horizonColorB, value, nameof(HorizonColorB));
        }

        /// <summary>
        /// Belichtung/Exposure für HDR (0.1-4)
        /// </summary>
        [DataMember(Name = "exposure", Order = 50)]
        public float Exposure
        {
            get => _exposure;
            set => SetProperty(ref _exposure, value, nameof(Exposure));
        }

        /// <summary>
        /// Pfad zur Cubemap-Textur
        /// </summary>
        [DataMember(Name = "cubemapPath", Order = 60)]
        public string CubemapPath
        {
            get => _cubemapPath;
            set => SetProperty(ref _cubemapPath, value, nameof(CubemapPath));
        }

        private string _texturePath = "";
        
        /// <summary>
        /// Pfad zur HDR/Equirectangular Textur
        /// </summary>
        [DataMember(Name = "texturePath", Order = 61)]
        public string TexturePath
        {
            get => _texturePath;
            set => SetProperty(ref _texturePath, value, nameof(TexturePath));
        }

        private string _skyboxMeshPath = "";
        
        /// <summary>
        /// Pfad zum Skybox-Mesh (z.B. .fbx Skydome)
        /// </summary>
        [DataMember(Name = "skyboxMeshPath", Order = 62)]
        public string SkyboxMeshPath
        {
            get => _skyboxMeshPath;
            set => SetProperty(ref _skyboxMeshPath, value, nameof(SkyboxMeshPath));
        }

        /// <summary>
        /// Berechnet die durchschnittliche Ambient-Farbe basierend auf den Skybox-Einstellungen
        /// </summary>
        public (float r, float g, float b) GetAmbientColor()
        {
            float intensity = _ambientIntensity * _exposure;
            
            switch (_skyboxType)
            {
                case SkyboxType.SolidColor:
                    return (_topColorR * intensity, _topColorG * intensity, _topColorB * intensity);
                
                case SkyboxType.Gradient:
                    // Durchschnitt der drei Farben für Ambient
                    float avgR = (_topColorR + _horizonColorR + _bottomColorR) / 3f * intensity;
                    float avgG = (_topColorG + _horizonColorG + _bottomColorG) / 3f * intensity;
                    float avgB = (_topColorB + _horizonColorB + _bottomColorB) / 3f * intensity;
                    return (avgR, avgG, avgB);
                
                case SkyboxType.Cubemap:
                case SkyboxType.Texture:
                    // Für Cubemap/Texture nutzen wir die Ambient-Intensität direkt
                    // Da wir keine IBL haben, verwenden wir eine neutrale Grundfarbe multipliziert mit der Intensität
                    return (intensity, intensity, intensity);
                
                default:
                    return (0.3f, 0.3f, 0.3f);
            }
        }

        public Skybox() : base() { }
        public Skybox(GameEntity entity) : base(entity) { }
    }
}
