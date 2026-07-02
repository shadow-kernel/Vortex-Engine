using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        /// <summary>All gameplay scripts under Assets/Scripts (project-relative, '/'-separated), excluding the
        /// auto-generated API stub. Single source of truth for the script picker + asset browser.</summary>
        public static List<string> EnumerateScripts()
        {
            var list = new List<string>();
            var dir = ScriptsDir;
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(root)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(f), "VortexScripting.cs", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(MakeRelative(root, f));
            }
            return list.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Convert an absolute path to a project-relative, '/'-separated path (or returns it unchanged).</summary>
        public static string MakeRelative(string root, string fullPath)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return fullPath;
            try
            {
                var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(fullPath);
                if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(rootFull.Length).Replace('\\', '/');
            }
            catch { }
            return fullPath.Replace('\\', '/');
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
            File.WriteAllText(apiPath, ApiTemplate()); // always refresh — the stub is auto-generated and must mirror the current engine API

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
            File.WriteAllText(apiPath, ApiTemplate()); // always refresh — the stub is auto-generated and must mirror the current engine API
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

// Smooth first-person player movement — 100% GAME-side (this script), driven via the Vortex API.
// Attached to the Main Camera. Mouse = look, WASD = move, Shift = sprint, Space = jump, Ctrl/C = crouch.
// Velocity eases toward the target each frame (accel/decel) for the smooth ""real multiplayer"" feel.
// The engine has NO hardcoded movement: every field below is yours to tune.
public class PlayerController : VortexBehaviour
{
    public float WalkSpeed   = 6f;     // units/sec
    public float SprintSpeed = 10f;    // while holding Shift
    public float CrouchSpeed = 3f;     // while crouching
    public float Accel       = 14f;    // how fast velocity ramps to the target (higher = snappier)
    public float MouseSens   = 0.10f;  // degrees per pixel of mouse movement
    public float JumpSpeed   = 7.5f;
    public float Gravity     = 20f;
    public float CrouchDrop  = 0.7f;   // how far the eye lowers when crouching

    private float _standEyeY;  // standing eye height, captured at spawn
    private float _vx, _vz;    // smoothed horizontal velocity
    private float _vy;         // vertical velocity
    private bool  _grounded;
    private bool  _jumpHeld;    // edge-trigger jump (holding Space must not re-fire every frame)
    private float _pitch, _yaw; // look angles (degrees), accumulated for smoothness

    public override void Start()
    {
        _standEyeY = Position.Y;
        var r = Rotation; _pitch = r.X; _yaw = r.Y;
        _vx = _vz = _vy = 0f;
        _grounded = true;
    }

