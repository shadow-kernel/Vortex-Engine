namespace Editor.Core.Services.Git
{
    public enum GitChangeKind
    {
        Modified,
        Added,
        Deleted,
        Renamed,
        Untracked,
        Conflicted,
    }

    /// <summary>One commit from <c>git log</c> (for the history view).</summary>
    public sealed class GitCommit
    {
        public string Hash { get; set; } = "";
        public string Author { get; set; } = "";
        public string Date { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Display => Hash + "  " + Subject;
        public string Meta => Author + " · " + Date;
    }

    /// <summary>One changed path reported by <c>git status</c>.</summary>
    public sealed class GitFileChange
    {
        public string Path { get; set; } = "";
        public GitChangeKind Kind { get; set; }
        public bool Staged { get; set; }

        /// <summary>One-letter badge for the UI (A/M/D/R/U/!).</summary>
        public string Badge
        {
            get
            {
                switch (Kind)
                {
                    case GitChangeKind.Added: return "A";
                    case GitChangeKind.Deleted: return "D";
                    case GitChangeKind.Renamed: return "R";
                    case GitChangeKind.Untracked: return "?";
                    case GitChangeKind.Conflicted: return "!";
                    default: return "M";
                }
            }
        }
    }
}
