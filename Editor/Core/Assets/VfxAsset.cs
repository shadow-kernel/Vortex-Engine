using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Editor.Core.Assets
{
    // ============================================================================================================
    // .vfx — a visual effect: one or more particle emitters (+ an optional beam for tracers / lasers), stored as
    // JSON (camelCase, enums as names). The same JSON objects are the native descriptor (VortexAPI ParticleApi /
    // Engine/Graphics/Particles/README.md), so an emitter goes to the engine as-is. Texture paths are relative to
    // the .vfx file (portable when a VFX folder is copied between projects); absolute and project-relative paths
    // resolve too (see ResolveTexture).
    // ============================================================================================================

    public enum VfxSpace { World, Local }
    public enum VfxShapeType { Point, Sphere, Hemisphere, Cone, Box, Circle, Edge }
    public enum VfxEmitFrom { Volume, Shell, Edge }
    public enum VfxRenderMode { Billboard, Stretched, Horizontal, Vertical }
    public enum VfxBlend { Alpha, Additive, Premultiplied }
    public enum VfxSort { Auto, None, Depth }
    public enum VfxFlipbookMode { Lifetime, Fps, Random }
    public enum VfxCollisionMode { Bounce, Kill }
    public enum VfxTextureMode { Stretch, Tile }

    /// <summary>One key of a curve: time 0..1 over the lifetime (or the trail / beam length) and a value.</summary>
    public class VfxCurveKey
    {
        public float T { get; set; }
        public float V { get; set; } = 1f;
        public VfxCurveKey() { }
        public VfxCurveKey(float t, float v) { T = t; V = v; }
    }

    /// <summary>Piecewise-linear curve (max 8 keys natively). No keys = constant 1.</summary>
    public class VfxCurve
    {
        public List<VfxCurveKey> Keys { get; set; } = new List<VfxCurveKey>();

        public static VfxCurve Constant(float v) => new VfxCurve { Keys = { new VfxCurveKey(0f, v) } };
        public static VfxCurve Linear(float a, float b) => new VfxCurve { Keys = { new VfxCurveKey(0f, a), new VfxCurveKey(1f, b) } };
        public static VfxCurve Of(params float[] tv)
        {
            var c = new VfxCurve();
            for (int i = 0; i + 1 < tv.Length; i += 2) c.Keys.Add(new VfxCurveKey(tv[i], tv[i + 1]));
            return c;
        }

        [JsonIgnore] public bool IsEmpty => Keys == null || Keys.Count == 0;

        public float Evaluate(float t)
        {
            if (IsEmpty) return 1f;
            var k = Keys;
            if (k.Count == 1 || t <= k[0].T) return k[0].V;
            for (int i = 1; i < k.Count; i++)
            {
                if (t <= k[i].T)
                {
                    float span = k[i].T - k[i - 1].T;
                    float f = span > 1e-6f ? (t - k[i - 1].T) / span : 1f;
                    return k[i - 1].V + (k[i].V - k[i - 1].V) * f;
                }
            }
            return k[k.Count - 1].V;
        }

        public void Sort() { Keys?.Sort((a, b) => a.T.CompareTo(b.T)); }
    }

    /// <summary>One stop of a gradient: time 0..1 and a colour (sRGB 0..1) with alpha.</summary>
    public class VfxGradientKey
    {
        public float T { get; set; }
        public float R { get; set; } = 1f;
        public float G { get; set; } = 1f;
        public float B { get; set; } = 1f;
        public float A { get; set; } = 1f;
        public VfxGradientKey() { }
        public VfxGradientKey(float t, float r, float g, float b, float a) { T = t; R = r; G = g; B = b; A = a; }
    }

    /// <summary>Colour ramp (max 8 stops natively). No stops = white. Multiplies the start colour.</summary>
    public class VfxGradient
    {
        public List<VfxGradientKey> Keys { get; set; } = new List<VfxGradientKey>();

        public static VfxGradient FadeOut(float r = 1f, float g = 1f, float b = 1f)
            => new VfxGradient { Keys = { new VfxGradientKey(0f, r, g, b, 1f), new VfxGradientKey(1f, r, g, b, 0f) } };
        public static VfxGradient Of(params VfxGradientKey[] keys) { var g = new VfxGradient(); g.Keys.AddRange(keys); return g; }

        [JsonIgnore] public bool IsEmpty => Keys == null || Keys.Count == 0;

        public float[] Evaluate(float t)
        {
            if (IsEmpty) return new[] { 1f, 1f, 1f, 1f };
            var k = Keys;
            if (k.Count == 1 || t <= k[0].T) return new[] { k[0].R, k[0].G, k[0].B, k[0].A };
            for (int i = 1; i < k.Count; i++)
            {
                if (t <= k[i].T)
                {
                    float span = k[i].T - k[i - 1].T;
                    float f = span > 1e-6f ? (t - k[i - 1].T) / span : 1f;
                    var a = k[i - 1]; var b = k[i];
                    return new[] { a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f, a.B + (b.B - a.B) * f, a.A + (b.A - a.A) * f };
                }
            }
            var l = k[k.Count - 1];
            return new[] { l.R, l.G, l.B, l.A };
        }

        public void Sort() { Keys?.Sort((a, b) => a.T.CompareTo(b.T)); }
    }

    /// <summary>Burst: Count..CountMax particles at Time (s into the cycle), Cycles times every Interval (0 cycles = repeat).</summary>
    public class VfxBurst
    {
        public float Time { get; set; }
        public int Count { get; set; } = 10;
        public int CountMax { get; set; } = 10;
        public int Cycles { get; set; } = 1;
        public float Interval { get; set; } = 0.1f;
        public float Probability { get; set; } = 1f;
    }

    /// <summary>Emission shape (emits along its local +Z; Rotation (-90, 0, 0) points it up).</summary>
    public class VfxShape
    {
        public VfxShapeType Type { get; set; } = VfxShapeType.Cone;
        public VfxEmitFrom EmitFrom { get; set; } = VfxEmitFrom.Volume;
        public float Radius { get; set; } = 0.1f;
        /// <summary>0 = surface / rim only, 1 = the whole volume / area.</summary>
        public float RadiusThickness { get; set; } = 1f;
        /// <summary>Cone half angle in degrees.</summary>
        public float Angle { get; set; } = 25f;
        public float Arc { get; set; } = 360f;
        public float Length { get; set; } = 1f;
        public float[] Box { get; set; } = { 1f, 1f, 1f };
        public float[] Offset { get; set; } = { 0f, 0f, 0f };
        /// <summary>Euler degrees (engine Z·X·Y order).</summary>
        public float[] Rotation { get; set; } = { 0f, 0f, 0f };
        public float RandomDirection { get; set; }
    }

    public class VfxNoise
    {
        /// <summary>Turbulence displacement speed (m/s); 0 = off.</summary>
        public float Strength { get; set; }
        public float Frequency { get; set; } = 1f;
        public float ScrollSpeed { get; set; }
        public int Octaves { get; set; } = 1;
    }

    /// <summary>Texture sheet animation: a TilesX × TilesY grid read left-to-right, top-to-bottom.</summary>
    public class VfxFlipbook
    {
        public int TilesX { get; set; } = 1;
        public int TilesY { get; set; } = 1;
        public VfxFlipbookMode Mode { get; set; } = VfxFlipbookMode.Lifetime;
        public float Cycles { get; set; } = 1f;
        public float Fps { get; set; } = 15f;
        public float[] StartFrame { get; set; } = { 0f, 0f };
        public bool Blend { get; set; }
    }

    public class VfxRender
    {
        public VfxRenderMode Mode { get; set; } = VfxRenderMode.Billboard;
        public VfxBlend Blend { get; set; } = VfxBlend.Alpha;
        /// <summary>Texture path (relative to the .vfx file, project-relative or absolute). Null = a soft round puff.</summary>
        public string Texture { get; set; }
        public bool Lit { get; set; }
        /// <summary>Soft particles: fade over this distance (m) where they meet geometry; 0 = hard edges.</summary>
        public float SoftDistance { get; set; } = 0.25f;
        /// <summary>Stretched mode: length = size × LengthScale + speed × VelocityScale.</summary>
        public float LengthScale { get; set; } = 2f;
        public float VelocityScale { get; set; }
        public VfxSort Sort { get; set; } = VfxSort.Auto;
        /// <summary>Colour multiplier (glow boost for additive effects).</summary>
        public float Emissive { get; set; } = 1f;
        /// <summary>Billboard width / height.</summary>
        public float Aspect { get; set; } = 1f;
    }

    /// <summary>Depth-buffer collision against the scene (screen space, world layer only).</summary>
    public class VfxCollision
    {
        public bool Enabled { get; set; }
        public VfxCollisionMode Mode { get; set; } = VfxCollisionMode.Bounce;
        public float Bounce { get; set; } = 0.4f;
        public float Dampen { get; set; } = 0.2f;
        public float LifetimeLoss { get; set; }
        public float Radius { get; set; } = 0.02f;
        public float Thickness { get; set; } = 0.5f;
    }

    /// <summary>Per-particle ribbon trails.</summary>
    public class VfxTrails
    {
        public bool Enabled { get; set; }
        public float Lifetime { get; set; } = 0.3f;
        public float MinDistance { get; set; } = 0.05f;
        public int MaxPoints { get; set; } = 16;
        /// <summary>Multiplier of the particle size.</summary>
        public float Width { get; set; } = 1f;
        public VfxCurve WidthCurve { get; set; } = new VfxCurve();
        public VfxGradient ColorGradient { get; set; } = new VfxGradient();
        public bool InheritColor { get; set; } = true;
        public string Texture { get; set; }
        public VfxTextureMode TextureMode { get; set; } = VfxTextureMode.Stretch;
    }

    /// <summary>One emitter of an effect. Property names are the native JSON keys (camelCase).</summary>
    public class VfxEmitter
    {
        public string Name { get; set; } = "Emitter";
        public bool Enabled { get; set; } = true;
        public int MaxParticles { get; set; } = 1000;
        public float Duration { get; set; } = 5f;
        public bool Looping { get; set; } = true;
        public bool Prewarm { get; set; }
        public float StartDelay { get; set; }
        public VfxSpace SimulationSpace { get; set; } = VfxSpace.World;
        public float SimulationSpeed { get; set; } = 1f;
        /// <summary>0 = random each play (the ParticleSystem component's seed overrides it).</summary>
        public uint Seed { get; set; }
        public float Rate { get; set; } = 10f;
        public float RateOverDistance { get; set; }
        public List<VfxBurst> Bursts { get; set; } = new List<VfxBurst>();
        public float[] Lifetime { get; set; } = { 2f, 2f };
        public float[] Speed { get; set; } = { 1f, 1f };
        public float[] Size { get; set; } = { 0.25f, 0.25f };
        public float[] Rotation { get; set; } = { 0f, 0f };
        public float[] RotationSpeed { get; set; } = { 0f, 0f };
        public float[] Color { get; set; } = { 1f, 1f, 1f, 1f };
        public float[] Color2 { get; set; } = { 1f, 1f, 1f, 1f };
        public float Gravity { get; set; }
        public float Drag { get; set; }
        public float InheritVelocity { get; set; }
        public VfxShape Shape { get; set; } = new VfxShape();
        public float[] Velocity { get; set; } = { 0f, 0f, 0f };
        public VfxSpace VelocitySpace { get; set; } = VfxSpace.World;
        public VfxCurve VelocityCurve { get; set; } = new VfxCurve();
        public VfxCurve SpeedCurve { get; set; } = new VfxCurve();
        public VfxCurve SizeCurve { get; set; } = new VfxCurve();
        public VfxGradient ColorGradient { get; set; } = new VfxGradient();
        public VfxNoise Noise { get; set; } = new VfxNoise();
        public VfxFlipbook Flipbook { get; set; } = new VfxFlipbook();
        public VfxRender Render { get; set; } = new VfxRender();
        public VfxCollision Collision { get; set; } = new VfxCollision();
        public VfxTrails Trails { get; set; } = new VfxTrails();
    }

    /// <summary>A beam between two points (tracer, laser, lightning) — see Vortex.Vfx.Beam.</summary>
    public class VfxBeam
    {
        public float Width { get; set; } = 0.05f;
        public VfxCurve WidthCurve { get; set; } = new VfxCurve();
        public VfxGradient ColorGradient { get; set; } = new VfxGradient();
        public float[] Color { get; set; } = { 1f, 1f, 1f, 1f };
        public string Texture { get; set; }
        public VfxBlend Blend { get; set; } = VfxBlend.Additive;
        public float Emissive { get; set; } = 1f;
        /// <summary>0 = the texture is stretched once; &gt; 0 = repeats per metre.</summary>
        public float UvTiling { get; set; }
        public float UvScroll { get; set; }
        public int Segments { get; set; } = 1;
        /// <summary>Jitter amplitude in metres (lightning) — needs Segments &gt; 1.</summary>
        public float Noise { get; set; }
        public float NoiseFrequency { get; set; } = 1f;
        public float NoiseSpeed { get; set; } = 5f;
        /// <summary>Seconds until the beam removes itself (0 = until destroyed).</summary>
        public float Duration { get; set; } = 0.1f;
        public float FadeIn { get; set; }
        public float FadeOut { get; set; } = 0.05f;
        /// <summary>&gt; 0: a streak of Length metres travels from start to end at this speed (bullet tracer).</summary>
        public float Speed { get; set; }
        public float Length { get; set; } = 2f;
        public float SoftDistance { get; set; }
    }

    /// <summary>A .vfx effect: emitters (+ optional beam). Load/Save are AssetVfs-aware like VortexMaterial.</summary>
    public class VfxAsset
    {
        public string Name { get; set; } = "New Effect";
        public int Version { get; set; } = 1;
        public List<VfxEmitter> Emitters { get; set; } = new List<VfxEmitter>();
        public VfxBeam Beam { get; set; }

        public const string Extension = ".vfx";

        private static JsonSerializerOptions _options;
        /// <summary>camelCase, enums by name, nulls omitted, indented (diff-friendly .vfx files).</summary>
        public static JsonSerializerOptions JsonOptions
        {
            get
            {
                if (_options != null) return _options;
                var o = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true,
                };
                o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                return _options = o;
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
        public static VfxAsset FromJson(string json) => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<VfxAsset>(json, JsonOptions);
        public VfxAsset Clone() => FromJson(ToJson());

        /// <summary>Load a .vfx (shipped game: from the mounted pak; editor: loose file). Null when missing or invalid.</summary>
        public static VfxAsset Load(string filePath)
        {
            try
            {
                string json = ReadText(filePath);
                if (json == null) return null;
                var a = FromJson(json);
                if (a != null) a.Normalize();
                return a;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VfxAsset] load failed " + filePath + ": " + ex.Message);
                return null;
            }
        }

        public bool Save(string filePath)
        {
            try
            {
                Normalize();
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, ToJson());
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VfxAsset] save failed " + filePath + ": " + ex.Message);
                return false;
            }
        }

        public static string ReadText(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            if (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.Contains(filePath))
                return Editor.Core.Services.AssetVfs.GetText(filePath);
            return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }

        /// <summary>Repair what a hand edit may have broken (missing sub-objects, short arrays, unsorted keys).</summary>
        public void Normalize()
        {
            if (Emitters == null) Emitters = new List<VfxEmitter>();
            for (int i = 0; i < Emitters.Count; i++)
            {
                var e = Emitters[i];
                if (e == null) { Emitters[i] = e = new VfxEmitter(); }
                if (e.Bursts == null) e.Bursts = new List<VfxBurst>();
                e.Lifetime = Fix(e.Lifetime, 2, 1f); e.Speed = Fix(e.Speed, 2, 1f); e.Size = Fix(e.Size, 2, 0.25f);
                e.Rotation = Fix(e.Rotation, 2, 0f); e.RotationSpeed = Fix(e.RotationSpeed, 2, 0f);
                e.Color = Fix(e.Color, 4, 1f); e.Color2 = Fix(e.Color2, 4, 1f); e.Velocity = Fix(e.Velocity, 3, 0f);
                if (e.Shape == null) e.Shape = new VfxShape();
                e.Shape.Box = Fix(e.Shape.Box, 3, 1f); e.Shape.Offset = Fix(e.Shape.Offset, 3, 0f); e.Shape.Rotation = Fix(e.Shape.Rotation, 3, 0f);
                if (e.VelocityCurve == null) e.VelocityCurve = new VfxCurve();
                if (e.SpeedCurve == null) e.SpeedCurve = new VfxCurve();
                if (e.SizeCurve == null) e.SizeCurve = new VfxCurve();
                if (e.ColorGradient == null) e.ColorGradient = new VfxGradient();
                if (e.Noise == null) e.Noise = new VfxNoise();
                if (e.Flipbook == null) e.Flipbook = new VfxFlipbook();
                e.Flipbook.StartFrame = Fix(e.Flipbook.StartFrame, 2, 0f);
                if (e.Render == null) e.Render = new VfxRender();
                if (e.Collision == null) e.Collision = new VfxCollision();
                if (e.Trails == null) e.Trails = new VfxTrails();
                if (e.Trails.WidthCurve == null) e.Trails.WidthCurve = new VfxCurve();
                if (e.Trails.ColorGradient == null) e.Trails.ColorGradient = new VfxGradient();
                foreach (var c in new[] { e.VelocityCurve, e.SpeedCurve, e.SizeCurve, e.Trails.WidthCurve }) { if (c.Keys == null) c.Keys = new List<VfxCurveKey>(); c.Sort(); }
                foreach (var g in new[] { e.ColorGradient, e.Trails.ColorGradient }) { if (g.Keys == null) g.Keys = new List<VfxGradientKey>(); g.Sort(); }
            }
            if (Beam != null)
            {
                if (Beam.WidthCurve == null) Beam.WidthCurve = new VfxCurve();
                if (Beam.ColorGradient == null) Beam.ColorGradient = new VfxGradient();
                if (Beam.WidthCurve.Keys == null) Beam.WidthCurve.Keys = new List<VfxCurveKey>();
                if (Beam.ColorGradient.Keys == null) Beam.ColorGradient.Keys = new List<VfxGradientKey>();
                Beam.Color = Fix(Beam.Color, 4, 1f);
                Beam.WidthCurve.Sort(); Beam.ColorGradient.Sort();
            }
        }

        private static float[] Fix(float[] a, int n, float fill)
        {
            if (a != null && a.Length == n) return a;
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = a != null && i < a.Length ? a[i] : (a != null && a.Length == 1 ? a[0] : fill);
            return r;
        }

        // ------------------------------------------------------------------------------------------ engine JSON

        /// <summary>The emitter as the native descriptor JSON — textures stripped (the runtime binds them by engine
        /// texture id, which also works for textures inside a shipped game's pak).</summary>
        public static string EmitterJson(VfxEmitter e)
        {
            var copy = JsonSerializer.Deserialize<VfxEmitter>(JsonSerializer.Serialize(e, JsonOptions), JsonOptions);
            copy.Render.Texture = null;
            copy.Trails.Texture = null;
            return JsonSerializer.Serialize(copy, JsonOptions);
        }

        /// <summary>The beam as the native descriptor JSON (texture stripped, bound by id) on render layer 0 / 1.</summary>
        public static string BeamJson(VfxBeam b, int layer = 0)
        {
            var copy = JsonSerializer.Deserialize<VfxBeam>(JsonSerializer.Serialize(b, JsonOptions), JsonOptions);
            copy.Texture = null;
            var node = JsonSerializer.SerializeToNode(copy, JsonOptions) as System.Text.Json.Nodes.JsonObject;
            if (node == null) return JsonSerializer.Serialize(copy, JsonOptions);
            node["layer"] = layer > 0 ? 1 : 0;
            return node.ToJsonString(JsonOptions);
        }

        public static VfxEmitter CloneEmitter(VfxEmitter e)
            => JsonSerializer.Deserialize<VfxEmitter>(JsonSerializer.Serialize(e, JsonOptions), JsonOptions);

        /// <summary>Absolute path of a texture referenced by a .vfx: absolute as-is, else next to the .vfx, else
        /// project-relative. Null when nothing is found (in the pak or on disk).</summary>
        public static string ResolveTexture(string texture, string vfxPath, string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(texture)) return null;
            string t = texture.Replace('\\', '/');
            try
            {
                if (Path.IsPathRooted(t)) return Editor.Core.Services.AssetVfs.Exists(t) ? t : null;
                if (!string.IsNullOrEmpty(vfxPath))
                {
                    string near = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(vfxPath) ?? "", t));
                    if (Editor.Core.Services.AssetVfs.Exists(near)) return near;
                }
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    string proj = Path.GetFullPath(Path.Combine(projectRoot, t));
                    if (Editor.Core.Services.AssetVfs.Exists(proj)) return proj;
                }
            }
            catch { }
            return null;
        }

        /// <summary>The path to store for a texture picked in the editor: relative to the .vfx when it lives under the
        /// same folder tree, otherwise project-relative (or absolute as a last resort).</summary>
        public static string MakeTexturePath(string textureFullPath, string vfxPath, string projectRoot)
        {
            if (string.IsNullOrEmpty(textureFullPath)) return null;
            try
            {
                string full = Path.GetFullPath(textureFullPath);
                string vfxDir = string.IsNullOrEmpty(vfxPath) ? null : Path.GetDirectoryName(Path.GetFullPath(vfxPath));
                if (!string.IsNullOrEmpty(vfxDir) && full.StartsWith(vfxDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(vfxDir.Length + 1).Replace('\\', '/');
                if (!string.IsNullOrEmpty(vfxDir))
                {
                    // sibling folders of the effect (../Textures/x.png) stay relative too
                    string parent = Path.GetDirectoryName(vfxDir);
                    if (!string.IsNullOrEmpty(parent) && full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        return "../" + full.Substring(parent.Length + 1).Replace('\\', '/');
                }
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
                    if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        return full.Substring(root.Length + 1).Replace('\\', '/');
                }
                return full;
            }
            catch { return textureFullPath; }
        }

        // ------------------------------------------------------------------------------------------ presets

        /// <summary>A new effect with one soft, rising emitter (what "New Effect" creates).</summary>
        public static VfxAsset CreateDefault(string name = "New Effect")
        {
            var a = new VfxAsset { Name = name };
            a.Emitters.Add(CreateEmitter("Emitter"));
            return a;
        }

        public static VfxEmitter CreateEmitter(string name)
        {
            return new VfxEmitter
            {
                Name = name,
                Rate = 20f,
                Lifetime = new[] { 1.5f, 2.5f },
                Speed = new[] { 0.8f, 1.6f },
                Size = new[] { 0.2f, 0.4f },
                Rotation = new[] { 0f, 360f },
                RotationSpeed = new[] { -45f, 45f },
                Shape = new VfxShape { Type = VfxShapeType.Cone, Radius = 0.1f, Angle = 15f, Rotation = new[] { -90f, 0f, 0f } },
                SizeCurve = VfxCurve.Linear(0.6f, 1.4f),
                ColorGradient = VfxGradient.Of(new VfxGradientKey(0f, 1f, 1f, 1f, 0f), new VfxGradientKey(0.15f, 1f, 1f, 1f, 1f), new VfxGradientKey(1f, 1f, 1f, 1f, 0f)),
            };
        }
    }
}
