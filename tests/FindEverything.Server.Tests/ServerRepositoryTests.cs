using FindEverything.Engine;
using FindEverything.Server;
using FindEverything.Server.Models;
using FindEverything.Server.Persistence;
using Xunit;

namespace FindEverything.Server.Tests;

public sealed class ServerRepositoryTests
{
    [Fact]
    public async Task ProfilesAndJobsSurviveReopenAndQueuedSnapshotDoesNotChangeWithProfileEdits()
    {
        using var workspace = new ServerWorkspace();
        var repository = Repository(workspace);
        await repository.InitializeAsync();
        var initial = workspace.ProfileDefinition() with
        {
            Options = ServerWorkspace.FastOptions with { ExcludedDirectoryNames = ["Cache"] },
            Output = new OutputDefinition { Columns = [OutputColumn.Name], DefaultPageSize = 7 }
        };
        var profile = await repository.CreateProfileAsync(initial);
        var queued = await repository.EnqueueAsync(profile.Id, "Work", true, "admin");
        var updated = await repository.UpdateProfileAsync(profile.Id, profile.Revision, initial with
        {
            Name = "Changed", Options = ServerWorkspace.FastOptions with { ExcludedDirectoryNames = ["Temporary"] }, ReaderIds = []
        });
        Assert.Equal(2, updated.Revision);
        var reopened = Repository(workspace);
        await reopened.InitializeAsync();
        var persistedProfile = Assert.IsType<ScanProfile>(await reopened.GetProfileAsync(profile.Id));
        Assert.Equal("Changed", persistedProfile.Definition.Name);
        Assert.Empty(persistedProfile.Definition.ReaderIds);
        var persistedJob = Assert.IsType<ScanJob>(await reopened.GetJobAsync(queued.Job.Id));
        Assert.Equal(JobStatus.Queued, persistedJob.Status);
        Assert.Equal(1, persistedJob.Snapshot.Profile.Revision);
        Assert.Equal("Cache", Assert.Single(persistedJob.Snapshot.Profile.Definition.Options.ExcludedDirectoryNames));
        Assert.Equal(OutputColumn.Name, Assert.Single(persistedJob.Snapshot.Profile.Definition.Output.Columns));
        Assert.Equal(Path.Combine(workspace.SourcePath, "Work"), persistedJob.Snapshot.ScopePath);
        Assert.True(persistedJob.Snapshot.OnDemand);
    }

    [Fact]
    public async Task OptimisticRevisionRejectsLostUpdatesAndDeletion()
    {
        using var workspace = new ServerWorkspace();
        var repository = Repository(workspace);
        await repository.InitializeAsync();
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        await repository.UpdateProfileAsync(profile.Id, 1, profile.Definition with { Name = "new" });
        await Assert.ThrowsAsync<ServerConflictException>(() => repository.UpdateProfileAsync(profile.Id, 1, profile.Definition));
        await Assert.ThrowsAsync<ServerConflictException>(() => repository.DeleteProfileAsync(profile.Id, 1));
        Assert.Equal("new", (await repository.GetProfileAsync(profile.Id))!.Definition.Name);
    }

    [Fact]
    public async Task QueueCoalescesIdenticalSnapshotButKeepsDifferentScopesAndEnforcesBound()
    {
        using var workspace = new ServerWorkspace();
        var repository = Repository(workspace);
        await repository.InitializeAsync();
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        var first = await repository.EnqueueAsync(profile.Id, null, false, "one");
        var duplicate = await repository.EnqueueAsync(profile.Id, ".", false, "two");
        Assert.True(duplicate.Coalesced);
        Assert.Equal(first.Job.Id, duplicate.Job.Id);
        var second = await repository.EnqueueAsync(profile.Id, "Work", false, "three");
        Assert.False(second.Coalesced);
        await Assert.ThrowsAsync<ServerQueueFullException>(() => repository.EnqueueAsync(profile.Id, "Another", false, "four"));
        var claimed = Assert.IsType<ScanJob>(await repository.ClaimNextJobAsync());
        Assert.Equal(first.Job.Id, claimed.Id);
        Assert.Equal(JobStatus.Running, claimed.Status);
        // Running work no longer consumes a waiting queue slot.
        await repository.EnqueueAsync(profile.Id, "Another", false, "four");
        await repository.CompleteJobAsync(claimed.Id, JobStatus.Completed);
        Assert.Equal(second.Job.Id, (await repository.ClaimNextJobAsync())!.Id);
    }

