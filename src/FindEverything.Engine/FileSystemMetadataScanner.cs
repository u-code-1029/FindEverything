using System.Diagnostics;
using System.IO.Enumeration;
using System.Security;

namespace FindEverything.Engine;

/// <summary>
/// Streams directory metadata without opening file contents or writing to the source.
/// Only one traversal is active: the enumerator stack grows with depth, not directory width.
/// </summary>
public sealed class FileSystemMetadataScanner : IMetadataScanner, IDirectoryDiscoveryScanner
{
    public Task<ScanReport> ScanAsync(ScanRequest request, Guid scanId,
        Func<IReadOnlyList<IndexedEntry>, CancellationToken, Task> writeBatch,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writeBatch);
        ArgumentNullException.ThrowIfNull(request.Options);
        var root = PathRules.Normalize(request.RootPath);
        var scope = PathRules.Normalize(request.ScopePath ?? root);
        if (!PathRules.IsWithin(scope, root))
            throw new ArgumentException("The scan scope must be inside the configured root.", nameof(request));

        var settings = Settings.Create(request.Options, root, request.CostHints, request.OnDemand);
        var inspectDirectory = request.InspectDirectory;
        var discovery = inspectDirectory is null
            ? null
            : new DiscoveryState(inspectDirectory, progress: null);
        // Native enumeration is synchronous, including SMB requests. Keep it off a WPF UI thread.
        // Do not pass the token to Task.Run: even pre-cancelled requests return a Cancelled report.
        return Task.Run(() => ScanCoreAsync(root, scope, scanId, settings,
            writeBatch, request.WriteCosts, progress, cancellationToken,
            discovery: discovery, emitEntries: true));
    }

    public Task<DirectoryDiscoveryReport> DiscoverDirectoriesAsync(
        DirectoryDiscoveryRequest request,
        Func<DirectoryCandidate, DirectoryTraversalDecision> inspectDirectory,
        IProgress<DirectoryDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(inspectDirectory);
        ArgumentNullException.ThrowIfNull(request.Options);
        var root = PathRules.Normalize(request.RootPath);
        var scope = PathRules.Normalize(request.ScopePath ?? root);
        if (!PathRules.IsWithin(scope, root))
            throw new ArgumentException("The discovery scope must be inside the configured root.", nameof(request));

        // Discovery is an explicit, non-persistent operation. Deferral is an indexing concern,
        // while exclusions, link checks, throttling and depth limits still apply.
        var settings = Settings.Create(request.Options, root,
            new Dictionary<string, DirectoryScanCost>(PathRules.Comparer), onDemand: true);
        var discovery = new DiscoveryState(inspectDirectory, progress);

        // Native enumeration is synchronous, including SMB requests. Keep it off a WPF UI thread.
        // Do not pass the token to Task.Run: even pre-cancelled requests return a Cancelled report.
        return Task.Run(async () =>
        {
            var scan = await ScanCoreAsync(root, scope, Guid.Empty, settings,
                static (_, _) => Task.CompletedTask, writeCosts: null, progress: null,
                cancellationToken: cancellationToken, discovery: discovery,
                emitEntries: false).ConfigureAwait(false);
            return new DirectoryDiscoveryReport(root, scope, scan.Status,
                discovery.LastProgress, scan.Errors)
            {
                Diagnostics = scan.Diagnostics
            };
        });
    }

    private static async Task<ScanReport> ScanCoreAsync(string root, string scope,
        Guid scanId, Settings settings,
        Func<IReadOnlyList<IndexedEntry>, CancellationToken, Task> writeBatch,
        Func<IReadOnlyList<DirectoryScanCost>, CancellationToken, Task>? writeCosts,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken,
        DiscoveryState? discovery, bool emitEntries)
    {
        var watch = Stopwatch.StartNew();
        var frames = new Stack<DirectoryFrame>();
        var batch = new List<IndexedEntry>(settings.Options.BatchSize);
        var errors = new List<ScanError>();
        var pending = new List<PendingScope>();
        var costs = new List<DirectoryScanCost>(64);
        var diagnostics = new ScanDiagnosticsCollector(settings.Options);
        long entries = 0, directories = 0, excluded = 0, links = 0, errorCount = 0;
        long deferralGeneration = 0;
        var stopRequested = false;
        var status = ScanStatus.Completed;
        var lastProgress = TimeSpan.Zero;
        var lastThrottle = TimeSpan.Zero;
        var sinceThrottle = 0;
        var throttleEvery = Math.Clamp(settings.Options.MaxEntriesPerSecond / 20, 1, 64);

        ScanProgress Snapshot() => new(entries, directories, excluded, links, errorCount, watch.Elapsed)
        {
            PendingDirectories = pending.Count
        };

        void ReportProgress(bool force = false)
        {
            if (force || watch.Elapsed - lastProgress >= TimeSpan.FromMilliseconds(250))
            {
                lastProgress = watch.Elapsed;
                var snapshot = Snapshot();
                progress?.Report(snapshot);
                discovery?.Report(snapshot);
            }
        }

        void RecordError(string path, string message)
        {
            errorCount++;
            if (errors.Count < settings.Options.MaxRecordedErrors)
                errors.Add(new ScanError(path, message));
        }
        Action<ScanError> recordExclusionError = error => RecordError(error.Path, error.Message);

        void Defer(PendingScope deferred)
        {
            deferralGeneration++;
            if (pending.Any(existing => PathRules.IsWithin(deferred.ScopePath, existing.ScopePath)))
                return;
            if (pending.Count >= settings.Options.Deferral.MaxPendingScopes)
            {
                pending.Clear();
                pending.Add(settings.MakePending(scope, root, DeferralReason.PendingLimit));
                stopRequested = true;
                return;
            }
            pending.Add(deferred);
        }

        bool BudgetExhausted()
        {
            if (stopRequested)
                return true;
            if (settings.OnDemand)
                return false;
            DeferralReason? reason = settings.Options.Deferral.EntryBudget is { } entryBudget && entries >= entryBudget
                ? DeferralReason.EntryBudget
                : settings.Options.Deferral.TimeBudget is { } timeBudget && watch.Elapsed >= timeBudget
                    ? DeferralReason.TimeBudget : null;
            if (reason is null)
                return false;
            // This scope covers all active frames and unvisited siblings. A partial frontier
            // list would otherwise grow with tree width and could forget unvisited work.
            pending.Clear();
            pending.Add(settings.MakePending(scope, root, reason.Value));
            deferralGeneration++;
            stopRequested = true;
            return true;
        }

        async Task SaveCompletedCostAsync(DirectoryFrame frame)
        {
            if (writeCosts is null || frame.StartingErrors != errorCount
                || frame.StartingDeferrals != deferralGeneration || stopRequested)
                return;
            costs.Add(new DirectoryScanCost(frame.Path, entries - frame.StartingEntries,
                watch.Elapsed - frame.Started, DateTimeOffset.UtcNow));
            if (costs.Count >= 64)
            {
                await writeCosts(costs.ToArray(), cancellationToken).ConfigureAwait(false);
                costs.Clear();
            }
        }

        async Task WaitWithinBudgetAsync(TimeSpan delay)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!settings.OnDemand && settings.Options.Deferral.TimeBudget is { } timeBudget)
            {
                var remaining = timeBudget - watch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    return;
                if (delay > remaining)
                    delay = remaining;
            }
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        async Task ThrottleAsync(bool force = false)
        {
            if (sinceThrottle == 0)
                return;
            if (!force && sinceThrottle < throttleEvery)
                return;

            var period = TimeSpan.FromSeconds((double)sinceThrottle / settings.Options.MaxEntriesPerSecond);
            var delay = period - (watch.Elapsed - lastThrottle);
            if (delay > TimeSpan.Zero)
                await WaitWithinBudgetAsync(delay).ConfigureAwait(false);
            // Slow network operations do not accumulate an unlimited allowance for a later burst.
            lastThrottle = watch.Elapsed;
            sinceThrottle = 0;
        }

        async Task OpenDirectoryAsync(string path, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BudgetExhausted())
                return;
            var started = watch.Elapsed;
            if (settings.Options.DirectoryDelay > TimeSpan.Zero)
                await WaitWithinBudgetAsync(settings.Options.DirectoryDelay).ConfigureAwait(false);
            if (BudgetExhausted())
                return;

            // Keep the catch local to source operations: a failed database callback must propagate.
            try
            {
                // Check again when entering; a directory can have changed since its parent was read.
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    RecordError(path, "The directory became a reparse point before it could be scanned.");
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (BudgetExhausted())
                    return;

                var enumerable = new FileSystemEnumerable<ScanItem>(path,
                    (ref FileSystemEntry entry) => settings.Transform(ref entry),
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        AttributesToSkip = 0,
                        ReturnSpecialDirectories = false,
                        BufferSize = settings.Options.EnumerationBufferSize
                    });
                frames.Push(new DirectoryFrame(path, depth, enumerable.GetEnumerator(),
                    entries, errorCount, deferralGeneration, started));
                directories++;
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                RecordError(path, exception.Message);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Check all configured ancestors before opening a scoped refresh. A scope under an
            // excluded folder must not bypass pruning just because its parent isn't enumerated.
            var excludedScope = settings.Exclusions.MatchScope(scope, root);
            settings.Exclusions.DrainErrors(recordExclusionError);
            if (excludedScope is not null)
            {
                excluded++;
                diagnostics.Exclude(excludedScope);
            }
            else if (settings.GetDeferral(scope, root, checkAncestors: true) is { } deferredScope)
            {
                Defer(deferredScope);
            }
            else if (!BudgetExhausted())
            {
                var ancestorsSafe = true;
                try
                {
                    PathRules.EnsureNoReparseAncestors(scope);
                }
                catch (Exception exception) when (IsFileSystemError(exception))
                {
                    RecordError(scope, exception.Message);
                    ancestorsSafe = false;
                }

                var inspectScope = ancestorsSafe && discovery is not null;
                DirectoryCandidate? scopeCandidate = null;
                if (inspectScope)
                {
                    try
                    {
                        var info = new DirectoryInfo(scope);
                        info.Refresh();
                        if (!info.Exists)
                            throw new DirectoryNotFoundException(
                                "The discovery scope disappeared before its metadata could be read.");
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException(
                                "The discovery scope became a reparse point before its metadata could be read.");
                        scopeCandidate = new DirectoryCandidate(scope,
                            string.IsNullOrEmpty(info.Name) ? scope : info.Name,
                            info.Parent?.FullName, 0,
                            new DateTimeOffset(info.CreationTimeUtc),
                            new DateTimeOffset(info.LastWriteTimeUtc));
                    }
                    catch (Exception exception) when (IsFileSystemError(exception))
                    {
                        RecordError(scope, exception.Message);
                        ancestorsSafe = false;
                    }
                }

                var scopeDecision = DirectoryTraversalDecision.Continue;
                if (scopeCandidate is not null)
                {
                    diagnostics.ObserveDirectory(scopeCandidate.FullPath, scopeCandidate.Name);
                    scopeDecision = discovery!.Inspect(scopeCandidate);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var enterScope = ancestorsSafe
                    && scopeDecision == DirectoryTraversalDecision.Continue;
                if (scopeDecision == DirectoryTraversalDecision.ExcludeSubtree)
                    excluded++;
                if (enterScope)
                    await OpenDirectoryAsync(scope, 0).ConfigureAwait(false);

                var scopeWasInspectedAndPruned = scopeCandidate is not null
                    && scopeDecision == DirectoryTraversalDecision.SkipDescendants;
                if (emitEntries && (frames.Count > 0 || scopeWasInspectedAndPruned)
                    && !PathRules.Comparer.Equals(scope, root) && !BudgetExhausted())
                {
                    IndexedEntry? scopeEntry = null;
                    try
                    {
                        // A scoped refresh must update this folder's dates as well as its
                        // children. This is one extra metadata read per refresh, not per file.
                        var info = new DirectoryInfo(scope);
                        info.Refresh();
                        if (!info.Exists)
                            throw new DirectoryNotFoundException("The refresh scope disappeared before its metadata could be read.");
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("The refresh scope became a reparse point before its metadata could be read.");
                        scopeEntry = new IndexedEntry(scope, info.Name, Path.GetDirectoryName(scope)!,
                            EntryKind.Directory, null,
                            new DateTimeOffset(info.CreationTimeUtc), new DateTimeOffset(info.LastWriteTimeUtc));
                    }
                    catch (Exception exception) when (IsFileSystemError(exception))
                    {
                        RecordError(scope, exception.Message);
                    }

                    if (scopeEntry is not null)
                    {
                        diagnostics.ObserveDirectory(scopeEntry.FullPath, scopeEntry.Name);
                        entries++;
                        sinceThrottle++;
                        await ThrottleAsync().ConfigureAwait(false);
                        batch.Add(scopeEntry);
                        if (batch.Count >= settings.Options.BatchSize)
                        {
                            await writeBatch(batch.ToArray(), cancellationToken).ConfigureAwait(false);
                            batch.Clear();
                        }
                    }
                }

                while (!stopRequested && frames.TryPeek(out var frame))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (BudgetExhausted())
                        break;
                    ScanItem item;
                    bool hasNext;
                    try
                    {
                        hasNext = frame.Enumerator.MoveNext();
                        item = hasNext ? frame.Enumerator.Current : default;
                    }
                    catch (Exception exception) when (IsFileSystemError(exception))
                    {
                        RecordError(frame.Path, exception.Message);
                        frames.Pop().Dispose();
                        continue;
                    }
                    settings.Exclusions.DrainErrors(recordExclusionError);

                    if (!hasNext)
                    {
                        frames.Pop().Dispose();
                        // A database/cost callback failure must not be mistaken for a
                        // source directory failure and swallowed by the catch above.
                        await SaveCompletedCostAsync(frame).ConfigureAwait(false);
                        continue;
                    }

                    // Count excluded files too: ignoring their DB rows doesn't remove the cost
                    // of enumerating the directory, and must not evade throttling.
                    entries++;
                    sinceThrottle++;
                    await ThrottleAsync().ConfigureAwait(false);
                    ReportProgress();
                    if (item.Kind == EntryKind.Directory)
                        diagnostics.ObserveDirectory(item.Path, item.Name);
                    if (item.Exclusion is { } exclusion)
                    {
                        excluded++;
                        diagnostics.Exclude(exclusion);
                        continue;
                    }
                    if (item.IsLink)
                    {
                        links++;
                        continue;
                    }

                    var indexed = item.Entry!;
                    var deferred = indexed.Kind == EntryKind.Directory
                        ? settings.GetDeferral(indexed.FullPath, root, checkAncestors: false) : null;
                    if (deferred is not null)
                        indexed = indexed with { CoveragePending = true };

                    var traversalDecision = DirectoryTraversalDecision.Continue;
                    if (indexed.Kind == EntryKind.Directory && discovery is not null)
                    {
                        traversalDecision = discovery.Inspect(new DirectoryCandidate(
                            indexed.FullPath, indexed.Name, indexed.ParentPath,
                            frame.Depth + 1, indexed.CreatedUtc, indexed.ModifiedUtc)
                        {
                            CoveragePending = indexed.CoveragePending,
                        });
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (traversalDecision == DirectoryTraversalDecision.ExcludeSubtree)
                    {
                        excluded++;
                        continue;
                    }

                    // Profile exclusions take precedence over index deferral: an intentionally
                    // omitted subtree must not leave a pending scope behind.
                    if (deferred is not null)
                        Defer(deferred);
                    if (emitEntries)
                    {
                        batch.Add(indexed);
                        if (batch.Count >= settings.Options.BatchSize)
                        {
                            await writeBatch(batch.ToArray(), cancellationToken).ConfigureAwait(false);
                            batch.Clear();
                        }
                    }

                    if (indexed.Kind == EntryKind.Directory && deferred is null
                        && traversalDecision == DirectoryTraversalDecision.Continue
                        && !stopRequested)
                    {
                        if (frame.Depth >= settings.Options.MaxDepth)
                        {
                            RecordError(indexed.FullPath,
                                "The maximum traversal depth was reached; descendants were not scanned.");
                        }
                        else
                        {
                            await OpenDirectoryAsync(indexed.FullPath, frame.Depth + 1).ConfigureAwait(false);
                        }
                    }
                }
            }

            await ThrottleAsync(force: true).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (batch.Count > 0)
            {
                await writeBatch(batch.ToArray(), cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }
            if (costs.Count > 0 && writeCosts is not null)
            {
                await writeCosts(costs.ToArray(), cancellationToken).ConfigureAwait(false);
                costs.Clear();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (errorCount != 0)
                status = ScanStatus.Partial;
            else if (pending.Count != 0)
                status = ScanStatus.Deferred;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = ScanStatus.Cancelled;
            // Previously delivered batches are staging data; IndexingEngine discards that run.
            batch.Clear();
            costs.Clear();
            pending.Clear();
        }
        finally
        {
            while (frames.TryPop(out var frame))
                frame.Dispose();
        }

        ReportProgress(force: true);
        return new ScanReport(scanId, root, scope, status, Snapshot(), errors.AsReadOnly())
        {
            PendingScopes = pending.AsReadOnly(),
            Diagnostics = diagnostics.Snapshot()
        };
    }

    private static bool IsFileSystemError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private readonly record struct ScanItem(string Path, string Name, EntryKind Kind,
        IndexedEntry? Entry, ExcludedEntry? Exclusion, bool IsLink);

    private sealed class DirectoryFrame(string path, int depth, IEnumerator<ScanItem> enumerator,
        long startingEntries, long startingErrors, long startingDeferrals, TimeSpan started) : IDisposable
    {
        public string Path { get; } = path;
        public int Depth { get; } = depth;
        public IEnumerator<ScanItem> Enumerator { get; } = enumerator;
        public long StartingEntries { get; } = startingEntries;
        public long StartingErrors { get; } = startingErrors;
        public long StartingDeferrals { get; } = startingDeferrals;
        public TimeSpan Started { get; } = started;
        public void Dispose() => Enumerator.Dispose();
    }

    private sealed class DiscoveryState(
        Func<DirectoryCandidate, DirectoryTraversalDecision> inspectDirectory,
        IProgress<DirectoryDiscoveryProgress>? progress)
    {
        private long _directories;
        private long _prunedDirectories;

        public DirectoryDiscoveryProgress LastProgress { get; private set; } =
            new(0, 0, 0, 0, 0, 0, TimeSpan.Zero);

        public DirectoryTraversalDecision Inspect(DirectoryCandidate candidate)
        {
            _directories++;
            return inspectDirectory(candidate) switch
            {
                DirectoryTraversalDecision.Continue => DirectoryTraversalDecision.Continue,
                DirectoryTraversalDecision.SkipDescendants =>
                    Prune(DirectoryTraversalDecision.SkipDescendants),
                DirectoryTraversalDecision.ExcludeSubtree =>
                    Prune(DirectoryTraversalDecision.ExcludeSubtree),
                var decision => throw new InvalidOperationException(
                    $"Unsupported directory traversal decision: {decision}.")
            };
        }

        public void Report(ScanProgress scan)
        {
            LastProgress = new DirectoryDiscoveryProgress(scan.Entries, _directories,
                scan.ExcludedEntries, scan.SkippedLinks, _prunedDirectories,
                scan.ErrorCount, scan.Elapsed);
            progress?.Report(LastProgress);
        }

        private DirectoryTraversalDecision Prune(DirectoryTraversalDecision decision)
        {
            _prunedDirectories++;
            return decision;
        }
    }

    private sealed class Settings(ScanOptions options, ExclusionMatcher exclusions,
        HashSet<string> deferredNames, string[] deferredPaths,
        IReadOnlyDictionary<string, DirectoryScanCost> costHints, bool onDemand)
    {
        public ScanOptions Options { get; } = options;
        public ExclusionMatcher Exclusions { get; } = exclusions;
        public bool OnDemand { get; } = onDemand;

        public static Settings Create(ScanOptions options, string root,
            IReadOnlyDictionary<string, DirectoryScanCost> costHints, bool onDemand)
        {
            options.Validate(root);
            var deferredPaths = options.Deferral.Paths.Select(PathRules.Normalize)
                .Distinct(PathRules.Comparer).ToArray();
            return new Settings(options, new ExclusionMatcher(options),
                new HashSet<string>(options.Deferral.DirectoryNames, PathRules.Comparer), deferredPaths,
                costHints, onDemand);
        }

        public PendingScope MakePending(string path, string root, DeferralReason reason)
        {
            costHints.TryGetValue(path, out var cost);
            return new PendingScope(root, path, reason, cost?.Entries, cost?.Duration, DateTimeOffset.UtcNow);
        }

        public PendingScope? GetDeferral(string path, string root, bool checkAncestors)
        {
            if (OnDemand)
                return null;
            if (deferredPaths.Any(deferredPath => PathRules.IsWithin(path, deferredPath))
                || (checkAncestors ? HasAncestorName(path, root, deferredNames)
                    : deferredNames.Contains(Path.GetFileName(path))))
                return MakePending(path, root, DeferralReason.ExplicitRule);

            // A full-root history includes every branch. Deferring it would prevent all
            // future normal scans from discovering smaller or newly added branches.
            if (PathRules.Comparer.Equals(path, root) || !costHints.TryGetValue(path, out var cost))
                return null;
            if (Options.Deferral.HistoricalEntryThreshold is { } entries && cost.Entries >= entries)
                return MakePending(path, root, DeferralReason.HistoricalEntryCount);
            if (Options.Deferral.HistoricalDurationThreshold is { } duration && cost.Duration >= duration)
                return MakePending(path, root, DeferralReason.HistoricalDuration);
            return null;
        }

        private static bool HasAncestorName(string scope, string root, HashSet<string> names)
        {
            for (var current = scope; PathRules.IsWithin(current, root);)
            {
                if (names.Contains(Path.GetFileName(current)))
                    return true;
                if (PathRules.Comparer.Equals(current, root))
                    break;
                var parent = Path.GetDirectoryName(current);
                if (parent is null)
                    break;
                current = parent;
            }
            return false;
        }

        public ScanItem Transform(ref FileSystemEntry entry)
        {
            var path = entry.ToFullPath();
            var name = entry.FileName.ToString();
            var directory = entry.IsDirectory;
            var kind = directory ? EntryKind.Directory : EntryKind.File;
            var attributes = entry.Attributes;
            if (Exclusions.Match(path, name, kind) is { } exclusion)
                return new ScanItem(path, name, kind, null, exclusion, IsLink: false);

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return new ScanItem(path, name, kind, null, null, IsLink: true);

            // On Windows these fields come from native directory enumeration data, avoiding
            // FileInfo construction and additional metadata requests for every file.
            return new ScanItem(path, name, kind, new IndexedEntry(path, name, entry.Directory.ToString(),
                kind,
                directory ? null : entry.Length, entry.CreationTimeUtc, entry.LastWriteTimeUtc),
                null, IsLink: false);
        }

    }
}
