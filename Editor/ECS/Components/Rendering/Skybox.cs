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
    /// Whether the sky draws the sun's disc at the scene's Directional Light (#499).
    /// </summary>
    public enum SkySunDisc
    {
        /// <summary>A gradient sky draws it (as it always drew a sun); a picture sky does not (it may carry its own sun, and a
        /// project's sky does not change by itself), nor does a solid colour.</summary>
        Auto,
        /// <summary>Always — on a picture too: a .png / .jpg, or an .hdr without a sun in it (an overcast or night panorama).</summary>
        On,
        /// <summary>Never.</summary>
        Off
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

        private SkySunDisc _sunDisc = SkySunDisc.Auto;
        private float _sunSize = 0.53f;
        private float _sunIntensity = 1f;
        private float _sunGlow = 1f;

        /// <summary>
        /// The sun's disc at the scene's Directional Light (#499): Auto draws it on the gradient sky (as before); a picture sky
        /// shows it when it is On (a picture may carry its own sun); a solid colour never. It sits where the light comes from,
        /// whatever the picture's rotation, takes the light's colour, brightens with its intensity (full at the Light's default
        /// 2.5), sets behind the horizon and is hidden by geometry in front of it. On an .hdr sky through the scene's exposure
        /// it blooms like a lamp; elsewhere it is a crisp disc with a darker rim.
        /// </summary>
        [DataMember(Name = "sunDisc", Order = 70)]
        public SkySunDisc SunDisc
        {
            get => _sunDisc;
            set => SetProperty(ref _sunDisc, value, nameof(SunDisc));
        }

        /// <summary>
        /// The disc's angular diameter in degrees (the real sun: 0.53; games often use 1–2 for a readable sun).
        /// </summary>
        [DataMember(Name = "sunSize", Order = 71)]
        public float SunSize
        {
            get => _sunSize;
            set => SetProperty(ref _sunSize, float.IsNaN(value) ? 0.53f : (value < 0.05f ? 0.05f : (value > 20f ? 20f : value)), nameof(SunSize));
        }

        /// <summary>
        /// The disc's brightness (1 = full: white at the centre for a white light, its rim darker; higher washes the rim out).
        /// </summary>
        [DataMember(Name = "sunIntensity", Order = 72)]
        public float SunIntensity
        {
            get => _sunIntensity;
            set => SetProperty(ref _sunIntensity, float.IsNaN(value) || value < 0f ? 0f : (value > 100f ? 100f : value), nameof(SunIntensity));
        }

        /// <summary>
        /// The glow around the disc: the corona hugging it and the wide aureole (0 = a bare disc).
        /// </summary>
        [DataMember(Name = "sunGlow", Order = 73)]
        public float SunGlow
        {
            get => _sunGlow;
            set => SetProperty(ref _sunGlow, float.IsNaN(value) || value < 0f ? 0f : (value > 10f ? 10f : value), nameof(SunGlow));
        }

        [OnDeserializing]
        private void OnDeserializingSkybox(StreamingContext context)
        {
            // DataContract skips constructors: a sky saved before #499 gets the defaults
            _sunDisc = SkySunDisc.Auto; _sunSize = 0.53f; _sunIntensity = 1f; _sunGlow = 1f;
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
