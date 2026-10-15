using System;
using System.IO;
using System.Linq;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services
{
    /// <summary>What <see cref="SceneService.ReloadScene"/> did (#411).</summary>
    public sealed class SceneReloadResult
    {
        /// <summary>The file was read and replaced the scene in memory.</summary>
        public bool Reloaded { get; internal set; }
        /// <summary>Nothing to do: the scene in memory already was its file and no data file was written since — nothing was read,
        /// no undo step dropped.</summary>
        public bool Unchanged { get; internal set; }
        /// <summary>Why the scene was not reloaded (it is untouched then).</summary>
        public string Error { get; internal set; }
        /// <summary>The scene in memory differed from its file: unsaved edits, leftovers of a play session, or a file changed on disk.</summary>
        public bool DiscardedChanges { get; internal set; }
        /// <summary>Undo and redo steps that acted on the discarded state and were dropped.</summary>
        public int UndoStepsDropped { get; internal set; }
        /// <summary>Terrain, foliage and area files put back to the last save — one made since then removed, with its .vmeta
        /// (absolute paths).</summary>
        public System.Collections.Generic.IReadOnlyList<string> DataFilesRestored { get; internal set; } = Array.Empty<string>();
        /// <summary>Data files written since the save that stayed: another loaded scene uses them too.</summary>
        public System.Collections.Generic.IReadOnlyList<string> DataFilesKept { get; internal set; } = Array.Empty<string>();
        /// <summary>Data files the editor wrote that changed on disk since (a git pull, another program): left as they are.</summary>
        public System.Collections.Generic.IReadOnlyList<string> DataFilesChangedOnDisk { get; internal set; } = Array.Empty<string>();

        internal static SceneReloadResult Refused(string why) => new SceneReloadResult { Error = why };
    }

    /// <summary>What a revert would do (<see cref="SceneService.PreviewReload"/>, #411).</summary>
    public sealed class SceneReloadPreview
    {
        /// <summary>Why a revert would be refused now; null when it can run.</summary>
        public string Blocked;
        /// <summary>The scene in memory differs from its file (or its data files were written since the save): a revert reads it.</summary>
        public bool Differs;
        /// <summary>The scene has unsaved changes (<see cref="SceneService.HasUnsavedChanges"/>).</summary>
        public bool Unsaved;
        /// <summary>Undo and redo steps a revert drops.</summary>
        public int UndoSteps;
        /// <summary>Terrain, foliage and area files a revert puts back (absolute paths).</summary>
        public System.Collections.Generic.List<string> DataFiles = new System.Collections.Generic.List<string>();
    }

    /// <summary>
    /// Service f�r das Laden, Speichern und Verwalten von Szenen.
    /// </summary>
    public class SceneService
    {
        private static SceneService _instance;
        private static readonly object _lock = new object();

        public static SceneService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new SceneService();
                        }
                    }
                }
                return _instance;
            }
        }

        private SceneService() { }

        /// <summary>
        /// Die aktuell aktive Szene
        /// </summary>
        public Scene CurrentScene { get; set; }

        /// <summary>
        /// Event wird ausgel�st wenn eine Szene gespeichert wurde
        /// </summary>
        public event EventHandler<Scene> SceneSaved;

        /// <summary>
        /// Event wird ausgel�st wenn eine Szene geladen wurde
        /// </summary>
        public event EventHandler<Scene> SceneLoaded;

        /// <summary>
        /// Speichert eine einzelne Szene
        /// </summary>
        public void SaveScene(Scene scene)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));

            scene.Save();
            SceneSaved?.Invoke(this, scene);
        }

        /// <summary>
        /// Speichert alle Szenen des Projekts
        /// </summary>
        public void SaveAllScenes(ProjectData project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            foreach (var scene in project.Scenes)
            {
                if (scene.IsLoaded && scene.IsDirty)
                {
                    SaveScene(scene);
                }
            }
        }

        /// <summary>
        /// L�dt eine Szene aus einer Datei
        /// </summary>
        public Scene LoadScene(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Szenen-Datei nicht gefunden", filePath);

            var scene = DataSerializer.LoadFromBinary<Scene>(filePath);
            scene.FilePath = filePath;
            scene.Load();
            scene.NoteFileState();
            SceneLoaded?.Invoke(this, scene);
            return scene;
        }

        /// <summary>
        /// What a revert of the scene would do (#411) — File ▸ Revert Scene asks with these numbers and open_scene reports them:
        /// whether the scene differs from its file at all, how many undo steps go, which data files are put back, and why a
        /// revert would be refused.
        /// </summary>
        public SceneReloadPreview PreviewReload(Scene scene)
        {
            var p = new SceneReloadPreview();
            if (scene == null) { p.Blocked = "No scene."; return p; }
            p.Blocked = Blocker(scene, out byte[] disk);
            if (p.Blocked != null) return p;
            p.Unsaved = HasUnsavedChanges(scene);
            p.DataFiles = SceneDataFiles.Pending(scene);
            p.Differs = p.DataFiles.Count > 0 || !MatchesFile(scene, disk);
            if (!p.Differs) return p;
            p.UndoSteps = UndoStepsOf(scene);
            p.Blocked = Conflicts(scene, disk);
            return p;
        }

        /// <summary>
        /// Revert (#411): discard the scene's in-memory state and read its .vscene again — into the same Scene object, so the
        /// project, the hierarchy and every reference to the scene stay valid. Entities, environment, name and id come from the
        /// file; the old entities leave the engine and the renderer, the new ones join when the scene is active; terrain,
        /// foliage and area files written since the last save are put back (<see cref="SceneDataFiles"/>); the undo and redo
        /// steps that act on the old state are dropped — undoing one would put a discarded entity back next to its reloaded twin
        /// — while the steps of other scenes and of files stay. When the scene already is its file nothing is read and no step
        /// goes (<see cref="SceneReloadResult.Unchanged"/>). Refused while playing (Stop puts the scenes of Play back), while an
        /// undo group is open, and when an entity of the file is in another loaded scene now. A file that cannot be read leaves
        /// the scene as it was.
        /// </summary>
        public SceneReloadResult ReloadScene(Scene scene)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            string blocked = Blocker(scene, out byte[] disk);
            if (blocked != null)
                return SceneReloadResult.Refused(blocked);
            var pending = SceneDataFiles.Pending(scene);
            if (pending.Count == 0 && MatchesFile(scene, disk))
            {
                // the scene is its file: nothing to read, no step to drop — and nothing unsaved, whatever the flag said
                scene.NoteFileState();
                return new SceneReloadResult { Unchanged = true };
            }
            string conflict = Conflicts(scene, disk);
            if (conflict != null)
                return SceneReloadResult.Refused(conflict);

            Scene file;
            try { file = DataSerializer.FromBinary<Scene>(disk); }
            catch (Exception ex) { return SceneReloadResult.Refused("Could not read " + Path.GetFileName(scene.FilePath) + ": " + ex.Message); }
            if (file == null)
                return SceneReloadResult.Refused("Could not read " + Path.GetFileName(scene.FilePath) + ".");

            bool changed = pending.Count > 0 || Differs(scene, file);
            var gone = ObjectsOf(scene);
            // the data files go back to the last save before the file's entities read them; one another loaded scene uses as
            // well stays as it is — that scene shows it
            var elsewhere = UsedElsewhere(scene);
            var paths = SceneDataFiles.PathsOf(scene);
            paths.UnionWith(SceneDataFiles.PathsOf(file));
            paths.UnionWith(SceneDataFiles.OwnedBy(scene));   // made for an entity that is gone again: removed unless used elsewhere
            paths.ExceptWith(elsewhere);
            var restored = SceneDataFiles.Restore(paths, out var changedOnDisk);
            Guid oldId = scene.Id;
            scene.ReplaceContent(file);
            var project = scene.Project ?? ProjectData.Current;
            if (project != null && project.StartSceneId == oldId && scene.Id != oldId) project.StartSceneId = scene.Id;
            int dropped = UndoRedo.UndoRedoManager.Instance.Drop(step => UndoRedo.UndoReferences.Uses(step, o => Classify(o, scene, gone)));
            try { Editor.Core.Services.Foliage.FoliageService.ForgetMeshes(); } catch { }   // ray soups are cached by entity id
            SceneLoaded?.Invoke(this, scene);
            return new SceneReloadResult
            {
                Reloaded = true, DiscardedChanges = changed, UndoStepsDropped = dropped,
                DataFilesRestored = restored, DataFilesKept = pending.Where(p => elsewhere.Contains(p)).ToList(),
                DataFilesChangedOnDisk = changedOnDisk,
            };
        }

        /// <summary>
        /// Whether the scene has unsaved changes (#411) — the one answer list_scenes, scene_outline, open_scene and File ▸ Revert
        /// Scene give: the scene in memory is not what it was when it was read or saved (<see cref="Scene.MatchesBaseline"/>,
        /// compared as this engine writes it — exact whichever way an edit came, and an edit taken back by hand is none), terrain
        /// or foliage data not written yet, or data files written since the save. Not <see cref="Scene.IsDirty"/>, a fast hint
        /// that edits without an undo step can miss. A file changed on disk is another question: <see cref="FileChangedOnDisk"/>.
        /// Serializes the scene (a few ms; ~50 ms for 10,000 entities).
        /// </summary>
        public bool HasUnsavedChanges(Scene scene)
            => scene != null && scene.IsLoaded && (HasUnsavedData(scene) || SceneDataFiles.Pending(scene).Count > 0 || !scene.MatchesBaseline());

        /// <summary>Whether the scene's file was changed on disk (a git checkout, another program) since the editor read or wrote
        /// it: the scene in memory does not show that version.</summary>
        public bool FileChangedOnDisk(Scene scene) => scene != null && scene.FileChangedOnDisk();

        /// <summary>Why a revert cannot run now (null when it can), and the file's bytes.</summary>
        private static string Blocker(Scene scene, out byte[] disk)
        {
            disk = null;
            if (PlayModeService.Instance.IsPlaying)
                return "Stop play mode first: Stop puts the scenes of Play back, over a scene read while playing.";
            if (UndoRedo.UndoRedoManager.Instance.IsGrouping)
                return "An edit is still in progress.";
            string path = scene.FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return "'" + scene.Name + "' was never saved: there is no file to read again.";
            try { disk = File.ReadAllBytes(path); }
            catch (Exception ex) { return "Could not read " + Path.GetFileName(path) + ": " + ex.Message; }
            return null;
        }

        /// <summary>
        /// The scene in memory is what its file holds, both as this engine writes them (#411 review): a file written by an older
        /// engine and not touched since matches. While the file is the one the editor read or wrote, the content noted then
        /// answers; a file changed on disk is read and written again by this engine to compare.
        /// </summary>
        private static bool MatchesFile(Scene scene, byte[] disk)
        {
            if (HasUnsavedData(scene)) return false;
            if (scene.HasBaseline && !scene.FileChangedOnDisk()) return scene.MatchesBaseline();
            try
            {
                var mine = DataSerializer.ToBinary(scene);
                if (mine.AsSpan().SequenceEqual(disk)) return true;
                var file = DataSerializer.FromBinary<Scene>(disk);
                return file != null && mine.AsSpan().SequenceEqual(DataSerializer.ToBinary(file));
            }
            catch { return false; }
        }

        /// <summary>The scene against what was just read from its file, both as this engine writes them (a file written by an
        /// older engine serializes differently although nothing changed).</summary>
        private static bool Differs(Scene scene, Scene file)
        {
            if (HasUnsavedData(scene))
                return true;
            try { return !DataSerializer.ToBinary(scene).AsSpan().SequenceEqual(DataSerializer.ToBinary(file)); }
            catch { return true; }
        }

        /// <summary>Terrain heights and foliage live in their own files, saved with the scene.</summary>
        private static bool HasUnsavedData(Scene scene)
        {
            var todo = new System.Collections.Generic.Stack<GameEntity>(scene.Entities);
            while (todo.Count > 0)
            {
                var e = todo.Pop();
                if (e == null) continue;
                if (Editor.Core.Services.Terrain.TerrainService.HasUnsavedChanges(e) || Editor.Core.Services.Foliage.FoliageService.HasUnsavedChanges(e))
                    return true;
                if (e.Children != null) foreach (var c in e.Children) todo.Push(c);
            }
            return false;
        }

        /// <summary>The undo and redo steps a revert of the scene drops.</summary>
        private static int UndoStepsOf(Scene scene)
        {
            var gone = ObjectsOf(scene);
            var undo = UndoRedo.UndoRedoManager.Instance;
            return undo.GetUndoHistory().Concat(undo.GetRedoHistory()).Count(step => UndoRedo.UndoReferences.Uses(step, o => Classify(o, scene, gone)));
        }

        /// <summary>The data files other loaded scenes use (a shared area file).</summary>
        private static System.Collections.Generic.HashSet<string> UsedElsewhere(Scene scene)
        {
            var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scenes = (scene.Project ?? ProjectData.Current)?.Scenes;
            if (scenes == null) return set;
            foreach (var other in scenes)
                if (other != null && !ReferenceEquals(other, scene) && other.IsLoaded) set.UnionWith(SceneDataFiles.PathsOf(other));
            return set;
        }

        /// <summary>
        /// An entity of the file that another loaded scene holds now — moved there since the save: a revert would put the same
        /// entity into two scenes (#411 review). Null when there is none. The ids are read from the file without building the
        /// scene (building one loads its resources).
        /// </summary>
        private static string Conflicts(Scene scene, byte[] disk)
        {
            var scenes = (scene.Project ?? ProjectData.Current)?.Scenes;
            if (scenes == null) return null;
            var inFile = EntityIdsIn(disk);
            if (inFile.Count == 0) return null;
            var hits = new System.Collections.Generic.List<string>();
            foreach (var other in scenes)
            {
                if (other == null || ReferenceEquals(other, scene) || !other.IsLoaded || other.Entities == null) continue;
                var todo = new System.Collections.Generic.Stack<GameEntity>(other.Entities);
                while (todo.Count > 0)
                {
                    var e = todo.Pop();
                    if (e == null) continue;
                    if (inFile.ContainsKey(e.Id)) hits.Add("'" + e.Name + "' (now in '" + other.Name + "')");
                    if (e.Children != null) foreach (var c in e.Children) todo.Push(c);
                }
            }
            if (hits.Count == 0) return null;
            return "Reverting '" + scene.Name + "' would put " + string.Join(", ", hits.Take(5)) + (hits.Count > 5 ? " and " + (hits.Count - 5) + " more" : "")
                 + " back into it while another scene holds the same entity. Move it back or delete it there first.";
        }

        /// <summary>The ids and names of every entity in a .vscene, read from the XML without building objects.</summary>
        internal static System.Collections.Generic.Dictionary<Guid, string> EntityIdsIn(byte[] vscene)
        {
            var map = new System.Collections.Generic.Dictionary<Guid, string>();
            try
            {
                using var r = System.Xml.XmlDictionaryReader.CreateBinaryReader(vscene, System.Xml.XmlDictionaryReaderQuotas.Max);
                var stack = new System.Collections.Generic.List<string>();
                string capture = null;
                Guid last = Guid.Empty;
                while (r.Read())
                {
                    switch (r.NodeType)
                    {
                        case System.Xml.XmlNodeType.Element:
                            string parent = stack.Count > 0 ? stack[stack.Count - 1] : null;
                            bool empty = r.IsEmptyElement;
                            capture = parent == "GameEntity" && (r.LocalName == "id" || r.LocalName == "name") && !empty ? r.LocalName : null;
                            if (!empty) stack.Add(r.LocalName);
                            break;
                        case System.Xml.XmlNodeType.Text:
                            if (capture == "id" && Guid.TryParse(r.Value, out var g)) { last = g; map[g] = ""; }
                            else if (capture == "name" && last != Guid.Empty) map[last] = r.Value;
                            break;
                        case System.Xml.XmlNodeType.EndElement:
                            if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                            capture = null;
                            break;
                    }
                }
            }
            catch { }
            return map;
        }

        /// <summary>Everything of the scene's in-memory state an undo step can hold: the scene, its root list and environment,
        /// every entity with its child and component lists, every component.</summary>
        private static System.Collections.Generic.HashSet<object> ObjectsOf(Scene scene)
        {
            var set = new System.Collections.Generic.HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance) { scene, scene.Entities, scene.Settings };
            var todo = new System.Collections.Generic.Stack<GameEntity>(scene.Entities);
            while (todo.Count > 0)
            {
                var e = todo.Pop();
                if (e == null || !set.Add(e)) continue;
                set.Add(e.Children);
                set.Add(e.Components);
                foreach (var c in e.Components) if (c != null) set.Add(c);
                foreach (var c in e.Children) todo.Push(c);
            }
            return set;
        }

        /// <summary>For the undo walk: does an object of a step belong to the reverted scene? An entity the step took out of the
        /// scene earlier still names it as its scene. Objects of other scenes are theirs — a step that runs out of budget among
        /// them stays; the project and lists of scenes are not looked into (the project's scene list holds this scene without
        /// being about its content).</summary>
        private static UndoRedo.UndoRef Classify(object o, Scene scene, System.Collections.Generic.HashSet<object> gone)
        {
            if (gone.Contains(o)) return UndoRedo.UndoRef.Target;
            switch (o)
            {
                case GameEntity e:
                    return ReferenceEquals(e.Scene, scene) ? UndoRedo.UndoRef.Target : e.Scene != null ? UndoRedo.UndoRef.Elsewhere : UndoRedo.UndoRef.Skip;
                case Editor.ECS.Component c:
                {
                    var s = c.Entity?.Scene;
                    return ReferenceEquals(s, scene) ? UndoRedo.UndoRef.Target : s != null ? UndoRedo.UndoRef.Elsewhere : UndoRedo.UndoRef.Skip;
                }
                case Scene _: return UndoRedo.UndoRef.Elsewhere;   // another scene: the reverted one is among the gone objects
                case ProjectData _: return UndoRedo.UndoRef.Skip;
                case System.Collections.Generic.IEnumerable<Scene> _: return UndoRedo.UndoRef.Skip;
                default: return UndoRedo.UndoRef.LookInto;
            }
        }

        /// <summary>
        /// Erstellt eine neue Szene mit Standard-Objekten
        /// </summary>
        public Scene CreateDefaultScene(ProjectData project, string name = "New Scene")
        {
            var scene = new Scene(project, name);

            // Standard-Kamera erstellen — sie ist zugleich der Spieler: ein PlayerController-Script
            // (im Projekt unter Assets/Scripts/Player) steuert die Bewegung. Bewegung = GAME-Logik im
            // Script, nicht in der Engine hartkodiert; im Script frei anpassbar.
            var camera = new GameEntity(scene, "Main Camera");
            camera.Transform.LocalPosition = new ECS.Vector3(0, 1, -10);
            camera.AddComponentDirect(new Camera(camera) { IsMainCamera = true });
            var playerScriptPath = ScriptingService.EnsurePlayerController(project.Path);
            if (!string.IsNullOrEmpty(playerScriptPath))
                camera.AddComponentDirect(new Editor.ECS.Components.Scripting.Script(camera, playerScriptPath));
            scene.Entities.Add(camera);

            // Standard-Licht erstellen (hell genug, damit die Szene nicht dunkel ist)
            var light = new GameEntity(scene, "Directional Light");
            light.Transform.LocalEulerAngles = new ECS.Vector3(50, -30, 0);
            light.AddComponentDirect(new Light(light, LightType.Directional) { Intensity = 3.0f });
            scene.Entities.Add(light);

            // Boden-Ebene erstellen — solide (wird im Play als statischer Collider registriert,
            // man kann darauf stehen) und gibt der Szene eine beleuchtete Grundfläche.
            var ground = new GameEntity(scene, "Ground");
            ground.Transform.LocalScale = new ECS.Vector3(20, 1, 20);
            ground.AddComponentDirect(new Editor.ECS.Components.Rendering.MeshRenderer(ground) { MeshPath = "Primitive:Plane" });
            scene.Entities.Add(ground);

            scene.IsDirty = true;
            return scene;
        }

        /// <summary>
        /// Speichert eine GameEntity als Prefab (.ventity)
        /// </summary>
        public void SaveEntityAsPrefab(GameEntity entity, string filePath)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            // Prefabs sollen f�r Nutzer lesbar sein -> JSON speichern
            bool created = !System.IO.File.Exists(filePath);
            DataSerializer.SaveAsJson(entity, filePath);
            PlaySession.NoteWrite(filePath, created);   // Stop re-syncs the instances of a prefab written while playing (#423)
        }

        /// <summary>
        /// L�dt eine GameEntity aus einer Prefab-Datei (.ventity)
        /// </summary>
        public GameEntity LoadEntityFromPrefab(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Prefab-Datei nicht gefunden", filePath);

            return DataSerializer.LoadFromJson<GameEntity>(filePath);
        }

        /// <summary>
        /// Gibt den Assets-Ordner f�r Szenen zur�ck
        /// </summary>
        public string GetScenesFolder(ProjectData project)
        {
            var folder = Path.Combine(project.Path, "Assets", "Scenes");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return folder;
        }

        /// <summary>
        /// Gibt den Assets-Ordner f�r Prefabs zur�ck
        /// </summary>
        public string GetPrefabsFolder(ProjectData project)
        {
            var folder = Path.Combine(project.Path, "Assets", "Prefabs");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return folder;
        }
    }
}
