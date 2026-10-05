using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using VortexEditor.Panels;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// Editor smoke check of the license check before builds (VORTEX_SMOKE_ONLY=license check): a NonCommercial, a
    /// ShareAlike and a CC-BY asset in the project → the scan groups them and credits the BY ones, the dialog keeps
    /// "Build Anyway" disabled until the ShareAlike confirmation, and Show jumps to the asset in the Asset Browser.
    /// </summary>
    internal static class LicenseCheckSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("license check", Run);

        private static string Asset(string dir, string name, string license)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, "license smoke " + license);
            DataSerializer.SaveAsJson(new AssetMetadata { Guid = Guid.NewGuid(), License = license, Author = "Smoke Tester", Source = "Smoke" }, path + AssetDatabase.MetaFileExtension);
            return path;
        }

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("license check smoke: " + why); return false; }
            var p = ProjectData.Current;
            if (p == null) return Fail("no project");
            string dir = Path.Combine(p.Path, "Assets", "_LicenseSmoke");
            Directory.CreateDirectory(dir);
            LicenseCheckDialog d = null;
            try
            {
                string nc = Asset(dir, "nc_whisper.txt", "CC-BY-NC-4.0");
                Asset(dir, "sa_crate.txt", "CC-BY-SA-4.0");
                Asset(dir, "by_planks.txt", "CC-BY-4.0");
                var audit = await Task.Run(() => LicenseAudit.Scan(p.Path));
                if (audit.NonCommercial.Count(a => a.RelPath.Contains("_LicenseSmoke")) != 1 || audit.ShareAlike.Count(a => a.RelPath.Contains("_LicenseSmoke")) != 1)
                    return Fail("scan did not group the NonCommercial / ShareAlike assets");
                if (audit.Credits.Count(a => a.RelPath.Contains("_LicenseSmoke")) != 3) return Fail("CC-BY assets are not all credited");
                if (!LicenseAudit.CreditsMarkdown(audit, p.Name).Contains("by_planks")) return Fail("CREDITS.md misses a CC-BY asset");

                d = new LicenseCheckDialog(audit, p.Path);
                d.Show(EditorWindows.Owner);
                await SmokeRegistry.Settle(500);
                if (d.BuildButton.IsEnabled) return Fail("Build Anyway is enabled before the ShareAlike confirmation");
                d.ViralBox.IsChecked = true;
                if (!d.BuildButton.IsEnabled) return Fail("Build Anyway stays disabled after the confirmation");
                SmokeRegistry.Capture(d, "license_check.png");

                d.JumpTo(nc);
                d = null;
                var panel = AssetBrowserPanel.Current;
                for (int i = 0; i < 30 && !(panel?.SelectedAssets.Any(t => string.Equals(t.FullPath, nc, StringComparison.OrdinalIgnoreCase)) ?? false); i++) await Task.Delay(100);
                if (panel == null || !panel.SelectedAssets.Any(t => string.Equals(t.FullPath, nc, StringComparison.OrdinalIgnoreCase)))
                    return Fail("Show did not select the asset in the Asset Browser (folder " + panel?.CurrentFolder + ")");
                log.Log("license check smoke: OK");
                return true;
            }
            finally
            {
                d?.Close();
                try { Directory.Delete(dir, true); } catch { }
                try { AssetDatabase.Instance.Refresh(); } catch { }
                AssetBrowserPanel.Current?.Navigate(Path.Combine(p.Path, "Assets"));
            }
        }
    }
}
