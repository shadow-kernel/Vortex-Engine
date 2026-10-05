using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VortexEditor.Claude
{
    /// <summary>One block of a tool's answer: text, or an image (PNG/JPEG bytes).</summary>
    public sealed class ToolContent
    {
        public string Text { get; init; }
        public byte[] Image { get; init; }
        public string MimeType { get; init; }
    }

    /// <summary>
    /// What a Vortex tool hands back to Claude — over MCP and in the embedded Claude panel alike: text (mostly compact
    /// JSON) and optional images. <see cref="IsError"/> marks a call that failed in a way the model should read and
    /// correct (an unknown entity, an invalid enum value — the message lists the valid ones).
    /// </summary>
    public sealed class ToolResult
    {
        public List<ToolContent> Content { get; } = new List<ToolContent>();
        public bool IsError { get; init; }

        public static ToolResult Text(string text)
        {
            var r = new ToolResult();
            r.Content.Add(new ToolContent { Text = text ?? "" });
            return r;
        }

        public static ToolResult Json(object value) => Text(ToolJson.Serialize(value));

        public static ToolResult Error(string message)
        {
            var r = new ToolResult { IsError = true };
            r.Content.Add(new ToolContent { Text = message ?? "error" });
            return r;
        }

        public ToolResult WithImage(byte[] data, string mimeType = "image/png")
        {
            if (data != null && data.Length > 0) Content.Add(new ToolContent { Image = data, MimeType = mimeType });
            return this;
        }

        /// <summary>Whatever a tool method returned, as a result: a ToolResult as-is, a string as text, anything else as JSON.</summary>
        public static ToolResult From(object raw) => raw switch
        {
            null => Text("ok"),
            ToolResult r => r,
            string s => Text(s),
            JsonElement e => Text(e.GetRawText()),
            _ => Json(raw),
        };

        /// <summary>The text of the answer, shortened — for the operation log and the panel's tool cards.</summary>
        public string Summary(int max = 160)
        {
            string text = string.Join(" ", Content.Where(c => c.Text != null).Select(c => c.Text));
            int images = Content.Count(c => c.Image != null);
            if (images > 0) text = (images == 1 ? "[image] " : "[" + images + " images] ") + text;
            text = text.Replace('\n', ' ').Replace('\r', ' ');
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }

    /// <summary>An expected failure inside a tool (bad reference, invalid value): the message reaches the model as an
    /// error result, worded so it can correct the call (valid values, the ids of ambiguous matches …).</summary>
    public sealed class ToolError : Exception
    {
        public ToolError(string message) : base(message) { }
    }

    /// <summary>JSON for tool results: snake_case names (like the tool and parameter names), no nulls, no escaping of
    /// non-ASCII names, numbers rounded by the tools themselves.</summary>
    public static class ToolJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new JsonStringEnumConverter() },
        };

        public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

        /// <summary>A float for JSON output: 3 decimals is plenty for positions, angles and settings.</summary>
        public static double R(float v) => float.IsFinite(v) ? Math.Round(v, 3) : 0;
        public static double R(double v) => double.IsFinite(v) ? Math.Round(v, 3) : 0;
        public static double[] V(Editor.ECS.Vector3 v) => new[] { R(v.X), R(v.Y), R(v.Z) };
    }
}
