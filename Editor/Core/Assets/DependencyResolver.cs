using System;
using System.Collections.Generic;
using System.Linq;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Resolves asset dependencies - what depends on what, and what is depended upon.
    /// </summary>
    public class DependencyResolver
    {
        private readonly AssetDatabase _assetDatabase;

        public DependencyResolver(AssetDatabase assetDatabase)
        {
            _assetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        }

        /// <summary>
        /// Gets all assets that depend on the specified asset.
        /// (Returns assets that reference the given asset)
        /// </summary>
        public List<AssetMetadata> GetDependents(Guid assetGuid)
        {
            var dependents = new List<AssetMetadata>();

            foreach (var asset in _assetDatabase.GetAllAssets())
            {
                if (asset.Dependencies != null && asset.Dependencies.Contains(assetGuid))
                {
                    dependents.Add(asset);
                }
            }

            return dependents;
        }

        /// <summary>
        /// Gets all assets that the specified asset depends on.
        /// (Returns assets that are referenced by the given asset)
        /// </summary>
        public List<AssetMetadata> GetDependencies(Guid assetGuid)
        {
            var asset = _assetDatabase.GetAsset(assetGuid);
            if (asset == null || asset.Dependencies == null)
                return new List<AssetMetadata>();

            return asset.Dependencies
                .Select(depGuid => _assetDatabase.GetAsset(depGuid))
                .Where(dep => dep != null)
                .ToList();
        }

        /// <summary>
        /// Gets all dependencies recursively (dependencies of dependencies).
        /// </summary>
        public List<AssetMetadata> GetDependenciesRecursive(Guid assetGuid)
        {
            var visited = new HashSet<Guid>();
            var result = new List<AssetMetadata>();
            CollectDependenciesRecursive(assetGuid, visited, result);
            return result;
        }

        private void CollectDependenciesRecursive(Guid assetGuid, HashSet<Guid> visited, List<AssetMetadata> result)
        {
            if (visited.Contains(assetGuid))
                return;

            visited.Add(assetGuid);

            var dependencies = GetDependencies(assetGuid);
            foreach (var dep in dependencies)
            {
                if (!visited.Contains(dep.Guid))
                {
                    result.Add(dep);
                    CollectDependenciesRecursive(dep.Guid, visited, result);
                }
            }
        }

        /// <summary>
        /// Gets orphaned dependencies - dependencies that would become unused if the asset is deleted.
        /// </summary>
        public List<AssetMetadata> GetOrphanedDependencies(Guid assetGuid)
        {
            var orphans = new List<AssetMetadata>();
            var dependencies = GetDependencies(assetGuid);

            foreach (var dep in dependencies)
            {
                // Check if this dependency is used by any other asset
                var otherDependents = GetDependents(dep.Guid)
                    .Where(d => d.Guid != assetGuid)
                    .ToList();

                if (otherDependents.Count == 0)
                {
                    orphans.Add(dep);
                }
            }

            return orphans;
        }

        /// <summary>
        /// Checks for circular dependencies.
        /// </summary>
        public bool HasCircularDependency(Guid assetGuid)
        {
            var visited = new HashSet<Guid>();
            return HasCircularDependencyRecursive(assetGuid, visited, assetGuid);
        }

        private bool HasCircularDependencyRecursive(Guid currentGuid, HashSet<Guid> visited, Guid originalGuid)
        {
            if (visited.Contains(currentGuid))
                return currentGuid == originalGuid;

            visited.Add(currentGuid);

            var dependencies = GetDependencies(currentGuid);
            foreach (var dep in dependencies)
            {
                if (HasCircularDependencyRecursive(dep.Guid, new HashSet<Guid>(visited), originalGuid))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the reference count for an asset (how many assets depend on it).
        /// </summary>
        public int GetReferenceCount(Guid assetGuid)
        {
            return GetDependents(assetGuid).Count;
        }
    }
}
