using System.Text.Json;
using System.Text.Json.Serialization;
using FindEverything.Engine;
using FindEverything.Server.Models;
using Microsoft.Data.Sqlite;

namespace FindEverything.Server.Persistence;

/// <summary>Owned local control database; profile snapshots and job reports do not touch scan sources.</summary>
public sealed class ServerRepository(ServerPaths paths, ProfileValidator validator, ServerOptions options, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private const int ApplicationId = 0x46455331; // FES1; deliberately distinct from the engine index.
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private bool initialized;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            PathRules.EnsureNoReparseAncestors(paths.DataDirectory);
            Directory.CreateDirectory(paths.DataDirectory);
            PathRules.EnsureNoReparseAncestors(paths.ControlDatabasePath);
            if (File.Exists(paths.ControlDatabasePath) && new FileInfo(paths.ControlDatabasePath).Length != 0)
            {
                await using var existing = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
                var owner = Convert.ToInt32(await ScalarAsync(existing, "PRAGMA application_id;", cancellationToken));
                var schema = Convert.ToInt32(await ScalarAsync(existing, "PRAGMA user_version;", cancellationToken));
                if (owner != ApplicationId || schema != 1) throw new IOException("The control database is not owned by this server, or its schema is unsupported.");
            }
            await using var connection = await OpenAsync(SqliteOpenMode.ReadWriteCreate, cancellationToken);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", cancellationToken);
            await using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, $"""
                PRAGMA application_id={ApplicationId};
                PRAGMA user_version=1;
                CREATE TABLE IF NOT EXISTS profiles(id TEXT PRIMARY KEY, revision INTEGER NOT NULL, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS schedules(id TEXT PRIMARY KEY, revision INTEGER NOT NULL, profile_id TEXT NOT NULL,
                  enabled INTEGER NOT NULL, due TEXT, document TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS schedules_due ON schedules(enabled,due);
                CREATE INDEX IF NOT EXISTS schedules_profile ON schedules(profile_id);
                CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, profile_id TEXT NOT NULL, profile_revision INTEGER NOT NULL,
                  scope TEXT NOT NULL, on_demand INTEGER NOT NULL, status INTEGER NOT NULL, cancel_requested INTEGER NOT NULL,
                  queued TEXT NOT NULL, document TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS jobs_queue ON jobs(status,queued,id);
                CREATE INDEX IF NOT EXISTS jobs_profile ON jobs(profile_id,queued,id);
                """, cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
            initialized = true;
        }
        finally { mutation.Release(); }
    }

    public Task<ScanProfile> CreateProfileAsync(ProfileDefinition definition, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        definition = validator.Validate(definition);
        var now = clock.GetUtcNow();
        var profile = new ScanProfile(Guid.NewGuid(), 1, definition, now, now);
        await ExecuteAsync(connection, "INSERT INTO profiles(id,revision,document) VALUES($id,$rev,$json);", cancellationToken, transaction,
            ("$id", Id(profile.Id)), ("$rev", profile.Revision), ("$json", Serialize(profile)));
        return profile;
    }, cancellationToken);

    public Task<ScanProfile> UpdateProfileAsync(Guid id, int expectedRevision, ProfileDefinition definition, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        definition = validator.Validate(definition);
        var existing = await RequiredProfileAsync(connection, id, transaction, cancellationToken);
        CheckRevision(existing.Revision, expectedRevision);
        // A changed source/root must not accidentally expose an older root's index.
        if (existing.Definition.SourceId != definition.SourceId || existing.Definition.RelativeRoot != definition.RelativeRoot)
            throw new ServerConflictException("A profile's source and root are immutable. Create a new profile to change its indexed scope.");
        var updated = existing with { Definition = definition, Revision = checked(existing.Revision + 1), UpdatedUtc = clock.GetUtcNow() };
        await ExecuteAsync(connection, "UPDATE profiles SET revision=$rev,document=$json WHERE id=$id;", cancellationToken, transaction,
            ("$rev", updated.Revision), ("$json", Serialize(updated)), ("$id", Id(id)));
        return updated;
    }, cancellationToken);

    public Task DeleteProfileAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        var profile = await RequiredProfileAsync(connection, id, transaction, cancellationToken);
        CheckRevision(profile.Revision, expectedRevision);
        var active = Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM jobs WHERE profile_id=$id AND status IN ($queued,$running);", cancellationToken, transaction,
            ("$id", Id(id)), ("$queued", (int)JobStatus.Queued), ("$running", (int)JobStatus.Running)));
        if (active != 0) throw new ServerConflictException("Cancel and finish active jobs before deleting their profile.");
        await ExecuteAsync(connection, "DELETE FROM schedules WHERE profile_id=$id; DELETE FROM profiles WHERE id=$id;", cancellationToken, transaction, ("$id", Id(id)));
        // Historical jobs and engine files intentionally remain for administrators.
        return true;
    }, cancellationToken);

    public Task<ScanProfile?> GetProfileAsync(Guid id, CancellationToken cancellationToken = default) => ReadDocumentAsync<ScanProfile>("profiles", id, cancellationToken);
    public Task<PageResult<ScanProfile>> ListProfilesAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default) => ReadPageAsync<ScanProfile>("SELECT document FROM profiles ORDER BY id LIMIT $limit OFFSET $offset;", limit, offset, cancellationToken);

    public Task<ScanSchedule> CreateScheduleAsync(ScheduleDefinition definition, DateTimeOffset? nextDueUtc, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        ValidateSchedule(definition);
        _ = await RequiredProfileAsync(connection, definition.ProfileId, transaction, cancellationToken);
        var now = clock.GetUtcNow();
        var schedule = new ScanSchedule(Guid.NewGuid(), 1, definition, nextDueUtc?.ToUniversalTime(), now, now);
        await SaveScheduleAsync(connection, transaction, schedule, cancellationToken, insert: true);
        return schedule;
    }, cancellationToken);

    public Task<ScanSchedule> UpdateScheduleAsync(Guid id, int expectedRevision, ScheduleDefinition definition, DateTimeOffset? nextDueUtc, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        ValidateSchedule(definition);
        _ = await RequiredProfileAsync(connection, definition.ProfileId, transaction, cancellationToken);
        var existing = await RequiredScheduleAsync(connection, id, transaction, cancellationToken);
        CheckRevision(existing.Revision, expectedRevision);
        var schedule = existing with { Revision = checked(existing.Revision + 1), Definition = definition, NextDueUtc = nextDueUtc?.ToUniversalTime(), UpdatedUtc = clock.GetUtcNow() };
        await SaveScheduleAsync(connection, transaction, schedule, cancellationToken);
        return schedule;
    }, cancellationToken);

    public Task DeleteScheduleAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        var schedule = await RequiredScheduleAsync(connection, id, transaction, cancellationToken);
        CheckRevision(schedule.Revision, expectedRevision);
        await ExecuteAsync(connection, "DELETE FROM schedules WHERE id=$id;", cancellationToken, transaction, ("$id", Id(id)));
        return true;
    }, cancellationToken);

    public Task<ScanSchedule?> GetScheduleAsync(Guid id, CancellationToken cancellationToken = default) => ReadDocumentAsync<ScanSchedule>("schedules", id, cancellationToken);
    public Task<PageResult<ScanSchedule>> ListSchedulesAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default) => ReadPageAsync<ScanSchedule>("SELECT document FROM schedules ORDER BY id LIMIT $limit OFFSET $offset;", limit, offset, cancellationToken);

    public async Task<IReadOnlyList<ScanSchedule>> ListDueSchedulesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        await using var command = Command(connection, """
            SELECT schedules.document FROM schedules JOIN profiles ON schedules.profile_id=profiles.id
            WHERE schedules.enabled=1 AND schedules.due IS NOT NULL AND schedules.due<=$now
              AND json_extract(profiles.document,'$.definition.enabled')=1
            ORDER BY schedules.due,schedules.id LIMIT 100;
            """, null, ("$now", Timestamp(nowUtc)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ScanSchedule>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize<ScanSchedule>(reader.GetString(0)));
        return result;
    }

    public Task<EnqueueResult> EnqueueAsync(Guid profileId, string? relativeScope, bool onDemand, string requestedBy, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        var profile = await RequiredProfileAsync(connection, profileId, transaction, cancellationToken);
        return await EnqueueCoreAsync(connection, transaction, profile, relativeScope, onDemand, requestedBy, null, clock.GetUtcNow(), cancellationToken);
    }, cancellationToken);

    public Task<EnqueueResult?> EnqueueScheduledAsync(Guid scheduleId, int expectedRevision, DateTimeOffset expectedNextDueUtc, DateTimeOffset? nextDueUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => WriteAsync<EnqueueResult?>(async (connection, transaction) =>
    {
        if (nextDueUtc is { } next && next <= nowUtc) throw new ArgumentException("The next scheduled occurrence must be in the future.");
        var schedule = await DocumentAsync<ScanSchedule>(connection, "schedules", scheduleId, transaction, cancellationToken);
        if (schedule is null || schedule.Revision != expectedRevision || !schedule.Definition.Enabled || schedule.NextDueUtc != expectedNextDueUtc || schedule.NextDueUtc > nowUtc) return null;
        var profile = await RequiredProfileAsync(connection, schedule.Definition.ProfileId, transaction, cancellationToken);
        if (!profile.Definition.Enabled) return null;
        var result = await EnqueueCoreAsync(connection, transaction, profile, null, false, "scheduler", scheduleId, nowUtc, cancellationToken);
        // Advancing the due time and enqueueing share one transaction. Restart cannot double-trigger it.
        var advanced = schedule with { NextDueUtc = nextDueUtc?.ToUniversalTime(), UpdatedUtc = nowUtc.ToUniversalTime() };
        await SaveScheduleAsync(connection, transaction, advanced, cancellationToken);
        return result;
    }, cancellationToken);

    private async Task<EnqueueResult> EnqueueCoreAsync(SqliteConnection connection, SqliteTransaction transaction, ScanProfile profile, string? relativeScope,
        bool onDemand, string requestedBy, Guid? scheduleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!profile.Definition.Enabled) throw new ServerConflictException("This profile is disabled.");
        if (string.IsNullOrWhiteSpace(requestedBy) || requestedBy.Length > 256 || requestedBy.Any(char.IsControl)) throw new ArgumentException("Invalid requester ID.");
        var snapshot = validator.CreateSnapshot(profile, relativeScope, onDemand);
        await using (var command = Command(connection, """
            SELECT document FROM jobs WHERE profile_id=$id AND profile_revision=$rev AND scope=$scope AND on_demand=$demand
            AND cancel_requested=0 AND status IN ($queued,$running) ORDER BY queued,id LIMIT 1;
            """, transaction, ("$id", Id(profile.Id)), ("$rev", profile.Revision), ("$scope", PathRules.Key(snapshot.ScopePath)), ("$demand", onDemand ? 1 : 0),
            ("$queued", (int)JobStatus.Queued), ("$running", (int)JobStatus.Running)))
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is string json) return new EnqueueResult(Deserialize<ScanJob>(json), true);
        }
        var queued = Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM jobs WHERE status=$queued;", cancellationToken, transaction, ("$queued", (int)JobStatus.Queued)));
        if (queued >= options.MaxQueuedJobs) throw new ServerQueueFullException("The scan queue is full.");
        var job = new ScanJob(Guid.NewGuid(), snapshot, JobStatus.Queued, requestedBy, scheduleId, now.ToUniversalTime(), null, null, false, null, null, null);
        if (JsonSerializer.Serialize(job, Json).Length > 4 * 1024 * 1024)
            throw new ArgumentException("The queued profile snapshot exceeds the 4 MiB size limit.");
        await SaveJobAsync(connection, transaction, job, cancellationToken, insert: true);
        return new EnqueueResult(job, false);
    }

    public Task<ScanJob?> ClaimNextJobAsync(CancellationToken cancellationToken = default) => WriteAsync<ScanJob?>(async (connection, transaction) =>
    {
        // Repository-level guard also prevents accidental second workers in the same server.
        if (Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM jobs WHERE status=$running;", cancellationToken, transaction, ("$running", (int)JobStatus.Running))) != 0) return null;
        while (true)
        {
            await using var command = Command(connection, "SELECT document FROM jobs WHERE status=$queued AND cancel_requested=0 ORDER BY queued,rowid LIMIT 1;", transaction, ("$queued", (int)JobStatus.Queued));
            if (await command.ExecuteScalarAsync(cancellationToken) is not string json) return null;
            var queued = Deserialize<ScanJob>(json);
            var current = await DocumentAsync<ScanProfile>(connection, "profiles", queued.Snapshot.Profile.Id, transaction, cancellationToken);
            if (current is null || !current.Definition.Enabled)
            {
                await SaveJobAsync(connection, transaction, queued with { Status = JobStatus.Cancelled, CancellationRequested = true,
                    FinishedUtc = clock.GetUtcNow(), Error = "The profile was disabled before this queued job started." }, cancellationToken);
                continue;
            }
            var job = queued with { Status = JobStatus.Running, StartedUtc = clock.GetUtcNow() };
            await SaveJobAsync(connection, transaction, job, cancellationToken);
            return job;
        }
    }, cancellationToken);

    public Task UpdateJobProgressAsync(Guid id, ScanProgress progress, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        ArgumentNullException.ThrowIfNull(progress);
        var job = await RequiredJobAsync(connection, id, transaction, cancellationToken);
        if (job.Status == JobStatus.Running) await SaveJobAsync(connection, transaction, job with { Progress = progress }, cancellationToken);
        return true;
    }, cancellationToken);

    public Task<ScanJob> CompleteJobAsync(Guid id, JobStatus status, ScanReport? report = null, string? error = null, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        if (status is JobStatus.Queued or JobStatus.Running || !Enum.IsDefined(status)) throw new ArgumentException("A terminal job status is required.");
        var job = await RequiredJobAsync(connection, id, transaction, cancellationToken);
        if (job.Status != JobStatus.Running) throw new ServerConflictException("Only a running job can be completed.");
        var completed = job with { Status = status, FinishedUtc = clock.GetUtcNow(), Progress = report?.Progress ?? job.Progress, Report = report,
            Error = error is null ? null : error[..Math.Min(error.Length, 4096)], ReportTruncated = error?.Length > 4096 };
        completed = BoundStoredReport(completed);
        await SaveJobAsync(connection, transaction, completed, cancellationToken);
        return completed;
    }, cancellationToken);

    public Task<ScanJob> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        var job = await RequiredJobAsync(connection, id, transaction, cancellationToken);
        if (job.Status is not JobStatus.Queued and not JobStatus.Running) return job;
        job = job with { CancellationRequested = true, Status = job.Status == JobStatus.Queued ? JobStatus.Cancelled : job.Status,
            FinishedUtc = job.Status == JobStatus.Queued ? clock.GetUtcNow() : job.FinishedUtc };
        await SaveJobAsync(connection, transaction, job, cancellationToken);
        return job;
    }, cancellationToken);

    public Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction) =>
    {
        // Read/update one row at a time so restart memory is independent of historical job count.
        while (true)
        {
            await using var command = Command(connection, "SELECT document FROM jobs WHERE status=$running LIMIT 1;", transaction, ("$running", (int)JobStatus.Running));
            if (await command.ExecuteScalarAsync(cancellationToken) is not string json) break;
            var job = Deserialize<ScanJob>(json) with { Status = JobStatus.Interrupted, FinishedUtc = clock.GetUtcNow(), Error = "Server stopped before the job finished. The previous published index remains available." };
            await SaveJobAsync(connection, transaction, job, cancellationToken);
        }
        return true;
    }, cancellationToken);

    public Task<ScanJob?> GetJobAsync(Guid id, CancellationToken cancellationToken = default) => ReadDocumentAsync<ScanJob>("jobs", id, cancellationToken);
    public Task<PageResult<ScanJob>> ListJobsAsync(Guid? profileId = null, int limit = 100, int offset = 0, CancellationToken cancellationToken = default) =>
        ReadPageAsync<ScanJob>("SELECT document FROM jobs " + (profileId.HasValue ? "WHERE profile_id=$profile " : "") + "ORDER BY queued DESC,id DESC LIMIT $limit OFFSET $offset;", limit, offset, cancellationToken,
            profileId.HasValue ? [("$profile", (object?)Id(profileId.Value))] : []);

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await mutation.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(SqliteOpenMode.ReadWrite, cancellationToken);
            await using var transaction = connection.BeginTransaction();
            var result = await action(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally { mutation.Release(); }
    }

    private async Task<T?> ReadDocumentAsync<T>(string table, Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        return await DocumentAsync<T>(connection, table, id, null, cancellationToken);
    }

    private async Task<PageResult<T>> ReadPageAsync<T>(string sql, int limit, int offset, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        if (limit is < 1 or > 200 || offset is < 0 or > 1000000) throw new ArgumentException("Page size must be 1 to 200 and offset 0 to 1000000.");
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        await using var command = Command(connection, sql, null, parameters.Concat([("$limit", (object?)(limit + 1)), ("$offset", (object?)offset)]).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<T>(limit + 1);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize<T>(reader.GetString(0)));
        var hasMore = result.Count > limit;
        if (hasMore) result.RemoveAt(result.Count - 1);
        return new PageResult<T>(result, hasMore);
    }

    private static async Task<T?> DocumentAsync<T>(SqliteConnection connection, string table, Guid id, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, $"SELECT document FROM {table} WHERE id=$id;", transaction, ("$id", Id(id)));
        return await command.ExecuteScalarAsync(cancellationToken) is string json ? Deserialize<T>(json) : default;
    }

    private static async Task<ScanProfile> RequiredProfileAsync(SqliteConnection connection, Guid id, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        await DocumentAsync<ScanProfile>(connection, "profiles", id, transaction, cancellationToken) ?? throw new ServerNotFoundException("Profile not found.");
    private static async Task<ScanSchedule> RequiredScheduleAsync(SqliteConnection connection, Guid id, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        await DocumentAsync<ScanSchedule>(connection, "schedules", id, transaction, cancellationToken) ?? throw new ServerNotFoundException("Schedule not found.");
    private static async Task<ScanJob> RequiredJobAsync(SqliteConnection connection, Guid id, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        await DocumentAsync<ScanJob>(connection, "jobs", id, transaction, cancellationToken) ?? throw new ServerNotFoundException("Job not found.");

    private async Task<SqliteConnection> OpenAsync(SqliteOpenMode mode, CancellationToken cancellationToken)
    {
        PathRules.EnsureNoReparseAncestors(paths.ControlDatabasePath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ControlDatabasePath, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task SaveScheduleAsync(SqliteConnection connection, SqliteTransaction transaction, ScanSchedule schedule, CancellationToken cancellationToken, bool insert = false) =>
        await ExecuteAsync(connection, insert
            ? "INSERT INTO schedules(id,revision,profile_id,enabled,due,document) VALUES($id,$rev,$profile,$enabled,$due,$json);"
            : "UPDATE schedules SET revision=$rev,profile_id=$profile,enabled=$enabled,due=$due,document=$json WHERE id=$id;", cancellationToken, transaction,
            ("$id", Id(schedule.Id)), ("$rev", schedule.Revision), ("$profile", Id(schedule.Definition.ProfileId)), ("$enabled", schedule.Definition.Enabled ? 1 : 0),
            ("$due", schedule.NextDueUtc.HasValue ? Timestamp(schedule.NextDueUtc.Value) : null), ("$json", Serialize(schedule)));

    private static async Task SaveJobAsync(SqliteConnection connection, SqliteTransaction transaction, ScanJob job, CancellationToken cancellationToken, bool insert = false) =>
        await ExecuteAsync(connection, insert
            ? "INSERT INTO jobs(id,profile_id,profile_revision,scope,on_demand,status,cancel_requested,queued,document) VALUES($id,$profile,$rev,$scope,$demand,$status,$cancel,$queued,$json);"
            : "UPDATE jobs SET status=$status,cancel_requested=$cancel,document=$json WHERE id=$id;", cancellationToken, transaction,
            ("$id", Id(job.Id)), ("$profile", Id(job.Snapshot.Profile.Id)), ("$rev", job.Snapshot.Profile.Revision), ("$scope", PathRules.Key(job.Snapshot.ScopePath)),
            ("$demand", job.Snapshot.OnDemand ? 1 : 0), ("$status", (int)job.Status), ("$cancel", job.CancellationRequested ? 1 : 0), ("$queued", Timestamp(job.QueuedUtc)), ("$json", Serialize(job)));

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static string Serialize<T>(T value)
    {
        var serialized = JsonSerializer.Serialize(value, Json);
        if (serialized.Length > 16 * 1024 * 1024) throw new ArgumentException("The stored document exceeds the server size limit.");
        return serialized;
    }
    // Reports may contain very long paths. A completed scan must never leave the queue stuck in Running
    // because verbose diagnostics exceeded a storage cap. Aggregate progress always remains intact.
    private static ScanJob BoundStoredReport(ScanJob job)
    {
        if (job.Report is not { } original) return job;
        var report = original;
        var truncated = job.ReportTruncated || report.Errors.Any(error => error.Message.Length > 4096);
        int TakeCount(int count, int cap) { if (count > cap) truncated = true; return Math.Min(count, cap); }
        report = report with
        {
            Errors = report.Errors.Take(TakeCount(report.Errors.Count, 1000)).Select(error =>
                error.Message.Length > 4096 ? error with { Message = error.Message[..4096] } : error).ToArray(),
            PendingScopes = report.PendingScopes.Take(TakeCount(report.PendingScopes.Count, 1024)).ToArray(),
            Diagnostics = report.Diagnostics with
            {
                ExcludedPaths = report.Diagnostics.ExcludedPaths.Take(TakeCount(report.Diagnostics.ExcludedPaths.Count, 1000)).ToArray(),
                RepeatedDirectoryNames = report.Diagnostics.RepeatedDirectoryNames.Take(TakeCount(report.Diagnostics.RepeatedDirectoryNames.Count, 1000)).ToArray()
            }
        };
        while (true)
        {
            var omitted = original.Diagnostics.ExcludedPaths.Count - report.Diagnostics.ExcludedPaths.Count;
            var candidate = job with { Report = report with { Diagnostics = report.Diagnostics with { OmittedExcludedPaths = original.Diagnostics.OmittedExcludedPaths + omitted } }, ReportTruncated = truncated };
            if (JsonSerializer.Serialize(candidate, Json).Length <= 16 * 1024 * 1024) return candidate;
            truncated = true;
            if (report.Errors.Count == 0 && report.PendingScopes.Count == 0 && report.Diagnostics.ExcludedPaths.Count == 0 && report.Diagnostics.RepeatedDirectoryNames.Count == 0)
                return job with { Report = null, ReportTruncated = true };
            report = report with
            {
                Errors = report.Errors.Take(report.Errors.Count / 2).ToArray(),
                PendingScopes = report.PendingScopes.Take(report.PendingScopes.Count / 2).ToArray(),
                Diagnostics = report.Diagnostics with
                {
                    ExcludedPaths = report.Diagnostics.ExcludedPaths.Take(report.Diagnostics.ExcludedPaths.Count / 2).ToArray(),
                    RepeatedDirectoryNames = report.Diagnostics.RepeatedDirectoryNames.Take(report.Diagnostics.RepeatedDirectoryNames.Count / 2).ToArray()
                }
            };
        }
    }
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new IOException("Invalid server database document.");
    private static string Id(Guid id) => id.ToString("N");
    private static string Timestamp(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O");
    private static void CheckRevision(int revision, int expectedRevision)
    {
        if (expectedRevision <= 0) throw new ArgumentException("An expected positive revision is required.");
        if (revision != expectedRevision) throw new ServerConflictException("The resource has changed. Reload before updating it.");
    }
    private static void ValidateSchedule(ScheduleDefinition definition)
    {
        Services.ScheduleCalculator.Validate(definition);
        if (definition.Kind == ScheduleKind.Interval && definition.IntervalMinutes is not (>= 1 and <= 525600)) throw new ArgumentException("Interval minutes must be between 1 and 525600.");
    }

    public ValueTask DisposeAsync() { mutation.Dispose(); return ValueTask.CompletedTask; }
}
