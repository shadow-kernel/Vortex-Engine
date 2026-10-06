using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Editor.Core.Assets.Store;
using Editor.Core.Claude;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "claude panel": the Claude sidebar end to end against a scripted Messages API (no network, no
    /// real key) — the sidebar opens as its own column; Claude's thinking shows as a collapsible summary; a
    /// create_entity call waits for the user's approval (the smoke clicks Allow); a read-only capture_viewport call runs
    /// without asking and shows its image; the final answer renders as Markdown; the entity is one "Claude: …" undo
    /// step; the next request sends the thinking back with its signature. Then Ask mode: a delete is refused and the
    /// cube stays. Last, without credentials the sidebar shows the sign-in view. Captures claude_panel_approval.png,
    /// claude_panel.png and claude_signin.png.
    /// </summary>
    internal static class ClaudePanelSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("claude panel", Run);

        private const string Api = "https://api.anthropic.com/v1/messages";

        private sealed class ScriptedApi : HttpMessageHandler
        {
            public readonly List<string> Bodies = new List<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri == null || !request.RequestUri.AbsoluteUri.StartsWith(Api, StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
                int n;
                lock (Bodies) { Bodies.Add(body); n = Bodies.Count; }
                string[] events = n switch
                {
                    1 => Concat(Start(120, write: 9000),
                                new[]
                                {
                                    "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}",
                                    "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"**Planning the cube**\\n\\nA cube at [0, 1, 3], then a look through the viewport.\"}}",
                                    "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig-smoke\"}}",
                                    "{\"type\":\"content_block_stop\",\"index\":0}",
                                },
                                Text(1, "Ich baue einen Würfel und schaue ihn mir an."),
                                Tool(2, "tu_a", "create_entity", "{\"kind\":\"cube\",\"name\":\"Panel Cube\",\"position\":[0,1,3]}"),
                                Tool(3, "tu_b", "capture_viewport", "{\"max_size\":640,\"focus\":[\"Panel Cube\"]}"),
                                Stop("tool_use", 90)),
                    2 => Concat(Start(200, read: 9000), Text(0, "Fertig: der **Würfel** steht bei `[0, 1, 3]`.\n\n- Name: Panel Cube\n- Rückgängig: ⌘Z"), Stop("end_turn", 20)),
                    3 => Concat(Start(220, read: 9100), Tool(0, "tu_c", "delete_entities", "{\"entities\":[\"Panel Cube\"]}"), Stop("tool_use", 15)),
                    _ => Concat(Start(240, read: 9200), Text(0, "Im Ask-Modus ändere ich nichts — wechsle zu Agent, dann lösche ich ihn."), Stop("end_turn", 12)),
                };
                var sse = string.Concat(events.Select(e => "event: " + JsonDocument.Parse(e).RootElement.GetProperty("type").GetString() + "\ndata: " + e + "\n\n"));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }

            private static string[] Concat(params string[][] parts) => parts.SelectMany(p => p).ToArray();

            private static string[] Start(int input, int write = 0, int read = 0) => new[]
            {
                "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_smoke\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"content\":[]," +
                "\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":1,\"cache_creation_input_tokens\":" + write +
                ",\"cache_read_input_tokens\":" + read + "}}}",
            };

            private static string[] Text(int i, string text) => new[]
            {
                "{\"type\":\"content_block_start\",\"index\":" + i + ",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                "{\"type\":\"content_block_delta\",\"index\":" + i + ",\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(text) + "}}",
                "{\"type\":\"content_block_stop\",\"index\":" + i + "}",
            };

            private static string[] Tool(int i, string id, string name, string input) => new[]
            {
                "{\"type\":\"content_block_start\",\"index\":" + i + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"input\":{}}}",
                "{\"type\":\"content_block_delta\",\"index\":" + i + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(input) + "}}",
                "{\"type\":\"content_block_stop\",\"index\":" + i + "}",
            };

            private static string[] Stop(string reason, int output) => new[]
            {
                "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + reason + "\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":" + output + "}}",
                "{\"type\":\"message_stop\"}",
            };
        }

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("claude panel: " + why); return false; }
            if (ProjectData.Current?.ActiveScene == null) return Fail("no scene");
            var window = EditorCommands.Window;
            var panel = ClaudePanel.Current;
            if (window == null || panel == null) return Fail("no Claude panel");
            var env = new Dictionary<string, string>();
            void SetEnv(string name, string value)
            {
                if (!env.ContainsKey(name)) env[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
            var oldHandler = StoreHttp.Handler;
            var settings = ClaudePanelSettings.Current;
            string oldMode = settings.Mode, oldModel = settings.Model;
            bool wasOpen = window.IsPanelVisible(MainWindow.PanelClaude);
            var api = new ScriptedApi();
            int undoBefore = UndoRedoManager.Instance.UndoCount;
            string emptyConfig = Path.Combine(Path.GetTempPath(), "vortex-smoke-anthropic-" + Environment.ProcessId);
            try
            {
                SetEnv("VORTEX_STORE_KEY_ANTHROPIC", "sk-ant-smoke");
                SetEnv("ANTHROPIC_CONFIG_DIR", emptyConfig);   // never the user's own Anthropic profile
                StoreKeys.Reload();
                StoreHttp.Handler = api;
                settings.Model = "claude-opus-5-5";
                panel.SetMode("Agent");
                SelectionService.Instance.SelectedEntity = null;
                window.ShowPanel(MainWindow.PanelClaude);
                await SmokeRegistry.Settle(400);
                if (!window.ClaudeColumn.IsVisible || window.ClaudeColumn.Bounds.Width < 290) return Fail("the sidebar did not open (" + window.ClaudeColumn.Bounds.Width + " px)");
                if (panel.ShowsSignIn) return Fail("the sign-in view shows although a key is set");
                // Inspector and Environment hidden: the sidebar takes the right column's place (no empty gap beside it)
                bool insp = window.IsPanelVisible(MainWindow.PanelInspector), envp = window.IsPanelVisible(MainWindow.PanelEnvironment);
                if (insp) window.TogglePanel(MainWindow.PanelInspector, forceShow: false);
                if (window.IsPanelVisible(MainWindow.PanelInspector)) window.TogglePanel(MainWindow.PanelInspector);
                if (envp) window.TogglePanel(MainWindow.PanelEnvironment);
                if (window.IsPanelVisible(MainWindow.PanelEnvironment)) window.TogglePanel(MainWindow.PanelEnvironment);
                await SmokeRegistry.Settle(300);
                bool movedIn = Grid.GetColumn(window.ClaudeColumn) == 4 && window.ClaudeColumn.Bounds.Width >= 290 && !window.RightColumn.IsVisible;
                SmokeRegistry.Capture(window, "claude_panel_alone.png");
                if (insp) window.ShowPanel(MainWindow.PanelInspector);
                if (envp) window.ShowPanel(MainWindow.PanelEnvironment);
                window.ShowPanel(MainWindow.PanelInspector);
                await SmokeRegistry.Settle(300);
                if (!movedIn) return Fail("with the Inspector hidden the sidebar did not take its place");
                if (Grid.GetColumn(window.ClaudeColumn) != 6 || window.ClaudeColumn.Bounds.Width < 290) return Fail("the sidebar did not go back beside the Inspector");

                var send = panel.SendAsync("Bau einen Würfel und zeig ihn mir");
                // the create_entity card asks first: click Allow
                Button allow = null;
                for (int i = 0; i < 200 && allow == null; i++)
                {
                    await Task.Delay(50);
                    allow = panel.Log.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "Allow");
                }
                if (allow == null) return Fail("no approval card for create_entity");
                SmokeRegistry.Capture(window, "claude_panel_approval.png");
                allow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var done = await Task.WhenAny(send, Task.Delay(20000));
                if (done != send) return Fail("the conversation did not finish");
                await SmokeRegistry.Settle(400);

                var cube = SceneModel.All(ProjectData.Current.ActiveScene).FirstOrDefault(e => e.Name == "Panel Cube");
                if (cube == null) return Fail("Claude's create_entity did not create the cube");
                if (UndoRedoManager.Instance.UndoName != "Claude: create Panel Cube") return Fail("undo step is '" + UndoRedoManager.Instance.UndoName + "'");
                var answers = panel.Log.Children.OfType<MarkdownBlock>().Select(m => m.Text).ToList();
                if (!answers.Contains("Ich baue einen Würfel und schaue ihn mir an.")) return Fail("streamed text missing: " + string.Join(" | ", answers));
                if (!answers.Any(a => a.StartsWith("Fertig: der **Würfel**", StringComparison.Ordinal))) return Fail("final answer missing");
                var thinking = panel.Log.Children.OfType<ClaudePanel.ThinkingBlock>().FirstOrDefault();
                if (thinking == null || !thinking.Text.Contains("Planning the cube")) return Fail("no thinking summary");
                if (!panel.Log.GetVisualDescendants().OfType<Image>().Any()) return Fail("the capture card shows no image");
                var marks = panel.Log.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == "✓");
                if (marks < 2) return Fail("expected two finished tool cards, got " + marks);
                if (api.Bodies.Count != 2) return Fail(api.Bodies.Count + " requests instead of 2");
                if (!api.Bodies[1].Contains("\"signature\":\"sig-smoke\"")) return Fail("the thinking did not go back with its signature");
                if (!api.Bodies[1].Contains("\"tool_use_id\":\"tu_b\"") || !api.Bodies[1].Contains("\"type\":\"image\"")) return Fail("the tool results did not go back to Claude");
                if (!api.Bodies[0].Contains("\"effort\":\"" + (settings.Effort.TryGetValue("claude-opus-5-5", out var eff) ? eff : "medium") + "\"")) return Fail("the effort was not sent");
                SmokeRegistry.Capture(window, "claude_panel.png");

                // Ask mode: the delete is refused, the cube stays
                panel.SetMode("Ask");
                var ask = panel.SendAsync("Lösch den Würfel");
                if (await Task.WhenAny(ask, Task.Delay(20000)) != ask) return Fail("the Ask turn did not finish");
                await SmokeRegistry.Settle(300);
                if (SceneModel.All(ProjectData.Current.ActiveScene).All(e => e.Name != "Panel Cube")) return Fail("Ask mode deleted the cube");
                if (!api.Bodies[3].Contains("Ask mode: delete_entities would change the project")) return Fail("Claude was not told why the delete was refused");
                if (!api.Bodies[2].Contains("[Ask mode]")) return Fail("the Ask note is missing");

                // no credentials: the sign-in view
                SetEnv("VORTEX_STORE_KEY_ANTHROPIC", null);
                SetEnv("ANTHROPIC_API_KEY", null);
                SetEnv("ANTHROPIC_AUTH_TOKEN", null);
                SetEnv("ANTHROPIC_PROFILE", null);
                bool hadStoredKey = StoreKeys.Has(ClaudeAccount.KeyName);
                StoreKeys.Reload();
                if (!StoreKeys.Has(ClaudeAccount.KeyName))
                {
                    panel.RefreshAccount();
                    await SmokeRegistry.Settle(300);
                    if (!panel.ShowsSignIn) return Fail("no sign-in view without credentials");
                    SmokeRegistry.Capture(window, "claude_signin.png");
                }
                else log.Log("claude panel: a stored Anthropic key exists — sign-in view not checked");

                log.Log("claude panel: OK — sidebar, thinking summary, approval, create_entity (one undo step), capture image, Markdown answer, " +
                        "thinking echoed with its signature, Ask mode refuses changes, sign-in view");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                StoreHttp.Handler = oldHandler;
                foreach (var kv in env) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
                StoreKeys.Reload();
                settings.Model = oldModel;
                settings.Mode = oldMode;
                settings.Save();
                while (UndoRedoManager.Instance.UndoCount > undoBefore && UndoRedoManager.Instance.Undo()) { }
                EditorCommands.AfterSceneEdit();
                panel.RefreshAccount();
                if (!wasOpen && window.IsPanelVisible(MainWindow.PanelClaude)) window.TogglePanel(MainWindow.PanelClaude);
                try { Directory.Delete(emptyConfig, true); } catch { }
            }
        }
    }
}
