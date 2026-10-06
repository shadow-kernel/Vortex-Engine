using System.Collections.Generic;
using System.Linq;

namespace Editor.Core.Claude
{
    /// <summary>A model the Claude panel offers and what it accepts.</summary>
    public sealed class ClaudeModelInfo
    {
        public string Id;
        public string Label;
        public string Blurb;
        /// <summary>The context window in tokens.</summary>
        public int ContextWindow;
        /// <summary>The effort levels the model takes; empty = it takes no effort parameter.</summary>
        public string[] Efforts;
        public string DefaultEffort;
        /// <summary>Adaptive thinking, shown as summaries (always on for the 5.x models).</summary>
        public bool Thinking;
        /// <summary>Server-side compaction (beta <c>compact-2026-01-12</c>).</summary>
        public bool Compaction;
        /// <summary>Server-side refusal fallbacks, <c>fallbacks: "default"</c> (beta <c>server-side-fallback-2026-07-01</c>).</summary>
        public bool Fallbacks;
        public int MaxOutputTokens;
        /// <summary>First-party API price per million tokens (cache writes cost 1.25 × input).</summary>
        public double InputPrice, OutputPrice, CacheReadPrice;

        public override string ToString() => Label;
    }

    /// <summary>The models the Claude panel offers (Claude API ids, cached 2026-09-25). <see cref="ClaudeModelInfo.DefaultEffort"/>
    /// is the API's own default for the model, so "default" costs the same whether it is sent or not.</summary>
    public static class ClaudeModels
    {
        public static readonly string[] AllEfforts = { "low", "medium", "high", "xhigh", "max" };

        /// <summary>Context sizes the panel offers: the conversation is compacted on the server once it reaches about 80 %
        /// of the chosen size (models with a smaller window offer only what fits).</summary>
        public static readonly int[] ContextSizes = { 200_000, 1_000_000 };

        public static readonly IReadOnlyList<ClaudeModelInfo> All = new[]
        {
            new ClaudeModelInfo
            {
                Id = "claude-opus-5-5", Label = "Opus 5.5", Blurb = "The default for most work, agentic coding included",
                ContextWindow = 1_000_000, Efforts = AllEfforts, DefaultEffort = "medium", Thinking = true, Compaction = true,
                Fallbacks = true, MaxOutputTokens = 128_000, InputPrice = 4, OutputPrice = 20, CacheReadPrice = 0.20,
            },
            new ClaudeModelInfo
            {
                Id = "claude-fable-5-1", Label = "Fable 5.1", Blurb = "The step up for the hardest, longest tasks",
                ContextWindow = 1_000_000, Efforts = AllEfforts, DefaultEffort = "high", Thinking = true, Compaction = true,
                Fallbacks = true, MaxOutputTokens = 128_000, InputPrice = 10, OutputPrice = 50, CacheReadPrice = 0.25,
            },
            new ClaudeModelInfo
            {
                Id = "claude-sonnet-5-5", Label = "Sonnet 5.5", Blurb = "Fast and capable, at half the price of Opus",
                ContextWindow = 1_000_000, Efforts = AllEfforts, DefaultEffort = "high", Thinking = true, Compaction = true,
                Fallbacks = true, MaxOutputTokens = 128_000, InputPrice = 2, OutputPrice = 10, CacheReadPrice = 0.20,
            },
            new ClaudeModelInfo
            {
                Id = "claude-haiku-4-5", Label = "Haiku 4.5", Blurb = "The fastest and cheapest, for quick questions",
                ContextWindow = 200_000, Efforts = new string[0], DefaultEffort = null, Thinking = false, Compaction = false,
                Fallbacks = false, MaxOutputTokens = 64_000, InputPrice = 1, OutputPrice = 5, CacheReadPrice = 0.10,
            },
        };

        public static ClaudeModelInfo Find(string id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];

        /// <summary>The context sizes that fit the model's window.</summary>
        public static IEnumerable<int> ContextSizesFor(ClaudeModelInfo m) => ContextSizes.Where(c => c <= m.ContextWindow);

        public static string ContextLabel(int tokens) => tokens >= 1_000_000 ? (tokens / 1_000_000) + "M" : (tokens / 1000) + "K";
    }
}