    public override void Update(float dt)
    {
        if (dt <= 0f) return;

        // ---- Look: mouse (locked while playing, ESC frees) + arrow keys; clamp pitch, no roll ----
        _yaw   += Input.MouseDeltaX * MouseSens;
        _pitch += Input.MouseDeltaY * MouseSens;
        if (Input.GetKey(""Left""))  _yaw   -= 90f * dt;
        if (Input.GetKey(""Right"")) _yaw   += 90f * dt;
        if (Input.GetKey(""Up""))    _pitch -= 90f * dt;
        if (Input.GetKey(""Down""))  _pitch += 90f * dt;
        // Clamp pitch, wrap yaw, reject NaN/Inf BEFORE writing the transform — a bad Euler value would
        // reach the native quaternion math and crash the engine on matrix normalize.
        if (_pitch > 89f) _pitch = 89f; else if (_pitch < -89f) _pitch = -89f;
        _yaw %= 360f;
        if (float.IsNaN(_yaw)   || float.IsInfinity(_yaw))   _yaw   = 0f;
        if (float.IsNaN(_pitch) || float.IsInfinity(_pitch)) _pitch = 0f;
        Rotation = new Vector3(_pitch, _yaw, 0f);

        // ---- Target horizontal velocity (relative to facing); Shift = sprint, Ctrl/C = crouch ----
        bool crouch = Input.GetKey(""LeftCtrl"") || Input.GetKey(""C"");
        bool sprint = Input.GetKey(""LeftShift"");
        float speed = crouch ? CrouchSpeed : (sprint ? SprintSpeed : WalkSpeed);

        // Move on the ground plane from YAW ONLY (decoupled from camera pitch).
        double yawRad = _yaw * System.Math.PI / 180.0;
        Vector3 f = new Vector3((float)System.Math.Sin(yawRad), 0f, (float)System.Math.Cos(yawRad));
        Vector3 r = new Vector3((float)System.Math.Cos(yawRad), 0f, (float)-System.Math.Sin(yawRad));
        float dx = 0f, dz = 0f;
        if (Input.GetKey(""W"")) { dx += f.X; dz += f.Z; }
        if (Input.GetKey(""S"")) { dx -= f.X; dz -= f.Z; }
        if (Input.GetKey(""D"")) { dx += r.X; dz += r.Z; }
        if (Input.GetKey(""A"")) { dx -= r.X; dz -= r.Z; }
        float len = (float)System.Math.Sqrt(dx * dx + dz * dz);
        float tx = 0f, tz = 0f;
        if (len > 0.001f) { tx = dx / len * speed; tz = dz / len * speed; }

        // ---- Ease velocity toward the target (smooth start/stop) ----
        float kk = Accel * dt; if (kk > 1f) kk = 1f;
        _vx += (tx - _vx) * kk;
        _vz += (tz - _vz) * kk;

        // ---- Apply move + jump/gravity (kinematic; lands on the eye-height floor) ----
        if (float.IsNaN(_vx) || float.IsInfinity(_vx)) _vx = 0f;
        if (float.IsNaN(_vz) || float.IsInfinity(_vz)) _vz = 0f;
        if (float.IsNaN(_vy) || float.IsInfinity(_vy)) _vy = 0f;

        float eyeY = crouch ? _standEyeY - CrouchDrop : _standEyeY;
        Vector3 p = Position;
        p.X += _vx * dt;
        p.Z += _vz * dt;

        bool jump = Input.GetKey(""Space"");
        if (_grounded)
        {
            if (jump && !_jumpHeld) { _vy = JumpSpeed; _grounded = false; } // edge-triggered: tap to jump
            else { p.Y = eyeY; }
        }
        if (!_grounded)
        {
            _vy -= Gravity * dt;
            p.Y += _vy * dt;
            if (p.Y <= eyeY) { p.Y = eyeY; _vy = 0f; _grounded = true; }
        }
        _jumpHeld = jump;

        // Finite-guard the position before it reaches the native transform (a NaN/Inf there crashes the engine).
        if (float.IsNaN(p.X) || float.IsInfinity(p.X)) p.X = 0f;
        if (float.IsNaN(p.Y) || float.IsInfinity(p.Y)) p.Y = eyeY;
        if (float.IsNaN(p.Z) || float.IsInfinity(p.Z)) p.Z = 0f;
        Position = p;
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
        /// <summary>Set this entity's base color at runtime (e.g. flash a color when a trigger is touched).</summary>
        public void SetColor(float r, float g, float b) { }

        /// <summary>Play an animation clip on this entity's Animator (clip-table name like ""Walk"" or a
        /// .vanim path). fade &gt; 0 crossfades from the current pose (seconds).</summary>
        public bool PlayAnimation(string clip, float fade = 0f) => false;
        /// <summary>Freeze this entity's animation on the current pose.</summary>
        public void StopAnimation() { }
        /// <summary>Playback speed multiplier (1 = authored speed).</summary>
        public void SetAnimationSpeed(float speed) { }
        /// <summary>Is an animation playing? Pass a clip name to ask about that clip specifically.</summary>
        public bool IsAnimationPlaying(string clip = null) => false;
        /// <summary>Current playback time (seconds) of this entity's animation.</summary>
        public float AnimationTime => 0f;

        public virtual void Start() { }
        public virtual void Update(float dt) { }
        public virtual void OnDestroy() { }

        /// <summary>Called once when another character enters this entity's TRIGGER collider.</summary>
        public virtual void OnTriggerEnter(TriggerHit other) { }
        /// <summary>Called every tick while another character stays in this entity's TRIGGER collider.</summary>
        public virtual void OnTriggerStay(TriggerHit other) { }
        /// <summary>Called once when another character leaves this entity's TRIGGER collider.</summary>
        public virtual void OnTriggerExit(TriggerHit other) { }
        /// <summary>Called once when a character touches this entity's SOLID collider.</summary>
        public virtual void OnCollisionEnter(TriggerHit other) { }
        /// <summary>Called when the playing clip crosses one of its EVENT markers (footsteps, hits, ...).</summary>
        public virtual void OnAnimationEvent(string name) { }
    }

