using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>The bone-palette budget (#415): the submeshes of an animated character share ONE palette in the GPU buffer.
    /// A crowd of 300 copies of the scene's first animated character (its scripts, behaviour trees and physics switched off)
    /// plays: no skinned draw falls back to the bind pose, and the buffer holds characters × bones matrices — not × submeshes,
    /// which used to fit only ~188 six-submesh characters. Runs on the real backend; skips in a project without an animated
    /// character (the Tactical Shooter's bots are one).</summary>
    internal static class BonePaletteSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("bone palette budget", Run);

        private const int Crowd = 300;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("bone palette budget: no scene — skipped"); return true; }
            if (!VortexAPI.TryGetBonePaletteStats(out _, out _, out _)) { log.Log("bone palette budget: the engine library has no palette stats — skipped"); return true; }

            GameEntity source = null; int bones = 0;
            foreach (var e in scene.Entities.ToList()) { FindCharacter(e, ref source, ref bones); if (source != null) break; }
            if (source == null) { log.Log("bone palette budget: no animated character in the scene — skipped"); return true; }
            int submeshes = Renderers(source).Count();

            var crowd = new List<GameEntity> { source };
            var copies = new List<GameEntity>();
            bool playing = false, ok = true;
            try
            {
                while (crowd.Count < Crowd)
                {
                    var made = EditorCommands.DuplicateEntities(crowd.Take(Crowd - crowd.Count).ToList());
                    if (made.Count == 0) { log.LogError("bone palette budget: duplicating '" + source.Name + "' failed"); return false; }
                    crowd.AddRange(made); copies.AddRange(made);
                }
                // a parade ground next to the original: 20 x 15 characters, 2 m apart; everything that would make 300 bots
                // fight, walk or fall is switched off on the copies — only their Animators run, all a looping clip at
                // slightly different speeds, so every character has a pose of its own (characters in the same pose hand over
                // identical palettes, which the renderer rightly keeps once: one without a playing clip draws the skeleton's
                // shared bind-pose palette, and after a one-shot clip — a death, a reload — every copy holds its last frame)
                string clip = MostMotion(source.GetComponent<Animator>());
                if (clip == null) { log.Log("bone palette budget: '" + source.Name + "' has no looping clip — skipped"); return true; }
                var origin = source.Transform.LocalPosition;
                for (int i = 0; i < copies.Count; i++)
                {
                    var c = copies[i];
                    c.Transform.LocalPosition = new Vector3(origin.X + 3f + (i % 20) * 2f, origin.Y, origin.Z + (i / 20) * 2f);
                    foreach (var comp in Components(c))
                        if (!(comp is Animator) && !(comp is MeshRenderer) && !(comp is Editor.ECS.Components.Transform)) comp.IsEnabled = false;
                    var anim = c.GetComponent<Animator>();
                    anim.DefaultClip = clip;
                    anim.PlayOnStart = true;
                    anim.Speed = 0.6f + 0.004f * i;
                }

                EditorCommands.Play();
                playing = PlayModeService.Instance.State == PlayState.Playing;
                if (!playing) { log.LogError("bone palette budget: play did not start"); return false; }
                // wait until the crowd animates apart: the slowest and the fastest copy then hold different poses
                string meshPath = Renderers(source).First().MeshPath;
                bool animating = false;
                for (int i = 0; i < 100 && !animating; i++)
                {
                    await Task.Delay(100);
                    AnimationService.Instance.TryGetPalette(copies[0], meshPath, out var a, out _);
                    AnimationService.Instance.TryGetPalette(copies[copies.Count - 1], meshPath, out var b, out _);
                    animating = a != null && b != null && !a.SequenceEqual(b);
                }
                if (!animating) { log.LogError("bone palette budget: the crowd did not start animating within 10 s"); return false; }
                await SmokeRegistry.Settle(500);
                int poses = DistinctPoses(crowd, meshPath);
                if (poses < Crowd / 2) { log.LogError("bone palette budget: only " + poses + " different poses in the crowd of " + Crowd + " — the test needs one per character"); return false; }

                // every other animated character of the running scene (the player's own arms, the original bots), one palette each
                int others = 0;
                foreach (var e in scene.Entities.ToList()) CountOthers(e, crowd, ref others);
                VortexAPI.TryGetBonePaletteStats(out int used, out int capacity, out int fallbacks);
                log.Log("bone palette budget: " + Crowd + " x '" + source.Name + "' (" + bones + " bones, " + submeshes + " submeshes, '" + clip + "', " + poses
                    + " poses) + " + others + " matrices of other characters -> " + used + " of " + capacity + " matrices used, " + fallbacks
                    + " bind-pose draws (per-submesh copies needed " + (Crowd * bones * submeshes) + ")");
                if (fallbacks != 0) { log.LogError("bone palette budget: " + fallbacks + " skinned draws in view fell back to the bind pose"); ok = false; }
                // (characters that happen to share a pose share one palette: at least half of the crowd must show up)
                if (used < Crowd * bones / 2) { log.LogError("bone palette budget: the crowd's palettes are missing from the buffer"); ok = false; }
                if (used > Crowd * bones + others) { log.LogError("bone palette budget: more than one palette copy per character — the submeshes do not share it"); ok = false; }
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(400); }
                if (copies.Count > 0) EditorCommands.DeleteEntities(copies);
                await SmokeRegistry.Settle(200);
            }
            return ok;
        }

        // the first active world-layer entity with an enabled Animator, clips to play and a skinned model (the palette the
        // renderer gets for it) — not a first-person viewmodel, which only draws while its weapon is equipped
        private static void FindCharacter(GameEntity e, ref GameEntity found, ref int bones)
        {
            if (e == null || found != null || !e.IsActive) return;
            var anim = e.GetComponent<Animator>();
            if (anim != null && anim.IsEnabled && anim.Clips.Count > 0 && e.Transform != null && Renderers(e).Any() && Renderers(e).All(r => r.RenderLayer == 0))
                foreach (var mr in Renderers(e))
                    if (AnimationService.Instance.TryGetPalette(e, mr.MeshPath, out _, out int n) && n > 0) { found = e; bones = n; return; }
            if (e.Children != null) foreach (var c in e.Children.ToList()) FindCharacter(c, ref found, ref bones);
        }

        // the looping clip of the animator with the most keys (the largest file): an aim or walk cycle rather than an idle that
        // barely moves — never a one-shot, which leaves every copy in the same last frame. Null when no clip loops.
        private static string MostMotion(Animator anim)
        {
            string root = ProjectData.Current?.Path ?? "", best = null;
            long size = -1;
            foreach (var c in anim.Clips)
            {
                var clip = AnimationService.Instance.GetClip(c.Path);
                if (clip == null || !clip.Loop) continue;
                long n = 0;
                try { n = new System.IO.FileInfo(System.IO.Path.Combine(root, c.Path ?? "")).Length; } catch { }
                if (n > size) { size = n; best = c.Name; }
            }
            return best;
        }

        // how many different poses the crowd hands over (the palettes the renderer keeps apart)
        private static int DistinctPoses(List<GameEntity> crowd, string meshPath)
        {
            var poses = new HashSet<long>();
            foreach (var c in crowd)
            {
                if (!AnimationService.Instance.TryGetPalette(c, meshPath, out var p, out _) || p == null) continue;
                long h = 17;
                for (int k = 0; k < p.Length; k++) h = h * 31 + BitConverter.SingleToInt32Bits(p[k]);
                poses.Add(h);
            }
            return poses.Count;
        }

        private static void CountOthers(GameEntity e, List<GameEntity> crowd, ref int matrices)
        {
            if (e == null || crowd.Contains(e)) return;
            var anim = e.GetComponent<Animator>();
            if (anim != null && anim.IsEnabled)
                foreach (var mr in Renderers(e))
                    if (AnimationService.Instance.TryGetPalette(e, mr.MeshPath, out _, out int n) && n > 0) { matrices += n; break; }
            if (e.Children != null) foreach (var c in e.Children.ToList()) CountOthers(c, crowd, ref matrices);
        }

        private static IEnumerable<MeshRenderer> Renderers(GameEntity e)
        {
            var mr = e.GetComponent<MeshRenderer>();
            if (mr != null && !string.IsNullOrEmpty(mr.MeshPath)) yield return mr;
            if (e.Children != null)
                foreach (var c in e.Children.ToList())
                    foreach (var r in Renderers(c)) yield return r;
        }

        private static IEnumerable<Editor.ECS.Component> Components(GameEntity e)
        {
            foreach (var comp in e.Components.ToList()) yield return comp;
            if (e.Children != null)
                foreach (var c in e.Children.ToList())
                    foreach (var comp in Components(c)) yield return comp;
        }
    }
}
