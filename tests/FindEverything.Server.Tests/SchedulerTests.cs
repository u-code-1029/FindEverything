using FindEverything.Engine;
using FindEverything.Server;
using FindEverything.Server.Models;
using FindEverything.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Server.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task OverdueIntervalQueuesOneJobAndAdvancesAtomicallyAcrossReopen()
    {
        using var workspace = new ServerWorkspace();
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 37, 0, TimeSpan.Zero));
        await using var repository = ServerRepositoryTests.Repository(workspace, clock);
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        var anchor = clock.GetUtcNow().AddDays(-1).AddMinutes(-37);
        var definition = new ScheduleDefinition
        {
            ProfileId = profile.Id, Name = "Hourly", Kind = ScheduleKind.Interval, AnchorUtc = anchor, IntervalMinutes = 60
        };
        var schedule = await repository.CreateScheduleAsync(definition, anchor);
        using var coordinator = Coordinator(workspace, repository, clock);
        await coordinator.ProcessDueSchedulesAsync();
        await coordinator.ProcessDueSchedulesAsync();
        var job = Assert.Single((await repository.ListJobsAsync()).Items);
        Assert.Equal(schedule.Id, job.ScheduleId);
        Assert.Equal("scheduler", job.RequestedBy);
        var advanced = (await repository.GetScheduleAsync(schedule.Id))!.NextDueUtc;
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 13, 0, 0, TimeSpan.Zero), advanced);
        await using var reopened = ServerRepositoryTests.Repository(workspace, clock);
        using var afterRestart = Coordinator(workspace, reopened, clock);
        await afterRestart.ProcessDueSchedulesAsync();
        Assert.Single((await reopened.ListJobsAsync()).Items);
        Assert.Equal(advanced, (await reopened.GetScheduleAsync(schedule.Id))!.NextDueUtc);
    }

    [Fact]
    public async Task OnceScheduleIsConsumedAndDisabledOrDeletedSchedulesDoNotTrigger()
    {
        using var workspace = new ServerWorkspace();
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using var repository = ServerRepositoryTests.Repository(workspace, clock);
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        var definition = new ScheduleDefinition { ProfileId = profile.Id, Name = "Once", Kind = ScheduleKind.Once, AnchorUtc = clock.GetUtcNow() };
        var once = await repository.CreateScheduleAsync(definition, clock.GetUtcNow());
        var disabled = await repository.CreateScheduleAsync(definition with { Name = "Disabled", Enabled = false }, clock.GetUtcNow());
        var deleted = await repository.CreateScheduleAsync(definition with { Name = "Deleted" }, clock.GetUtcNow());
        await repository.DeleteScheduleAsync(deleted.Id, deleted.Revision);
        using var coordinator = Coordinator(workspace, repository, clock);
        await coordinator.ProcessDueSchedulesAsync();
        clock.Advance(TimeSpan.FromDays(7));
        await coordinator.ProcessDueSchedulesAsync();
        Assert.Single((await repository.ListJobsAsync()).Items);
        Assert.Null((await repository.GetScheduleAsync(once.Id))!.NextDueUtc);
        Assert.Equal(definition.AnchorUtc, (await repository.GetScheduleAsync(disabled.Id))!.NextDueUtc);
    }

    [Fact]
    public async Task FullQueueDoesNotLoseDueOccurrence()
    {
        using var workspace = new ServerWorkspace();
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using var repository = ServerRepositoryTests.Repository(workspace, clock);
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        await repository.EnqueueAsync(profile.Id, "first", false, "admin");
        await repository.EnqueueAsync(profile.Id, "second", false, "admin");
        var definition = new ScheduleDefinition { ProfileId = profile.Id, Name = "Once", Kind = ScheduleKind.Once, AnchorUtc = clock.GetUtcNow() };
        var schedule = await repository.CreateScheduleAsync(definition, clock.GetUtcNow());
        using var coordinator = Coordinator(workspace, repository, clock);
        await coordinator.ProcessDueSchedulesAsync();
        Assert.Equal(definition.AnchorUtc, (await repository.GetScheduleAsync(schedule.Id))!.NextDueUtc);
        var running = (await repository.ClaimNextJobAsync())!;
        await repository.CompleteJobAsync(running.Id, JobStatus.Completed);
        await coordinator.ProcessDueSchedulesAsync();
        Assert.Null((await repository.GetScheduleAsync(schedule.Id))!.NextDueUtc);
        Assert.Contains((await repository.ListJobsAsync()).Items, job => job.ScheduleId == schedule.Id);
    }

    [Fact]
    public async Task DisabledProfileDoesNotConsumeScheduleOrStartPreviouslyQueuedWork()
    {
        using var workspace = new ServerWorkspace();
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using var repository = ServerRepositoryTests.Repository(workspace, clock);
        var profile = await repository.CreateProfileAsync(workspace.ProfileDefinition());
        var queued = await repository.EnqueueAsync(profile.Id, null, false, "admin");
        var definition = new ScheduleDefinition { ProfileId = profile.Id, Name = "Once", Kind = ScheduleKind.Once, AnchorUtc = clock.GetUtcNow() };
        var schedule = await repository.CreateScheduleAsync(definition, clock.GetUtcNow());
        await repository.UpdateProfileAsync(profile.Id, profile.Revision, profile.Definition with { Enabled = false });
        using var coordinator = Coordinator(workspace, repository, clock);
        await coordinator.ProcessDueSchedulesAsync();
        Assert.Equal(definition.AnchorUtc, (await repository.GetScheduleAsync(schedule.Id))!.NextDueUtc);
        Assert.Null(await repository.ClaimNextJobAsync());
        Assert.Equal(JobStatus.Cancelled, (await repository.GetJobAsync(queued.Job.Id))!.Status);
    }

    private static ScanCoordinator Coordinator(ServerWorkspace workspace, FindEverything.Server.Persistence.ServerRepository repository, TimeProvider clock) => new(
        repository, new ServerPaths(workspace.Options), new FileSystemMetadataScanner(), workspace.Options,
        clock, NullLogger<ScanCoordinator>.Instance);

    private sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
