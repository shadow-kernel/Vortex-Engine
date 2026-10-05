using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Editor.Core;
using Editor.Core.Editing;
using Editor.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace VortexEditor.Claude
{
    public enum McpServerState { Stopped, Starting, Running, Failed }

    /// <summary>
    /// The Vortex MCP server (#85): Kestrel inside the editor process, Streamable HTTP on
    /// <c>http://127.0.0.1:&lt;port&gt;/mcp</c> — Claude Code (<c>claude mcp add --transport http vortex …</c>) and Claude
    /// Desktop operate the open editor through the tools in <see cref="ToolCatalog"/>.
    /// <list type="bullet">
    /// <item>Loopback only, and a request guard in front of the endpoint: a browser page (any Origin that is not
    /// localhost) and DNS rebinding (a Host that is not loopback) get 403 before the MCP handler sees them.</item>
    /// <item>Stateless transport: no sessions to lose when the editor restarts; every request stands alone.</item>
    /// <item>Off until the user enables it (<see cref="EditorPreferences.McpServerEnabled"/>); the port is fixed per
    /// user (default 7420) so a committed .mcp.json stays valid.</item>
    /// </list>
    /// </summary>
    public static class McpHost
    {
        public const int DefaultPort = 7420;
        public const string Path = "/mcp";

        private static WebApplication _app;
        private static readonly SemaphoreSlim Lifecycle = new SemaphoreSlim(1, 1);

        public static McpServerState State { get; private set; } = McpServerState.Stopped;
        /// <summary>The port the server listens on (or tried to).</summary>
        public static int Port { get; private set; } = DefaultPort;
        public static string LastError { get; private set; }
        /// <summary>Tool calls served since the server started.</summary>
        public static int Calls => _calls;
        private static int _calls;

        public static string Url => "http://127.0.0.1:" + Port + Path;
        public static bool IsRunning => State == McpServerState.Running;

        /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
        public static event Action StatusChanged;

        /// <summary>The configured port (preferences), or the default.</summary>
        public static int ConfiguredPort
        {
            get { int p = EditorPreferences.Current.McpPort; return p > 0 && p < 65536 ? p : DefaultPort; }
        }

        /// <summary>Start the server if the user enabled it (editor start-up).</summary>
        public static void AutoStart()
        {
            if (EditorPreferences.Current.McpServerEnabled) _ = StartAsync();
        }

        /// <summary>Turn the server on or off and remember the choice.</summary>
        public static async Task<bool> SetEnabledAsync(bool enabled, int port = 0)
        {
            var prefs = EditorPreferences.Current;
            prefs.McpServerEnabled = enabled;
            if (port > 0) prefs.McpPort = port;
            prefs.Save();
            if (!enabled) { await StopAsync(); return true; }
            if (IsRunning && Port == ConfiguredPort) return true;
            await StopAsync();
            return await StartAsync();
        }

        public static async Task<bool> StartAsync(int port = 0)
        {
            await Lifecycle.WaitAsync();
            try
            {
                if (_app != null) return true;
                Port = port > 0 ? port : ConfiguredPort;
                SetState(McpServerState.Starting);
                WebApplication app = null;
                try
                {
                    app = Build(Port);
                    await app.StartAsync();
                    _app = app;
                    _calls = 0;
                    LastError = null;
                    SetState(McpServerState.Running);
                    ConsoleService.Instance.LogSystem("Claude MCP server: " + Url + " (" + ToolCatalog.All.Count + " tools)");
                    return true;
                }
                catch (Exception ex)
                {
                    try { if (app != null) await app.DisposeAsync(); } catch { }
                    LastError = Explain(ex, Port);
                    SetState(McpServerState.Failed);
                    ConsoleService.Instance.LogWarning("Claude MCP server did not start: " + LastError);
                    return false;
                }
            }
            finally { Lifecycle.Release(); }
        }

        public static async Task StopAsync()
        {
            await Lifecycle.WaitAsync();
            try
            {
                var app = _app;
                _app = null;
                if (app != null)
                {
                    try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await app.StopAsync(cts.Token); } catch { }
                    try { await app.DisposeAsync(); } catch { }
                    ConsoleService.Instance.LogSystem("Claude MCP server stopped");
                }
                if (State != McpServerState.Failed || app != null) SetState(McpServerState.Stopped);
            }
            finally { Lifecycle.Release(); }
        }

        private static WebApplication Build(int port)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(McpHost).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = "Production",
                Args = Array.Empty<string>(),
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new EditorLogProvider());
            builder.Logging.SetMinimumLevel(MsLogLevel.Warning);
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, port);
                // clients that resolve "localhost" to ::1 first; skipped where IPv6 is off
                if (Socket.OSSupportsIPv6) k.Listen(IPAddress.IPv6Loopback, port);
                k.AddServerHeader = false;
            });
            builder.Services
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new Implementation { Name = "vortex", Title = "Vortex Engine Editor", Version = EngineInfo.VersionString };
                    o.ServerInstructions = Instructions;
                })
                .WithHttpTransport(o => o.Stateless = true)
                .WithListToolsHandler((ctx, ct) => ValueTask.FromResult(new ListToolsResult { Tools = ToolCatalog.All.Select(ToProtocol).ToList() }))
                .WithCallToolHandler(async (ctx, ct) =>
                {
                    Interlocked.Increment(ref _calls);
                    var args = ctx.Params?.Arguments?.ToDictionary(kv => kv.Key, kv => (object)kv.Value) ?? new Dictionary<string, object>();
                    var result = await ToolHost.CallAsync(ctx.Params?.Name, args, "mcp", ct);
                    return ToProtocol(result);
                });
            var app = builder.Build();
            app.Use(async (HttpContext http, RequestDelegate next) =>
            {
                string why = Reject(http.Request);
                if (why != null)
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsync(why);
                    return;
                }
                await next(http);
            });
            app.MapMcp(Path);
            return app;
        }

        // ------------------------------------------------------------------ request guard

        /// <summary>Why a request must not reach the tools (null = fine): a browser page from anywhere but localhost
        /// (Origin), or a Host that is not loopback — a DNS-rebound name pointing at 127.0.0.1.</summary>
        internal static string Reject(HttpRequest req)
        {
            if (!IsLoopbackName(req.Host.Host)) return "Forbidden host";
            string origin = req.Headers.Origin;
            if (!string.IsNullOrEmpty(origin))
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || !IsLoopbackName(u.Host)) return "Forbidden origin";
            }
            return null;
        }

        internal static bool IsLoopbackName(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.Trim('[', ']');
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1";
        }

        // ------------------------------------------------------------------ protocol mapping

        private static Tool ToProtocol(ToolDef t) => new Tool
        {
            Name = t.Name,
            Title = t.Title,
            Description = t.Description,
            InputSchema = t.InputSchema,
            Annotations = new ToolAnnotations
            {
                Title = t.Title,
                ReadOnlyHint = t.ReadOnly,
                DestructiveHint = t.ReadOnly ? null : t.Destructive,
                IdempotentHint = t.ReadOnly ? null : t.Idempotent,
                OpenWorldHint = t.OpenWorld,
            },
        };

        private static CallToolResult ToProtocol(ToolResult r)
        {
            var content = new List<ContentBlock>();
            foreach (var c in r.Content)
            {
                if (c.Image != null) content.Add(ImageContentBlock.FromBytes(c.Image, c.MimeType ?? "image/png"));
                else content.Add(new TextContentBlock { Text = c.Text ?? "" });
            }
            if (content.Count == 0) content.Add(new TextContentBlock { Text = "ok" });
            return new CallToolResult { Content = content, IsError = r.IsError };
        }

        private static string Explain(Exception ex, int port)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse
                    || e is System.IO.IOException && e.Message.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "port " + port + " is already in use (another Vortex editor, or another program). Pick another port in Tools ▸ Claude ▸ Connect Claude….";
            }
            return ex.Message;
        }

        private static void SetState(McpServerState s)
        {
            State = s;
            void Raise() { try { StatusChanged?.Invoke(); } catch { } }
            if (Dispatcher.UIThread.CheckAccess()) Raise(); else Dispatcher.UIThread.Post(Raise);
        }

        // ------------------------------------------------------------------ instructions for the model

        /// <summary>What a client's model reads once on connect: the conventions every tool shares.</summary>
        internal const string Instructions =
            "Vortex Engine editor (the user's open project). Conventions:\n" +
            "- World: metres, Y up, +Z forward. Primitives are 1 m (cube 1×1×1, plane 1×1 in XZ). Rotations are Euler angles in degrees (X pitch, Y yaw, Z roll).\n" +
            "- Entities are referenced by id (from any result), by path ('Level/Corridor/Lamp 2') or by a unique name.\n" +
            "- Start with scene_outline (or find_entities) to learn the scene; get_entity shows one entity's components and properties.\n" +
            "- Every mutating call is ONE undo step in the editor ('Claude: …'); the user can undo it with Ctrl/Cmd+Z, and so can you (undo).\n" +
            "- Errors list the valid values — read them and retry instead of guessing.\n" +
            "- Look at your work: capture_viewport returns an image of the editor viewport (frame the area first with focus_camera).\n" +
            "- Nothing is saved to disk until save_scene.";

        /// <summary>ASP.NET Core / MCP warnings into the editor console (everything below Warning is dropped).</summary>
        private sealed class EditorLogProvider : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new EditorLogger(categoryName);
            public void Dispose() { }

            private sealed class EditorLogger : ILogger
            {
                private readonly string _category;
                public EditorLogger(string category) { _category = category; }
                public IDisposable BeginScope<TState>(TState state) => null;
                public bool IsEnabled(MsLogLevel level) => level >= MsLogLevel.Warning;
                public void Log<TState>(MsLogLevel level, EventId id, TState state, Exception ex, Func<TState, Exception, string> formatter)
                {
                    if (!IsEnabled(level)) return;
                    string msg = "MCP: " + formatter(state, ex) + (ex != null ? " — " + ex.Message : "");
                    if (level >= MsLogLevel.Error) ConsoleService.Instance.LogError(msg); else ConsoleService.Instance.LogWarning(msg);
                }
            }
        }
    }
}
