namespace FindEverything.Engine;

internal sealed class ScanDiagnosticsCollector(ScanOptions options)
{
    private readonly List<ExcludedEntry> _excluded = [];
    private readonly long[] _reasons = new long[4];
    private readonly Dictionary<string, NameCount> _names = new(PathRules.Comparer);
    private long _directories, _files, _omitted, _untracked;

    public void Exclude(ExcludedEntry entry)
    {
        _reasons[(int)entry.Reason]++;
        if (entry.Kind == EntryKind.Directory) _directories++; else _files++;
        if (_excluded.Count < options.MaxRecordedExclusions) _excluded.Add(entry); else _omitted++;
    }

    public void ObserveDirectory(string path, string name)
    {
        if (!_names.TryGetValue(name, out var count))
        {
            if (_names.Count >= options.MaxTrackedDirectoryNames)
            {
                _untracked++;
                return;
            }
            count = new NameCount();
            _names.Add(name, count);
        }
        count.Count++;
        if (count.Paths.Count < 3) count.Paths.Add(path);
    }

    public ScanDiagnostics Snapshot() => new()
    {
        ExcludedPaths = _excluded.OrderBy(entry => entry.Path, PathRules.Comparer).ToArray(),
        OmittedExcludedPaths = _omitted,
        Exclusions = new(_reasons[0], _reasons[1], _reasons[2], _reasons[3], _directories, _files),
        RepeatedDirectoryNames = _names.Where(pair => pair.Value.Count > 1)
            .OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, PathRules.Comparer)
            .Select(pair => new RepeatedDirectoryName(pair.Key, pair.Value.Count,
                pair.Value.Paths.Order(PathRules.Comparer).ToArray())).ToArray(),
        UntrackedDirectoryNameOccurrences = _untracked
    };

    private sealed class NameCount
    {
        public long Count;
        public List<string> Paths { get; } = [];
    }
}
