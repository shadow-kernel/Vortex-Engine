using System.Runtime.Serialization;

namespace Editor.ECS.Components.Rendering
{
    /// <summary>Loop behaviour of a <see cref="ParticleSystem"/>: the effect's own setting, or forced.</summary>
    public enum ParticleLoopMode { UseEffect = 0, Loop = 1, Once = 2 }

    /// <summary>Simulation space of a <see cref="ParticleSystem"/>: the effect's own setting, or forced.</summary>
    public enum ParticleSpaceMode { UseEffect = 0, World = 1, Local = 2 }

    /// <summary>
    /// Plays a visual effect (.vfx — one or more particle emitters, VFX epic #116) at this entity. The effect follows
    /// the entity (world-space particles stay where they were emitted, local-space ones move with it). Driven by
    /// <see cref="Editor.Core.Services.Particles.ParticleService"/>: created on Play (and live in the editor viewport
    /// while editing), destroyed on Stop / scene switch. Scripts control it through <c>Vortex.Vfx</c>.
    /// </summary>
    [DataContract(Name = "ParticleSystem", Namespace = "")]
    public class ParticleSystem : Component
    {
        private string _vfxPath;
        private bool _playOnStart = true;
        private int _loop;
        private int _simulationSpace;
        private int _renderLayer;
        private int _seed;
        private float _simulationSpeed = 1f;
        private bool _previewInEditor = true;

        public override string DisplayName => "Particle System";
        public override string IconCode => "";
        public override string IconColor => "#F0A04B";

        /// <summary>Project-relative path of the .vfx effect (e.g. "Assets/VFX/MuzzleSparks.vfx").</summary>
        [DataMember(Name = "vfxPath", Order = 10)]
        public string VfxPath
        {
            get => _vfxPath;
            set => SetProperty(ref _vfxPath, value, nameof(VfxPath));
        }

        /// <summary>Start emitting when play starts (off = wait for Vfx.Play / Vfx.Burst from a script).</summary>
        [DataMember(Name = "playOnStart", Order = 11)]
        public bool PlayOnStart
        {
            get => _playOnStart;
            set => SetProperty(ref _playOnStart, value, nameof(PlayOnStart));
        }

        /// <summary>0 = as authored in the effect, 1 = always loop, 2 = play once.</summary>
        [DataMember(Name = "loop", Order = 12)]
        public int Loop
        {
            get => _loop;
            set => SetProperty(ref _loop, value < 0 ? 0 : (value > 2 ? 2 : value), nameof(Loop));
        }

        /// <summary>0 = as authored, 1 = world (particles stay behind a moving entity), 2 = local (they move with it).</summary>
        [DataMember(Name = "simulationSpace", Order = 13)]
        public int SimulationSpace
        {
            get => _simulationSpace;
            set => SetProperty(ref _simulationSpace, value < 0 ? 0 : (value > 2 ? 2 : value), nameof(SimulationSpace));
        }

        /// <summary>0 = world, 1 = first-person viewmodel layer (drawn with the weapon: own FOV, never clips walls).</summary>
        [DataMember(Name = "renderLayer", Order = 14)]
        public int RenderLayer
        {
            get => _renderLayer;
            set => SetProperty(ref _renderLayer, value <= 0 ? 0 : 1, nameof(RenderLayer));
        }

        /// <summary>Random seed; 0 = different every play, anything else = the same sequence every time.</summary>
        [DataMember(Name = "seed", Order = 15)]
        public int Seed
        {
            get => _seed;
            set => SetProperty(ref _seed, value, nameof(Seed));
        }

        /// <summary>Time scale of this effect (1 = normal).</summary>
        [DataMember(Name = "simulationSpeed", Order = 16)]
        public float SimulationSpeed
        {
            get => _simulationSpeed;
            set => SetProperty(ref _simulationSpeed, value < 0f ? 0f : value, nameof(SimulationSpeed));
        }

        /// <summary>Simulate in the editor viewport while editing (looping effects run, one-shots replay on demand).</summary>
        [DataMember(Name = "previewInEditor", Order = 17)]
        public bool PreviewInEditor
        {
            get => _previewInEditor;
            set => SetProperty(ref _previewInEditor, value, nameof(PreviewInEditor));
        }

        public ParticleSystem() : base() { }
        public ParticleSystem(GameEntity entity) : base(entity) { }

        /// <summary>DataContract serializers skip constructors and field initialisers: members missing from an older
        /// scene file must still get their defaults (a speed of 0 would freeze the effect).</summary>
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            _playOnStart = true;
            _simulationSpeed = 1f;
            _previewInEditor = true;
        }
    }
}
