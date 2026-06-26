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
        /// Erstellt eine neue Szene mit Standard-Objekten
        /// </summary>
        public Scene CreateDefaultScene(ProjectData project, string name = "New Scene")
        {
            var scene = new Scene(project, name);

            // Standard-Kamera erstellen
            var camera = new GameEntity(scene, "Main Camera");
            camera.Transform.LocalPosition = new ECS.Vector3(0, 1, -10);
            camera.AddComponentDirect(new Camera(camera) { IsMainCamera = true });
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
            DataSerializer.SaveAsJson(entity, filePath);
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
