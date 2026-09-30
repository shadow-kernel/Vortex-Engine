using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell.Prefab
{
    /// <summary>Smoke checks of the prefab workflow (Prefab Editor, prefab-instance bar) and the inspector shell.</summary>
    internal static class PrefabSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("prefab editor: Weapon_Vityaz preview, child selection, close without saving", async () =>
            {
                string path = FindPrefab("Weapon_Vityaz");
                if (path == null) return Fail("no prefab found in Assets/Prefabs");
                var before = File.ReadAllBytes(path);
                PrefabEditorWindow.Open(path);
                var w = PrefabEditorWindow.Find(path);
                if (w == null) return Fail("window did not open");
                await SmokeRegistry.Settle(1500);
                w.Preview.RenderNow();
                bool rendered = w.Preview.LastImage != null && HasContent(w.Preview.LastImage);
                var child = w.Root.Children.FirstOrDefault(c => c.GetComponent<MeshRenderer>() != null) ?? w.Root.Children.FirstOrDefault();
                bool childOk = child != null;
                if (child != null)
                {
                    w.SelectEntity(child);
                    await SmokeRegistry.Settle(500);
                    childOk = ReferenceEquals(w.Inspector.Entity, child) && w.Inspector.Cards.Any() && ReferenceEquals(w.SelectedEntity, child);
                    w.Preview.RenderNow();
                }
                SmokeRegistry.Capture(w, "prefab_editor.png");
                // an unsaved edit marks the editor dirty; closing without saving leaves the file untouched
                w.Root.Name = w.Root.Name + "_edited";
                await SmokeRegistry.Settle(400);
                bool dirty = w.IsDirty;
                w.CloseWithoutSaving();
                await SmokeRegistry.Settle(300);
                bool unchanged = File.ReadAllBytes(path).SequenceEqual(before);
                bool closed = PrefabEditorWindow.Find(path) == null;
                Log("prefab editor: rendered=" + rendered + " child=" + (child?.Name ?? "-") + " inspector=" + childOk + " dirty=" + dirty + " unchanged=" + unchanged + " closed=" + closed);
                return rendered && childOk && dirty && unchanged && closed;
            });

            SmokeRegistry.Add("prefab editor: Character_Soldier (skinned, nested meshes) preview", async () =>
            {
                string path = FindPrefab("Character_Soldier");
                if (path == null) return true;   // optional in other projects
                PrefabEditorWindow.Open(path);
                var w = PrefabEditorWindow.Find(path);
                if (w == null) return Fail("window did not open");
                await SmokeRegistry.Settle(1800);
                w.Preview.RenderNow();
                bool rendered = w.Preview.LastImage != null && HasContent(w.Preview.LastImage);
                SmokeRegistry.Capture(w, "prefab_editor_soldier.png");
                w.CloseWithoutSaving();
                await SmokeRegistry.Settle(200);
                return rendered;
            });

            SmokeRegistry.Add("prefab editor: edit + save rewrites the .ventity and reloads placed instances", async () =>
            {
                string root = ProjectData.Current?.Path;
                var scene = ProjectData.Current?.ActiveScene;
                string src = FindPrefab("Shell") ?? FindPrefab("Weapon_Vityaz");
                if (root == null || scene == null || src == null) return Fail("no project / scene / prefab");
                string tmp = Path.Combine(root, "Assets", "Prefabs", "SmokePrefab.ventity");
                File.Copy(src, tmp, true);
                try
                {
                    var inst = PrefabService.Instance.InstantiatePrefab(tmp, scene);
                    if (inst == null) return Fail("could not instantiate the temp prefab");
                    PrefabEditorWindow.Open(tmp);
                    var w = PrefabEditorWindow.Find(tmp);
                    if (w == null) return Fail("window did not open");
                    await SmokeRegistry.Settle(800);
                    var child = w.AddChild(w.Root, "SmokeChild", "Cube");
                    w.SelectEntity(w.Root);
                    await SmokeRegistry.Settle(300);
                    w.Inspector.AddNew(new BoxCollider(w.Root), w.Root);
                    // what the tree's drops build: a model child (submesh structure) and a nested prefab instance
                    string model = null;
                    try { model = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.glb", SearchOption.AllDirectories).FirstOrDefault(); } catch { }
                    if (model != null) { var m = PrefabWorkflow.BuildModelEntity(model, "SmokeModel"); m.Parent = w.Root; w.Root.Children.Add(m); }
                    string nestedSrc = FindPrefab("npc_gun") ?? FindPrefab("Monster");
                    if (nestedSrc != null && !PrefabWorkflow.SamePath(nestedSrc, tmp)) { var n = PrefabWorkflow.LoadNestedInstance(nestedSrc); n.Name = "SmokeNested"; n.Parent = w.Root; w.Root.Children.Add(n); }
                    await SmokeRegistry.Settle(400);
                    bool dirty = w.IsDirty;
                    bool saved = w.Save();
                    await SmokeRegistry.Settle(300);
                    var reloaded = SceneService.Instance.LoadEntityFromPrefab(tmp);
                    bool fileOk = reloaded != null && reloaded.Children.Any(c => c.Name == "SmokeChild" && c.GetComponent<MeshRenderer>() != null) && reloaded.GetComponent<BoxCollider>() != null && string.IsNullOrEmpty(reloaded.PrefabPath)
                                  && (model == null || reloaded.Children.Any(c => c.Name == "SmokeModel" && (c.GetComponent<MeshRenderer>() != null || c.Children.Any(k => k.GetComponent<MeshRenderer>()?.MeshPath?.Contains("#submesh") == true))))
                                  && (nestedSrc == null || reloaded.Children.Any(c => c.Name == "SmokeNested" && c.IsPrefabInstance && !Path.IsPathRooted(c.PrefabPath)));
                    var instances = PrefabWorkflow.InstancesOf(tmp);
                    bool instOk = instances.Count == 1 && instances[0].Children.Any(c => c.Name == "SmokeChild") && instances[0].GetComponent<BoxCollider>() != null;
                    bool clean = !w.IsDirty;
                    w.CloseWithoutSaving();
                    Log("prefab save: dirty=" + dirty + " saved=" + saved + " file=" + fileOk + " instances=" + instances.Count + "/" + instOk + " clean=" + clean);
                    return dirty && saved && fileOk && instOk && clean;
                }
                finally
                {
                    try { PrefabService.Instance.OnPrefabDeleted(tmp); } catch { }
                    try { File.Delete(tmp); File.Delete(tmp + ".vmeta"); } catch { }
                    SceneRenderService.RuntimeDirty = true;
                }
            });

            SmokeRegistry.Add("prefab workflow: save as prefab links the entity, thumbnail + file", async () =>
            {
                var scene = ProjectData.Current?.ActiveScene;
                string root = ProjectData.Current?.Path;
                if (scene == null || root == null) return Fail("no scene");
                var e = scene.CreatePrimitive(PrimitiveType.Cube);
                e.Name = "SmokeSaved";
                string path = null;
                try
                {
                    path = PrefabWorkflow.SaveAsPrefab(e, "SmokeSaved");
                    await SmokeRegistry.Settle(300);
                    bool linked = e.IsPrefabInstance && PrefabWorkflow.SamePath(e.PrefabPath, path) && !Path.IsPathRooted(e.PrefabPath);
                    var t = path != null ? SceneService.Instance.LoadEntityFromPrefab(path) : null;
                    bool template = t != null && string.IsNullOrEmpty(t.PrefabPath) && t.GetComponent<MeshRenderer>()?.MeshPath == "Primitive:Cube";
                    bool noOverrides = PrefabOverrides.Compute(e).Count == 0;
                    Avalonia.Media.Imaging.Bitmap thumb = null;
                    if (path != null) VortexEditor.Services.ThumbnailService.Request(path, 96, b => thumb = b);
                    for (int i = 0; i < 50 && thumb == null; i++) await Task.Delay(100);
                    Log("save as prefab: linked=" + linked + " template=" + template + " noOverrides=" + noOverrides + " thumb=" + (thumb != null));
                    return linked && template && noOverrides && thumb != null;
                }
                finally
                {
                    SelectionService.Instance.Select(e); EditorCommands.Delete();
                    try { if (path != null) { File.Delete(path); File.Delete(path + ".vmeta"); } } catch { }
                }
            });

            SmokeRegistry.Add("inspector: prefab instance bar (place, overrides, revert, unpack + undo)", async () =>
            {
                string path = FindPrefab("Weapon_Vityaz");
                var insp = EditorCommands.Window?.Inspector;
                if (path == null || insp == null || ProjectData.Current?.ActiveScene == null) return Fail("no prefab / inspector / scene");
                try { EditorCommands.Window.RightTabs.SelectedIndex = 0; } catch { }
                var inst = PrefabWorkflow.PlaceInScene(path);
                if (inst == null) return Fail("could not place the prefab");
                await SmokeRegistry.Settle(700);
                bool bar = insp.PrefabBar != null && ReferenceEquals(insp.Entity, inst);
                string clean = insp.PrefabBarText ?? "";
                var mr = inst.Children.Select(c => c.GetComponent<MeshRenderer>()).FirstOrDefault(m => m != null) ?? inst.GetComponent<MeshRenderer>();
                if (mr == null) return Fail("prefab has no mesh renderer to override");
                mr.ColorR = 0.123f;
                await SmokeRegistry.Settle(1100);
                string edited = insp.PrefabBarText ?? "";
                SmokeRegistry.Capture(EditorCommands.Window, "inspector_prefab_bar.png");
                var fresh = PrefabWorkflow.Revert(inst);
                await SmokeRegistry.Settle(600);
                bool reverted = fresh != null && ReferenceEquals(insp.Entity, fresh) && PrefabOverrides.Compute(fresh).Count == 0 && !PrefabWorkflow.IsInScene(inst);
                PrefabWorkflow.Unpack(fresh);
                bool unpacked = !fresh.IsPrefabInstance;
                await SmokeRegistry.Settle(300);
                bool barGone = insp.PrefabBar == null;
                UndoRedoManager.Instance.Undo();
                await SmokeRegistry.Settle(300);
                bool relinked = fresh.IsPrefabInstance && insp.PrefabBar != null;
                SelectionService.Instance.Select(fresh);
                EditorCommands.Delete();
                Log("prefab bar: bar=" + bar + " clean='" + clean + "' edited='" + edited + "' reverted=" + reverted + " unpacked=" + unpacked + " barGone=" + barGone + " relinked=" + relinked);
                return bar && clean.Contains("no overrides") && edited.Contains("1 override") && reverted && unpacked && barGone && relinked;
            });

            SmokeRegistry.Add("inspector: add from catalog, copy/paste, reset, move, enable — undoable", async () =>
            {
                var scene = ProjectData.Current?.ActiveScene;
                var insp = EditorCommands.Window?.Inspector;
                if (scene == null || insp == null) return Fail("no scene / inspector");
                var e = scene.CreateEntity("SmokeInspector");
                var e2 = scene.CreateEntity("SmokeInspector2");
                try
                {
                    SelectionService.Instance.Select(e);
                    await SmokeRegistry.Settle(300);
                    insp.AddNew(new Light(e, LightType.Point), e);
                    insp.AddNew(new AudioSource(e), e);
                    await SmokeRegistry.Settle(300);
                    var light = e.GetComponent<Light>();
                    var audio = e.GetComponent<AudioSource>();
                    if (light == null || audio == null) return Fail("add component failed");
                    bool cards = insp.Cards.Count() == 3;
                    light.Intensity = 7.5f;
                    insp.CopyComponent(light);
                    // reset (+ undo)
                    insp.ResetComponent(light);
                    bool reset = Math.Abs(light.Intensity - 7.5f) > 0.01f && light.LightType == LightType.Point;
                    UndoRedoManager.Instance.Undo();
                    bool resetUndo = Math.Abs(light.Intensity - 7.5f) < 0.001f;
                    // move (+ undo)
                    int li = e.Components.IndexOf(light), ai = e.Components.IndexOf(audio);
                    insp.MoveComponent(audio, -1);
                    bool moved = e.Components.IndexOf(audio) == li;
                    UndoRedoManager.Instance.Undo();
                    bool moveUndo = e.Components.IndexOf(audio) == ai;
                    // enable (+ undo)
                    insp.SetComponentEnabled(light, false);
                    bool disabled = !light.IsEnabled;
                    UndoRedoManager.Instance.Undo();
                    bool enableUndo = light.IsEnabled;
                    // paste values into another entity's light, and paste as new
                    SelectionService.Instance.Select(e2);
                    await SmokeRegistry.Settle(300);
                    insp.PasteAsNew(e2);
                    await SmokeRegistry.Settle(200);
                    var l2 = e2.GetComponent<Light>();
                    bool pastedNew = l2 != null && Math.Abs(l2.Intensity - 7.5f) < 0.001f && l2.Id != light.Id;
                    l2.Intensity = 1f;
                    insp.PasteValues(l2);
                    bool pastedValues = Math.Abs(l2.Intensity - 7.5f) < 0.001f;
                    SelectionService.Instance.Select(e);
                    await SmokeRegistry.Settle(400);
                    var fly = insp.ShowAddComponentPopup(null);
                    await SmokeRegistry.Settle(400);
                    if (fly?.Content is Avalonia.Controls.Control popup) SmokeRegistry.Capture(popup, "inspector_add_component.png");
                    // search: "joint" lists the five physics joints
                    var dock = (fly?.Content as Avalonia.Controls.Border)?.Child as Avalonia.Controls.DockPanel;
                    var search = dock?.Children.OfType<Avalonia.Controls.TextBox>().FirstOrDefault();
                    var rows = (dock?.Children.OfType<Avalonia.Controls.ScrollViewer>().FirstOrDefault()?.Content as Avalonia.Controls.StackPanel);
                    bool joints = false;
                    if (search != null && rows != null)
                    {
                        search.Text = "joint";
                        await SmokeRegistry.Settle(300);
                        var names = rows.Children.OfType<Avalonia.Controls.Border>().Select(b => ((b.Child as Avalonia.Controls.Grid)?.Children.OfType<Avalonia.Controls.TextBlock>().FirstOrDefault())?.Text).Where(t => t != null).ToList();
                        joints = new[] { "Hinge Joint", "Ball Joint", "Slider Joint", "Fixed Joint", "Distance Joint" }.All(names.Contains);
                        if (fly.Content is Avalonia.Controls.Control popup2) SmokeRegistry.Capture(popup2, "inspector_add_component_search.png");
                    }
                    fly?.Hide();
                    SmokeRegistry.Capture(insp, "inspector_components.png");
                    Log("inspector ops: cards=" + cards + " reset=" + reset + "/" + resetUndo + " move=" + moved + "/" + moveUndo + " enable=" + disabled + "/" + enableUndo + " pasteNew=" + pastedNew + " pasteValues=" + pastedValues + " jointsInCatalog=" + joints);
                    return cards && reset && resetUndo && moved && moveUndo && disabled && enableUndo && pastedNew && pastedValues && joints;
                }
                finally
                {
                    foreach (var x in new[] { e, e2 }) { SelectionService.Instance.Select(x); EditorCommands.Delete(); }
                    SelectionService.Instance.ClearSelection();
                }
            });

            SmokeRegistry.Add("prefab editor: isolated inspector never touches the global undo stack", async () =>
            {
                string path = FindPrefab("Weapon_Vityaz");
                if (path == null) return true;
                PrefabEditorWindow.Open(path);
                var w = PrefabEditorWindow.Find(path);
                if (w == null) return Fail("window did not open");
                await SmokeRegistry.Settle(700);
                int undo = UndoRedoManager.Instance.UndoCount;
                var target = w.Root.Children.FirstOrDefault() ?? w.Root;
                w.SelectEntity(target);
                await SmokeRegistry.Settle(300);
                w.Inspector.AddNew(new Rigidbody(target), target);
                var rb = target.GetComponent<Rigidbody>();
                w.Inspector.MoveComponent(rb, -1);
                w.Inspector.SetComponentEnabled(rb, false);
                w.Inspector.RemoveComponentFromEntity(rb);
                bool untouched = UndoRedoManager.Instance.UndoCount == undo && target.GetComponent<Rigidbody>() == null;
                w.CloseWithoutSaving();
                await SmokeRegistry.Settle(200);
                return untouched;
            });
        }

        private static string FindPrefab(string name)
        {
            string root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return null;
            string dir = Path.Combine(root, "Assets", "Prefabs");
            string exact = Path.Combine(dir, name + ".ventity");
            if (File.Exists(exact)) return exact;
            try { return Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.ventity", SearchOption.AllDirectories).FirstOrDefault(f => !Path.GetFileName(f).StartsWith("Smoke", StringComparison.OrdinalIgnoreCase)); }
            catch { return null; }
        }

        private static bool Fail(string why) { ConsoleService.Instance.LogWarning("smoke: " + why); return false; }
        private static void Log(string s) => ConsoleService.Instance.Log("smoke: " + s);

        internal static bool HasContent(PreviewImage img)
        {
            var seen = new HashSet<int>();
            for (int y = 0; y < img.Height; y += Math.Max(1, img.Height / 16))
                for (int x = 0; x < img.Width; x += Math.Max(1, img.Width / 16))
                {
                    int o = y * img.Stride + x * 4;
                    seen.Add((img.Bgra[o] >> 3) | ((img.Bgra[o + 1] >> 3) << 5) | ((img.Bgra[o + 2] >> 3) << 10));
                }
            return seen.Count > 3;
        }
    }
}
