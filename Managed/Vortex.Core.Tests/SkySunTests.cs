using System.Text.RegularExpressions;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexTests
{
    /// <summary>The sun's disc in the sky (#499): the Skybox's Sun settings (Auto / On / Off, size, intensity, glow) with their
    /// defaults and clamps, skies saved before them reading the defaults, and a script's Sky.SetSun lasting until Sky.Clear.
    /// Headless: the picture itself is checked by the "sky sun disc" smoke.</summary>
    public static class SkySunTests
    {
        [Test]
        public static void ASkyboxHasSunSettings(TestContext t)
        {
            var s = new Skybox();
            t.Equal(SkySunDisc.Auto, s.SunDisc, "Auto by default: a gradient / 8-bit picture draws the disc, an .hdr keeps its own sun");
            t.Equal(0.53f, s.SunSize, "the real sun's angular diameter");
            t.Equal(1f, s.SunIntensity, "intensity 1");
            t.Equal(1f, s.SunGlow, "glow 1");
            s.SunSize = 0f; t.Equal(0.05f, s.SunSize, "at least 0.05°");
            s.SunSize = 90f; t.Equal(20f, s.SunSize, "at most 20°");
            s.SunSize = float.NaN; t.Equal(0.53f, s.SunSize, "NaN falls back to 0.53°");
            s.SunIntensity = -1f; t.Equal(0f, s.SunIntensity, "intensity never negative");
            s.SunGlow = 50f; t.Equal(10f, s.SunGlow, "glow at most 10");
        }

        [Test]
        public static void SkiesSavedBeforeReadTheDefaults(TestContext t)
        {
            var scene = new Scene { Name = "Dusk" };
            var e = new GameEntity(scene, "Sky");
            scene.Entities.Add(e);
            e.AddComponentDirect(new Skybox(e) { SkyboxType = SkyboxType.Texture, TexturePath = "Assets/Skies/dusk.hdr", SunDisc = SkySunDisc.On, SunSize = 1.5f, SunIntensity = 2f, SunGlow = 0.5f });

            var back = DataSerializer.FromBinary<Scene>(DataSerializer.ToBinary(scene)).Entities[0].GetComponent<Skybox>();
            t.Equal(SkySunDisc.On, back.SunDisc, "On is saved");
            t.Equal(1.5f, back.SunSize, "the size is saved");
            t.Equal(2f, back.SunIntensity, "the intensity is saved");
            t.Equal(0.5f, back.SunGlow, "the glow is saved");

            // a sky saved before #499 has no sun members: DataContract skips the field initialisers (the XML form of the same
            // contract the binary scene files use — a Skybox cannot go through the JSON serializer, its own isEnabled duplicates
            // the base member)
            var dcs = new System.Runtime.Serialization.DataContractSerializer(typeof(Skybox));
            var sw = new System.IO.StringWriter();
            using (var xw = System.Xml.XmlWriter.Create(sw)) dcs.WriteObject(xw, back);
            string xml = sw.ToString();
            t.True(xml.Contains("<sunSize>") && xml.Contains("<sunDisc>"), "the XML form carries the members");
            string old = Regex.Replace(xml, "<sun(Disc|Size|Intensity|Glow)>[^<]*</sun(Disc|Size|Intensity|Glow)>", "");
            t.False(old.Contains("<sunSize>") || old.Contains("<sunDisc>"), "the members are gone from the old-style XML");
            Skybox oldSky;
            using (var xr = System.Xml.XmlReader.Create(new System.IO.StringReader(old))) oldSky = (Skybox)dcs.ReadObject(xr);
            t.Equal(SkyboxType.Texture, oldSky.SkyboxType, "the rest of the old sky reads back");
            t.Equal(SkySunDisc.Auto, oldSky.SunDisc, "an old sky: Auto");
            t.Equal(0.53f, oldSky.SunSize, "an old sky: 0.53° (not 0, which would hide the disc)");
            t.Equal(1f, oldSky.SunIntensity, "an old sky: intensity 1");
            t.Equal(1f, oldSky.SunGlow, "an old sky: glow 1");
        }

        [Test]
        public static void SetSunLastsUntilSkyClear(TestContext t)
        {
            var before = SceneRenderService.ScriptSkySun; var beforeSky = SceneRenderService.ScriptSky;
            try
            {
                Vortex.Sky.SetSun(true, 1.2f, 3f, 0.5f);
                var o = SceneRenderService.ScriptSkySun;
                t.NotNull(o, "the script's sun disc is kept for the scene submits");
                t.True(o.Draw && o.SizeDegrees == 1.2f && o.Intensity == 3f && o.Glow == 0.5f, "its values");
                Vortex.Sky.SetSun(false);
                t.False(SceneRenderService.ScriptSkySun.Draw, "off");
                t.Equal(0.53f, SceneRenderService.ScriptSkySun.SizeDegrees, "the short form: the real sun's size");
                Vortex.Sky.Clear();
                t.True(SceneRenderService.ScriptSkySun == null && SceneRenderService.ScriptSky == null, "Sky.Clear hands the picture and the sun back to the Skybox");
            }
            finally { SceneRenderService.ScriptSkySun = before; SceneRenderService.ScriptSky = beforeSky; SceneRenderService.RuntimeDirty = false; }
        }
    }
}
