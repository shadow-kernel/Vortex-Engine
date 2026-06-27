namespace Editor.Core.Services.Git
{
    /// <summary>Result of a single git command invocation.</summary>
    public sealed class GitResult
    {
        public int ExitCode { get; set; } = -1;
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";

        public bool Success => ExitCode == 0;

        /// <summary>Best-effort message for UI (stderr, falling back to stdout).</summary>
        public string Message =>
            !string.IsNullOrWhiteSpace(StdErr) ? StdErr.Trim()
            : (StdOut ?? "").Trim();
    }
}
