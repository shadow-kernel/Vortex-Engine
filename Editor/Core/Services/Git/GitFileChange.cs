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
