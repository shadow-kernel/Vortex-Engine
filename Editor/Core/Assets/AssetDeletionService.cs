using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Service for safely deleting assets with cascading delete support.
    /// </summary>
    public class AssetDeletionService
    {
        private readonly AssetDatabase _assetDatabase;
        private readonly DependencyResolver _dependencyResolver;

        public AssetDeletionService(AssetDatabase assetDatabase)
        {
            _assetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
            _dependencyResolver = new DependencyResolver(assetDatabase);
        }

        /// <summary>
        /// Result of a deletion analysis.
        /// </summary>
        public class DeletionAnalysis
        {
            public AssetMetadata Asset { get; set; }
            public List<AssetMetadata> OrphanedDependencies { get; set; } = new List<AssetMetadata>();
            public List<AssetMetadata> Dependents { get; set; } = new List<AssetMetadata>();
            public bool CanDelete => Dependents.Count == 0;
            public string WarningMessage { get; set; }
        }

        /// <summary>
        /// Analyzes what would happen if an asset is deleted.
        /// </summary>
        public DeletionAnalysis AnalyzeDeletion(Guid assetGuid)
        {
            var asset = _assetDatabase.GetAsset(assetGuid);
            if (asset == null)
                return null;

            var analysis = new DeletionAnalysis
            {
                Asset = asset,
                OrphanedDependencies = _dependencyResolver.GetOrphanedDependencies(assetGuid),
                Dependents = _dependencyResolver.GetDependents(assetGuid)
            };

            if (!analysis.CanDelete)
            {
                analysis.WarningMessage = $"Cannot delete {asset.FileName} because it is referenced by {analysis.Dependents.Count} other asset(s).";
            }
            else if (analysis.OrphanedDependencies.Count > 0)
            {
                analysis.WarningMessage = $"Deleting {asset.FileName} will also delete {analysis.OrphanedDependencies.Count} unused dependency asset(s).";
            }

            return analysis;
        }

        /// <summary>
        /// Deletes an asset and optionally its orphaned dependencies.
        /// </summary>
        public bool DeleteAsset(Guid assetGuid, bool deleteOrphans = true)
        {
            var analysis = AnalyzeDeletion(assetGuid);
            if (analysis == null)
                return false;

            // Can't delete if other assets depend on this
            if (!analysis.CanDelete)
                return false;

            try
            {
                // Delete orphaned dependencies first if requested
                if (deleteOrphans && analysis.OrphanedDependencies.Count > 0)
                {
                    foreach (var orphan in analysis.OrphanedDependencies)
                    {
                        DeleteAssetFiles(orphan);
                    }
                }

                // Delete the main asset
                DeleteAssetFiles(analysis.Asset);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes multiple assets.
        /// </summary>
        public List<Guid> DeleteAssets(List<Guid> assetGuids, bool deleteOrphans = true)
        {
            var deletedGuids = new List<Guid>();

            foreach (var guid in assetGuids)
            {
                if (DeleteAsset(guid, deleteOrphans))
                {
                    deletedGuids.Add(guid);
                }
            }

            return deletedGuids;
        }

        /// <summary>
        /// Deletes the physical files for an asset.
        /// </summary>
        private void DeleteAssetFiles(AssetMetadata asset)
        {
            if (asset == null)
                return;

            var assetPath = _assetDatabase.GetAssetPath(asset.Guid);
            if (string.IsNullOrEmpty(assetPath))
                return;

            // Delete main asset file
            if (File.Exists(assetPath))
            {
                File.Delete(assetPath);
            }

            // Delete .vmeta file
            var metaPath = assetPath + AssetDatabase.MetaFileExtension;
            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
            }

            // Note: We don't remove from AssetDatabase here because that should happen
            // when the database is refreshed, or the caller should handle it
        }

        /// <summary>
        /// Gets a user-friendly deletion summary message.
        /// </summary>
        public string GetDeletionSummary(Guid assetGuid)
        {
            var analysis = AnalyzeDeletion(assetGuid);
            if (analysis == null)
                return "Asset not found.";

            if (!analysis.CanDelete)
            {
                var summary = $"Cannot delete '{analysis.Asset.FileName}'.\n\n";
                summary += "Referenced by:\n";
                foreach (var dependent in analysis.Dependents)
                {
                    summary += $"  • {dependent.FileName} ({dependent.Type})\n";
                }
                return summary;
            }

            var message = $"Delete '{analysis.Asset.FileName}'";

            if (analysis.OrphanedDependencies.Count > 0)
            {
                message += $"\n\nThis will also delete {analysis.OrphanedDependencies.Count} unused dependency/dependencies:\n";
                foreach (var orphan in analysis.OrphanedDependencies.Take(5))
                {
                    message += $"  • {orphan.FileName} ({orphan.Type})\n";
                }
                if (analysis.OrphanedDependencies.Count > 5)
                {
                    message += $"  ... and {analysis.OrphanedDependencies.Count - 5} more\n";
                }
            }

            message += "\n\nAre you sure you want to continue?";

            return message;
        }
    }
}
