using System.Text.RegularExpressions;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Lighting;

namespace VortexTests
{
    /// <summary>The directional light's shadow distance (#466): a Light component setting the scene submit passes on (it fell
    /// back to the wrapper's 80 m every frame), the classic 80 m for lights saved before it existed, and a script's
    /// Lighting.SetDirectional that lasts — shadow values it leaves out are the scene sun's. Headless: no native engine.</summary>
    public static class ShadowDistanceTests
    {
        [Test]
        public static void ALightKeepsItsShadowDistance(TestContext t)
        {
            var l = new Light();
            t.Equal(80f, l.ShadowDistance, "a new light reaches the classic 80 m");
            l.ShadowDistance = 300f;
            t.Equal(300f, l.ShadowDistance, "300 m is kept");
            l.ShadowDistance = 0f;
            t.Equal(5f, l.ShadowDistance, "at least 5 m (the renderer's minimum)");
            l.ShadowDistance = float.NaN;
            t.Equal(80f, l.ShadowDistance, "NaN falls back to 80 m");
            l.ShadowDistance = 1e9f;
            t.Equal(10000f, l.ShadowDistance, "at most 10 km");
        }

        [Test]
        public static void SavedLightsRoundTripAndOldOnesReadEighty(TestContext t)
        {
            var scene = new Scene { Name = "Road" };
            var sun = new GameEntity(scene, "Sun");
            scene.Entities.Add(sun);
            sun.AddComponentDirect(new Light(sun, LightType.Directional) { ShadowDistance = 300f, ShadowType = ShadowType.Soft });

            var back = DataSerializer.FromBinary<Scene>(DataSerializer.ToBinary(scene));
            t.Equal(300f, back.Entities[0].GetComponent<Light>().ShadowDistance, "a saved scene keeps 300 m");

            // a prefab / scene written before #466 has no shadowDistance member: DataContract skips the field initialiser
            string json = DataSerializer.ToJson(sun);
            t.True(Regex.IsMatch(json, "\"shadowDistance\"\\s*:\\s*300"), "the JSON form carries the member: " + json.Substring(0, System.Math.Min(json.Length, 200)));
            string old = Regex.Replace(json, ",?\\s*\"shadowDistance\"\\s*:\\s*[0-9.eE+-]+", "");
            t.False(old.Contains("shadowDistance"), "the member is gone from the old-style JSON");
            var oldSun = DataSerializer.FromJson<GameEntity>(old);
            t.Equal(80f, oldSun.GetComponent<Light>().ShadowDistance, "an old light reads the classic 80 m (not 0, which the renderer clamps to 5 m)");
        }

        [Test]
        public static void AScriptSunTakesTheShadowValuesItLeavesOut(TestContext t)
        {
            var s = new SceneRenderService.SunOverride { DirY = -1f, Intensity = 3f };
            s.Resolve(true, true, 0.8f, 250f, out bool cast, out float strength, out float distance);
            t.True(cast, "left out: the scene sun's shadows stay on");
            t.Equal(0.8f, strength, "the scene sun's strength");
            t.Equal(250f, distance, "the scene sun's distance");

            s.Resolve(false, true, 0.8f, 250f, out cast, out strength, out distance);
            t.False(cast, "no scene sun: no shadows unless asked");
            t.Equal(1f, strength, "default strength 1");
            t.Equal(80f, distance, "default distance 80 m");

            var given = new SceneRenderService.SunOverride { CastShadows = false, ShadowStrength = 0.5f, ShadowDistance = 400f };
            given.Resolve(true, true, 0.8f, 250f, out cast, out strength, out distance);
            t.False(cast, "the script's castShadows wins");
            t.Equal(0.5f, strength, "the script's strength wins");
            t.Equal(400f, distance, "the script's distance wins");

            // a script's distance is clamped as the Light's is (the renderer only clamps the low end)
            new SceneRenderService.SunOverride { ShadowDistance = 1e7f }.Resolve(true, true, 1f, 80f, out _, out _, out distance);
            t.Equal(10000f, distance, "a script's distance is at most 10 km");
            new SceneRenderService.SunOverride { ShadowDistance = 1f }.Resolve(true, true, 1f, 80f, out _, out _, out distance);
            t.Equal(5f, distance, "at least 5 m");
            new SceneRenderService.SunOverride { ShadowDistance = float.NaN }.Resolve(true, true, 1f, 80f, out _, out _, out distance);
            t.Equal(80f, distance, "NaN: 80 m");
        }

        [Test]
        public static void SetDirectionalLastsUntilCleared(TestContext t)
        {
            var before = SceneRenderService.ScriptSun;
            try
            {
                Vortex.Lighting.SetDirectional(-0.3f, -0.8f, 0.5f, 1f, 0.9f, 0.8f, 5f, castShadows: true, shadowDistance: 300f);
                var s = SceneRenderService.ScriptSun;
                t.NotNull(s, "the script's sun is kept for the next scene-light submit");
                t.Equal(5f, s.Intensity, "its intensity");
                t.Equal(-0.8f, s.DirY, "its direction");
                t.Equal(true, s.CastShadows, "shadows asked for");
                t.Equal(300f, s.ShadowDistance, "its shadow distance");
                t.True(s.ShadowStrength == null, "the strength it left out stays the scene's");
                t.True(SceneRenderService.RuntimeDirty, "the scene is re-submitted (a shipped game's submit-once loop)");

                Vortex.Lighting.SetDirectional(0f, -1f, 0f, 1f, 1f, 1f, 2f);   // the old 7-argument call still compiles
                t.True(SceneRenderService.ScriptSun.CastShadows == null && SceneRenderService.ScriptSun.ShadowDistance == null,
                    "the short form leaves every shadow value to the scene");

                Vortex.Lighting.ClearDirectional();
                t.True(SceneRenderService.ScriptSun == null, "ClearDirectional hands the sun back to the scene");
            }
            finally { SceneRenderService.ScriptSun = before; SceneRenderService.RuntimeDirty = false; }
        }
    }
}
