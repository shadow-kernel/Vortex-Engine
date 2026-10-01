using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Exceptions;
using Editor.Core.Migration;
using Editor.Core.Services;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Project-open compatibility gate of the macOS editor (port of the Windows ProjectMigrationDialog): an up-to-date
    /// project opens silently; an older-format one is upgraded after asking (full backup first, restored on failure);
    /// a project saved by a NEWER engine is refused — re-saving it here could silently destroy data that build wrote.
    /// Runs inside ProjectService's synchronous load, so the questions are modal (a nested dispatcher frame).
    /// </summary>
    internal static class ProjectCompatibility
    {
        /// <summary>Smoke hook: answers the questions without a dialog (0 = first button).</summary>
        internal static Func<int> TestAnswer;
        internal static string LastLog;

        [ModuleInitializer]
        internal static void Register()
        {
            ProjectService.CompatibilityGate = EnsureCompatible;
            RegisterSmoke();
        }

        private static void EnsureCompatible(string projectDir, ProjectManifest manifest, string manifestPath)
        {
            var plan = ProjectMigrationService.Evaluate(manifest);
            if (plan.Status == MigrationStatus.UpToDate || plan.Status == MigrationStatus.Unknown) return;
            string name = manifest?.Name ?? Path.GetFileName(projectDir);

            if (plan.Status == MigrationStatus.NewerThanEngine)
            {
                string by = string.IsNullOrEmpty(plan.SavedWithEngine) ? "a newer Vortex version" : "Vortex " + plan.SavedWithEngine;
                Ask("Project needs a newer Vortex", "“" + name + "” was saved with " + by + " (this editor is " + Editor.Core.EngineInfo.VersionString
                    + "). Opening and saving it here could lose data that version wrote — update Vortex to open it.", "OK");
                throw new ProjectException("“" + name + "” was saved with " + by + " — update Vortex to open it.");
            }

            string steps = string.Join("\n", plan.Steps.Select(s => "• " + s.Description));
            int choice = Ask("Upgrade “" + name + "”?",
                "The project uses format v" + plan.From + "; this editor uses v" + plan.To + ". Vortex makes a full backup of the project first and restores it if anything fails.\n\n" + steps,
                "Upgrade Project", "Cancel");
            if (choice != 0) throw new ProjectException("Opening “" + name + "” was cancelled (it needs an upgrade to format v" + plan.To + ").");

            var log = new System.Text.StringBuilder();
            bool ok = ProjectMigrationService.Migrate(projectDir, manifest, manifestPath, line => { log.AppendLine(line); ConsoleService.Instance.Log("[Migration] " + line); });
            LastLog = log.ToString();
            if (!ok) throw new ProjectException("Upgrading “" + name + "” failed — the backup was restored and the project was not opened.\n" + LastLog);
            try { Dispatcher.UIThread.Post(() => EditorCommands.Toast("“" + name + "” upgraded to format v" + plan.To + " (backup in .ve/backups)")); } catch { }
        }

        /// <summary>A modal question answered synchronously (the load that asks is synchronous).</summary>
        private static int Ask(string title, string message, params string[] buttons)
        {
            if (TestAnswer != null) return TestAnswer();
            if (!Dispatcher.UIThread.CheckAccess()) return Dispatcher.UIThread.Invoke(() => Ask(title, message, buttons));
            var owner = EditorKit.ActiveWindow() ?? EditorCommands.Window;
            Task<int> task;
            try { task = EditorKit.Choose(owner, title, message, buttons); }
            catch { return buttons.Length - 1; }
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.UIThread.PushFrame(frame);
            }
            return task.IsCompletedSuccessfully ? task.Result : buttons.Length - 1;
        }

        private static void RegisterSmoke()
        {
            // a throwaway copy of a v1 project: refused when cancelled, upgraded (+ backup) when accepted, refused when
            // saved by a newer engine — the real project open path (ProjectService.LoadProjectFromPath)
            SmokeRegistry.Add("project compatibility: upgrade an old project (backup), refuse cancel + newer engine", () =>
            {
                string tmp = Path.Combine(Path.GetTempPath(), "vortex-compat-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    Directory.CreateDirectory(Path.Combine(tmp, "Assets", "Scripts"));
                    string manifestPath = Path.Combine(tmp, "project.vortex");
                    void Write(int format, string engine) => File.WriteAllText(manifestPath,
                        "{\n  \"id\": \"" + Guid.NewGuid() + "\",\n  \"name\": \"CompatTest\",\n  \"formatVersion\": " + format + ",\n  \"engineVersion\": \"" + engine + "\",\n  \"scenes\": []\n}");
                    File.WriteAllText(Path.Combine(tmp, "Assets", "Scripts", "VortexScripting.cs"), "// obsolete stub");
                    var m = new ProjectManifest { Name = "CompatTest", FormatVersion = 1, EngineVersion = "1.0.0" };

                    TestAnswer = () => 1;   // Cancel
                    bool refused = false;
                    try { EnsureCompatible(tmp, m, manifestPath); } catch (ProjectException) { refused = true; }

                    TestAnswer = () => 0;   // Upgrade
                    Write(1, "1.0.0");
                    m = new ProjectManifest { Name = "CompatTest", FormatVersion = 1, EngineVersion = "1.0.0" };
                    bool upgraded = false;
                    try { EnsureCompatible(tmp, m, manifestPath); upgraded = m.FormatVersion == ProjectMigrationService.Current; } catch { }
                    bool migrated = Directory.Exists(Path.Combine(tmp, "Assets", "Animations")) && !File.Exists(Path.Combine(tmp, "Assets", "Scripts", "VortexScripting.cs"));
                    string backups = Path.Combine(tmp, ".ve", "backups");
                    bool backup = Directory.Exists(backups) && Directory.EnumerateDirectories(backups).Any();

                    var newer = new ProjectManifest { Name = "CompatTest", FormatVersion = ProjectMigrationService.Current, EngineVersion = "99.0.0" };
                    bool blocked = false;
                    try { EnsureCompatible(tmp, newer, manifestPath); } catch (ProjectException) { blocked = true; }

                    ConsoleService.Instance.Log("  compatibility smoke: cancel refused=" + refused + " upgraded=" + upgraded + " migrated=" + migrated + " backup=" + backup + " newer engine blocked=" + blocked);
                    return Task.FromResult(refused && upgraded && migrated && backup && blocked);
                }
                finally
                {
                    TestAnswer = null;
                    try { Directory.Delete(tmp, true); } catch { }
                }
            });
        }
    }
}
