using Editor.Core.Data;

namespace Editor.Core.Migration
{
    /// <summary>Where a project's format sits relative to the running engine.</summary>
    public enum MigrationStatus
    {
        UpToDate,        // project matches the engine's current format — open normally
        NeedsMigration,  // project is older + registered migrations exist — must migrate before opening
        NewerThanEngine, // project was made with a NEWER engine — warn (may not open correctly)
        Unknown
    }

    /// <summary>
    /// A single-step project-format migration (transforms a project AT <see cref="FromVersion"/> up to
    /// <see cref="ToVersion"/> == FromVersion + 1). The runner chains steps in order. Implementations mutate the
    /// manifest (re-saved by the runner) and may also rewrite on-disk assets/scenes under <c>projectDir</c>.
    ///
    /// The registry ships EMPTY (no breaking format changes yet). When the project format changes in a breaking
    /// way, bump <see cref="Editor.Core.EngineInfo.CurrentProjectFormatVersion"/> and register a migration here.
    /// </summary>
    public interface IProjectMigration
    {
        int FromVersion { get; }
        int ToVersion { get; }
        string Description { get; }
        void Apply(string projectDir, ProjectManifest manifest);
    }
}
