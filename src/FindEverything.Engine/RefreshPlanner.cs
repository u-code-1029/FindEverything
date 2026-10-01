namespace FindEverything.Engine;

/// <summary>
/// Coalesces invalidated directory subtrees before a host requests metadata refreshes.
/// This class performs no filesystem calls and does not infer deletion or provide change detection.
/// </summary>
public static class RefreshPlanner
{
    /// <summary>
    /// Returns normalized, deterministic, nonoverlapping scopes inside <paramref name="rootPath"/>.
    /// Inputs are directory paths, including directories that no longer exist. Excluded directories
    /// and their descendants are dropped; a queued ancestor covers all queued descendants.
    /// </summary>
    /// <remarks>
    /// Ancestor hash-set walks avoid comparing every pair of invalidations. For n input directories,
    /// maximum depth d, and k returned scopes, the expected number of hash lookups and ordering
    /// comparisons is O(n × d + k log k), with O(n + e) storage for e configured exclusions. Path string
    /// normalization, hashing, and comparisons additionally depend on path lengths. No paths are
    /// opened, enumerated, or checked for existence. The caller supplies a bounded invalidation queue
    /// and remains responsible for detecting missed changes and scheduling periodic reconciliation.
    /// </remarks>
    public static IReadOnlyList<ScanRequest> Plan(string rootPath,
        IEnumerable<string> dirtyDirectoryPaths, ScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dirtyDirectoryPaths);
        var root = PathRules.Normalize(rootPath);
        options ??= new ScanOptions();
        options.Validate(root);

        var excludedPaths = new HashSet<string>(
            options.ExcludedPaths.Select(PathRules.Normalize), PathRules.Comparer);
        var excludedNames = new HashSet<string>(options.ExcludedDirectoryNames, PathRules.Comparer);
        var candidates = new Dictionary<string, string>(PathRules.Comparer);

        foreach (var input in dirtyDirectoryPaths)
        {
            var path = PathRules.Normalize(input);
            if (!PathRules.IsWithin(path, root))
                throw new ArgumentException("Invalidated directories must be within the source root.", nameof(dirtyDirectoryPaths));
            if (IsExcluded(path))
                continue;

            // Choose an ordinal representative when the host treats differently cased paths as
            // equal, so results do not depend on the order of incoming change notifications.
            if (!candidates.TryGetValue(path, out var existing) || StringComparer.Ordinal.Compare(path, existing) < 0)
                candidates[path] = path;
        }

        var scopes = new List<string>(candidates.Count);
        foreach (var path in candidates.Values)
        {
            var covered = false;
            for (var ancestor = ParentWithinRoot(path); ancestor is not null; ancestor = ParentWithinRoot(ancestor))
            {
                if (!candidates.ContainsKey(ancestor))
                    continue;
                covered = true;
                break;
            }
            if (!covered)
                scopes.Add(path);
        }

        scopes.Sort(PathRules.Comparer);
        return scopes.Select(scope => new ScanRequest(root) { ScopePath = scope, Options = options }).ToArray();

        bool IsExcluded(string path)
        {
            for (string? current = path; current is not null; current = ParentWithinRoot(current))
            {
                if (excludedPaths.Contains(current) || excludedNames.Contains(Path.GetFileName(current)))
                    return true;
            }
            return false;
        }

        string? ParentWithinRoot(string path) => PathRules.Comparer.Equals(path, root)
            ? null : Path.GetDirectoryName(path);
    }
}
