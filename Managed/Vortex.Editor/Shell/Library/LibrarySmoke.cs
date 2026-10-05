using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Panels;
using VortexEditor.Panels.AssetBrowser;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// Editor smoke check of the global asset library (VORTEX_SMOKE_ONLY=library): files go into the library, the
    /// Library tab lists and finds them, a texture thumbnail renders, "Add to Project" copies with the library hash in
    /// the .vmeta, a second add is recognised, and the import dialog flags a file the library already has.
    /// The smoke run uses a private VORTEX_APPDATA_DIR, so the library it fills is a scratch one.
    /// </summary>
    internal static class LibrarySmoke
    {
        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("library smoke: " + why); return false; }
            var lib = GlobalAssetDatabase.Instance;
            if (!lib.IsAvailable) return Fail("library unavailable: " + lib.LastError);
            var view = LibraryView.Current;
            if (VortexEditor.Panels.LibraryPanel.Current == null || view == null) return Fail("no Library tab");
            string project = ProjectData.Current?.Path;
            if (project == null) return Fail("no project open");

            string dir = Path.Combine(Path.GetTempPath(), "vx-libsmoke-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string png = Path.Combine(dir, "smoke_checker.png");
            string wav = Path.Combine(dir, "smoke_beep.wav");
            WritePng(png);
            WriteWav(wav);

            int added = await view.AddFilesToLibrary(new[] { png, wav });
            if (added != 2) return Fail("expected 2 registrations, got " + added);

            EditorCommands.ShowLibrary("smoke_");
            await SmokeRegistry.Settle(900);
            if (!EditorCommands.Window.IsPanelVisible(MainWindow.PanelLibrary)) return Fail("the Library tab is not showing");
            if (view.ResultCount < 2) return Fail("Library tab lists " + view.ResultCount + " result(s) for “smoke_”");
            var texTile = view.Tiles.FirstOrDefault(t => t.Entry.Type == AssetType.Texture && t.Entry.Name == "smoke_checker");
            if (texTile == null) return Fail("texture tile missing");
            for (int i = 0; i < 40 && !texTile.HasThumbnail; i++) await Task.Delay(150);
            if (!texTile.HasThumbnail) return Fail("texture thumbnail did not arrive");
            if (!lib.HasThumbnail(texTile.Entry.Hash)) return Fail("thumbnail not cached in the library");
            view.SelectOnly(texTile);
            await SmokeRegistry.Settle(400);
            SmokeRegistry.Capture(EditorCommands.Window, "library_tab.png");

            var paths = await view.AddToProject(new List<LibraryEntry> { texTile.Entry }, null, false);
            if (paths.Count != 1 || !File.Exists(paths[0])) return Fail("Add to Project produced no file");
            var meta = Editor.Core.Serialization.DataSerializer.LoadFromJson<AssetMetadata>(paths[0] + AssetDatabase.MetaFileExtension);
            if (meta?.ContentHash != texTile.Entry.Hash) return Fail(".vmeta has no library hash");
            var again = LibraryProjects.AddToProject(lib, texTile.Entry.Id, project);
            if (again.Status != AddToProjectStatus.AlreadyInProject) return Fail("second add was not recognised (" + again.Status + ")");
            if (lib.Usages(texTile.Entry.Hash).All(u => !string.Equals(u.ProjectPath, GlobalAssetDatabase.NormalizeDir(project), StringComparison.OrdinalIgnoreCase)))
                return Fail("no usage recorded for the project");

            // the import dialog knows the file is in the library already
            var dlg = new AssetImportDialog(new[] { png }, null);
            dlg.Show();
            await dlg.LibraryCheck;
            await SmokeRegistry.Settle(300);
            SmokeRegistry.Capture(dlg, "library_import_banner.png");
            bool known = dlg.KnownInLibrary(png);
            dlg.Close();
            if (!known) return Fail("import dialog did not recognise a library file");

            // clean up: the project copy, the library entries
            try { File.Delete(paths[0]); File.Delete(paths[0] + AssetDatabase.MetaFileExtension); } catch { }
            lib.Delete(view.Tiles.Where(t => t.Name.StartsWith("smoke_", StringComparison.Ordinal)).Select(t => t.Id).ToList(), out _);
            EditorCommands.ShowLibrary("");
            EditorCommands.Window.ShowPanel(MainWindow.PanelProject);
            try { Directory.Delete(dir, true); } catch { }
            log.Log("library smoke: OK");
            return true;
        }

        private static void WritePng(string path)
        {
            var rtb = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext())
            {
                dc.FillRectangle(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), new Rect(0, 0, 64, 64));
                var dark = new SolidColorBrush(Color.FromRgb(0x3A, 0x7B, 0xD5));
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) if (((x + y) & 1) == 0) dc.FillRectangle(dark, new Rect(x * 8, y * 8, 8, 8));
            }
            rtb.Save(path);
        }

        /// <summary>0.25 s of a 440 Hz tone, 22.05 kHz mono 16-bit PCM.</summary>
        private static void WriteWav(string path)
        {
            const int rate = 22050, samples = rate / 4;
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + samples * 2);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(samples * 2);
                for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 8000 * (1 - (double)i / samples)));
            }
        }
    }
}
