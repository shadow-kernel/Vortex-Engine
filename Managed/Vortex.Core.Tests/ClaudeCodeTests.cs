using System.Collections.Generic;
using System.Text;
using Editor.Core.Claude;

namespace VortexTests
{
    /// <summary>The Claude Code engine behind the sidebar (the user's own plan, run headless): parsing the
    /// <c>--output-format stream-json</c> events into the panel's streaming events, tokens and cost.</summary>
    public static class ClaudeCodeTests
    {
        [Test]
        public static void StreamsTextAndCountsUsage(TestContext t)
        {
            var s = new ClaudeCodeSession();
            bool textStarted = false;
            var text = new StringBuilder();
            s.TextStarted += () => textStarted = true;
            s.TextDelta += d => text.Append(d);

            s.FeedLine(@"{""type"":""stream_event"",""event"":{""type"":""content_block_start"",""index"":0,""content_block"":{""type"":""text"",""text"":""""}}}");
            s.FeedLine(@"{""type"":""stream_event"",""event"":{""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""text_delta"",""text"":""pong""}}}");
            string result = s.FeedLine(@"{""type"":""result"",""subtype"":""success"",""is_error"":false,""result"":""pong"",""total_cost_usd"":0.0132,""usage"":{""input_tokens"":2,""cache_creation_input_tokens"":3140,""cache_read_input_tokens"":3144,""output_tokens"":4}}");

            t.True(textStarted, "a text block started");
            t.Equal("pong", text.ToString(), "the text streamed as deltas");
            t.Equal("pong", result, "the result line carries the final text");
            t.Equal(2L, s.InputTokens, "input tokens");
            t.Equal(4L, s.OutputTokens, "output tokens");
            t.Equal(3144L, s.CacheReadTokens, "cache-read tokens");
            t.Equal(3140L, s.CacheWriteTokens, "cache-write tokens");
            t.Equal(6286L, s.ContextTokens, "context = input + cache read + cache write");
            t.True(s.Cost > 0.013 && s.Cost < 0.014, "cost accumulated from total_cost_usd");
        }

        [Test]
        public static void StreamsThinking(TestContext t)
        {
            var s = new ClaudeCodeSession();
            bool started = false;
            var think = new StringBuilder();
            s.ThinkingStarted += () => started = true;
            s.ThinkingDelta += d => think.Append(d);

            s.FeedLine(@"{""type"":""stream_event"",""event"":{""type"":""content_block_start"",""index"":0,""content_block"":{""type"":""thinking"",""thinking"":""""}}}");
            s.FeedLine(@"{""type"":""stream_event"",""event"":{""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""thinking_delta"",""thinking"":""Let me look""}}}");

            t.True(started, "a thinking block started");
            t.Equal("Let me look", think.ToString(), "thinking streamed");
        }

        [Test]
        public static void RunsToolThroughTheEditorAndStripsThePrefix(TestContext t)
        {
            var s = new ClaudeCodeSession();
            ChatToolCall started = null, finished = null;
            s.ToolStarted += c => started = c;
            s.ToolFinished += c => finished = c;

            s.FeedLine(@"{""type"":""assistant"",""message"":{""role"":""assistant"",""content"":[{""type"":""tool_use"",""id"":""tool_1"",""name"":""mcp__vortex__create_entity"",""input"":{""name"":""Lamp"",""kind"":""cube""}}]}}");
            t.NotNull(started, "tool_use raised ToolStarted");
            t.Equal("create_entity", started.Name, "the mcp__vortex__ prefix is stripped for the card");
            t.Equal("Lamp", started.Input?["name"]?.ToString(), "the tool input is parsed");

            s.FeedLine(@"{""type"":""user"",""message"":{""role"":""user"",""content"":[{""type"":""tool_result"",""tool_use_id"":""tool_1"",""is_error"":false,""content"":""created id 7""}]}}");
            t.NotNull(finished, "the tool_result raised ToolFinished");
            t.Equal("tool_1", finished.Id, "matched by tool_use_id");
            t.Equal("created id 7", finished.Result?.Text, "the result text");
            t.False(finished.Result?.IsError ?? true, "not an error");
        }

