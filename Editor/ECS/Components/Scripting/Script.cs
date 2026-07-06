using System.Runtime.Serialization;

namespace Editor.ECS.Components.Scripting
{
    /// <summary>One serialized public script field (#47): the field name + its value as an invariant-
    /// culture string ("3.5", "true", "Fast", "1,2,3" for Vector3). Parsed by the REFLECTED field type
    /// at play start, so a renamed/removed field simply stops applying instead of breaking the scene.</summary>
    [DataContract(Name = "ScriptFieldValue", Namespace = "")]
    public class ScriptFieldValue
    {
        [DataMember(Name = "name", Order = 0)] public string Name { get; set; }
        [DataMember(Name = "value", Order = 1)] public string Value { get; set; }
    }

    /// <summary>
    /// Script-Komponente f�r benutzerdefinierte Logik.
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

        /// <summary>Per-instance overrides of the script's public fields (#47) — only fields the user
        /// actually EDITED are stored (everything else keeps the code default), so adding fields to a
        /// script never touches existing scenes. Optional in the file: pre-#47 scenes load with null.</summary>
        [DataMember(Name = "fieldValues", Order = 12, IsRequired = false, EmitDefaultValue = false)]
        public System.Collections.Generic.List<ScriptFieldValue> FieldValues { get; set; }

        /// <summary>The stored override for a field, or null.</summary>
        public string GetFieldValue(string name)
        {
            if (FieldValues == null) return null;
            foreach (var fv in FieldValues)
                if (fv != null && fv.Name == name) return fv.Value;
            return null;
        }

        /// <summary>Store (or clear with null) a field override.</summary>
        public void SetFieldValue(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (FieldValues == null)
            {
                if (value == null) return;
                FieldValues = new System.Collections.Generic.List<ScriptFieldValue>();
            }
            for (int i = 0; i < FieldValues.Count; i++)
            {
                if (FieldValues[i] != null && FieldValues[i].Name == name)
                {
                    if (value == null) FieldValues.RemoveAt(i);
                    else FieldValues[i].Value = value;
                    return;
                }
            }
            if (value != null) FieldValues.Add(new ScriptFieldValue { Name = name, Value = value });
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
