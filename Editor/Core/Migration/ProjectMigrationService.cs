using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Data;
using Editor.Core.Serialization;

namespace Editor.Core.Migration
{
    /// <summary>The outcome of evaluating a project against the engine's current format.</summary>
    public sealed class MigrationPlan
    {
        public MigrationStatus Status;
        public int From;
        public int To;
        public string SavedWithEngine;              // manifest.EngineVersion (informational)
        public List<IProjectMigration> Steps = new List<IProjectMigration>();
    }

    /// <summary>
    /// Project-version compatibility + migration. On open, <see cref="Evaluate"/> compares the project's stored
    /// format version to the engine's; if it's older AND migrations are registered, <see cref="Migrate"/> backs
    /// the project up, runs the ordered migration steps, and re-stamps the version. If it's NEWER than the engine,
    /// the caller warns the user. Registering zero migrations is valid — the framework ships empty and every
    /// current project simply reads as up-to-date; the version stamp still gets written on save.
    /// </summary>
    public static class ProjectMigrationService
    {
        // Ordered registry of format migrations. EMPTY today — add entries as the project format evolves, e.g.
        //   new MigrateV1ToV2(),
        private static readonly List<IProjectMigration> _migrations = new List<IProjectMigration>();

        public static int Current => Editor.Core.EngineInfo.CurrentProjectFormatVersion;

        // Absent/0 in an existing project.vortex means "pre-formatVersion" — treat as the current baseline (1),
        // so legacy projects are considered compatible, NOT force-migrated.
        private static int Normalize(int pf) => pf <= 0 ? 1 : pf;

        public static MigrationPlan Evaluate(ProjectManifest m)
        {
            int pf = Normalize(m != null ? m.FormatVersion : 0);
            var plan = new MigrationPlan { From = pf, To = Current, SavedWithEngine = m != null ? m.EngineVersion : null };

            if (pf == Current) { plan.Status = MigrationStatus.UpToDate; return plan; }
            if (pf > Current) { plan.Status = MigrationStatus.NewerThanEngine; return plan; }

            plan.Steps = GetPath(pf, Current);
            // Older format but nothing registered to migrate it -> nothing to do (compatible enough).
            plan.Status = plan.Steps.Count > 0 ? MigrationStatus.NeedsMigration : MigrationStatus.UpToDate;
            return plan;
        }

        private static List<IProjectMigration> GetPath(int from, int to)
        {
            var path = new List<IProjectMigration>();
            var sorted = _migrations.OrderBy(x => x.FromVersion).ToList();
            int v = from;
            while (v < to)
            {
                var step = sorted.FirstOrDefault(x => x.FromVersion == v);
                if (step == null) break; // gap in the chain — stop
                path.Add(step);
                v = step.ToVersion;
            }
            return path;
        }

        /// <summary>Back up the project, run the ordered migrations, and re-stamp the format + engine version.
        /// Returns true on success (or if nothing to do); restores the backup on any step failure.</summary>
        public static bool Migrate(string projectDir, ProjectManifest m, string manifestPath, Action<string> log = null)
        {
            var plan = Evaluate(m);
            if (plan.Status != MigrationStatus.NeedsMigration) { StampAndSave(m, manifestPath); return true; }

            string backup;
            try { log?.Invoke("Backing up project…"); backup = BackupProject(projectDir); }
            catch (Exception ex) { log?.Invoke("Backup failed: " + ex.Message); return false; }

            try
            {
                foreach (var step in plan.Steps)
                {
                    log?.Invoke("Migrating (" + step.FromVersion + "→" + step.ToVersion + "): " + step.Description);
                    step.Apply(projectDir, m);
                    m.FormatVersion = step.ToVersion;
                }
                StampAndSave(m, manifestPath);
                log?.Invoke("Migration complete — project is now compatible.");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("Migration failed: " + ex.Message + " — restoring the backup.");
                try { RestoreBackup(backup, projectDir); } catch { }
                return false;
            }
        }

        private static void StampAndSave(ProjectManifest m, string manifestPath)
        {
            if (m == null) return;
            m.FormatVersion = Current;
            m.EngineVersion = Editor.Core.EngineInfo.VersionString;
            if (!string.IsNullOrEmpty(manifestPath))
                try { DataSerializer.SaveAsJson(m, manifestPath); } catch { }
        }

        // ---- backup / restore ----
        private static readonly string[] _skipDirs = { "bin", "obj", ".git", ".vs", "library", "backups", "packages" };

        /// <summary>Copy the project into <c>&lt;project&gt;\.ve\backups\&lt;timestamp&gt;\</c> (excluding build/VCS dirs).
        /// Returns the backup directory.</summary>
        public static string BackupProject(string projectDir)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backupRoot = Path.Combine(projectDir, ".ve", "backups", stamp);
            Directory.CreateDirectory(backupRoot);
            CopyFiltered(projectDir, backupRoot);
            return backupRoot;
        }

        // Copies everything except build/VCS dirs. The "backups" skip means we never recurse into the backup tree
        // itself (it lives under .ve\backups), so this is safe even though we back up in-place.
        private static void CopyFiltered(string src, string dst)
        {
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);

            foreach (var dir in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(dir);
                if (_skipDirs.Contains(name.ToLowerInvariant())) continue;
                string sub = Path.Combine(dst, name);
                Directory.CreateDirectory(sub);
                CopyFiltered(dir, sub);
            }
        }

        private static void RestoreBackup(string backupDir, string projectDir)
        {
            if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir)) return;
            foreach (var file in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(backupDir.Length).TrimStart('\\', '/');
                string target = Path.Combine(projectDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }
    }
}