        [Test]
        public static void AskModeExplainsADeniedChange(TestContext t)
        {
            var s = new ClaudeCodeSession { Mode = ClaudeMode.Ask };
            string why = null;
            s.Refused += w => why = w;

            s.FeedLine(@"{""type"":""result"",""subtype"":""success"",""is_error"":false,""result"":""I would delete it, but Ask mode changes nothing."",""total_cost_usd"":0.001,""usage"":{""input_tokens"":1,""output_tokens"":1},""permission_denials"":[{""tool_name"":""mcp__vortex__delete_entity""}]}");

            t.NotNull(why, "a denied change is surfaced");
            t.True(why.Contains("delete_entity"), "it names the tool");
            t.True(why.Contains("Agent"), "it points at Agent mode");
        }

        private static ChatTool[] SampleTools() => new[]
        {
            new ChatTool { Name = "scene_outline", ReadOnly = true },
            new ChatTool { Name = "capture_viewport", ReadOnly = true },
            new ChatTool { Name = "create_entity", ReadOnly = false },
            new ChatTool { Name = "delete_entity", ReadOnly = false },
        };

        private static string ArgAfter(System.Collections.Generic.List<string> a, string flag)
        {
            int i = a.IndexOf(flag);
            return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
        }

        [Test]
        public static void AgentModeAsksForChangesAndPreAllowsReads(TestContext t)
        {
            var s = new ClaudeCodeSession
            {
                Mode = ClaudeMode.Agent,
                Model = ClaudeModels.Find("claude-opus-5-5"),
                Effort = "high",
                ContextSize = 1_000_000,
                Tools = SampleTools(),
            };
            var a = s.BuildArgsForTest("build a corridor");

            t.Equal("mcp__vortex__approve", ArgAfter(a, "--permission-prompt-tool"), "Agent routes changes to the approval card");
            t.False(a.Contains("--disallowedTools"), "Agent does not hard-deny changes");
            string allowed = ArgAfter(a, "--allowedTools");
            t.True(allowed != null && allowed.Contains("mcp__vortex__scene_outline") && allowed.Contains("mcp__vortex__capture_viewport"), "read-only tools are pre-allowed (no prompt)");
            t.False(allowed.Contains("create_entity"), "a change is not pre-allowed");
            t.Equal("high", ArgAfter(a, "--effort"), "effort is passed");
            t.Equal("1000000", ArgAfter(a, "--autocompact"), "context size maps to autocompact");
            t.Equal("claude-opus-5-5", ArgAfter(a, "--model"), "model is passed");
        }

        [Test]
        public static void AskModeHardDeniesChanges(TestContext t)
        {
            var s = new ClaudeCodeSession { Mode = ClaudeMode.Ask, ContextSize = 200_000, Tools = SampleTools() };
            var a = s.BuildArgsForTest("what is in the scene?");

            string denied = ArgAfter(a, "--disallowedTools");
            t.True(denied != null && denied.Contains("mcp__vortex__create_entity") && denied.Contains("mcp__vortex__delete_entity"), "Ask denies every change");
            string allowed = ArgAfter(a, "--allowedTools");
            t.True(allowed != null && allowed.Contains("mcp__vortex__scene_outline"), "Ask still allows reads");
            t.Equal("200000", ArgAfter(a, "--autocompact"), "the smaller context maps through");
        }

        [Test]
        public static void AlwaysAllowedToolsSkipThePrompt(TestContext t)
        {
            var s = new ClaudeCodeSession
            {
                Mode = ClaudeMode.Agent,
                Tools = SampleTools(),
                AlwaysAllowedTools = new[] { "create_entity" },
            };
            string allowed = ArgAfter(s.BuildArgsForTest("go"), "--allowedTools");
            t.True(allowed.Contains("mcp__vortex__create_entity"), "an always-allowed change is pre-allowed too");
        }

        [Test]
        public static void HaikuSendsNoEffort(TestContext t)
        {
            var s = new ClaudeCodeSession { Model = ClaudeModels.Find("claude-haiku-4-5"), Tools = SampleTools() };
            var a = s.BuildArgsForTest("hi");
            t.False(a.Contains("--effort"), "a model without effort levels sends none");
        }

        [Test]
        public static void ReportsAFailedTurn(TestContext t)
        {
            var s = new ClaudeCodeSession();
            string why = null;
            s.Refused += w => why = w;

            s.FeedLine(@"{""type"":""result"",""subtype"":""error_during_execution"",""is_error"":true,""result"":"""",""api_error_status"":""overloaded"",""usage"":{""input_tokens"":1,""output_tokens"":0}}");

            t.NotNull(why, "a failed turn is surfaced");
            t.True(why.Contains("overloaded"), "it carries the API error");
        }
    }
}
