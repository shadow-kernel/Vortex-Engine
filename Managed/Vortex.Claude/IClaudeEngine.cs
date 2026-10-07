using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Core.Claude
{
    /// <summary>
    /// A conversation engine behind the Claude panel. The panel drives either engine the same way:
    /// <list type="bullet">
    /// <item><see cref="ClaudeSession"/> — the official Anthropic C# SDK (Messages API), billed to an API key or a
    /// Console account;</item>
    /// <item><see cref="ClaudeCodeSession"/> — the user's own installed Claude Code, run headless, billed to their
    /// Claude plan (Pro / Max) — no terminal, the panel is still the UI.</item>
    /// </list>
    /// Both stream the same events and expose the same counters, so the panel binds to the interface and switches the
    /// engine under it when the credentials change.
    /// </summary>
    public interface IClaudeEngine
    {
        ClaudeModelInfo Model { get; set; }
        /// <summary>Effort level; null = the model's default. Ignored by models that take none.</summary>
        string Effort { get; set; }
        /// <summary>The context size the conversation may grow to; summarized before it is reached.</summary>
        int ContextSize { get; set; }
        ClaudeMode Mode { get; set; }
        string SystemPrompt { get; set; }
        /// <summary>The tools Claude may call (the SDK engine sends them; Claude Code reaches them over MCP).</summary>
        IReadOnlyList<ChatTool> Tools { get; set; }

        long InputTokens { get; }
        long OutputTokens { get; }
        long CacheReadTokens { get; }
        long CacheWriteTokens { get; }
        /// <summary>How much of the context the last request used.</summary>
        long ContextTokens { get; }
        /// <summary>What the conversation cost so far, in USD (an estimate for the SDK engine, the metered cost for
        /// Claude Code — on a plan that is included usage, shown for transparency).</summary>
        double Cost { get; }
        int MessageCount { get; }
        /// <summary>The input size at which the conversation is summarized.</summary>
        int CompactAt { get; }
        string EffectiveEffort { get; }
        /// <summary>True when the last turn can be sent again (it failed or was stopped).</summary>
        bool CanRetry { get; }

        event Action TextStarted;
        event Action<string> TextDelta;
        event Action ThinkingStarted;
        event Action<string> ThinkingDelta;
        event Action<ChatToolCall> ToolStarted;
        event Action<ChatToolCall> ToolFinished;
        /// <summary>Earlier turns were summarized to stay within the context size.</summary>
        event Action Compacted;
        /// <summary>The model declined, or the turn failed in a way worth showing; the argument explains why.</summary>
        event Action<string> Refused;
        /// <summary>A turn finished and the counters moved.</summary>
        event Action TurnCompleted;

        Task<string> SendAsync(string message, CancellationToken ct);
        Task<string> RetryAsync(CancellationToken ct);
        void Reset();
    }
}
