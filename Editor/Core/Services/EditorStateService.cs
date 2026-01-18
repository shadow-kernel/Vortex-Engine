using System;
using System.IO;
using System.Runtime.Serialization;
using Editor.Core.Serialization;

namespace Editor.Core.Services
{
    /// <summary>
    /// Speichert und lädt den Editor-Zustand aus dem Roaming AppData-Ordner.
    /// </summary>
    public sealed class EditorStateService
    {
        private static readonly Lazy<EditorStateService> _instance = new Lazy<EditorStateService>(() => new EditorStateService());
        public static EditorStateService Instance => _instance.Value;

        private readonly string _stateFilePath;
        private EditorState _currentState;

        private EditorStateService()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VortexEngine"
            );
            
            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }
            
            _stateFilePath = Path.Combine(appDataPath, "editor-state.json");
            _currentState = LoadState();
        }

        /// <summary>
        /// Gibt den Pfad des zuletzt geöffneten Projekts zurück, oder null wenn keins gespeichert ist.
        /// </summary>
        public string LastProjectPath => _currentState?.LastProjectPath;

        /// <summary>
        /// Gibt die ID des zuletzt geöffneten Projekts zurück.
        /// </summary>
        public Guid? LastProjectId => _currentState?.LastProjectId;

        /// <summary>
        /// Speichert das aktuelle Projekt als letztes geöffnetes Projekt.
        /// </summary>
        public void SetLastProject(Guid projectId, string projectPath)
        {
            _currentState = new EditorState
            {
                LastProjectId = projectId,
                LastProjectPath = projectPath
            };
            SaveState();
        }

        /// <summary>
        /// Löscht die Information über das letzte Projekt.
        /// Wird beim Schließen eines Projekts aufgerufen.
        /// </summary>
        public void ClearLastProject()
        {
            _currentState = new EditorState();
            SaveState();
        }

        /// <summary>
        /// Prüft ob das gespeicherte Projekt noch existiert.
        /// </summary>
        public bool IsLastProjectValid()
        {
            if (string.IsNullOrEmpty(_currentState?.LastProjectPath))
                return false;

            if (!Directory.Exists(_currentState.LastProjectPath))
                return false;

            var projectFile = Path.Combine(_currentState.LastProjectPath, ".ve", "project.json");
            return File.Exists(projectFile);
        }

        private EditorState LoadState()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    return DataSerializer.LoadFromJson<EditorState>(_stateFilePath);
                }
            }
            catch
            {
                // Bei Fehlern einfach neuen Zustand erstellen
            }
            
            return new EditorState();
        }

        private void SaveState()
        {
            try
            {
                DataSerializer.SaveAsJson(_currentState, _stateFilePath);
            }
            catch
            {
                // Fehler beim Speichern ignorieren - nicht kritisch
            }
        }
    }

    /// <summary>
    /// Repräsentiert den persistierten Editor-Zustand.
    /// </summary>
    [DataContract(Name = "EditorState", Namespace = "")]
    internal class EditorState
    {
        [DataMember(Name = "lastProjectId", Order = 0)]
        public Guid? LastProjectId { get; set; }

        [DataMember(Name = "lastProjectPath", Order = 1)]
        public string LastProjectPath { get; set; }
    }
}
