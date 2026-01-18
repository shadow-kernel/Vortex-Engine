using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Editor.Core.Data
{
    /// <summary>
    /// Leichtgewichtiges Projekt-Manifest das nur Metadaten und Referenzen enthält.
    /// Die eigentlichen Szenen werden in separaten .vscene Dateien gespeichert.
    /// </summary>
    [DataContract(Name = "ProjectManifest", Namespace = "")]
    public class ProjectManifest
    {
        /// <summary>
        /// Eindeutige Projekt-ID
        /// </summary>
        [DataMember(Name = "id", Order = 0)]
        public Guid Id { get; set; }

        /// <summary>
        /// Projekt-Name
        /// </summary>
        [DataMember(Name = "name", Order = 1)]
        public string Name { get; set; }

        /// <summary>
        /// Projekt-Version
        /// </summary>
        [DataMember(Name = "version", Order = 2)]
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// Engine-Version
        /// </summary>
        [DataMember(Name = "engineVersion", Order = 3)]
        public string EngineVersion { get; set; } = "1.0.0";

        /// <summary>
        /// Letzte Änderung
        /// </summary>
        [DataMember(Name = "lastModified", Order = 4)]
        public DateTime LastModified { get; set; }

        /// <summary>
        /// Erstellungsdatum
        /// </summary>
        [DataMember(Name = "createdAt", Order = 5)]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Pfad zum Thumbnail-Bild (relativ zum Projektordner)
        /// </summary>
        [DataMember(Name = "thumbnailPath", Order = 6)]
        public string ThumbnailPath { get; set; }

        /// <summary>
        /// ID der Start-Szene
        /// </summary>
        [DataMember(Name = "startSceneId", Order = 7)]
        public Guid? StartSceneId { get; set; }

        /// <summary>
        /// ID der zuletzt geöffneten Szene (für Editor)
        /// </summary>
        [DataMember(Name = "lastOpenSceneId", Order = 8)]
        public Guid? LastOpenSceneId { get; set; }

        /// <summary>
        /// Liste der Szenen-Referenzen (nur IDs und Namen, keine Inhalte)
        /// </summary>
        [DataMember(Name = "scenes", Order = 10)]
        public List<SceneReference> Scenes { get; set; } = new List<SceneReference>();

        /// <summary>
        /// Projekt-Einstellungen
        /// </summary>
        [DataMember(Name = "settings", Order = 20)]
        public ProjectSettings Settings { get; set; } = new ProjectSettings();

        public ProjectManifest()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.Now;
            LastModified = DateTime.Now;
        }

        public ProjectManifest(string name) : this()
        {
            Name = name;
        }
    }

    /// <summary>
    /// Referenz zu einer Szene (ohne den Inhalt)
    /// </summary>
    [DataContract(Name = "SceneReference", Namespace = "")]
    public class SceneReference
    {
        /// <summary>
        /// Eindeutige Szenen-ID
        /// </summary>
        [DataMember(Name = "id", Order = 0)]
        public Guid Id { get; set; }

        /// <summary>
        /// Szenen-Name
        /// </summary>
        [DataMember(Name = "name", Order = 1)]
        public string Name { get; set; }

        /// <summary>
        /// Relativer Pfad zur Szenen-Datei (relativ zu Assets/Scenes/)
        /// </summary>
        [DataMember(Name = "path", Order = 2)]
        public string RelativePath { get; set; }

        /// <summary>
        /// Ob die Szene beim Start geladen werden soll
        /// </summary>
        [DataMember(Name = "loadOnStart", Order = 3)]
        public bool LoadOnStart { get; set; }

        public SceneReference() { }

        public SceneReference(Guid id, string name, string relativePath)
        {
            Id = id;
            Name = name;
            RelativePath = relativePath;
        }
    }

    /// <summary>
    /// Projekt-Einstellungen
    /// </summary>
    [DataContract(Name = "ProjectSettings", Namespace = "")]
    public class ProjectSettings
    {
        /// <summary>
        /// Unternehmen/Entwickler Name
        /// </summary>
        [DataMember(Name = "companyName", Order = 0)]
        public string CompanyName { get; set; } = "DefaultCompany";

        /// <summary>
        /// Produkt-Name
        /// </summary>
        [DataMember(Name = "productName", Order = 1)]
        public string ProductName { get; set; } = "My Game";

        /// <summary>
        /// Standard-Bildschirmbreite
        /// </summary>
        [DataMember(Name = "defaultScreenWidth", Order = 2)]
        public int DefaultScreenWidth { get; set; } = 1920;

        /// <summary>
        /// Standard-Bildschirmhöhe
        /// </summary>
        [DataMember(Name = "defaultScreenHeight", Order = 3)]
        public int DefaultScreenHeight { get; set; } = 1080;

        /// <summary>
        /// Ob Vollbild standardmäßig aktiv ist
        /// </summary>
        [DataMember(Name = "fullscreenByDefault", Order = 4)]
        public bool FullscreenByDefault { get; set; } = false;

        /// <summary>
        /// VSync aktivieren
        /// </summary>
        [DataMember(Name = "vSync", Order = 5)]
        public bool VSync { get; set; } = true;

        /// <summary>
        /// Target FPS (0 = unlimited)
        /// </summary>
        [DataMember(Name = "targetFPS", Order = 6)]
        public int TargetFPS { get; set; } = 60;
    }
}