    /// <summary>Skeletal animation on OTHER entities (your own entity has PlayAnimation() directly).</summary>
    public static class Animation
    {
        public static bool Play(long entityId, string clip, float fade = 0f) => false;
        public static void Stop(long entityId) { }
        public static void SetSpeed(long entityId, float speed) { }
        public static bool IsPlaying(long entityId, string clip = null) => false;
        public static float Time(long entityId) => 0f;
    }

    /// <summary>A collision/trigger contact — identifies the OTHER entity (who touched your trigger / what you hit).</summary>
    public struct TriggerHit { public long EntityId; public string Name; public string Tag; }

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

    /// <summary>RGBA color, channels 0..1.</summary>
    public struct Color
    {
        public float R, G, B, A;
        public Color(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
        public static Color Rgb(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f, 1f);
        public static Color Rgba(int r, int g, int b, int a) => new Color(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    /// <summary>Scene control — the GAME decides when/which scene to load (e.g. lobby PLAY -> ""Match"").</summary>
    public static class Scene { public static void Load(string name) { } }

    /// <summary>Mouse mode. Locked = captured + hidden for mouse-look; unlocked = free cursor for UI.</summary>
    public static class Cursor { public static bool Locked { get; set; } }

    /// <summary>Application-level control.</summary>
    public static class Application { public static void Quit() { } }

    /// <summary>Player camera control.</summary>
    public static class Camera { public static void SetFieldOfView(float fovDegrees) { } }

    /// <summary>Generic engine settings (the GAME's options menu applies these).</summary>
    public static class Settings
    {
        public static void SetVSync(bool on) { }
        public static void ToggleFullscreen() { }
        public static bool IsFullscreen => false;
    }

    /// <summary>Lighting / atmosphere — flicker, lightning, mood. A static scene keeps what you last set.</summary>
    public static class Lighting
    {
        public static void SetAmbient(float strength) { }
        public static void SetDirectional(float dx, float dy, float dz, float r, float g, float b, float intensity) { }
        public static void ClearLights() { }
    }

    /// <summary>Script-built world geometry — assemble a level/backdrop from meshes (render-only).</summary>
    public static class World
    {
        public static void Add(string meshPath, float x, float y, float z, float yawDegrees, float scale) { }
        public static void Clear() { }
    }

    /// <summary>Immediate-mode 2D overlay (drawn each frame over the 3D). align: 0/1/2 = left/center/right.</summary>
    public static class UI
    {
        public static float Width => 0f;
        public static float Height => 0f;
        public static float MouseX => 0f;
        public static float MouseY => 0f;
        public static bool MouseDown => false;
        public static void Rect(float x, float y, float w, float h, Color c, float radius) { }
        public static void Rect(float x, float y, float w, float h, Color c) { }
        public static void Text(string text, float x, float y, float w, float h, float size, Color c, int align, int weight) { }
        public static void Text(string text, float x, float y, float w, float h, float size, Color c) { }
        public static void Line(float x1, float y1, float x2, float y2, Color c, float thick) { }
        public static void Image(string path, float x, float y, float w, float h, Color tint) { }
        public static void Image(string path, float x, float y, float w, float h) { }
        public static bool Hover(float x, float y, float w, float h) => false;
        public static bool Button(float x, float y, float w, float h, string label, Color bg, Color fg, float size, float radius) => false;
    }

    /// <summary>A loaded retained-UI screen (.vui). Drive named slots + read events by stable id.</summary>
    public sealed class VuiHandle
    {
        public bool IsValid => false;
        public void Show() { }
        public void Hide() { }
        public void SetValue(string id, float v) { }
        public void SetText(string id, string t) { }
        public void SetVisible(string id, bool v) { }
        public void SetColor(string id, Color c) { }
        public void SetImage(string id, string asset) { }
        public void SetList(string id, System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyDictionary<string, string>> rows) { }
        public bool WasClicked(string id) => false;
        public float GetSlider(string id) => 0f;
        public bool GetToggle(string id) => false;
        public string GetText(string id) => """";
        public int GetStep(string id) => 0;
        public int GetCapturedKey(string id) => 0;
    }

    /// <summary>Retained-mode 2D UI: load .vui screens, stack them, drive them by id.</summary>
    public static class Gui
    {
        public static VuiHandle Load(string name) => new VuiHandle();
        public static VuiHandle Push(string name) => new VuiHandle();
        public static void Pop() { }
        public static bool HasScreens => false;
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
