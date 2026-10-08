using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using FindEverything.Engine;
using FindEverything.Server;
using FindEverything.Server.Api;
using FindEverything.Server.Models;
using Xunit;

namespace FindEverything.Server.Tests;

public sealed class JobCoordinationTests
{
    [Fact]
    public async Task JobsAreSerializedCoalescedBoundedAndQueuedCancellationPreventsScanning()
    {
        using var workspace = new ServerWorkspace();
        var scanner = new BlockingScanner();
        await using var factory = new ServerTestFactory(workspace, scanner);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition());
        var otherProfile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition("Separate index"));
        var first = await ServerApiTests.EnqueueAsync(admin, profile.Id);
        var firstInvocation = await scanner.NextAsync();
        var duplicate = await ServerApiTests.EnqueueAsync(admin, profile.Id);
        Assert.True(duplicate.Coalesced);
        Assert.Equal(first.Job.Id, duplicate.Job.Id);
        var queued = await ServerApiTests.EnqueueAsync(admin, profile.Id, "Work");
        var following = await ServerApiTests.EnqueueAsync(admin, otherProfile.Id, "Archive");
        using var full = await admin.PostAsJsonAsync("/api/admin/jobs", new EnqueueScanRequest { ProfileId = profile.Id, RelativeScope = "Another" }, ServerTestFactory.Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        using var cancelledQueuedResponse = await admin.PostAsync($"/api/admin/jobs/{queued.Job.Id}/cancel", null);
        Assert.Equal(JobStatus.Cancelled, (await ServerTestFactory.ReadAsync<ScanJob>(cancelledQueuedResponse)).Status);
        firstInvocation.Release();
        Assert.Equal(JobStatus.Completed, (await ServerTestFactory.WaitForJobAsync(admin, first.Job.Id)).Status);
        var nextInvocation = await scanner.NextAsync();
        Assert.Equal(Path.Combine(workspace.SourcePath, "Archive"), nextInvocation.Request.ScopePath);
        nextInvocation.Release();
        Assert.Equal(JobStatus.Completed, (await ServerTestFactory.WaitForJobAsync(admin, following.Job.Id)).Status);
        Assert.Equal(2, scanner.StartedCount);
        Assert.Equal(1, scanner.MaxConcurrent);
    }

    [Fact]
    public async Task RunningCancellationDiscardsStagedRowsAndKeepsPreviouslyPublishedIndex()
    {
        using var workspace = new ServerWorkspace();
        workspace.WriteFile("original.txt");
        var scanner = new BlockingScanner();
        await using var factory = new ServerTestFactory(workspace, scanner);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition());
        await using (var store = new SqliteIndexStore(new ServerPaths(workspace.Options).GetIndexPath(profile.Id)))
        {
            var initial = await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(new ScanRequest(workspace.SourcePath) { Options = ServerWorkspace.FastOptions });
            Assert.Equal(ScanStatus.Completed, initial.Status);
        }
        var job = await ServerApiTests.EnqueueAsync(admin, profile.Id);
        await scanner.NextAsync(); // The fake has already staged its replacement row.
        using var cancel = await admin.PostAsync($"/api/admin/jobs/{job.Job.Id}/cancel", null);
        Assert.True(cancel.IsSuccessStatusCode);
        var cancelled = await ServerTestFactory.WaitForJobAsync(admin, job.Job.Id);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.CancellationRequested);
        using var search = await admin.GetAsync($"/api/profiles/{profile.Id}/search");
        var result = await ServerTestFactory.ReadAsync<ProfileSearchResponse>(search);
        Assert.Equal(1, result.TotalCount);
        var json = await search.Content.ReadAsStringAsync();
        Assert.Contains("original.txt", json);
        Assert.DoesNotContain("staged-replacement", json);
    }

    [Fact]
    public async Task EditingQueuedProfilesDoesNotChangeQueuedSettingsSnapshot()
    {
        using var workspace = new ServerWorkspace();
        var scanner = new BlockingScanner();
        await using var factory = new ServerTestFactory(workspace, scanner);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition() with
        {
            Options = ServerWorkspace.FastOptions with { ExcludedDirectoryNames = ["old-rule"] }
        });
        var first = await ServerApiTests.EnqueueAsync(admin, profile.Id);
        var firstInvocation = await scanner.NextAsync();
        var queued = await ServerApiTests.EnqueueAsync(admin, profile.Id, "Work");
        using var edit = await admin.PutAsJsonAsync($"/api/admin/profiles/{profile.Id}", new ProfileUpdate(profile.Revision, profile.Definition with
        {
            Options = ServerWorkspace.FastOptions with { ExcludedDirectoryNames = ["new-rule"] }
        }), ServerTestFactory.Json);
        Assert.True(edit.IsSuccessStatusCode);
        firstInvocation.Release();
        await ServerTestFactory.WaitForJobAsync(admin, first.Job.Id);
        var secondInvocation = await scanner.NextAsync();
        Assert.Equal("old-rule", Assert.Single(secondInvocation.Request.Options.ExcludedDirectoryNames));
        secondInvocation.Release();
        Assert.Equal(JobStatus.Completed, (await ServerTestFactory.WaitForJobAsync(admin, queued.Job.Id)).Status);
    }

    [Fact]
    public async Task GracefulServerStopRecordsRunningWorkAsInterrupted()
    {
        using var workspace = new ServerWorkspace();
        var scanner = new BlockingScanner();
        var factory = new ServerTestFactory(workspace, scanner);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition());
        var job = await ServerApiTests.EnqueueAsync(admin, profile.Id);
        await scanner.NextAsync();
        await factory.DisposeAsync();
        await using var repository = ServerRepositoryTests.Repository(workspace);
        var persisted = (await repository.GetJobAsync(job.Job.Id))!;
        Assert.Equal(JobStatus.Interrupted, persisted.Status);
        Assert.NotNull(persisted.FinishedUtc);
        Assert.False(persisted.CancellationRequested);
    }

    private sealed class BlockingScanner : IMetadataScanner
    {
        private readonly Channel<Invocation> calls = Channel.CreateUnbounded<Invocation>();
        private int concurrent;
        private int maxConcurrent;
        private int started;
        public int MaxConcurrent => Volatile.Read(ref maxConcurrent);
        public int StartedCount => Volatile.Read(ref started);

        public async Task<Invocation> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await calls.Reader.ReadAsync(timeout.Token);
        }

        public async Task<ScanReport> ScanAsync(ScanRequest request, Guid scanId,
            Func<IReadOnlyList<IndexedEntry>, CancellationToken, Task> writeBatch,
            IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref started);
            var active = Interlocked.Increment(ref concurrent);
            if (active > maxConcurrent) Interlocked.Exchange(ref maxConcurrent, active);
            try
            {
                var path = Path.Combine(request.ScopePath ?? request.RootPath, "staged-replacement.txt");
                await writeBatch([new IndexedEntry(path, Path.GetFileName(path), Path.GetDirectoryName(path)!, EntryKind.File, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)], cancellationToken);
                var value = new ScanProgress(1, 1, 0, 0, 0, TimeSpan.Zero);
                progress?.Report(value);
                var invocation = new Invocation(request);
                await calls.Writer.WriteAsync(invocation, cancellationToken);
                await invocation.Completion.Task.WaitAsync(cancellationToken);
                return new ScanReport(scanId, request.RootPath, request.ScopePath ?? request.RootPath, ScanStatus.Completed, value, []);
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }
    }

    private sealed class Invocation(ScanRequest request)
    {
        public ScanRequest Request { get; } = request;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Completion.TrySetResult();
    }
}
