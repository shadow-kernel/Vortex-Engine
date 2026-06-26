using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Editor.Core.Data;

namespace Editor.Core.Services
{
    /// <summary>
    /// Manages C# gameplay scripts and real Visual Studio integration: creates scripts
    /// from a template, generates a proper VS solution + SDK project (so scripts get full
    /// IntelliSense), and opens them in Visual Studio.
    /// </summary>
    public static class ScriptingService
    {
        public static string ProjectRoot => ProjectData.Current?.Path;

        public static string ScriptsDir
        {
            get
            {
                var root = ProjectRoot;
                if (string.IsNullOrEmpty(root)) return null;
                var dir = Path.Combine(root, "Assets", "Scripts");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string ScriptsProjectName =>
            Sanitize(ProjectData.Current?.Name ?? "Game") + "Scripts";

        /// <summary>Creates Assets/Scripts/&lt;name&gt;.cs from the behaviour template (unique name) and returns its path.</summary>
        public static string CreateScript(string name = "NewBehaviour")
        {
            var dir = ScriptsDir ?? throw new InvalidOperationException("No project is open.");
            name = Sanitize(name);
            var path = Path.Combine(dir, name + ".cs");
            int n = 1;
            while (File.Exists(path)) path = Path.Combine(dir, name + (n++) + ".cs");
            File.WriteAllText(path, ScriptTemplate(Path.GetFileNameWithoutExtension(path)));
            EnsureScriptsProject();
            return path;
        }

        /// <summary>
        /// Writes the starter PlayerController movement script (game-side, fully editable) into
        /// <paramref name="projectRoot"/>'s Assets/Scripts/Player, plus the API stub. Returns the
        /// project-relative script path. Safe to call repeatedly (won't overwrite an edited script).
        /// Takes an explicit root so it works during project creation, before ProjectData.Current is set.
        /// </summary>
        public static string EnsurePlayerController(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return null;
            var scriptsDir = Path.Combine(projectRoot, "Assets", "Scripts");
            var playerDir = Path.Combine(scriptsDir, "Player");
            Directory.CreateDirectory(playerDir);

            var apiPath = Path.Combine(scriptsDir, "VortexScripting.cs");
            if (!File.Exists(apiPath)) File.WriteAllText(apiPath, ApiTemplate());

            var path = Path.Combine(playerDir, "PlayerController.cs");
            if (!File.Exists(path)) File.WriteAllText(path, PlayerControllerTemplate());
            return "Assets/Scripts/Player/PlayerController.cs";
        }

        /// <summary>Ensures the VS solution, SDK project and scripting-API source exist; returns the .sln path.</summary>
        public static string EnsureScriptsProject()
        {
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(root)) return null;
            var name = ScriptsProjectName;
            var csproj = Path.Combine(root, name + ".csproj");
            var sln = Path.Combine(root, name + ".sln");

            var scriptsDir = ScriptsDir;
            var apiPath = Path.Combine(scriptsDir, "VortexScripting.cs");
            if (!File.Exists(apiPath)) File.WriteAllText(apiPath, ApiTemplate());
            if (!File.Exists(csproj)) File.WriteAllText(csproj, CsprojTemplate(name));
            if (!File.Exists(sln)) File.WriteAllText(sln, SlnTemplate(name));
            return sln;
        }

        /// <summary>Opens the scripts solution (and optionally a specific file) in Visual Studio.</summary>
        public static void OpenInVisualStudio(string filePath = null)
        {
            var sln = EnsureScriptsProject();
            var devenv = FindDevenv();
            try
            {
                if (devenv != null && sln != null)
                {
                    Process.Start(new ProcessStartInfo(devenv, "\"" + sln + "\"") { UseShellExecute = false });
                    if (!string.IsNullOrEmpty(filePath) && filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        Process.Start(new ProcessStartInfo(devenv, "/Edit \"" + filePath + "\"") { UseShellExecute = false });
                }
                else
                {
                    // Fallback to the OS default handler for the file/solution.
                    Process.Start(new ProcessStartInfo(filePath ?? sln) { UseShellExecute = true });
                }
            }
            catch (Exception ex) { Debug.WriteLine("[Scripting] OpenInVisualStudio failed: " + ex); }
        }

        private static string FindDevenv()
        {
            try
            {
                var vswhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
                if (File.Exists(vswhere))
                {
                    var psi = new ProcessStartInfo(vswhere, "-latest -property productPath")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        var output = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(4000);
                        if (!string.IsNullOrEmpty(output) && File.Exists(output)) return output;
                    }
                }
            }
            catch { /* fall through to known path */ }

            foreach (var candidate in new[]
            {
                @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe",
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "NewBehaviour";
            var sb = new StringBuilder();
            foreach (var c in s.Trim())
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            var name = sb.Length == 0 ? "NewBehaviour" : sb.ToString();
            if (char.IsDigit(name[0])) name = "_" + name;
            return name;
        }

        // ---- templates ----

        private static string ScriptTemplate(string className) =>
@"using Vortex;

// A gameplay behaviour. Attach the compiled class to an entity via a Script component.
public class " + className + @" : VortexBehaviour
{
    // Called once when play begins.
    public override void Start()
    {
    }

    // Called every simulation tick. dt is the time in seconds since the last tick.
    public override void Update(float dt)
    {
    }
}
";

        // NOTE: this stub is for editor/VS IntelliSense only (ScriptRuntime excludes VortexScripting.cs
        // and links the REAL API from the editor assembly at runtime). It MUST mirror the real public
        // surface in Editor/Scripting/VortexScriptApi.cs, or scripts will mislead in VS / fail at runtime.
        // The starter player movement — this is GAME code (a script), not engine code, so every line
        // here is yours to change. It runs on the entity that carries the Script component (the Main
        // Camera in the default scene): WASD walks relative to where you look, the arrow keys turn/look.
        private static string PlayerControllerTemplate() =>
@"using Vortex;

// Player movement lives HERE, in the game — not hardcoded in the engine — so you can fully tune it.
// Attached to the Main Camera via a Script component. Tweak the speeds, swap the keys, or rewrite it.
public class PlayerController : VortexBehaviour
{
    public float MoveSpeed = 6f;     // units per second
    public float MouseSens = 0.12f;  // degrees per pixel of mouse movement
    public float TurnSpeed = 90f;    // degrees per second (arrow-key fallback)

    public override void Update(float dt)
    {
        // --- Look: mouse (cursor is locked while playing; ESC frees it) + arrow keys as fallback ---
        float yaw   = Input.MouseDeltaX * MouseSens;
        float pitch = Input.MouseDeltaY * MouseSens;
        if (Input.GetKey(""Left""))  yaw   -= TurnSpeed * dt;
        if (Input.GetKey(""Right"")) yaw   += TurnSpeed * dt;
        if (Input.GetKey(""Up""))    pitch -= TurnSpeed * dt;
        if (Input.GetKey(""Down""))  pitch += TurnSpeed * dt;
        if (yaw != 0f || pitch != 0f) Rotate(pitch, yaw, 0f);

        // --- Move (WASD), relative to where you're facing ---
        Vector3 f = Forward, r = Right;
        float mx = 0f, mz = 0f;
        if (Input.GetKey(""W"")) { mx += f.X; mz += f.Z; }
        if (Input.GetKey(""S"")) { mx -= f.X; mz -= f.Z; }
        if (Input.GetKey(""D"")) { mx += r.X; mz += r.Z; }
        if (Input.GetKey(""A"")) { mx -= r.X; mz -= r.Z; }

        float len = (float)System.Math.Sqrt(mx * mx + mz * mz);
        if (len > 0.001f)
        {
            mx /= len; mz /= len;
            Translate(mx * MoveSpeed * dt, 0f, mz * MoveSpeed * dt);
        }
    }
}
";

        private static string ApiTemplate() =>
@"// Auto-generated by Vortex Engine — the scripting API your behaviours build on (IntelliSense stub;
// the engine provides the real implementation at runtime). Edit your own scripts, not this file.
namespace Vortex
{
    public struct Vector3
    {
        public float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vector3 Zero => new Vector3(0f, 0f, 0f);
        public static Vector3 One => new Vector3(1f, 1f, 1f);
        public static Vector3 Up => new Vector3(0f, 1f, 0f);
        public static Vector3 Forward => new Vector3(0f, 0f, 1f);
    }

    /// <summary>Base class for all gameplay behaviours (like MonoBehaviour).</summary>
    public abstract class VortexBehaviour
    {
        /// <summary>Engine id of the entity this behaviour is attached to.</summary>
        public long EntityId { get; internal set; }

        /// <summary>World position of this entity (read/write).</summary>
        public Vector3 Position { get; set; }
        /// <summary>Euler rotation in degrees (X = pitch, Y = yaw, Z = roll).</summary>
        public Vector3 Rotation { get; set; }
        /// <summary>Unit forward vector in world space, from this entity's yaw + pitch.</summary>
        public Vector3 Forward => Vector3.Forward;
        /// <summary>Unit right vector (horizontal) in world space, from this entity's yaw.</summary>
        public Vector3 Right => new Vector3(1f, 0f, 0f);

        /// <summary>Move this entity by a delta.</summary>
        public void Translate(float dx, float dy, float dz) { }
        /// <summary>Rotate this entity by a delta (degrees).</summary>
        public void Rotate(float dPitch, float dYaw, float dRoll) { }

        public virtual void Start() { }
        public virtual void Update(float dt) { }
        public virtual void OnDestroy() { }
    }

    /// <summary>Keyboard + mouse input. Key names match WPF keys, e.g. ""W"", ""Space"", ""Left"".</summary>
    public static class Input
    {
        public static bool GetKey(string key) => false;
        /// <summary>Mouse movement since the last tick, in pixels (non-zero only while the game has the cursor).</summary>
        public static float MouseDeltaX { get; }
        public static float MouseDeltaY { get; }
    }

    /// <summary>Frame timing.</summary>
    public static class Time
    {
        public static float DeltaTime => 0f;
    }
}
";

        private static string CsprojTemplate(string name) =>
@"<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <AssemblyName>" + name + @"</AssemblyName>
    <RootNamespace>Game</RootNamespace>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <!-- All gameplay scripts in this project. -->
    <Compile Include=""Assets\Scripts\**\*.cs"" />
  </ItemGroup>

</Project>
";

        private static string SlnTemplate(string name)
        {
            // SDK-style C# project type GUID + a fresh project GUID.
            const string typeGuid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";
            var projGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var sb = new StringBuilder();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("# Visual Studio Version 17");
            sb.AppendLine("Project(\"" + typeGuid + "\") = \"" + name + "\", \"" + name + ".csproj\", \"" + projGuid + "\"");
            sb.AppendLine("EndProject");
            sb.AppendLine("Global");
            sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
            sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
            sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
            sb.AppendLine("\t\t" + projGuid + ".Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine("\t\t" + projGuid + ".Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine("\t\t" + projGuid + ".Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine("\t\t" + projGuid + ".Release|Any CPU.Build.0 = Release|Any CPU");
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("EndGlobal");
            return sb.ToString();
        }
    }
}
