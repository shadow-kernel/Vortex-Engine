using System.Collections.Generic;
using Editor.Core.Services;

namespace VortexTests
{
    /// <summary>Shared plain materials (#364 D) are keyed by the look: the same colour / metallic / roughness must hash
    /// and compare equal (so 8k identical cubes share one material), any difference must not.</summary>
    public static class MaterialKeyTests
    {
        private static SceneRenderService.MaterialKey Key(float r, float g, float b, float a = 1f, float metallic = 0f, float roughness = 0.5f, long tex = -1)
            => new SceneRenderService.MaterialKey { R = r, G = g, B = b, A = a, Metallic = metallic, Roughness = roughness, Texture = tex };

        [Test]
        public static void SameLookIsTheSameKey(TestContext t)
        {
            var a = Key(0.8f, 0.2f, 0.1f);
            var b = Key(0.8f, 0.2f, 0.1f);
            t.True(a.Equals(b), "equal looks compare equal");
            t.Equal(a.GetHashCode(), b.GetHashCode(), "and hash the same");
            var d = new Dictionary<SceneRenderService.MaterialKey, long> { [a] = 7 };
            t.True(d.ContainsKey(b), "a dictionary finds the look under the second key");
        }

        [Test]
        public static void AnyDifferenceIsAnotherKey(TestContext t)
        {
            var a = Key(0.8f, 0.2f, 0.1f);
            t.False(a.Equals(Key(0.8f, 0.2f, 0.11f)), "colour");
            t.False(a.Equals(Key(0.8f, 0.2f, 0.1f, 0.5f)), "alpha");
            t.False(a.Equals(Key(0.8f, 0.2f, 0.1f, 1f, 1f)), "metallic");
            t.False(a.Equals(Key(0.8f, 0.2f, 0.1f, 1f, 0f, 0.9f)), "roughness");
            t.False(a.Equals(Key(0.8f, 0.2f, 0.1f, 1f, 0f, 0.5f, 3)), "texture");
        }
    }
}
