using FindEverything.Engine;
using FindEverything.Server.Models;
using FindEverything.Server.Persistence;

namespace FindEverything.Server.Services;

/// <summary>
/// Owns durable job execution independently of HTTP requests. Only this worker
/// enters an indexing engine, so all registered drives share one scan slot.
/// </summary>
public sealed class ScanCoordinator(
    ServerRepository repository,
    ServerPaths paths,
    IMetadataScanner scanner,
    ServerOptions options,
    TimeProvider timeProvider,
    ILogger<ScanCoordinator> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _activeGate = new();
    private Guid? _activeJobId;
    private CancellationTokenSource? _activeCancellation;
    private IAsyncDisposable? _instanceLease;
    private int _accepting;
    private int _stopping;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _instanceLease = await paths.AcquireInstanceLeaseAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await repository.RecoverInterruptedJobsAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _accepting, 1);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _accepting, 0);
            await ReleaseLeaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<EnqueueResult> EnqueueAsync(Guid profileId, string? relativeScope,
        bool onDemand, string requestedBy, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _accepting) == 0)
            throw new ServerConflictException("The server is not accepting new scan jobs.");
        var result = await repository.EnqueueAsync(profileId, relativeScope, onDemand,
            requestedBy, cancellationToken).ConfigureAwait(false);
        WakeWorker();
        return result;
    }

    public async Task<ScanJob?> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await repository.RequestCancellationAsync(jobId, cancellationToken).ConfigureAwait(false);
        lock (_activeGate)
        {
            if (_activeJobId == jobId)
                _activeCancellation?.Cancel();
        }
        WakeWorker();
        return job;
    }

    /// <summary>Exposed for deterministic host tests; claiming schedules is atomic in storage.</summary>
    public async Task ProcessDueSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var due = await repository.ListDueSchedulesAsync(now, cancellationToken).ConfigureAwait(false);
        foreach (var schedule in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!schedule.Definition.Enabled || schedule.NextDueUtc is not { } expectedDue) continue;
            try
            {
                // Advancing to the first future occurrence intentionally collapses
                // missed intervals into one job, rather than replaying old work.
                var nextDue = ScheduleCalculator.GetNextDueUtc(schedule.Definition, now);
                var accepted = await repository.EnqueueScheduledAsync(schedule.Id, schedule.Revision,
                    expectedDue, nextDue, now, cancellationToken).ConfigureAwait(false);
                if (accepted is not null) WakeWorker();
            }
            catch (ServerQueueFullException)
            {
                // Keep the original due occurrence durable and retry after work
                // leaves the queue; never lose it by advancing a full queue.
                logger.LogWarning("Scan queue is full; schedule {ScheduleId} remains due.", schedule.Id);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogError(error, "Could not enqueue due schedule {ScheduleId}.", schedule.Id);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(RunWorkerAsync(stoppingToken), RunSchedulerAsync(stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Volatile.Write(ref _accepting, 0);
            // Release only after the native scan has actually stopped. A stalled
            // SMB call must not let another process start on the same data dir.
            await ReleaseLeaseAsync().ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _accepting, 0);
        Volatile.Write(ref _stopping, 1);
        lock (_activeGate) _activeCancellation?.Cancel();
        WakeWorker();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ScanJob? job;
            try
            {
                job = await repository.ClaimNextJobAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Could not claim the next scan job.");
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken).ConfigureAwait(false);
                continue;
            }
            if (job is null)
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                continue;
            }
            await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunSchedulerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessDueSchedulesAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Could not read due schedules."); }
            await Task.Delay(TimeSpan.FromSeconds(options.SchedulerPollSeconds), timeProvider,
                stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunJobAsync(ScanJob job, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_activeGate)
        {
            _activeJobId = job.Id;
            _activeCancellation = cancellation;
        }
        var latest = new LatestProgress();
        using var progressCancellation = new CancellationTokenSource();
        var progressTask = FlushProgressAsync(job.Id, latest, progressCancellation.Token);
        ScanReport? report = null;
        string? errorMessage = null;
        JobStatus status;
        try
        {
            // Cancel can arrive between durable claim and installing the active
            // token. Recheck storage after installing it to close that race.
            var current = await repository.GetJobAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            if (current?.CancellationRequested == true) cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            ValidateSnapshot(job.Snapshot);
            await using var store = new SqliteIndexStore(paths.GetIndexPath(job.Snapshot.Profile.Id));
            var engine = new IndexingEngine(scanner, store);
            report = await engine.ScanAsync(new ScanRequest(job.Snapshot.RootPath)
            {
                ScopePath = job.Snapshot.ScopePath,
                Options = job.Snapshot.Profile.Definition.Options,
                OnDemand = job.Snapshot.OnDemand
            }, latest, cancellation.Token).ConfigureAwait(false);
            status = IsStopping(stoppingToken) ? JobStatus.Interrupted : report.Status switch
            {
                ScanStatus.Completed => JobStatus.Completed,
                ScanStatus.Partial => JobStatus.Partial,
                ScanStatus.Deferred => JobStatus.Deferred,
                ScanStatus.Cancelled => JobStatus.Cancelled,
                _ => JobStatus.Failed
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status = IsStopping(stoppingToken) ? JobStatus.Interrupted : JobStatus.Cancelled;
        }
        catch (Exception error)
        {
            status = IsStopping(stoppingToken) ? JobStatus.Interrupted : JobStatus.Failed;
            errorMessage = error.Message;
            logger.LogError(error, "Scan job {JobId} failed.", job.Id);
        }
        finally
        {
            progressCancellation.Cancel();
            try { await progressTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (progressCancellation.IsCancellationRequested) { }
            lock (_activeGate)
            {
                _activeJobId = null;
                _activeCancellation = null;
            }
        }
        try
        {
            // Finalization does not use the cancelled job or host token. A
            // graceful stop still records what stopped and keeps old index data.
            if (report is null && latest.Value is { } value)
                await repository.UpdateJobProgressAsync(job.Id, value, CancellationToken.None).ConfigureAwait(false);
            await repository.CompleteJobAsync(job.Id, status, report, errorMessage,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Could not persist completion of scan job {JobId}.", job.Id);
            try
            {
                // An unusually large diagnostic document must not leave a
                // completed job Running and permanently block the whole queue.
                await repository.CompleteJobAsync(job.Id, JobStatus.Failed, null,
                    "The scan stopped, but its report could not be persisted: " + error.Message,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception fallbackError)
            {
                // If even minimal completion cannot be written (for example a
                // full local disk), restart recovers the job as Interrupted.
                logger.LogError(fallbackError, "Could not persist minimal failure for scan job {JobId}.", job.Id);
            }
        }
    }

    private bool IsStopping(CancellationToken token) =>
        token.IsCancellationRequested || Volatile.Read(ref _stopping) != 0;

    private void ValidateSnapshot(JobSnapshot snapshot)
    {
        var configuredRoot = paths.ResolveProfileRoot(snapshot.Profile.Definition);
        if (!string.Equals(PathRules.Normalize(snapshot.RootPath), configuredRoot, PathRules.Comparison))
            throw new ArgumentException("The queued source no longer matches the configured source root.");
        if (!PathRules.IsWithin(snapshot.ScopePath, snapshot.RootPath))
            throw new ArgumentException("The queued scan scope lies outside its source root.");
        if (PathRules.IsWithin(paths.DataDirectory, snapshot.RootPath) || PathRules.IsWithin(snapshot.RootPath, paths.DataDirectory))
            throw new ArgumentException("The scan source and server data directory must not overlap.");
        PathRules.EnsureNoReparseAncestors(snapshot.RootPath);
        PathRules.EnsureNoReparseAncestors(snapshot.ScopePath);
        PathRules.EnsureNoReparseAncestors(paths.DataDirectory);
    }

    private async Task FlushProgressAsync(Guid jobId, LatestProgress latest, CancellationToken cancellationToken)
    {
        ScanProgress? written = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken).ConfigureAwait(false);
            var value = latest.Value;
            if (value is null || value == written) continue;
            try
            {
                await repository.UpdateJobProgressAsync(jobId, value, cancellationToken).ConfigureAwait(false);
                written = value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.LogError(error, "Could not persist progress for scan job {JobId}.", jobId); }
        }
    }

    private void WakeWorker()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task ReleaseLeaseAsync()
    {
        var lease = Interlocked.Exchange(ref _instanceLease, null);
        if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class LatestProgress : IProgress<ScanProgress>
    {
        private ScanProgress? _value;
        public ScanProgress? Value => Volatile.Read(ref _value);
        public void Report(ScanProgress value) => Volatile.Write(ref _value, value);
    }
}
