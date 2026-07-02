using System.Runtime.Serialization;

namespace Editor.ECS.Components.Audio
{
    /// <summary>
    /// Audio-Rolloff-Modus
    /// </summary>
    public enum AudioRolloffMode
    {
        Logarithmic,
        Linear,
        Custom
    }

    /// <summary>
    /// AudioSource-Komponente für Audio-Wiedergabe.
    /// </summary>
    [DataContract(Name = "AudioSource", Namespace = "")]
    public class AudioSource : Component
    {
        private string _audioClipPath;
        private float _volume = 1f;
        private float _pitch = 1f;
        private bool _loop;
        private bool _playOnAwake = true;
        private bool _mute;
        private float _spatialBlend;
        private float _minDistance = 1f;
        private float _maxDistance = 500f;
        private AudioRolloffMode _rolloffMode = AudioRolloffMode.Logarithmic;
        private int _priority = 128;
        private float _stereoPan;
        private float _reverbZoneMix = 1f;
        private float _dopplerLevel = 1f;
        private float _spread;
        private bool _streaming;
        private int _outputBus = 2; // SFX
        private bool _enableHrtf;
        private bool _enableOcclusion;

        public override string DisplayName => "Audio Source";
        public override string IconCode => "\uE767";
        public override string IconColor => "#CE9178";

        /// <summary>
        /// Pfad zur Audio-Datei
        /// </summary>
        [DataMember(Name = "audioClipPath", Order = 10)]
        public string AudioClipPath
        {
            get => _audioClipPath;
            set => SetProperty(ref _audioClipPath, value, nameof(AudioClipPath));
        }

        /// <summary>
        /// Lautstärke (0-1)
        /// </summary>
        [DataMember(Name = "volume", Order = 11)]
        public float Volume
        {
            get => _volume;
            set => SetProperty(ref _volume, value, nameof(Volume));
        }

        /// <summary>
        /// Tonhöhe
        /// </summary>
        [DataMember(Name = "pitch", Order = 12)]
        public float Pitch
        {
            get => _pitch;
            set => SetProperty(ref _pitch, value, nameof(Pitch));
        }

        /// <summary>
        /// Ob das Audio in Schleife abgespielt wird
        /// </summary>
        [DataMember(Name = "loop", Order = 13)]
        public bool Loop
        {
            get => _loop;
            set => SetProperty(ref _loop, value, nameof(Loop));
        }

        /// <summary>
        /// Ob das Audio beim Start abgespielt wird
        /// </summary>
        [DataMember(Name = "playOnAwake", Order = 14)]
        public bool PlayOnAwake
        {
            get => _playOnAwake;
            set => SetProperty(ref _playOnAwake, value, nameof(PlayOnAwake));
        }

        /// <summary>
        /// Ob das Audio stumm geschaltet ist
        /// </summary>
        [DataMember(Name = "mute", Order = 15)]
        public bool Mute
        {
            get => _mute;
            set => SetProperty(ref _mute, value, nameof(Mute));
        }

        /// <summary>
        /// Räumliche Mischung (0=2D, 1=3D)
        /// </summary>
        [DataMember(Name = "spatialBlend", Order = 16)]
        public float SpatialBlend
        {
            get => _spatialBlend;
            set => SetProperty(ref _spatialBlend, value, nameof(SpatialBlend));
        }

        /// <summary>
        /// Minimale Distanz für 3D-Audio
        /// </summary>
        [DataMember(Name = "minDistance", Order = 17)]
        public float MinDistance
        {
            get => _minDistance;
            set => SetProperty(ref _minDistance, value, nameof(MinDistance));
        }

        /// <summary>
        /// Maximale Distanz für 3D-Audio
        /// </summary>
        [DataMember(Name = "maxDistance", Order = 18)]
        public float MaxDistance
        {
            get => _maxDistance;
            set => SetProperty(ref _maxDistance, value, nameof(MaxDistance));
        }

        /// <summary>
        /// Rolloff-Modus
        /// </summary>
        [DataMember(Name = "rolloffMode", Order = 19)]
        public AudioRolloffMode RolloffMode
        {
            get => _rolloffMode;
            set => SetProperty(ref _rolloffMode, value, nameof(RolloffMode));
        }

