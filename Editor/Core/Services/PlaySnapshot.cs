using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components.Lighting;

namespace Editor.Core.Services
{
    /// <summary>
    /// Play is non-destructive: what scripts change while playing is put back on Stop. Both editors snapshot
    /// transforms and mesh colours themselves; this holds the component state scripts can write beyond that — the
    /// Light (on/off, intensity, range, spot angles, colour, shadows), which a flickering light or a flashlight changes
    /// every frame and which was otherwise saved into the scene after play.
    /// </summary>
    public sealed class PlaySnapshot
    {
        private readonly List<Action> _restore = new List<Action>();

        /// <summary>Remember the scene's component state (call before scripts start).</summary>
        public static PlaySnapshot Take(Scene scene)
        {
            var snap = new PlaySnapshot();
            if (scene?.Entities != null)
                foreach (var e in scene.Entities) snap.Walk(e);
            return snap;
        }

        private void Walk(GameEntity e)
        {
            if (e == null) return;
            var l = e.GetComponent<Light>();
            if (l != null)
            {
                bool on = l.IsEnabled;
                float intensity = l.Intensity, range = l.Range, spot = l.SpotAngle, inner = l.InnerSpotAngle;
                float r = l.ColorR, g = l.ColorG, b = l.ColorB, strength = l.ShadowStrength;
                var shadows = l.ShadowType;
                _restore.Add(() =>
                {
                    l.IsEnabled = on; l.Intensity = intensity; l.Range = range; l.SpotAngle = spot; l.InnerSpotAngle = inner;
                    l.ColorR = r; l.ColorG = g; l.ColorB = b; l.ShadowStrength = strength; l.ShadowType = shadows;
                });
            }
            if (e.Children != null)
                foreach (var c in e.Children) Walk(c);
        }

        /// <summary>Put everything back (call after the scripts' OnDestroy ran).</summary>
        public void Restore()
        {
            foreach (var r in _restore)
            {
                try { r(); } catch { /* a component torn down with its entity */ }
            }
            _restore.Clear();
        }
    }
}
