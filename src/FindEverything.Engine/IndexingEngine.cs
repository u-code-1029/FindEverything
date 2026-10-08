using System.Diagnostics;

namespace FindEverything.Engine;

public sealed class IndexingEngine(IMetadataScanner scanner, IIndexStore store)
{
    public async Task<ScanReport> ScanAsync(ScanRequest request,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = PathRules.Normalize(request.RootPath);
        ArgumentNullException.ThrowIfNull(request.Options);
        request.Options.Validate(root);
        var scope = PathRules.Normalize(request.ScopePath ?? root);
        if (!PathRules.IsWithin(scope, root))
            throw new ArgumentException("The refresh scope must be within its registered root.", nameof(request));
        var database = PathRules.Normalize(store.DatabasePath);
        // Validate paths before any index file, directory or writer lock is created.
        PathRules.EnsureNoReparseAncestors(root);
        PathRules.EnsureNoReparseAncestors(scope);
        PathRules.EnsureNoReparseAncestors(database);
        request = request with
        {
            RootPath = root,
            ScopePath = scope,
            Options = ExcludeIndexArtifactsWhenNeeded(request.Options, database, root)
        };

        await using var writer = await store.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var scanId = Guid.NewGuid();
        var timer = Stopwatch.StartNew();
        var latest = new ScanProgress(0, 0, 0, 0, 0, TimeSpan.Zero);
        var forwardingProgress = new DirectProgress(value =>
        {
            latest = value;
            progress?.Report(value);
        });
        var hints = request.OnDemand
            ? new Dictionary<string, DirectoryScanCost>(PathRules.Comparer)
            : await store.GetDirectoryCostsAsync(root, scope, request.Options.Deferral,
                cancellationToken).ConfigureAwait(false);
        request = request with
        {
            CostHints = hints,
            WriteCosts = (costs, token) => store.StageDirectoryCostsAsync(scanId, costs, token)
        };
        await store.BeginScanAsync(scanId, root, scope, cancellationToken).ConfigureAwait(false);
        try
        {
            var report = await scanner.ScanAsync(request, scanId,
                (batch, token) => store.StageAsync(scanId, batch, token),
                forwardingProgress, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                report = report with { Status = ScanStatus.Cancelled };
            if (report.Status == ScanStatus.Cancelled)
                await store.DiscardAsync(scanId, CancellationToken.None).ConfigureAwait(false);
            else
                await store.PublishAsync(report, cancellationToken).ConfigureAwait(false);
            return report;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.DiscardAsync(scanId, CancellationToken.None).ConfigureAwait(false);
            return new ScanReport(scanId, root, scope, ScanStatus.Cancelled,
                latest with { Elapsed = timer.Elapsed }, []);
        }
        catch
        {
            // Preserve the actual failure; orphan staging can be cleared next time
            // under the exclusive writer lease if cleanup itself is unavailable.
            try { await store.DiscardAsync(scanId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
            throw;
        }
    }

    private sealed class DirectProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    private static ScanOptions ExcludeIndexArtifactsWhenNeeded(
        ScanOptions options,
        string databasePath,
        string rootPath)
    {
        if (!PathRules.IsWithin(databasePath, rootPath))
            return options;

        // A LocalAppData index naturally lives below C:\ when the user scans the
        // complete system drive. Excluding the SQLite files is safer and more useful
        // than requiring a second physical drive. Keep the list exact so neighboring
        // application data remains searchable.
        var excludedPaths = options.ExcludedPaths
            .Concat(
            [
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
                databasePath + "-journal",
                databasePath + ".writer.lock",
            ])
            .Distinct(PathRules.Comparer)
            .ToArray();
        var updated = options with { ExcludedPaths = excludedPaths };
        updated.Validate(rootPath);
        return updated;
    }
}
