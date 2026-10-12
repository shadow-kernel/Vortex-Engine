using System.Runtime.Serialization;

namespace Editor.ECS.Components.World
{
    /// <summary>
    /// An area (zone) of the world: a box, sphere or cylinder around the entity, turned and scaled by its transform —
    /// a cave, a no-fly zone, a village, a safe zone, as big as the level. The engine only tracks who is inside
    /// (AreaService): every behaviour on an entity gets OnAreaEnter / OnAreaStay (per tick) / OnAreaExit, and
    /// Vortex.Areas answers "is X inside a 'cave'?" for anyone. What an area MEANS is the scripts' business — its tags and
    /// free properties carry that. A <c>.varea</c> file (DataPath) is a reusable definition; without one the component
    /// holds its own values. The viewport draws the outline; select the entity and move / turn / scale it like any other.
    /// </summary>
    [DataContract(Name = "Area", Namespace = "")]
    public class Area : Component
    {
        private string _dataPath = "";
        private int _shape;
        private float _sizeX = 20f, _sizeY = 10f, _sizeZ = 20f;
        private string _tags = "";
        private string _properties = "";
        private float _colorR = 0.35f, _colorG = 0.85f, _colorB = 0.45f;
        private int _priority;
        private bool _trackAll = true;
        private bool _drawInGame;

        public Area() : base() { }
        public Area(GameEntity entity) : base(entity) { }

        public override string DisplayName => "Area";
        public override string IconCode => "";
        public override string IconColor => "#59D98C";

        /// <summary>A <c>.varea</c> file this area takes its shape, tags and properties from (project-relative). Empty = the
        /// values below are the area's own.</summary>
        [DataMember(Name = "dataPath", Order = 10)]
        public string DataPath { get => _dataPath; set => SetProperty(ref _dataPath, value ?? "", nameof(DataPath)); }

        /// <summary>0 = Box, 1 = Sphere, 2 = Cylinder (vertical).</summary>
        [DataMember(Name = "shape", Order = 11)]
        public int Shape { get => _shape; set => SetProperty(ref _shape, value < 0 ? 0 : (value > 2 ? 2 : value), nameof(Shape)); }

        /// <summary>Full size in metres before the transform's scale: a box's width / height / depth; a sphere's diameter in X;
        /// a cylinder's diameter in X and height in Y.</summary>
        [DataMember(Name = "sizeX", Order = 12)] public float SizeX { get => _sizeX; set => SetProperty(ref _sizeX, value < 0.1f ? 0.1f : value, nameof(SizeX)); }
        [DataMember(Name = "sizeY", Order = 13)] public float SizeY { get => _sizeY; set => SetProperty(ref _sizeY, value < 0.1f ? 0.1f : value, nameof(SizeY)); }
        [DataMember(Name = "sizeZ", Order = 14)] public float SizeZ { get => _sizeZ; set => SetProperty(ref _sizeZ, value < 0.1f ? 0.1f : value, nameof(SizeZ)); }

        /// <summary>Tags, comma-separated ("cave, no-fly"): what scripts ask for.</summary>
        [DataMember(Name = "tags", Order = 15)] public string Tags { get => _tags; set => SetProperty(ref _tags, value ?? "", nameof(Tags)); }

        /// <summary>Free properties, one key=value per line ("flightAllowed=false").</summary>
        [DataMember(Name = "properties", Order = 16)] public string Properties { get => _properties; set => SetProperty(ref _properties, value ?? "", nameof(Properties)); }

        [DataMember(Name = "colorR", Order = 17)] public float ColorR { get => _colorR; set => SetProperty(ref _colorR, value, nameof(ColorR)); }
        [DataMember(Name = "colorG", Order = 18)] public float ColorG { get => _colorG; set => SetProperty(ref _colorG, value, nameof(ColorG)); }
        [DataMember(Name = "colorB", Order = 19)] public float ColorB { get => _colorB; set => SetProperty(ref _colorB, value, nameof(ColorB)); }

        /// <summary>Higher wins when several areas overlap and a script asks for THE area at a point.</summary>
        [DataMember(Name = "priority", Order = 20)] public int Priority { get => _priority; set => SetProperty(ref _priority, value, nameof(Priority)); }

        /// <summary>Track every scripted entity (default) — off: only entities a script registers with Areas.Track.</summary>
        [DataMember(Name = "trackAll", Order = 21)] public bool TrackAll { get => _trackAll; set => SetProperty(ref _trackAll, value, nameof(TrackAll)); }

        /// <summary>Draw the outline in play mode too (debugging a zone's bounds in the running game).</summary>
        [DataMember(Name = "drawInGame", Order = 22)] public bool DrawInGame { get => _drawInGame; set => SetProperty(ref _drawInGame, value, nameof(DrawInGame)); }

        /// <summary>The definition this area resolves to: the .varea file when set and readable, else its own values.</summary>
        public Editor.Core.Areas.AreaDefinition Resolve(string projectPath)
        {
            Editor.Core.Areas.AreaDefinition d = null;
            if (!string.IsNullOrEmpty(DataPath))
            {
                try
                {
                    string full = System.IO.Path.IsPathRooted(DataPath) || string.IsNullOrEmpty(projectPath) ? DataPath : System.IO.Path.Combine(projectPath, DataPath);
                    d = Editor.Core.Areas.AreaDefinition.Load(full);
                }
                catch { d = null; }
            }
            if (d == null)
            {
                d = new Editor.Core.Areas.AreaDefinition
                {
                    Name = Entity?.Name ?? "Area", Shape = (Editor.Core.Areas.AreaShape)Shape, SizeX = SizeX, SizeY = SizeY, SizeZ = SizeZ,
                    ColorR = ColorR, ColorG = ColorG, ColorB = ColorB, Priority = Priority,
                };
                foreach (var t in Editor.Core.Areas.AreaDefinition.ParseTags(Tags)) d.Tags.Add(t);
                foreach (var kv in Editor.Core.Areas.AreaDefinition.ParseProperties(Properties)) d.Properties[kv.Key] = kv.Value;
            }
            return d;
        }

        /// <summary>Take a definition's values into the component (after loading a .varea, or to edit a copy).</summary>
        public void ApplyDefinition(Editor.Core.Areas.AreaDefinition d)
        {
            if (d == null) return;
            Shape = (int)d.Shape; SizeX = d.SizeX; SizeY = d.SizeY; SizeZ = d.SizeZ;
            Tags = string.Join(", ", d.Tags);
            var lines = new System.Collections.Generic.List<string>();
            foreach (var kv in d.Properties) lines.Add(kv.Key + "=" + kv.Value);
            Properties = string.Join("\n", lines);
            ColorR = d.ColorR; ColorG = d.ColorG; ColorB = d.ColorB; Priority = d.Priority;
        }
    }
}
