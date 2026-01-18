using System.Runtime.Serialization;

namespace Editor.ECS.Components.Scripting
{
    /// <summary>
    /// Script-Komponente für benutzerdefinierte Logik.
    /// </summary>
    [DataContract(Name = "Script", Namespace = "")]
    public class Script : Component
    {
        private string _scriptPath;
        private string _scriptClassName;
        private bool _isCompiled;

        public override string DisplayName => string.IsNullOrEmpty(_scriptClassName) ? "Script" : _scriptClassName;
        public override string IconCode => "\uE756";
        public override string IconColor => "#DCDCAA";

        /// <summary>
        /// Pfad zur Script-Datei
        /// </summary>
        [DataMember(Name = "scriptPath", Order = 10)]
        public string ScriptPath
        {
            get => _scriptPath;
            set => SetProperty(ref _scriptPath, value, nameof(ScriptPath));
        }

        /// <summary>
        /// Name der Script-Klasse
        /// </summary>
        [DataMember(Name = "scriptClassName", Order = 11)]
        public string ScriptClassName
        {
            get => _scriptClassName;
            set
            {
                if (SetProperty(ref _scriptClassName, value, nameof(ScriptClassName)))
                    OnPropertyChanged(nameof(DisplayName));
            }
        }

        /// <summary>
        /// Ob das Script kompiliert ist
        /// </summary>
        [IgnoreDataMember]
        public bool IsCompiled
        {
            get => _isCompiled;
            set => SetProperty(ref _isCompiled, value, nameof(IsCompiled));
        }

        public Script() : base() { }
        public Script(GameEntity entity) : base(entity) { }
        public Script(GameEntity entity, string scriptPath) : base(entity)
        {
            ScriptPath = scriptPath;
            ScriptClassName = System.IO.Path.GetFileNameWithoutExtension(scriptPath);
        }
    }
}
