using System;
using System.IO;
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
        /// <summary>Why the scene was not reloaded (it is unchanged then).</summary>
        public string Error { get; internal set; }
        /// <summary>The scene in memory differed from its file: unsaved edits, leftovers of a play session, or a file changed on disk.</summary>
        public bool DiscardedChanges { get; internal set; }
        /// <summary>Undo and redo steps that acted on the discarded state and were dropped.</summary>
        public int UndoStepsDropped { get; internal set; }

        internal static SceneReloadResult Refused(string why) => new SceneReloadResult { Error = why };
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
            SceneLoaded?.Invoke(this, scene);
            return scene;
        }

        /// <summary>
        /// Revert (#411): discard the scene's in-memory state and read its .vscene again — into the same Scene object, so the
        /// project, the hierarchy and every reference to the scene stay valid. Entities, environment and name come from the
        /// file; the old entities leave the engine and the renderer, the new ones join when the scene is active; the undo and
        /// redo steps that act on the old state are dropped (undoing one would put a discarded entity back next to its
        /// reloaded twin), the steps of other scenes and of files stay. Refused while playing — Stop puts the scenes of Play
        /// back — and while an undo group is open. A file that cannot be read leaves the scene as it was.
        /// </summary>
        public SceneReloadResult ReloadScene(Scene scene)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (PlayModeService.Instance.IsPlaying)
                return SceneReloadResult.Refused("Stop play mode first: Stop puts the scenes of Play back, over a scene read while playing.");
            if (UndoRedo.UndoRedoManager.Instance.IsGrouping)
                return SceneReloadResult.Refused("An edit is still in progress.");
            string path = scene.FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return SceneReloadResult.Refused("'" + scene.Name + "' was never saved: there is no file to read again.");

            byte[] disk;
            Scene file;
            try
            {
                disk = File.ReadAllBytes(path);
                file = DataSerializer.FromBinary<Scene>(disk);
            }
            catch (Exception ex) { return SceneReloadResult.Refused("Could not read " + Path.GetFileName(path) + ": " + ex.Message); }
            if (file == null)
                return SceneReloadResult.Refused("Could not read " + Path.GetFileName(path) + ".");

            bool changed = Differs(scene, disk, file);
            var gone = ObjectsOf(scene);
            scene.ReplaceContent(file);
            int dropped = UndoRedo.UndoRedoManager.Instance.Drop(step => UndoRedo.UndoReferences.Uses(step, o => Classify(o, scene, gone)));
            try { Editor.Core.Services.Foliage.FoliageService.ForgetMeshes(); } catch { }   // ray soups are cached by entity id
            SceneLoaded?.Invoke(this, scene);
            return new SceneReloadResult { Reloaded = true, DiscardedChanges = changed, UndoStepsDropped = dropped };
        }

        /// <summary>
        /// Whether the scene in memory differs from its .vscene file (#411) — what saving it would change: edits never saved,
        /// leftovers of a play session, unsaved terrain sculpting or foliage painting, or a file that changed on disk since it
        /// was read (a git checkout). A scene that was never saved has unsaved changes. <see cref="Scene.IsDirty"/> cannot say
        /// it: reading a scene sets it (through the name's setter) and most edits do not. It serializes the scene to compare —
        /// call it on demand, not every frame. A file written by an older engine version can count as different although
        /// nothing was edited.
        /// </summary>
        public bool HasUnsavedChanges(Scene scene)
        {
            if (scene == null || !scene.IsLoaded)
                return false;
            if (HasUnsavedData(scene))
                return true;
            string path = scene.FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return true;
            try { return !DataSerializer.ToBinary(scene).AsSpan().SequenceEqual(File.ReadAllBytes(path)); }
            catch { return true; }
        }

        /// <summary>The scene against what was just read from its file: a file written by an older engine serializes differently
        /// although nothing changed, so it is compared the way this engine writes it too.</summary>
        private static bool Differs(Scene scene, byte[] disk, Scene file)
        {
            if (HasUnsavedData(scene))
                return true;
            try
            {
                var mine = DataSerializer.ToBinary(scene);
                return !mine.AsSpan().SequenceEqual(disk) && !mine.AsSpan().SequenceEqual(DataSerializer.ToBinary(file));
            }
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

        /// <summary>For <see cref="UndoRedo.UndoReferences.Uses"/>: does an object of a step belong to the reloaded scene? An
        /// entity the step took out of the scene earlier still names it as its scene. Other scenes, the project and lists of
        /// scenes are not looked into — the project's scene list holds this scene without being about its content.</summary>
        private static bool? Classify(object o, Scene scene, System.Collections.Generic.HashSet<object> gone)
        {
            if (gone.Contains(o)) return true;
            switch (o)
            {
                case GameEntity e: return ReferenceEquals(e.Scene, scene);
                case Editor.ECS.Component c: return c.Entity != null && ReferenceEquals(c.Entity.Scene, scene);
                case Scene _: return false;
                case ProjectData _: return false;
                case System.Collections.Generic.IEnumerable<Scene> _: return false;
                default: return null;
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
