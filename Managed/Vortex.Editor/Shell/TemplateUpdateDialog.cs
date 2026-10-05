using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Services;
using VortexEditor.Shell.Library;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Project Hub ▸ Update from Template… (#186): pick the template (guessed from the project), see exactly what an
    /// update would do — new and overwritten files per folder, a line diff of every changed text file, new scenes (the
    /// dry run) — choose the folders, then back up and update. The backup lands in .ve/backups/template-update-&lt;time&gt;/
    /// of the project. Replacing scenes the project already has is off by default: that would discard level edits.
    /// </summary>
    public sealed class TemplateUpdateDialog : Window
    {
        public static async Task Run(string projectDir, string projectName) => await LibraryUi.ShowModal(new TemplateUpdateDialog(projectDir, projectName));

        private const string ReplacedScenes = "Scenes (replace existing)";
        private const int MaxDiffLines = 400;

        private readonly string _project;
        private readonly ComboBox _template = new ComboBox { MinWidth = 260 };
        private readonly StackPanel _folders = new StackPanel { Spacing = 4 };
        private readonly TextBlock _summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly ListBox _fileList = new ListBox { Height = 170 };
        private readonly TextBlock _diffTitle = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel _diff = new StackPanel();
        private readonly Button _apply;
        private readonly List<ProjectTemplate> _templates;
        private TemplateUpdatePlan _plan;
        private List<TemplateFileChange> _shown = new List<TemplateFileChange>();
        private readonly Dictionary<string, CheckBox> _groupBoxes = new Dictionary<string, CheckBox>();
        private readonly FontFamily _mono;

        private TemplateUpdateDialog(string projectDir, string projectName)
        {
            _project = projectDir;
            Title = "Update from Template";
            Width = 760; Height = 820; MinWidth = 560; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            _mono = Application.Current != null && Application.Current.TryFindResource("VxMono", out var m) && m is FontFamily ff ? ff : FontFamily.Default;
            _templates = ProjectTemplateService.Discover().Where(t => !t.IsEmpty).ToList();
            foreach (var t in _templates) _template.Items.Add(t.Name);
            var guess = TemplateUpdateService.GuessTemplate(projectDir, _templates);
            _template.SelectedIndex = guess != null ? _templates.IndexOf(guess) : (_templates.Count > 0 ? 0 : -1);
            _template.SelectionChanged += (s, e) => _ = Rebuild();
            _fileList.SelectionChanged += (s, e) => ShowDiff(_fileList.SelectedIndex >= 0 && _fileList.SelectedIndex < _shown.Count ? _shown[_fileList.SelectedIndex] : null);

            var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 10 };
            stack.Children.Add(LibraryUi.Title("Update “" + projectName + "” from its template"));
            stack.Children.Add(LibraryUi.Para("Copies the template's current scripts, prefabs, materials, models, audio and scenes into the project: new files are added, " +
                                              "changed ones overwritten — your version of every overwritten file is backed up first. Files you added stay untouched."));
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = "Template", VerticalAlignment = VerticalAlignment.Center }, _template } });
            stack.Children.Add(_summary);
            stack.Children.Add(Ui.Header("Folders"));
            stack.Children.Add(_folders);
            stack.Children.Add(Ui.Header("Files"));
            stack.Children.Add(_fileList);
            stack.Children.Add(_diffTitle);
            stack.Children.Add(new Border
            {
                Background = EditorKit.Brush("VxToolbarBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8),
                Child = new ScrollViewer { Content = _diff, Height = 220, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            });

            _apply = Ui.Button("Back Up and Update", () => _ = ApplyAsync(), null, "accent", 150);
            var close = Ui.Button("Cancel", Close, null, null, 84);
            Content = LibraryUi.Layout(stack, LibraryUi.Footer(close, _apply));
            Opened += (s, e) => _ = Rebuild();
        }

        private ProjectTemplate Selected => _template.SelectedIndex >= 0 && _template.SelectedIndex < _templates.Count ? _templates[_template.SelectedIndex] : null;

        /// <summary>The dialog's folder groups: the service's top folder, with replacing existing scenes split off.</summary>
        private static string GroupOf(TemplateFileChange c) => c.Group == "Scenes" && !c.IsNew ? ReplacedScenes : c.Group;

        private async Task Rebuild()
        {
            _apply.IsEnabled = false;
            _folders.Children.Clear();
            _groupBoxes.Clear();
            _shown = new List<TemplateFileChange>();
            _fileList.ItemsSource = null;
            ShowDiff(null);
            var t = Selected;
            if (t == null) { _summary.Text = "No template with content is installed."; return; }
            try
            {
                string dir = t.ProjectDir;
                if (t.NeedsDownload)
                {
                    _summary.Text = "Downloading the template content…";
                    dir = await TemplatePacks.EnsureAsync(t, new Progress<double>(f => _summary.Text = "Downloading the template content… " + (int)(f * 100) + " %"));
                }
                _summary.Text = "Comparing…";
                _plan = await Task.Run(() => TemplateUpdateService.Plan(_project, dir));
            }
            catch (Exception ex) { _summary.Text = ex.Message; _plan = null; return; }

            if (_plan.Changes.Count == 0 && _plan.NewScenes.Count == 0)
            {
                _summary.Text = "The project already has the template's current content (" + _plan.Unchanged + " files identical).";
                return;
            }
            _summary.Text = _plan.Additions + " new file(s), " + _plan.Overwrites + " changed file(s) to overwrite, " + _plan.Unchanged + " identical" +
                            (_plan.NewScenes.Count > 0 ? "; new scene(s): " + string.Join(", ", _plan.NewScenes) : "") + ".";
            foreach (var g in _plan.Changes.GroupBy(GroupOf).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                bool scenes = g.Key == ReplacedScenes;
                var box = new CheckBox
                {
                    IsChecked = !scenes,
                    Content = scenes
                        ? g.Key + " — " + g.Count() + " scene(s) (" + LibraryUi.Bytes(g.Sum(c => c.Size)) + ")"
                        : g.Key + " — " + g.Count(c => c.IsNew) + " new, " + g.Count(c => !c.IsNew) + " changed (" + LibraryUi.Bytes(g.Sum(c => c.Size)) + ")",
                };
                if (scenes) ToolTip.SetTip(box, "The template's version replaces the whole scene — level edits in it are lost (your version goes to the backup).");
                box.IsCheckedChanged += (s, e) => UpdateFiles();
                _groupBoxes[g.Key] = box;
                _folders.Children.Add(box);
            }
            if (_groupBoxes.ContainsKey(ReplacedScenes))
                _folders.Children.Add(LibraryUi.Small("Replacing scenes the project already has is off by default: it would discard the level edits in them."));
            UpdateFiles();
        }

        private IEnumerable<TemplateFileChange> Chosen() =>
            _plan == null ? Enumerable.Empty<TemplateFileChange>() : _plan.Changes.Where(c => _groupBoxes.TryGetValue(GroupOf(c), out var b) && b.IsChecked == true);

        private void UpdateFiles()
        {
            var keep = _fileList.SelectedIndex >= 0 && _fileList.SelectedIndex < _shown.Count ? _shown[_fileList.SelectedIndex] : null;
            _shown = Chosen().ToList();
            _fileList.ItemsSource = _shown.Select(c => (c.IsNew ? "+  " : "~  ") + c.Path).ToList();
            // keep the file the user looked at; else show the first overwrite (the interesting diff)
            int index = keep != null ? _shown.IndexOf(keep) : -1;
            if (index < 0) index = _shown.FindIndex(c => !c.IsNew);
            if (index < 0 && _shown.Count > 0) index = 0;
            _fileList.SelectedIndex = index;
            ShowDiff(index >= 0 ? _shown[index] : null);   // also when the index did not change (new items, same position)
            _apply.IsEnabled = _shown.Count > 0;
        }

        /// <summary>Your version → the template's, as a line diff for text files; a size note for binary ones.</summary>
        private void ShowDiff(TemplateFileChange c)
        {
            _diff.Children.Clear();
            if (c == null || _plan == null) { _diffTitle.Text = _plan == null ? "" : "Select a file to see what changes."; return; }
            string mine = Path.Combine(_plan.ProjectDir, c.Path), theirs = Path.Combine(_plan.TemplateDir, c.Path);
            try
            {
                if (c.IsNew)
                {
                    _diffTitle.Text = "New from the template: " + c.Path;
                    _diff.Children.Add(Line('~', "Added to the project (" + LibraryUi.Bytes(c.Size) + ")."));
                    return;
                }
                _diffTitle.Text = "Your version → the template's: " + c.Path;
                long mineSize = new FileInfo(mine).Length;
                if (!IsText(mine) || !IsText(theirs) || mineSize > 512 * 1024 || c.Size > 512 * 1024)
                {
                    _diff.Children.Add(Line('~', "Replaced as a whole — yours " + LibraryUi.Bytes(mineSize) + ", the template's " + LibraryUi.Bytes(c.Size) + "."));
                    return;
                }
                var lines = VortexEditor.Claude.LineDiff.Compute(File.ReadAllText(mine), File.ReadAllText(theirs), 3);
                if (lines.Count == 0) _diff.Children.Add(Line('~', "Only line endings or encoding differ."));
                foreach (var (kind, text) in lines.Take(MaxDiffLines)) _diff.Children.Add(Line(kind, text));
                if (lines.Count > MaxDiffLines) _diff.Children.Add(Line('~', "… " + (lines.Count - MaxDiffLines) + " more lines"));
            }
            catch (Exception ex) { _diff.Children.Add(Line('~', "Could not compare: " + ex.Message)); }
        }

        private TextBlock Line(char kind, string text) => new TextBlock
        {
            Text = (kind == '+' ? "+ " : kind == '-' ? "- " : "  ") + text,
            FontFamily = _mono, FontSize = 11, TextWrapping = TextWrapping.NoWrap,
            Foreground = EditorKit.Brush(kind == '+' ? "VxGreenBrush" : kind == '-' ? "VxRedBrush" : kind == '~' ? "VxTextTertiaryBrush" : "VxTextSecondaryBrush"),
        };

        /// <summary>No NUL byte in the first 8 KB — scripts, scenes, materials, prefabs, shaders, docs.</summary>
        private static bool IsText(string file)
        {
            using (var fs = File.OpenRead(file))
            {
                var buf = new byte[8192];
                int n = fs.Read(buf, 0, buf.Length);
                return Array.IndexOf(buf, (byte)0, 0, n) < 0;
            }
        }

        private async Task ApplyAsync()
        {
            if (_plan == null) return;
            var chosen = Chosen().ToList();
            _apply.IsEnabled = false;
            try
            {
                var result = await Task.Run(() => TemplateUpdateService.Apply(_plan, chosen));
                await Dialogs.Alert("Project updated", result.FilesCopied + " file(s) updated" +
                                                     (result.ScenesAdded.Count > 0 ? ", scene(s) added: " + string.Join(", ", result.ScenesAdded) : "") +
                                                     ". Your previous files are in " + result.BackupDir + ".");
                Close();
            }
            catch (Exception ex)
            {
                await Dialogs.Alert("Update failed", ex.Message);
                _apply.IsEnabled = true;
            }
        }

        // ------------------------------------------------------------------ smoke check "template update"

        [ModuleInitializer]
        internal static void RegisterSmoke() => SmokeRegistry.Add("template update", SmokeAsync);

        private static async Task<bool> SmokeAsync()
        {
            var log = Editor.Core.Services.ConsoleService.Instance;
            var template = ProjectTemplateService.Discover().FirstOrDefault(t => t.Id == "Default3D" && !t.NeedsDownload);
            if (template == null) { log.Log("template update: skipped (no Default 3D template with content)"); return true; }
            string dir = Path.Combine(Path.GetTempPath(), "vortex-template-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            TemplateUpdateDialog d = null;
            try
            {
                CopyDir(template.ProjectDir, dir);
                var script = Directory.GetFiles(Path.Combine(dir, "Assets", "Scripts"), "*.cs", SearchOption.AllDirectories).First();
                File.AppendAllText(script, "\n// changed in the project\n");
                var scene = Directory.GetFiles(Path.Combine(dir, "Assets", "Scenes"), "*.vscene").First();
                File.AppendAllText(scene, "\n");
                d = new TemplateUpdateDialog(dir, "Update Smoke");
                d.Show(EditorCommands.Window);
                for (int i = 0; i < 100 && d._plan == null; i++) await Task.Delay(50);
                await SmokeRegistry.Settle(400);
                SmokeRegistry.Capture(d, "template_update.png");
                if (d._plan == null) { log.LogError("template update: no plan (" + d._summary.Text + ")"); return false; }
                if (d._plan.Overwrites != 2 || !d._plan.Changes.Any(c => !c.IsNew && script.Replace('\\', '/').EndsWith(c.Path)))
                {
                    log.LogError("template update: expected the changed script and scene to be listed, got " + d._plan.Overwrites + " (" + d._summary.Text + ")");
                    return false;
                }
                var chosen = d.Chosen().ToList();
                if (chosen.Count != 1 || chosen[0].Group != "Scripts")
                {
                    log.LogError("template update: by default only the script should be chosen (replacing a scene is opt-in), got " + string.Join(", ", chosen.Select(c => c.Path)));
                    return false;
                }
                var removed = d._diff.Children.OfType<TextBlock>().Select(b => b.Text).ToList();
                if (!removed.Any(x => x.StartsWith("- ", StringComparison.Ordinal) && x.Contains("changed in the project")))
                {
                    log.LogError("template update: the diff does not show the project's extra line (" + d._diffTitle.Text + ": " + string.Join(" | ", removed.Take(6)) + ")");
                    return false;
                }
                log.Log("template update: OK — " + d._summary.Text + " Diff: " + d._diffTitle.Text);
                return true;
            }
            catch (Exception ex) { log.LogError("template update: " + ex.Message); return false; }
            finally
            {
                d?.Close();
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var sub in Directory.GetDirectories(from))
                if (!Path.GetFileName(sub).StartsWith(".", StringComparison.Ordinal)) CopyDir(sub, Path.Combine(to, Path.GetFileName(sub)));
        }
    }
}
