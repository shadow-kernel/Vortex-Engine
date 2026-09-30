using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Every user-facing action of the editor in one place, so the menu bar, toolbar, context menus and
    /// keyboard shortcuts all run the same code path against the shared core.
    /// </summary>
    public static class EditorCommands
    {
        public static MainWindow Window { get; set; }
        private static EditorSession Session => EditorSession.Instance;
        private static Scene ActiveScene => ProjectData.Current?.ActiveScene;
        private static GameEntity Selected => SelectionService.Instance.SelectedEntity;

        // ------------------------------------------------------------ project
        public static void NewProject() => Window?.ShowProjectHub(createTab: true);
        public static void OpenProject() => Window?.ShowProjectHub(createTab: false);
        public static void SaveProject() { try { Session.SaveProject(); } catch (Exception ex) { Fail("Save failed", ex); } }
        public static void SaveAll() { try { Session.SaveAll(); } catch (Exception ex) { Fail("Save failed", ex); } }
        public static async Task CloseProject()
        {
            if (!Session.HasProject) return;
            if (await Dialogs.Confirm("Close project?", "Unsaved changes will be lost.", "Close", "Cancel", destructive: true))
            {
                Session.CloseProject();
                Window?.ShowProjectHub(createTab: false);
            }
        }
        public static void ProjectSettings() => Window?.OpenProjectSettings();
        public static async Task Exit()
        {
            if (Session.HasProject && !await Dialogs.Confirm("Quit Vortex Editor?", "Unsaved changes will be lost.", "Quit", "Cancel", destructive: true)) return;
            Window?.Close();
        }

        // ------------------------------------------------------------ build
        public static void Build() => Window?.OpenBuildDialog(runAfter: false);
        public static void BuildAndRun() => Window?.OpenBuildDialog(runAfter: true);

        // ------------------------------------------------------------ edit
        public static void Undo() => UndoRedoManager.Instance.Undo();
        public static void Redo() => UndoRedoManager.Instance.Redo();
        public static void Cut() { var e = Selected; if (e != null) { EntityClipboardService.Instance.Cut(new[] { e }); SelectionService.Instance.ClearSelection(); } }
        public static void Copy() { var e = Selected; if (e != null) EntityClipboardService.Instance.Copy(new[] { e }); }
        public static void Paste()
        {
            var scene = ActiveScene; if (scene == null || !EntityClipboardService.Instance.HasContent) return;
            var pasted = EntityClipboardService.Instance.Paste(scene, Selected);
            if (pasted != null && pasted.Count > 0) SelectionService.Instance.Select(pasted[pasted.Count - 1]);
            SceneRenderService.RuntimeDirty = true;
        }
        public static void Delete()
        {
            var e = Selected; if (e == null) return;
            Session.Hierarchy.SelectedEntity = e;
            Session.Hierarchy.DeleteEntityCommand.Execute(null);
            SceneRenderService.RuntimeDirty = true;
        }
        public static void Duplicate()
        {
            var e = Selected; if (e == null) return;
            Session.Hierarchy.SelectedEntity = e;
            Session.Hierarchy.DuplicateEntityCommand.Execute(null);
            SceneRenderService.RuntimeDirty = true;
        }
        public static void SelectAll() => Session.Hierarchy.SelectAllCommand.Execute(null);
        public static void Rename() => Window?.RenameSelected();
        public static void Find() => Window?.FocusHierarchySearch();

        // ------------------------------------------------------------ view
        public static void ToggleGrid() => EditorViewportService.Instance.ToggleGrid();
        public static void ToggleSnap() => EditorViewportService.Instance.ToggleSnapToGrid();
        public static void ToggleGizmos() => EditorViewportService.Instance.ToggleGizmos();
        public static void ToggleColliders() => EditorViewportService.Instance.ToggleColliders();
        /// <summary>Physics v2 (#106): draw the live Jolt shapes (cyan wire lines) over the play view.</summary>
        public static void TogglePhysicsDebug()
            => Editor.Core.Services.Physics.PhysicsService.ShowPhysicsDebug = !Editor.Core.Services.Physics.PhysicsService.ShowPhysicsDebug;
        public static void FocusSelected() => Editor.Core.Viewport.EditorViewportSession.Main?.FocusOnSelected();
        public static void ResetCamera() => Editor.Core.Viewport.EditorViewportSession.Main?.Camera.Reset();
        public static void ToggleReleaseMode() => PlayModeService.Instance.IsReleaseMode = !PlayModeService.Instance.IsReleaseMode;

        // ------------------------------------------------------------ assets
        public static async Task ImportAsset()
        {
            if (Window == null || !Session.HasProject) return;
            var files = await Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Asset",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("All supported") { Patterns = new[] { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.blend", "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr", "*.wav", "*.mp3", "*.ogg", "*.flac" } },
                    new FilePickerFileType("3D models") { Patterns = new[] { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.blend" } },
                    new FilePickerFileType("Textures") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr" } },
                    new FilePickerFileType("Audio") { Patterns = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac" } },
                }
            });
            foreach (var f in files)
            {
                string path = f.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) continue;
                Window.AssetBrowser?.ImportFile(path);
            }
        }
        public static void CreateMaterial() => Window?.AssetBrowser?.CreateMaterial("Standard");
        public static void CreateShader() => Window?.AssetBrowser?.CreateShader("Standard");
        public static void CreateScript()
        {
            if (!Session.HasProject) return;
            var path = ScriptingService.CreateScript("NewBehaviour");
            Window?.AssetBrowser?.Refresh();
            OpenInIde(path);
            Toast("Script created: " + Path.GetFileName(path));
        }
        public static void OpenScriptsProject()
        {
            if (!Session.HasProject) return;
            try { ScriptingService.EnsureScriptsProject(); } catch { }
            OpenInIde(null);
        }
        public static void ExportAsset() => Window?.AssetBrowser?.ExportSelected();

        /// <summary>Open a script (or the scripts project) in the user's IDE: VS Code, Rider, Visual Studio (Windows),
        /// else the OS default handler. Works when started from Finder (no shell PATH).</summary>
        public static void OpenInIde(string filePath)
        {
            string root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return;
            try
            {
                if (OperatingSystem.IsWindows()) { ScriptingService.OpenInVisualStudio(filePath); return; }
                try { ScriptingService.EnsureScriptsProject(); } catch { }
                string target = filePath ?? root;
                foreach (var exe in new[] { "/usr/local/bin/code", "/opt/homebrew/bin/code", "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Visual Studio Code.app/Contents/Resources/app/bin/code"), "/usr/bin/code" })
                {
                    if (!File.Exists(exe)) continue;
                    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                    psi.ArgumentList.Add(root);
                    if (filePath != null) { psi.ArgumentList.Add("--goto"); psi.ArgumentList.Add(filePath); }
                    if (Process.Start(psi) != null) return;
                }
                foreach (var app in new[] { "Visual Studio Code", "Rider", "Visual Studio" })
                {
                    if (!Directory.Exists("/Applications/" + app + ".app") && !Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", app + ".app"))) continue;
                    var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                    psi.ArgumentList.Add("-a"); psi.ArgumentList.Add(app); psi.ArgumentList.Add(target);
                    if (Process.Start(psi) != null) return;
                }
                var fallback = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
                fallback.ArgumentList.Add(target);
                Process.Start(fallback);
            }
            catch (Exception ex) { Fail("Could not open the IDE", ex); }
        }

        // ------------------------------------------------------------ game objects
        public static void CreateEmpty() => Run(() => { var e = ActiveScene?.CreateEntity("New Entity"); Focus(e); });
        public static void CreatePrimitive(PrimitiveType type) => Run(() => { var e = ActiveScene?.CreatePrimitive(type); Focus(e); });
        public static void CreateLight(LightType type) => Run(() => { var e = ActiveScene?.CreateLight(type); Focus(e); });
        public static void CreateCamera() => Run(() => { var e = ActiveScene?.CreateCamera(); Focus(e); });
        public static void CreateSkybox() => Run(() => { var e = ActiveScene?.CreateSkybox(); Focus(e); });
        public static void CreatePlayer() => Run(() => Session.Hierarchy.CreatePlayerCommand.Execute(null));
        public static void CreateFolder() => Run(() => Session.Hierarchy.CreateFolderCommand.Execute(null));
        public static void CreateAudioSource() => Run(() => Session.Hierarchy.CreateAudioSourceCommand.Execute(null));
        public static void CreateReverbZone() => Run(() => Session.Hierarchy.CreateReverbZoneCommand.Execute(null));
        public static void CreateUI(string kind) => Run(() =>
        {
            var h = Session.Hierarchy;
            switch (kind) { case "Canvas": h.CreateUICanvasCommand.Execute(null); break; case "Text": h.CreateUITextCommand.Execute(null); break; case "Image": h.CreateUIImageCommand.Execute(null); break; default: h.CreateUIButtonCommand.Execute(null); break; }
        });

        private static void Focus(GameEntity e)
        {
            if (e == null) return;
            SelectionService.Instance.Select(e);
            SelectionService.Instance.RequestFocus(e);
            SceneRenderService.RuntimeDirty = true;
        }

        // ------------------------------------------------------------ components
        public static void AddComponent(Editor.ECS.Component component)
        {
            var e = Selected; if (e == null || component == null) return;
            e.AddComponent(component);
            SceneRenderService.RuntimeDirty = true;
            Window?.Inspector?.Refresh();
        }

        // ------------------------------------------------------------ play
        public static void Play() => Session.Play();
        public static void Pause() => Session.Pause();
        public static void Stop() => Session.Stop();
        public static void TogglePlay() => Session.TogglePlay();
        public static void PlayInNewWindow() => Window?.LaunchStandalonePlayer();

        // ------------------------------------------------------------ window / help
        public static void AudioMixer() => Window?.OpenAudioMixer();
        public static void GitWindow() => Window?.OpenGit();
        public static void ResetLayout() => Window?.ResetLayout();
        public static void Documentation() => OpenUrl("https://github.com/shadow-kernel/Vortex-Engine/wiki");
        public static void ApiReference() => OpenUrl("https://github.com/shadow-kernel/Vortex-Engine/wiki/Scripting-API");
        public static void About() => Window?.OpenAbout();
        public static void CheckForUpdates() => OpenUrl("https://github.com/shadow-kernel/Vortex-Engine/releases");

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        public static void RevealInFinder(string path)
        {
            try
            {
                if (OperatingSystem.IsMacOS()) Process.Start("open", File.Exists(path) ? "-R \"" + path + "\"" : "\"" + path + "\"");
                else if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"");
                else Process.Start("xdg-open", "\"" + (File.Exists(path) ? Path.GetDirectoryName(path) : path) + "\"");
            }
            catch { }
        }

        // ------------------------------------------------------------ helpers
        private static void Run(Action a) { if (!Session.HasProject) return; try { a(); } catch (Exception ex) { Fail("Action failed", ex); } }
        public static void Toast(string message) => Window?.ShowToast(message);
        public static void Fail(string what, Exception ex)
        {
            ConsoleService.Instance.LogError(what + ": " + ex.Message);
            Toast(what + ": " + ex.Message);
        }
    }
}