    [Fact]
    public async Task RecoveryPreservesQueuedJobsAndMarksAbandonedRunningJobInterrupted()
    {
        using var workspace = new ServerWorkspace();
        var repository = Repository(workspace);
        await repository.InitializeAsync();
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        var abandoned = await repository.EnqueueAsync(profile.Id, "one", false, "admin");
        var waiting = await repository.EnqueueAsync(profile.Id, "two", false, "admin");
        Assert.Equal(abandoned.Job.Id, (await repository.ClaimNextJobAsync())!.Id);
        var reopened = Repository(workspace);
        await reopened.InitializeAsync();
        await reopened.RecoverInterruptedJobsAsync();
        Assert.Equal(JobStatus.Interrupted, (await reopened.GetJobAsync(abandoned.Job.Id))!.Status);
        Assert.Equal(JobStatus.Queued, (await reopened.GetJobAsync(waiting.Job.Id))!.Status);
        Assert.Equal(waiting.Job.Id, (await reopened.ClaimNextJobAsync())!.Id);
    }

    [Fact]
    public async Task CancellingQueuedJobPreventsItFromBeingClaimed()
    {
        using var workspace = new ServerWorkspace();
        var repository = Repository(workspace);
        await repository.InitializeAsync();
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        var queued = await repository.EnqueueAsync(profile.Id, null, false, "admin");
        var cancelled = Assert.IsType<ScanJob>(await repository.RequestCancellationAsync(queued.Job.Id));
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.CancellationRequested);
        Assert.Null(await repository.ClaimNextJobAsync());
    }

    [Fact]
    public async Task CompletedReportAndDiagnosticsPersistInControlDatabase()
    {
        using var workspace = new ServerWorkspace();
        var repository = Repository(workspace);
        await repository.InitializeAsync();
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        var queued = await repository.EnqueueAsync(profile.Id, null, false, "admin");
        await repository.ClaimNextJobAsync();
        var progress = new ScanProgress(12, 3, 1, 0, 0, TimeSpan.FromSeconds(1));
        var report = new ScanReport(Guid.NewGuid(), workspace.SourcePath, workspace.SourcePath, ScanStatus.Completed, progress, [])
        {
            Diagnostics = new ScanDiagnostics
            {
                ExcludedPaths = [new ExcludedEntry(Path.Combine(workspace.SourcePath, "Cache"), EntryKind.Directory, ExclusionReason.DirectoryName, "Cache")],
                RepeatedDirectoryNames = [new RepeatedDirectoryName("Common", 2, ["first", "second"])]
            }
        };
        await repository.CompleteJobAsync(queued.Job.Id, JobStatus.Completed, report);
        var reopened = Repository(workspace);
        await reopened.InitializeAsync();
        var persisted = (await reopened.GetJobAsync(queued.Job.Id))!.Report!;
        Assert.Equal(report.ScanId, persisted.ScanId);
        Assert.Equal(12, persisted.Progress.Entries);
        Assert.Equal("Cache", Assert.Single(persisted.Diagnostics.ExcludedPaths).Rule);
        Assert.Equal(2, Assert.Single(persisted.Diagnostics.RepeatedDirectoryNames).Count);
        Assert.True(File.Exists(new ServerPaths(workspace.Options).ControlDatabasePath));
        Assert.False(File.Exists(new ServerPaths(workspace.Options).GetIndexPath(profile.Id)));
    }

    internal static ServerRepository Repository(ServerWorkspace workspace, TimeProvider? clock = null)
    {
        var paths = new ServerPaths(workspace.Options);
        return new ServerRepository(paths, new ProfileValidator(paths), workspace.Options, clock);
    }

    [Fact]
    public async Task OversizedReportIsTruncatedWithoutLeavingRunningJobAndBlockingQueue()
    {
        using var workspace = new ServerWorkspace();
        await using var repository = Repository(workspace);
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        var first = await repository.EnqueueAsync(profile.Id, null, false, "admin");
        var following = await repository.EnqueueAsync(profile.Id, "following", false, "admin");
        await repository.ClaimNextJobAsync();
        var longPath = new string('a', 6 * 1024 * 1024);
        var progress = new ScanProgress(12, 4, 3, 0, 0, TimeSpan.FromSeconds(2));
        var report = new ScanReport(Guid.NewGuid(), workspace.SourcePath, workspace.SourcePath, ScanStatus.Completed, progress, [])
        {
            Diagnostics = new ScanDiagnostics
            {
                ExcludedPaths = Enumerable.Range(0, 3).Select(_ => new ExcludedEntry(longPath, EntryKind.Directory, ExclusionReason.DirectoryName, "rule")).ToArray()
            }
        };
        var completed = await repository.CompleteJobAsync(first.Job.Id, JobStatus.Completed, report);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.True(completed.ReportTruncated);
        Assert.Equal(progress, completed.Progress);
        Assert.True(completed.Report!.Diagnostics.ExcludedPaths.Count < 3);
        Assert.True(completed.Report.Diagnostics.OmittedExcludedPaths > 0);
        Assert.Equal(following.Job.Id, (await repository.ClaimNextJobAsync())!.Id);
    }
}
