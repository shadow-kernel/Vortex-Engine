using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Audio.SoundStudio;
using Editor.Core.Data;
using Editor.Core.Services;

namespace VortexEditor.Shell.Audio
{
    /// <summary>
    /// Editor smoke check of the Sound Studio (VORTEX_SMOKE_ONLY=sound studio): the offline procedural backend makes two
    /// takes, one is saved into the library + project with its recipe, and "Open in Sound Studio" reloads the recipe.
    /// </summary>
    internal static class SoundStudioSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("sound studio", Run);

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("sound studio smoke: " + why); return false; }
            if (ProjectData.Current == null) return Fail("no project");
            var w = SoundStudioWindow.Open();
            try
            {
                await SmokeRegistry.Settle(600);
                // offline backend, no Claude: no keys needed
                w.LoadRecipe(new SoundRecipe { Prompt = "short metal impact in a basement with reverb", Backend = "procedural", DurationSeconds = 1.2, Seed = 1 }, null);
                w.UseClaude(false);
                await w.Generate();
                for (int i = 0; i < 50 && w.Busy; i++) await Task.Delay(100);
                if (w.Takes.Count < 2) return Fail("expected 2 takes, got " + w.Takes.Count + " (" + w.LastError + ")");
                await SmokeRegistry.Settle(500);
                SmokeRegistry.Capture(w, "sound_studio.png");
                var r = w.SaveTake(0, addToProject: true);
                if (!r.Success) return Fail("save: " + r.Error);
                var e = GlobalAssetDatabase.Instance.Get(r.Entry.Id);
                var recipe = SoundRecipe.FromJson(e.Recipe);
                if (recipe?.Prompt == null || recipe.Backend != "procedural") return Fail("library entry has no recipe");
                if (r.ProjectPath == null || !File.Exists(r.ProjectPath)) return Fail("not in the project");
                var meta = Editor.Core.Serialization.DataSerializer.LoadFromJson<AssetMetadata>(r.ProjectPath + AssetDatabase.MetaFileExtension);
                if (SoundRecipe.FromJson(meta?.Recipe) == null) return Fail("project .vmeta has no recipe");
                SoundStudioWindow.Open(recipe, e.Hash);
                log.Log("sound studio smoke: OK → " + Path.GetFileName(r.ProjectPath));
                try { File.Delete(r.ProjectPath); File.Delete(r.ProjectPath + AssetDatabase.MetaFileExtension); } catch { }
                GlobalAssetDatabase.Instance.Delete(new[] { e.Id }, out _);
                return true;
            }
            finally { w.Close(); }
        }
    }
}
