namespace FindEverything.Engine;

public enum EntryKind { File, Directory }
public enum ScanStatus { Completed, Partial, Cancelled, Deferred }
public enum DeferralReason { ExplicitRule, HistoricalEntryCount, HistoricalDuration, EntryBudget, TimeBudget, PendingLimit }

public sealed record IndexedEntry(
    string FullPath, string Name, string ParentPath, EntryKind Kind,
    long? SizeBytes, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc)
{
    // Metadata exists, but this scope has deferred/unverified contents.
    public bool CoveragePending { get; init; }
}

public sealed record DirectoryScanCost(string Path, long Entries, TimeSpan Duration, DateTimeOffset RecordedUtc);
public sealed record PendingScope(string RootPath, string ScopePath, DeferralReason Reason,
    long? EstimatedEntries, TimeSpan? EstimatedDuration, DateTimeOffset DeferredUtc);

public sealed record DeferralPolicy
{
    public IReadOnlyList<string> DirectoryNames { get; init; } = [];
    public IReadOnlyList<string> Paths { get; init; } = [];
    public long? HistoricalEntryThreshold { get; init; }
    public TimeSpan? HistoricalDurationThreshold { get; init; }
    public long? EntryBudget { get; init; }
    public TimeSpan? TimeBudget { get; init; }
    public int MaxPendingScopes { get; init; } = 256;

    public void Validate(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(DirectoryNames);
        ArgumentNullException.ThrowIfNull(Paths);
        foreach (var name in DirectoryNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name is "." or ".." || name.Contains('/') || name.Contains('\\'))
                throw new ArgumentException("Deferred directory names must be single names.");
        }
        foreach (var path in Paths)
            if (!PathRules.IsWithin(path, rootPath))
                throw new ArgumentException("Deferred paths must be within the source root.");
        if (HistoricalEntryThreshold is <= 0 || EntryBudget is <= 0)
            throw new ArgumentOutOfRangeException(nameof(EntryBudget), "Entry thresholds must be positive.");
        if (HistoricalDurationThreshold is { } duration && duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(HistoricalDurationThreshold));
        if (TimeBudget is { } budget && budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TimeBudget));
        if (MaxPendingScopes is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingScopes));
    }
}

public sealed record ScanError(string Path, string Message);
public sealed record ScanProgress(long Entries, long Directories, long ExcludedEntries,
    long SkippedLinks, long ErrorCount, TimeSpan Elapsed)
{
    public long PendingDirectories { get; init; }
}

public sealed record ScanReport(
    Guid ScanId, string RootPath, string ScopePath, ScanStatus Status,
    ScanProgress Progress, IReadOnlyList<ScanError> Errors)
{
    public IReadOnlyList<PendingScope> PendingScopes { get; init; } = [];
}

public sealed record ScanOptions
{
    public IReadOnlyList<string> ExcludedDirectoryNames { get; init; } = [];
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
    public IReadOnlyList<string> ExcludedFilePatterns { get; init; } = [];
    public int BatchSize { get; init; } = 256;
    public int MaxEntriesPerSecond { get; init; } = 2000;
    public TimeSpan DirectoryDelay { get; init; } = TimeSpan.FromMilliseconds(5);
    public int MaxDepth { get; init; } = 256;
    public int MaxRecordedErrors { get; init; } = 100;
    public int EnumerationBufferSize { get; init; } = 16 * 1024;
    public DeferralPolicy Deferral { get; init; } = new();

    public void Validate(string rootPath)
    {
        var root = PathRules.Normalize(rootPath);
        ArgumentNullException.ThrowIfNull(Deferral);
        Deferral.Validate(root);
        if (BatchSize is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(BatchSize), "Batch size must be between 1 and 65536.");
        if (MaxEntriesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEntriesPerSecond), "The entry rate must be positive.");
        if (DirectoryDelay < TimeSpan.Zero || DirectoryDelay.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(DirectoryDelay));
        if (MaxDepth is < 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (MaxRecordedErrors is < 0 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(MaxRecordedErrors));
        if (EnumerationBufferSize is <= 0 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(EnumerationBufferSize));
        ArgumentNullException.ThrowIfNull(ExcludedDirectoryNames);
        ArgumentNullException.ThrowIfNull(ExcludedPaths);
        ArgumentNullException.ThrowIfNull(ExcludedFilePatterns);
        foreach (var name in ExcludedDirectoryNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name is "." or ".." || name.Contains('/') || name.Contains('\\'))
                throw new ArgumentException("Excluded directory names must be single directory names.");
        }
        foreach (var path in ExcludedPaths)
            if (!PathRules.IsWithin(path, root))
                throw new ArgumentException("Excluded paths must be within the source root.");
        foreach (var pattern in ExcludedFilePatterns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
            if (pattern.Contains('/') || pattern.Contains('\\'))
                throw new ArgumentException("File patterns match file names only, not directory paths.");
        }
    }
}

public sealed record ScanRequest(string RootPath)
{
    // A refresh may cover one subtree while preserving entries elsewhere in the root.
    public string? ScopePath { get; init; }
    public ScanOptions Options { get; init; } = new();
    // Bypass deferral only: exclusions, rate limits, link checks and cancellation remain active.
    public bool OnDemand { get; init; }
    internal IReadOnlyDictionary<string, DirectoryScanCost> CostHints { get; init; } =
        new Dictionary<string, DirectoryScanCost>(PathRules.Comparer);
    internal Func<IReadOnlyList<DirectoryScanCost>, CancellationToken, Task>? WriteCosts { get; init; }
}

public sealed record SearchQuery
{
    public string? NameContains { get; init; }
    public string? RootPath { get; init; }
    public EntryKind? Kind { get; init; }
    public DateTimeOffset? CreatedFromUtc { get; init; }
    public DateTimeOffset? CreatedBeforeUtc { get; init; }
    public DateTimeOffset? ModifiedFromUtc { get; init; }
    public DateTimeOffset? ModifiedBeforeUtc { get; init; }
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

public sealed record SearchResult(IReadOnlyList<IndexedEntry> Entries, bool HasMore)
{
    public bool HasPendingScopes { get; init; }
}

public sealed record PendingQuery
{
    public string? RootPath { get; init; }
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}
public sealed record PendingResult(IReadOnlyList<PendingScope> Entries, bool HasMore);

// Scanner owns no database and never writes to a source tree.
public interface IMetadataScanner
{
    Task<ScanReport> ScanAsync(ScanRequest request, Guid scanId,
        Func<IReadOnlyList<IndexedEntry>, CancellationToken, Task> writeBatch,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

public interface IIndexStore : IAsyncDisposable
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken = default);
    Task BeginScanAsync(Guid scanId, string rootPath, string scopePath, CancellationToken cancellationToken = default);
    Task StageAsync(Guid scanId, IReadOnlyList<IndexedEntry> entries, CancellationToken cancellationToken = default);
    Task StageDirectoryCostsAsync(Guid scanId, IReadOnlyList<DirectoryScanCost> costs, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, DirectoryScanCost>> GetDirectoryCostsAsync(string rootPath, string scopePath,
        DeferralPolicy policy, CancellationToken cancellationToken = default);
    // Partial scans upsert but never delete unseen entries; cancelled scans publish nothing.
    Task PublishAsync(ScanReport report, CancellationToken cancellationToken = default);
    Task DiscardAsync(Guid scanId, CancellationToken cancellationToken = default);
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task<PendingResult> ListPendingAsync(PendingQuery query, CancellationToken cancellationToken = default);
}
