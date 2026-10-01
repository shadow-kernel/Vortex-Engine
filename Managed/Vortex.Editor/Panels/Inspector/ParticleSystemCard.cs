using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Particles;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using VortexEditor.Shell;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>
    /// Inspector card of the Particle System component: the .vfx effect (pick / drop / create / edit in the VFX editor),
    /// how it plays (on start, loop override, simulation space, first-person layer, seed, speed), and Play / Stop /
    /// Restart buttons that run it in the editor viewport without entering Play mode.
    /// </summary>
    internal static class ParticleSystemCard
    {
        private static readonly string[] VfxPatterns = { "*.vfx" };

        [ModuleInitializer]
        internal static void Register()
        {
            ComponentEditors.Custom[typeof(ParticleSystem)] = (c, e) => Rows((ParticleSystem)c, e ?? c.Entity);
        }

        private static IEnumerable<Control> Rows(ParticleSystem ps, GameEntity entity)
        {
            ParticleService.EnsureRegistered();
            var status = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center };
            Action update = () =>
            {
                if (!ParticleService.Available) { status.Text = "Particles are not available in this engine build"; return; }
                if (string.IsNullOrEmpty(ps.VfxPath)) { status.Text = "No effect assigned"; return; }
                string full = ParticleService.ResolveAssetPath(ps.VfxPath);
                if (full == null || !File.Exists(full)) { status.Text = "Effect not found: " + ps.VfxPath; return; }
                status.Text = ParticleService.IsPlaying(ps) ? ParticleService.AliveCount(ps).ToString("N0") + " particles alive" : "Stopped";
            };
            update();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) => { try { update(); } catch { } };
            status.AttachedToVisualTree += (s, e) => timer.Start();
            status.DetachedFromVisualTree += (s, e) => timer.Stop();
            Refreshers[status] = update;

            yield return Row("Effect", AssetPath(() => ps.VfxPath, v => { ps.VfxPath = string.IsNullOrEmpty(v) ? null : v.Replace('\\', '/'); update(); }, "Visual Effect", VfxPatterns,
                () => AssetPickerDialog.Pick("Visual Effect", VfxPatterns, ParticleService.ResolveAssetPath(ps.VfxPath))), "The .vfx to play (drop one from the Project panel).");

            var edit = Ghost("Edit Effect…", () =>
            {
                string full = ParticleService.ResolveAssetPath(ps.VfxPath);
                if (full != null && File.Exists(full)) EditorWindows.VfxEditor(full);
                else EditorCommands.Toast("Assign an effect first (or create a new one)");
            }, "Open the effect in the VFX editor (emitters, live preview)");
            var create = Ghost("New Effect…", () =>
            {
                string root = ProjectData.Current?.Path; if (root == null) return;
                string path = VfxEditorWindow.CreateNew(Path.Combine(root, "Assets", "VFX"), entity?.Name ?? "NewEffect");
                ps.VfxPath = ToProjectRelative(path).Replace('\\', '/');
                RefreshAll();
                EditorWindows.VfxEditor(path);
            }, "Create a new .vfx in Assets/VFX, assign it and open it in the VFX editor");
            yield return Row("", Actions(edit, create));

            yield return Row("Play on start", Bool(() => ps.PlayOnStart, v => ps.PlayOnStart = v), "Start emitting when play starts (off = a script calls Vfx.Play / Vfx.Burst).");
            yield return Row("Loop", Choice(() => ps.Loop, v => ps.Loop = v, "As authored", "Always loop", "Play once"));
            yield return Row("Simulation space", Choice(() => ps.SimulationSpace, v => ps.SimulationSpace = v, "As authored", "World (trails behind)", "Local (moves with it)"));
            yield return Row("Render layer", Choice(() => ps.RenderLayer, v => ps.RenderLayer = v, "World", "First-person (viewmodel)"),
                "First-person: drawn with the weapon (own field of view, never clips into walls) — muzzle flashes of the player's gun.");
            yield return Row("Speed", SliderRow(() => ps.SimulationSpeed, v => ps.SimulationSpeed = v, 0, 3, "0.00"));
            yield return Row("Seed", IntBox(() => ps.Seed, v => ps.Seed = v), "0 = different every play; any other value repeats the same sequence.");
            yield return Row("Preview in editor", Bool(() => ps.PreviewInEditor, v => { ps.PreviewInEditor = v; update(); }), "Simulate in the Scene view while editing.");

            var play = Ghost("▶ Play", () => { ParticleService.EnsureRegistered(); if (!ps.PreviewInEditor) ps.PreviewInEditor = true; ParticleService.Restart(ps); RefreshAll(); }, "Run the effect in the Scene view now");
            var stop = Ghost("■ Stop", () => { ParticleService.Stop(ps, clear: true); update(); }, "Stop and clear the particles");
            yield return Row("Preview", Actions(play, stop));
            yield return Row("", status);
            yield return Hint("Scripts: Vfx.Play(entity) / Vfx.Burst(entity, n), or Vfx.SpawnAt(\"Assets/VFX/Impact_Metal.vfx\", position, rotation) for one-shots — the effect emits along its +Z.");
        }
    }
}