        /// <summary>
        /// Priorität (0=höchste, 256=niedrigste)
        /// </summary>
        [DataMember(Name = "priority", Order = 20)]
        public int Priority
        {
            get => _priority;
            set => SetProperty(ref _priority, value, nameof(Priority));
        }

        /// <summary>
        /// Stereo-Balance (-1=links, 1=rechts)
        /// </summary>
        [DataMember(Name = "stereoPan", Order = 21)]
        public float StereoPan
        {
            get => _stereoPan;
            set => SetProperty(ref _stereoPan, value, nameof(StereoPan));
        }

        /// <summary>
        /// Reverb-Zone-Mix
        /// </summary>
        [DataMember(Name = "reverbZoneMix", Order = 22)]
        public float ReverbZoneMix
        {
            get => _reverbZoneMix;
            set => SetProperty(ref _reverbZoneMix, value, nameof(ReverbZoneMix));
        }

        /// <summary>
        /// Doppler-Effekt-Stärke
        /// </summary>
        [DataMember(Name = "dopplerLevel", Order = 23)]
        public float DopplerLevel
        {
            get => _dopplerLevel;
            set => SetProperty(ref _dopplerLevel, value, nameof(DopplerLevel));
        }

        /// <summary>
        /// Räumliche Ausbreitung in Grad
        /// </summary>
        [DataMember(Name = "spread", Order = 24)]
        public float Spread
        {
            get => _spread;
            set => SetProperty(ref _spread, value, nameof(Spread));
        }

        /// <summary>
        /// Streaming statt Voll-Dekodierung — für Musik und lange Ambience-Loops.
        /// Ein 5-Minuten-OGG voll dekodiert = zig MB PCM; gestreamt bleibt nur die
        /// komprimierte Datei plus ein kleines Dekodier-Fenster im Speicher.
        /// </summary>
        [DataMember(Name = "streaming", Order = 25)]
        public bool Streaming
        {
            get => _streaming;
            set => SetProperty(ref _streaming, value, nameof(Streaming));
        }

        /// <summary>
        /// Mixer-Bus (0 Master, 1 Music, 2 SFX, 3 Ambience, 4 UI). Bus-Volume/Mute
        /// wirken auf alle Voices dieses Busses in Echtzeit.
        /// </summary>
        [DataMember(Name = "outputBus", Order = 26)]
        public int OutputBus
        {
            get => _outputBus;
            set => SetProperty(ref _outputBus, value, nameof(OutputBus));
        }

        /// <summary>
        /// Steam Audio v2 (#21): render this source with HRTF binaural spatialization instead of the v1 stereo
        /// panner — real front/back &amp; above/below on headphones. Only takes effect when Steam Audio is enabled
        /// for the project AND phonon.dll is present; otherwise the v1 spatializer is used.
        /// </summary>
        [DataMember(Name = "enableHrtf", Order = 27)]
        public bool EnableHrtf
        {
            get => _enableHrtf;
            set => SetProperty(ref _enableHrtf, value, nameof(EnableHrtf));
        }

        /// <summary>
        /// Steam Audio v2 (#21): include this source in ray-traced occlusion against the level's collision
        /// geometry — a wall between it and the listener muffles it. Requires <see cref="EnableHrtf"/>.
        /// </summary>
        [DataMember(Name = "enableOcclusion", Order = 28)]
        public bool EnableOcclusion
        {
            get => _enableOcclusion;
            set => SetProperty(ref _enableOcclusion, value, nameof(EnableOcclusion));
        }

        public AudioSource() : base() { }
        public AudioSource(GameEntity entity) : base(entity) { }
        public AudioSource(GameEntity entity, string clipPath) : base(entity)
        {
            AudioClipPath = clipPath;
        }
    }

    /// <summary>
    /// AudioListener-Komponente (normalerweise an der Hauptkamera)
    /// </summary>
    [DataContract(Name = "AudioListener", Namespace = "")]
    public class AudioListener : Component
    {
        public override string DisplayName => "Audio Listener";
        public override string IconCode => "\uE7F6";
        public override string IconColor => "#CE9178";

        public AudioListener() : base() { }
        public AudioListener(GameEntity entity) : base(entity) { }
    }
}
