using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexEditor.Claude
{
    /// <summary>
    /// A behaviour's public fields as the tools see them (#405) — what the Inspector shows under a Script: every
    /// inspectable public field of the compiled class with its value on this entity (the entity's override, else the
    /// code default). get_entity lists them; attach_script's fields and set_component_properties on a Script set them
    /// through <see cref="Set"/>, one undoable path that also reaches the running behaviour in play mode (#345).
    /// </summary>
    public static class ScriptFields
    {
        /// <summary>The script's compiled behaviour class, or null while the scripts do not compile.</summary>
        public static Type TypeOf(Script sc) =>
            string.IsNullOrEmpty(sc?.ScriptClassName) ? null : ScriptRuntime.Instance.GetScriptTypeForInspector(sc.ScriptClassName);

        /// <summary>The fields the Inspector edits: public instance fields of an inspectable type.</summary>
        public static List<FieldInfo> Inspectable(Type type) =>
            type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => ScriptRuntime.IsInspectableFieldType(f.FieldType)).ToList();

        /// <summary>A public field by name — "MoveSpeed", "moveSpeed" and "move_speed" are the same — or null.</summary>
        public static FieldInfo Find(Type type, string name) =>
            type.GetField(name ?? "", BindingFlags.Public | BindingFlags.Instance)
            ?? type.GetFields(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(f => ComponentProps.Norm(f.Name) == ComponentProps.Norm(name));

        private static string Names(Type type) { var n = Inspectable(type).Select(f => f.Name).ToList(); return n.Count == 0 ? "(none)" : string.Join(", ", n); }

        /// <summary>For get_entity: "fields" — every inspectable field with the entity's value (override, else code default) —
        /// and "overridden_fields", the names the entity overrides. Without a compiled class only the stored overrides
        /// can be shown, with a note why.</summary>
        public static void Describe(Script sc, IDictionary<string, object> into)
        {
            var type = TypeOf(sc);
            if (type == null)
            {
                if (sc.FieldValues?.Count > 0)
                    into["fields"] = sc.FieldValues.Where(v => !string.IsNullOrEmpty(v?.Name)).GroupBy(v => v.Name)
                                       .ToDictionary(g => g.Key, g => (object)g.Last().Value?.Replace(ScriptRuntime.ArraySeparator, ','));
                into["fields_note"] = "the class " + sc.ScriptClassName + " is not compiled (compile_scripts shows why) — only the stored overrides are listed";
                return;
            }
            object defaults = null;
            try { defaults = Activator.CreateInstance(type); } catch { }   // the code defaults, as the Inspector reads them
            var values = new Dictionary<string, object>();
            var overridden = new List<string>();
            foreach (var f in Inspectable(type))
            {
                string stored = sc.GetFieldValue(f.Name);
                if (stored != null) overridden.Add(f.Name);
                object v;
                try { v = stored != null ? ScriptRuntime.ParseFieldValue(f.FieldType, stored) : defaults != null ? f.GetValue(defaults) : null; }
                catch { v = stored; }   // an override the field's current type does not parse: as stored
                values[f.Name] = Json(v);
            }
            into["fields"] = values;
            if (overridden.Count > 0) into["overridden_fields"] = overridden;
        }

        /// <summary>A field value for JSON: numbers and bools as they are, Vector3 as [x, y, z], enums by name, arrays
        /// element-wise; a non-finite float as text (JSON has no NaN / Infinity).</summary>
        private static object Json(object v) => v switch
        {
            null => null,
            float f => Num(f),
            Vortex.Vector3 v3 => new[] { Num(v3.X), Num(v3.Y), Num(v3.Z) },
            Enum e => e.ToString(),
            string s => s,
            Array a => a.Cast<object>().Select(Json).ToArray(),
            _ => v,
        };

        private static object Num(float f) => float.IsFinite(f) ? f : f.ToString(CultureInfo.InvariantCulture);

        /// <summary>set_component_properties on a Script (#405): names other than the component's properties are the
        /// behaviour's fields — {"Speed": 2} — and "fields" sets several at once, the shape get_entity shows.</summary>
        public static string SetFromProperties(Script sc, string name, JsonElement value)
        {
            if (ComponentProps.Norm(name) == "fields")
            {
                if (value.ValueKind != JsonValueKind.Object) throw new ToolError("fields expects an object {\"Field\": value}.");
                return string.Join(", ", value.EnumerateObject().Select(p => Set(sc, p.Name, p.Value)));
            }
            var type = TypeOf(sc);
            if (type != null && Find(type, name) == null)
                throw new ToolError("Script has no property '" + name + "' and " + type.Name + " no public field of that name. Fields of " + type.Name + ": " + Names(type) +
                                    ". Properties: " + string.Join(", ", ComponentProps.Editable(typeof(Script)).Select(p => ComponentProps.SnakeName(p.Name))) + ".");
            return Set(sc, name, value);
        }

        /// <summary>Set one public field of the behaviour (undoable): a JSON value of the field's type; null drops the
        /// entity's override, so the field follows the code default again. While playing, the running behaviour takes
        /// the value at once (#345). Returns the field's name.</summary>
        public static string Set(Script sc, string name, JsonElement value)
        {
            var type = TypeOf(sc) ?? throw new ToolError("The script's class " + sc.ScriptClassName + " could not be compiled or found, so its fields are unknown — fix compile_scripts errors first (the class name must match the file name).");
            var f = Find(type, name) ?? throw new ToolError(type.Name + " has no public field '" + name + "'. Fields: " + Names(type));
            if (!ScriptRuntime.IsInspectableFieldType(f.FieldType)) throw new ToolError(f.Name + " (" + f.FieldType.Name + ") cannot be set from the editor.");
            string formatted = value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined ? null : ScriptRuntime.FormatFieldValue(Convert(value, f.FieldType, f.Name));
            string old = sc.GetFieldValue(f.Name);
            if (formatted != old)
            {
                var target = sc; string fname = f.Name;
                UndoRedoManager.Instance.Execute(new ActionCommand("Set " + fname, () => target.SetFieldValue(fname, formatted), () => target.SetFieldValue(fname, old)));
            }
            if (PlayModeService.Instance.State == PlayState.Playing && sc.Entity != null) ScriptRuntime.Instance.ApplyFieldsToLiveBehaviour(sc.Entity);
            return f.Name;
        }

        /// <summary>A JSON value as a field's type: numbers, bools, strings, enums by name, [x, y, z], arrays.</summary>
        public static object Convert(JsonElement v, Type t, string name)
        {
            if (t == typeof(Vortex.Vector3))
            {
                var e = (Editor.ECS.Vector3)ComponentProps.Convert(v, typeof(Editor.ECS.Vector3), name);
                return new Vortex.Vector3(e.X, e.Y, e.Z);
            }
            if (t.IsArray)
            {
                if (v.ValueKind != JsonValueKind.Array) throw new ToolError(name + " expects an array.");
                var et = t.GetElementType();
                var items = v.EnumerateArray().Select(x => Convert(x, et, name)).ToList();
                var arr = Array.CreateInstance(et, items.Count);
                for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                return arr;
            }
            return ComponentProps.Convert(v, t, name);
        }
    }
}
