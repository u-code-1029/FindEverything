using FindEverything.Engine;

namespace FindEverything.Server.Models;

public enum OutputColumn { Name, FullPath, ParentPath, Kind, SizeBytes, CreatedUtc, ModifiedUtc, CoveragePending }
public enum OutputFormat { Json, Csv }
public enum ScheduleKind { Once, Interval, Daily }
public enum JobStatus { Queued, Running, Completed, Partial, Deferred, Cancelled, Failed, Interrupted }

public sealed record OutputDefinition
{
    public IReadOnlyList<OutputColumn> Columns { get; init; } = [OutputColumn.Name, OutputColumn.FullPath, OutputColumn.Kind, OutputColumn.SizeBytes, OutputColumn.ModifiedUtc, OutputColumn.CoveragePending];
    public EntrySortField SortBy { get; init; } = EntrySortField.Name;
    public SortDirection SortDirection { get; init; } = SortDirection.Ascending;
    public int DefaultPageSize { get; init; } = 100;
    public OutputFormat Format { get; init; } = OutputFormat.Json;
}

public sealed record ProfileDefinition
{
    public required string Name { get; init; }
    public required string SourceId { get; init; }
    public string RelativeRoot { get; init; } = ".";
    public bool Enabled { get; init; } = true;
    public ScanOptions Options { get; init; } = new();
    public OutputDefinition Output { get; init; } = new();
    public IReadOnlyList<string> ReaderIds { get; init; } = [];
}

public sealed record ScanProfile(Guid Id, int Revision, ProfileDefinition Definition, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
public sealed record ProfileUpdate(int ExpectedRevision, ProfileDefinition Definition);

public sealed record ScheduleDefinition
{
    public required Guid ProfileId { get; init; }
    public required string Name { get; init; }
    public ScheduleKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset AnchorUtc { get; init; }
    public int? IntervalMinutes { get; init; }
    public TimeOnly? LocalTime { get; init; }
    public string? TimeZoneId { get; init; }
}

public sealed record ScanSchedule(Guid Id, int Revision, ScheduleDefinition Definition, DateTimeOffset? NextDueUtc, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
public sealed record ScheduleUpdate(int ExpectedRevision, ScheduleDefinition Definition);
public sealed record StartScanRequest
{
    public string? RelativeScope { get; init; }
    public bool OnDemand { get; init; }
}

public sealed record JobSnapshot(ScanProfile Profile, string RootPath, string ScopePath, bool OnDemand);
public sealed record ScanJob(Guid Id, JobSnapshot Snapshot, JobStatus Status, string RequestedBy, Guid? ScheduleId,
    DateTimeOffset QueuedUtc, DateTimeOffset? StartedUtc, DateTimeOffset? FinishedUtc, bool CancellationRequested,
    ScanProgress? Progress, ScanReport? Report, string? Error)
{
    public bool ReportTruncated { get; init; }
}
public sealed record EnqueueResult(ScanJob Job, bool Coalesced);
public sealed record PageResult<T>(IReadOnlyList<T> Items, bool HasMore);

public sealed class ServerConflictException(string message) : Exception(message);
public sealed class ServerNotFoundException(string message) : Exception(message);
public sealed class ServerQueueFullException(string message) : Exception(message);
