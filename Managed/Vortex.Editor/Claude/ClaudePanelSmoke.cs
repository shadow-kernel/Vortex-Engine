using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
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
    /// Editor smoke check "claude panel": the embedded panel end to end against a scripted Messages API (no network, no
    /// key): streamed text, a create_entity call that waits for the user's approval (the smoke clicks Allow), a
    /// read-only capture_viewport call that runs without asking and shows its image, the final answer — and the
    /// entity is one "Claude: …" undo step. Captures claude_panel.png.
    /// </summary>
    internal static class ClaudePanelSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("claude panel", Run);

        private sealed class ScriptedApi : HttpMessageHandler
        {
            public readonly List<string> Bodies = new List<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri?.AbsoluteUri != ClaudeChat.ApiUrl) return new HttpResponseMessage(HttpStatusCode.NotFound);
                Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
                string sse = Bodies.Count == 1
                    ? Sse("{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":120,\"cache_creation_input_tokens\":9000}}}",
                          "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                          "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Ich baue einen Würfel \"}}",
                          "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"und schaue ihn mir an.\"}}",
                          "{\"type\":\"content_block_stop\",\"index\":0}",
                          "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_a\",\"name\":\"create_entity\",\"input\":{}}}",
                          "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"kind\\\":\\\"cube\\\",\\\"name\\\":\\\"Panel Cube\\\",\"}}",
                          "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"position\\\":[0,1,3]}\"}}",
                          "{\"type\":\"content_block_stop\",\"index\":1}",
                          "{\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_b\",\"name\":\"capture_viewport\",\"input\":{}}}",
                          "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"max_size\\\":640,\\\"focus\\\":[\\\"Panel Cube\\\"]}\"}}",
                          "{\"type\":\"content_block_stop\",\"index\":2}",
                          "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":90}}")
                    : Sse("{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":200,\"cache_read_input_tokens\":9000}}}",
                          "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                          "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Fertig: der Würfel steht bei [0, 1, 3].\"}}",
                          "{\"type\":\"content_block_stop\",\"index\":0}",
                          "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":20}}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }

            private static string Sse(params string[] events) => string.Join("", events.Select(e => "event: x\ndata: " + e + "\n\n"));
        }

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("claude panel: " + why); return false; }
            if (ProjectData.Current?.ActiveScene == null) return Fail("no scene");
            var window = EditorCommands.Window;
            var panel = ClaudePanel.Current;
            if (window == null || panel == null) return Fail("no Claude panel");
            string oldKey = Environment.GetEnvironmentVariable("VORTEX_STORE_KEY_ANTHROPIC");
            var oldHandler = StoreHttp.Handler;
            var api = new ScriptedApi();
            int undoBefore = UndoRedoManager.Instance.UndoCount;
            try
            {
                Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_ANTHROPIC", "sk-ant-smoke");
                StoreKeys.Reload();
                StoreHttp.Handler = api;
                window.ShowPanel(MainWindow.PanelClaude);
                await SmokeRegistry.Settle(300);

                var send = panel.SendAsync("Bau einen Würfel und zeig ihn mir");
                // the create_entity card asks first: click Allow
                Button allow = null;
                for (int i = 0; i < 100 && allow == null; i++)
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
                var texts = panel.Log.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text ?? "").ToList();
                if (!texts.Any(t => t == "Ich baue einen Würfel und schaue ihn mir an.")) return Fail("streamed text missing: " + string.Join(" | ", texts.Take(8)));
                if (!texts.Any(t => t.StartsWith("Fertig: der Würfel", StringComparison.Ordinal))) return Fail("final answer missing");
                if (!panel.Log.GetVisualDescendants().OfType<Image>().Any()) return Fail("the capture card shows no image");
                var marks = panel.Log.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == "✓");
                if (marks < 2) return Fail("expected two finished tool cards, got " + marks);
                if (api.Bodies.Count != 2 || !api.Bodies[1].Contains("\"tool_use_id\":\"tu_b\"") || !api.Bodies[1].Contains("\"type\":\"image\"")) return Fail("the tool results did not go back to Claude");
                SmokeRegistry.Capture(window, "claude_panel.png");
                log.Log("claude panel: OK — approval, create_entity (one undo step), capture image, streamed answer");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                StoreHttp.Handler = oldHandler;
                Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_ANTHROPIC", oldKey);
                StoreKeys.Reload();
                while (UndoRedoManager.Instance.UndoCount > undoBefore && UndoRedoManager.Instance.Undo()) { }
                EditorCommands.AfterSceneEdit();
                window.ShowPanel(MainWindow.PanelInspector);
            }
        }
    }
}
