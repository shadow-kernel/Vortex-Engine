using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude.Tools
{
    /// <summary>The scene's air and sky for the MCP server (the Open World template is built through these): the Skybox
    /// (gradient / HDR texture, exposure, ambient, the environment colours), the sun aligned to an HDRI's brightest pixel,
    /// and the per-scene environment settings (fog and height fog, SSAO, bloom, volumetrics, grading, vignette).</summary>
    [McpServerToolType, DisplayName("Environment")]
    public static class EnvironmentTools
    {
        private static Scene Scene() => ProjectData.Current?.ActiveScene ?? throw new ToolError("No scene is open.");

        [McpServerTool(Name = "set_sky", Idempotent = true)]
        [Description("Sets the scene's sky: a gradient (top / horizon / bottom colours) or an equirectangular HDR / image texture, its exposure and ambient " +
                     "strength. With a texture the gradient colours stay the ENVIRONMENT (what water and glossy surfaces reflect) — give them the picture's " +
                     "zenith / horizon / ground colours. align_sun turns the scene's directional light to the brightest pixel of the HDR (its sun). " +
                     "Creates a Sky entity with a Skybox component when the scene has none. Colours are linear rgb 0..1. One undo step.")]
        public static object SetSky(
            [Description("\"gradient\", \"texture\" or \"solid\" (default: keep)")] string type = null,
            [Description("Project-relative equirect .hdr / .png / .jpg (implies type texture)")] string texture = null,
            [Description("Exposure multiplier (default: keep; 0.7–1.0 for a midday HDR)")] float exposure = -1f,
            [Description("Ambient light strength (default: keep; ~1.2 for a desert day)")] float ambient = -1f,
            [Description("Zenith colour [r, g, b]")] float[] top_color = null,
            [Description("Horizon colour [r, g, b]")] float[] horizon_color = null,
            [Description("Ground / bottom colour [r, g, b]")] float[] bottom_color = null,
            [Description("Turn the directional light to the HDR texture's sun (default false)")] bool align_sun = false,
            [Description("Sun intensity when aligning (default: keep)")] float sun_intensity = -1f,
            [Description("Only report")] bool dry_run = false)
        {
            var scene = Scene();
            Skybox sky = null; GameEntity skyEntity = null;
            foreach (var e in SceneModel.All(scene)) { var s = e.GetComponent<Skybox>(); if (s != null) { sky = s; skyEntity = e; break; } }
            if (dry_run) return new { sky = sky != null ? skyEntity.Name : "(would create)", type, texture, exposure, ambient, align_sun };
            if (sky == null)
            {
                skyEntity = new GameEntity(scene, "Sky");
                sky = new Skybox(skyEntity) { IsEnabled = true, SkyboxType = SkyboxType.Gradient };
                skyEntity.AddComponent(sky);
                scene.AddEntity(skyEntity);
            }
            if (!string.IsNullOrEmpty(texture))
            {
                string full = Path.IsPathRooted(texture) ? texture : Path.Combine(ProjectData.Current.Path, texture);
                if (!File.Exists(full)) throw new ToolError("texture not found: " + texture);
                sky.TexturePath = texture.Replace('\\', '/');
                sky.SkyboxType = SkyboxType.Texture;
            }
            if (!string.IsNullOrEmpty(type))
            {
                switch (type.ToLowerInvariant())
                {
                    case "gradient": sky.SkyboxType = SkyboxType.Gradient; break;
                    case "texture": sky.SkyboxType = SkyboxType.Texture; break;
                    case "solid": sky.SkyboxType = SkyboxType.SolidColor; break;
                    default: throw new ToolError("type must be gradient, texture or solid.");
                }
            }
            if (exposure >= 0f) sky.Exposure = exposure;
            if (ambient >= 0f) sky.AmbientIntensity = ambient;
            if (top_color != null && top_color.Length >= 3) { sky.TopColorR = top_color[0]; sky.TopColorG = top_color[1]; sky.TopColorB = top_color[2]; }
            if (horizon_color != null && horizon_color.Length >= 3) { sky.HorizonColorR = horizon_color[0]; sky.HorizonColorG = horizon_color[1]; sky.HorizonColorB = horizon_color[2]; }
            if (bottom_color != null && bottom_color.Length >= 3) { sky.BottomColorR = bottom_color[0]; sky.BottomColorG = bottom_color[1]; sky.BottomColorB = bottom_color[2]; }
            sky.IsEnabled = true;

            object sun = null;
            if (align_sun)
            {
                if (sky.SkyboxType != SkyboxType.Texture || string.IsNullOrEmpty(sky.TexturePath)) throw new ToolError("align_sun needs a texture sky.");
                string full = Path.IsPathRooted(sky.TexturePath) ? sky.TexturePath : Path.Combine(ProjectData.Current.Path, sky.TexturePath);
                Vector3 toSun; float azimuth, elevation;
                if (!HdrSun.Find(full, out toSun, out azimuth, out elevation)) throw new ToolError("could not read the sun from " + sky.TexturePath + " (Radiance .hdr only).");
                GameEntity sunEntity = null; Light light = null;
                foreach (var e in SceneModel.All(scene)) { var l = e.GetComponent<Light>(); if (l != null && l.LightType == LightType.Directional) { light = l; sunEntity = e; break; } }
                if (light == null)
                {
                    sunEntity = new GameEntity(scene, "Sun");
                    light = new Light(sunEntity, LightType.Directional) { Intensity = 4f, ShadowType = ShadowType.Soft };
                    sunEntity.AddComponent(light);
                    scene.AddEntity(sunEntity);
                }
                float yaw, pitch;
                TransformMath.LookAngles(new Vector3(0f, 0f, 0f), new Vector3(-toSun.X, -toSun.Y, -toSun.Z), out yaw, out pitch);
                if (sunEntity.Transform != null) sunEntity.Transform.LocalRotation = new Vector3(pitch, yaw, 0f);
                if (sun_intensity >= 0f) light.Intensity = sun_intensity;
                sun = new { entity = sunEntity.Name, azimuth_deg = Math.Round(azimuth, 1), elevation_deg = Math.Round(elevation, 1), rotation = new[] { pitch, yaw, 0f }, intensity = light.Intensity };
            }
            ToolContext.UndoLabel = "sky";
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return new { entity = skyEntity.Name, type = sky.SkyboxType.ToString(), texture = sky.TexturePath, exposure = sky.Exposure, ambient = sky.AmbientIntensity,
                         top = new[] { sky.TopColorR, sky.TopColorG, sky.TopColorB }, horizon = new[] { sky.HorizonColorR, sky.HorizonColorG, sky.HorizonColorB }, bottom = new[] { sky.BottomColorR, sky.BottomColorG, sky.BottomColorB }, sun };
        }

        [McpServerTool(Name = "set_environment", Idempotent = true)]
        [Description("Sets the scene's environment settings (saved with the scene): exp² distance fog with optional height fog, SSAO, bloom, volumetric fog, " +
                     "colour grading and vignette. Every argument is optional — only the ones given change. Fog density 0.002 ≈ a 500 m desert haze, 0.02 a wood, " +
                     "0.08 a dungeon; height fog thins above fog_height_y by exp(-falloff·Δy). One undo step.")]
        public static object SetEnvironment(
            [Description("Fog on / off")] bool? fog = null,
            [Description("Fog density (exp², per metre)")] float fog_density = -1f,
            [Description("Fog colour [r, g, b]")] float[] fog_color = null,
            [Description("Height (m) up to which the fog is uniform")] float fog_height_y = float.NaN,
            [Description("Height falloff per metre above it (0 = no height fog)")] float fog_height_falloff = -1f,
            [Description("SSAO on / off")] bool? ao = null,
            [Description("SSAO radius (m)")] float ao_radius = -1f,
            [Description("SSAO intensity")] float ao_intensity = -1f,
            [Description("Bloom on / off")] bool? bloom = null,
            [Description("Bloom threshold")] float bloom_threshold = -1f,
            [Description("Bloom intensity")] float bloom_intensity = -1f,
            [Description("Volumetric fog on / off")] bool? volumetric = null,
            [Description("Volumetric density")] float volumetric_density = -1f,
            [Description("Volumetric sun strength (god rays)")] float volumetric_sun = -1f,
            [Description("Colour grading on / off")] bool? grading = null,
            [Description("Grading exposure (stops, 0 = neutral)")] float exposure = float.NaN,
            [Description("Grading contrast (1 = neutral)")] float contrast = -1f,
            [Description("Grading saturation (1 = neutral)")] float saturation = -1f,
            [Description("Grading temperature (-1 cool .. 1 warm)")] float temperature = float.NaN,
            [Description("Vignette on / off")] bool? vignette = null,
            [Description("Vignette intensity")] float vignette_intensity = -1f,
            [Description("Only report")] bool dry_run = false)
        {
            var scene = Scene();
            var st = scene.Settings;
            if (dry_run) return Info(st);
            if (fog.HasValue) st.FogEnabled = fog.Value;
            if (fog_density >= 0f) st.FogDensity = fog_density;
            if (fog_color != null && fog_color.Length >= 3) { st.FogR = fog_color[0]; st.FogG = fog_color[1]; st.FogB = fog_color[2]; }
            if (!float.IsNaN(fog_height_y)) st.FogHeightY = fog_height_y;
            if (fog_height_falloff >= 0f) st.FogHeightFalloff = fog_height_falloff;
            if (ao.HasValue) st.AoEnabled = ao.Value;
            if (ao_radius >= 0f) st.AoRadius = ao_radius;
            if (ao_intensity >= 0f) st.AoIntensity = ao_intensity;
            if (bloom.HasValue) st.BloomEnabled = bloom.Value;
            if (bloom_threshold >= 0f) st.BloomThreshold = bloom_threshold;
            if (bloom_intensity >= 0f) st.BloomIntensity = bloom_intensity;
            if (volumetric.HasValue) st.VolumetricEnabled = volumetric.Value;
            if (volumetric_density >= 0f) st.VolumetricDensity = volumetric_density;
            if (volumetric_sun >= 0f) st.VolumetricSun = volumetric_sun;
            if (grading.HasValue) st.GradeEnabled = grading.Value;
            if (!float.IsNaN(exposure)) st.Exposure = exposure;
            if (contrast >= 0f) st.Contrast = contrast;
            if (saturation >= 0f) st.Saturation = saturation;
            if (!float.IsNaN(temperature)) st.Temperature = temperature;
            if (vignette.HasValue) st.VignetteEnabled = vignette.Value;
            if (vignette_intensity >= 0f) st.VignetteIntensity = vignette_intensity;
            st.Apply();
            scene.IsDirty = true;
            ToolContext.UndoLabel = "environment";
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            return Info(st);
        }

        [McpServerTool(Name = "environment_info", ReadOnly = true, Idempotent = true)]
        [Description("The scene's environment settings (fog, SSAO, bloom, volumetrics, grading, vignette) and its sky and directional light.")]
        public static object EnvironmentInfo()
        {
            var scene = Scene();
            object sky = null, sun = null;
            foreach (var e in SceneModel.All(scene))
            {
                var s = e.GetComponent<Skybox>();
                if (s != null && sky == null) sky = new { entity = e.Name, type = s.SkyboxType.ToString(), texture = s.TexturePath, exposure = s.Exposure, ambient = s.AmbientIntensity };
                var l = e.GetComponent<Light>();
                if (l != null && l.LightType == LightType.Directional && sun == null)
                    sun = new { entity = e.Name, rotation = e.Transform != null ? new[] { e.Transform.LocalRotation.X, e.Transform.LocalRotation.Y, e.Transform.LocalRotation.Z } : null, intensity = l.Intensity, color = new[] { l.ColorR, l.ColorG, l.ColorB }, shadows = l.ShadowType.ToString() };
            }
            return new { settings = Info(scene.Settings), sky, sun };
        }

        private static object Info(SceneSettings st) => new
        {
            fog = new { on = st.FogEnabled, density = st.FogDensity, color = new[] { st.FogR, st.FogG, st.FogB }, height_y = st.FogHeightY, height_falloff = st.FogHeightFalloff },
            ao = new { on = st.AoEnabled, radius = st.AoRadius, intensity = st.AoIntensity },
            bloom = new { on = st.BloomEnabled, threshold = st.BloomThreshold, intensity = st.BloomIntensity },
            volumetric = new { on = st.VolumetricEnabled, density = st.VolumetricDensity, sun = st.VolumetricSun },
            grading = new { on = st.GradeEnabled, exposure = st.Exposure, contrast = st.Contrast, saturation = st.Saturation, temperature = st.Temperature },
            vignette = new { on = st.VignetteEnabled, intensity = st.VignetteIntensity }
        };
    }

    /// <summary>Reads a Radiance RGBE (.hdr) picture and finds its sun — the brightest texel — as a world direction in the
    /// engine's sky mapping (u = atan2(x, z) / 2π + 0.5, v from the top = zenith).</summary>
    internal static class HdrSun
    {
        public static bool Find(string path, out Vector3 toSun, out float azimuthDeg, out float elevationDeg)
        {
            toSun = new Vector3(0f, 1f, 0f); azimuthDeg = 0f; elevationDeg = 90f;
            try
            {
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    string line = ReadLine(br);
                    if (!line.StartsWith("#?")) return false;
                    int w = 0, h = 0;
                    while (true)
                    {
                        line = ReadLine(br);
                        if (line == null) return false;
                        if (line.StartsWith("-Y"))
                        {
                            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            h = int.Parse(parts[1]); w = int.Parse(parts[3]);
                            break;
                        }
                    }
                    double best = -1; int bx = 0, by = 0;
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        if (!ReadScanline(br, row, w)) return false;
                        for (int x = 0; x < w; x++)
                        {
                            int e = row[x * 4 + 3];
                            if (e == 0) continue;
                            double f = Math.Pow(2.0, e - 136);
                            double lum = (row[x * 4] * 0.2126 + row[x * 4 + 1] * 0.7152 + row[x * 4 + 2] * 0.0722) * f;
                            if (lum > best) { best = lum; bx = x; by = y; }
                        }
                    }
                    azimuthDeg = 360f * (bx + 0.5f) / w;
                    elevationDeg = 90f - 180f * (by + 0.5f) / h;
                    double phi = (azimuthDeg / 360.0 - 0.5) * 2.0 * Math.PI, th = elevationDeg * Math.PI / 180.0;
                    toSun = new Vector3((float)(Math.Cos(th) * Math.Sin(phi)), (float)Math.Sin(th), (float)(Math.Cos(th) * Math.Cos(phi)));
                    return true;
                }
            }
            catch { return false; }
        }

        private static string ReadLine(BinaryReader br)
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                if (br.BaseStream.Position >= br.BaseStream.Length) return sb.Length > 0 ? sb.ToString() : null;
                char c = (char)br.ReadByte();
                if (c == '\n') return sb.ToString();
                sb.Append(c);
            }
        }

        // new-style RLE scanline (2 2 w_hi w_lo), else flat RGBE
        private static bool ReadScanline(BinaryReader br, byte[] row, int w)
        {
            byte a = br.ReadByte(), b = br.ReadByte(), c = br.ReadByte(), d = br.ReadByte();
            if (a != 2 || b != 2 || (c & 0x80) != 0 || ((c << 8) | d) != w)
            {
                row[0] = a; row[1] = b; row[2] = c; row[3] = d;
                for (int x = 1; x < w; x++) { row[x * 4] = br.ReadByte(); row[x * 4 + 1] = br.ReadByte(); row[x * 4 + 2] = br.ReadByte(); row[x * 4 + 3] = br.ReadByte(); }
                return true;
            }
            for (int ch = 0; ch < 4; ch++)
            {
                int x = 0;
                while (x < w)
                {
                    int count = br.ReadByte();
                    if (count > 128)
                    {
                        count -= 128; byte v = br.ReadByte();
                        for (int i = 0; i < count && x < w; i++, x++) row[x * 4 + ch] = v;
                    }
                    else for (int i = 0; i < count && x < w; i++, x++) row[x * 4 + ch] = br.ReadByte();
                }
            }
            return true;
        }
    }
}
