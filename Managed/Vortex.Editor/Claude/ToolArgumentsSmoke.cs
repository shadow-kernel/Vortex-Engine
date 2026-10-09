using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Editor.Core.Services;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>MCP tool calls with an unknown top-level argument are refused with the parameter list and a hint (#355)
    /// instead of silently running with defaults — create_entity {"primitive": "Cube"} used to make an empty entity.</summary>
    internal static class ToolArgumentsSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("tool arguments", Run);

        private static bool Run()
        {
            var log = ConsoleService.Instance;
            var create = ToolCatalog.All.FirstOrDefault(t => t.Name == "create_entity");
            var focus = ToolCatalog.All.FirstOrDefault(t => t.Name == "focus_camera");
            var openScene = ToolCatalog.All.FirstOrDefault(t => t.Name == "open_scene");
            if (create == null || focus == null) { log.LogError("tool arguments: create_entity / focus_camera not in the catalog"); return false; }

            string bad = ToolHost.UnknownArguments(create, new Dictionary<string, object> { ["name"] = "x", ["primitive"] = "Cube" });
            string ok = ToolHost.UnknownArguments(create, new Dictionary<string, object> { ["name"] = "x", ["kind"] = "cube" });
            string look = ToolHost.UnknownArguments(focus, new Dictionary<string, object> { ["position"] = new[] { 0, 5, -10 }, ["target"] = new[] { 0, 0, 0 } });
            string dry = openScene != null ? ToolHost.UnknownArguments(openScene, new Dictionary<string, object> { ["scene"] = "Match", ["dry_run"] = true }) : "n/a";
            log.Log("tool arguments: primitive → " + (bad ?? "accepted") + " | kind → " + (ok ?? "accepted") + " | target → " + (look ?? "accepted") + " | dry_run on open_scene → " + (dry ?? "accepted"));
            bool good = bad != null && bad.Contains("'primitive'") && bad.Contains("'kind'") && bad.Contains("Parameters:")
                        && ok == null
                        && look != null && look.Contains("'look_at'")
                        && (openScene == null || (dry != null && dry.Contains("dry_run")));
            if (!good) log.LogError("tool arguments: unknown parameters are not refused with the expected hints");
            return good;
        }
    }
}
