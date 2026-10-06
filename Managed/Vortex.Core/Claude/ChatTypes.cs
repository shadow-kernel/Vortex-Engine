using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;

namespace Editor.Core.Claude
{
    /// <summary>A tool Claude may call: name, description, JSON input schema.</summary>
    public sealed class ChatTool
    {
        public string Name;
        public string Description;
        /// <summary>JSON Schema of the input (an object schema).</summary>
        public string InputSchemaJson;
        /// <summary>Read-only tools run without asking.</summary>
        public bool ReadOnly;
    }

    /// <summary>One tool call in a conversation, as the UI shows it.</summary>
    public sealed class ChatToolCall
    {
        public string Id;
        public string Name;
        public JsonObject Input;
        public string InputJson => Input?.ToJsonString() ?? "{}";
        public ChatToolResult Result;
        /// <summary>The user refused the call.</summary>
        public bool Denied;
    }

    /// <summary>What a tool returned: text and images; IsError for failures the model should read.</summary>
    public sealed class ChatToolResult
    {
        public string Text;
        public List<(byte[] data, string mimeType)> Images = new List<(byte[] data, string mimeType)>();
        public bool IsError;
    }

    public enum ToolApproval { Allow, Deny }

    public sealed class ClaudeChatException : Exception
    {
        public HttpStatusCode? Status { get; }
        public ClaudeChatException(string message, HttpStatusCode? status = null) : base(message) { Status = status; }
    }
}
