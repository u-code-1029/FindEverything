using System.Text;
using Microsoft.Data.Sqlite;

namespace FindEverything.Engine;

/// <summary>
/// A local, persistent metadata index. All source access belongs to the scanner;
/// searches use this database exclusively. Store the database on local storage.
/// </summary>
public sealed class SqliteIndexStore : IIndexStore
{
    private const int ApplicationId = 0x46455631; // FEV1: never initialize an unrelated SQLite file.
    private const int SchemaVersion = 2;
    private const int MaximumWriteBatch = 512;
    private readonly SemaphoreSlim _writerSemaphore = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private WriterLease? _writerLease;
    private bool _disposed;

    public SqliteIndexStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = PathRules.Normalize(databasePath);
        EnsureLocalDatabasePath();
    }

    public string DatabasePath { get; }

    public async Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writerSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? lockFile = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureLocalDatabasePath();
            PathRules.EnsureNoReparseAncestors(DatabasePath);
            var lockPath = DatabasePath + ".writer.lock";
            PathRules.EnsureNoReparseAncestors(lockPath);
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                PathRules.EnsureNoReparseAncestors(lockPath);
                try
                {
                    lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
                {
                    // The lease is shared across store instances and processes, not only threads.
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _writerLease = new WriterLease(this, lockFile);
                return _writerLease;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch
        {
            if (lockFile is not null)
                await lockFile.DisposeAsync().ConfigureAwait(false);
            _writerSemaphore.Release();
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireWriter();
            // Reinitializing within the same lease must never purge an active scan.
            // A new lease can safely clean up staging left by a crashed prior writer.
            if (_writerLease!.Initialized)
                return;
            ValidateDatabaseLocation();
            var exists = File.Exists(DatabasePath);
            if (exists)
                ValidateOwnedHeader();

            await using var connection = await OpenAsync(readOnly: false, cancellationToken).ConfigureAwait(false);
            if (exists)
                await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
            await using (var transaction = connection.BeginTransaction())
            {
                await ExecuteAsync(connection, SchemaSql, cancellationToken, transaction).ConfigureAwait(false);
                await EnsureNameUpdateTriggerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                // Holding the sole writer lease makes abandoned staging rows safe to remove.
                await ExecuteAsync(connection, "DELETE FROM scans;", cancellationToken, transaction).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            // Persist the ownership marker in the main file, including on first initialization.
            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(PASSIVE);", cancellationToken).ConfigureAwait(false);
            _writerLease.Initialized = true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task BeginScanAsync(Guid scanId, string rootPath, string scopePath,
        CancellationToken cancellationToken = default)
    {
        var root = PathRules.Normalize(rootPath);
        var scope = PathRules.Normalize(scopePath);
        var rootKey = PathRules.Key(root);
        var scopeKey = PathRules.Key(scope);
        if (scopeKey != rootKey && !IsStrictDescendant(scopeKey, rootKey))
            throw new ArgumentException("The scan scope must be inside its root.", nameof(scopePath));

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireInitializedWriter();
            await using var connection = await OpenAsync(false, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO roots(root_key, root_path) VALUES($root, $rootPath)
                    ON CONFLICT(root_key) DO UPDATE SET root_path=excluded.root_path;
                INSERT INTO scans(scan_id, root_key, scope_key, scope_path, started_ticks)
                    VALUES($scan, $root, $scope, $scopePath, $started);
                """;
            command.Parameters.AddWithValue("$root", rootKey);
            command.Parameters.AddWithValue("$rootPath", root);
            command.Parameters.AddWithValue("$scope", scopeKey);
            command.Parameters.AddWithValue("$scopePath", scope);
            command.Parameters.AddWithValue("$scan", scanId.ToString("N"));
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.UtcTicks);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task StageAsync(Guid scanId, IReadOnlyList<IndexedEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireInitializedWriter();
            await using var connection = await OpenAsync(false, cancellationToken).ConfigureAwait(false);
            var scan = await ReadScanAsync(connection, scanId, cancellationToken).ConfigureAwait(false);
            for (var start = 0; start < entries.Count; start += MaximumWriteBatch)
            {
                await using var transaction = connection.BeginTransaction();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO staging(scan_id, path_key, full_path, name, name_search,
                        parent_path, kind, size_bytes, created_ticks, modified_ticks)
                    VALUES($scan, $pathKey, $path, $name, $nameSearch,
                        $parent, $kind, $size, $created, $modified)
                    ON CONFLICT(scan_id, path_key) DO UPDATE SET
                        full_path=excluded.full_path, name=excluded.name,
                        name_search=excluded.name_search, parent_path=excluded.parent_path,
                        kind=excluded.kind, size_bytes=excluded.size_bytes,
                        created_ticks=excluded.created_ticks, modified_ticks=excluded.modified_ticks;
                    """;
                command.Parameters.AddWithValue("$scan", scanId.ToString("N"));
                var pathKeyParameter = command.Parameters.Add("$pathKey", SqliteType.Text);
                var pathParameter = command.Parameters.Add("$path", SqliteType.Text);
                var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
                var searchParameter = command.Parameters.Add("$nameSearch", SqliteType.Text);
                var parentParameter = command.Parameters.Add("$parent", SqliteType.Text);
                var kindParameter = command.Parameters.Add("$kind", SqliteType.Integer);
                var sizeParameter = command.Parameters.Add("$size", SqliteType.Integer);
                var createdParameter = command.Parameters.Add("$created", SqliteType.Integer);
                var modifiedParameter = command.Parameters.Add("$modified", SqliteType.Integer);
                command.Prepare();

                for (var index = start; index < Math.Min(entries.Count, start + MaximumWriteBatch); index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = entries[index];
                    ArgumentNullException.ThrowIfNull(entry);
                    ArgumentNullException.ThrowIfNull(entry.Name);
                    if (!Enum.IsDefined(entry.Kind) || entry.SizeBytes is < 0)
                        throw new ArgumentException("An indexed entry has invalid metadata.", nameof(entries));
                    var path = PathRules.Normalize(entry.FullPath);
                    var pathKey = PathRules.Key(path);
                    var isScopeDirectory = pathKey == scan.ScopeKey && scan.ScopeKey != scan.RootKey
                        && entry.Kind == EntryKind.Directory;
                    if (!isScopeDirectory && !IsStrictDescendant(pathKey, scan.ScopeKey))
                        throw new ArgumentException("Staged entries must be descendants or the non-root scope directory itself.", nameof(entries));

                    pathKeyParameter.Value = pathKey;
                    pathParameter.Value = path;
                    nameParameter.Value = entry.Name;
                    searchParameter.Value = entry.Name.ToLowerInvariant();
                    parentParameter.Value = PathRules.Normalize(entry.ParentPath);
                    kindParameter.Value = (int)entry.Kind;
                    sizeParameter.Value = (object?)entry.SizeBytes ?? DBNull.Value;
                    createdParameter.Value = entry.CreatedUtc.UtcTicks;
                    modifiedParameter.Value = entry.ModifiedUtc.UtcTicks;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task StageDirectoryCostsAsync(Guid scanId, IReadOnlyList<DirectoryScanCost> costs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(costs);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireInitializedWriter();
            await using var connection = await OpenAsync(false, cancellationToken).ConfigureAwait(false);
            var scan = await ReadScanAsync(connection, scanId, cancellationToken).ConfigureAwait(false);
            for (var start = 0; start < costs.Count; start += MaximumWriteBatch)
            {
                await using var transaction = connection.BeginTransaction();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO staged_costs(scan_id, path_key, full_path, entries, duration_ticks, recorded_ticks)
                    VALUES($scan, $key, $path, $entries, $duration, $recorded)
                    ON CONFLICT(scan_id, path_key) DO UPDATE SET full_path=excluded.full_path,
                        entries=excluded.entries, duration_ticks=excluded.duration_ticks,
                        recorded_ticks=excluded.recorded_ticks;
                    """;
                command.Parameters.AddWithValue("$scan", scanId.ToString("N"));
                var key = command.Parameters.Add("$key", SqliteType.Text);
                var path = command.Parameters.Add("$path", SqliteType.Text);
                var entries = command.Parameters.Add("$entries", SqliteType.Integer);
                var duration = command.Parameters.Add("$duration", SqliteType.Integer);
                var recorded = command.Parameters.Add("$recorded", SqliteType.Integer);
                command.Prepare();
                for (var index = start; index < Math.Min(costs.Count, start + MaximumWriteBatch); index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cost = costs[index];
                    ArgumentNullException.ThrowIfNull(cost);
                    var normalized = PathRules.Normalize(cost.Path);
                    var pathKey = PathRules.Key(normalized);
                    if (pathKey != scan.ScopeKey && !IsStrictDescendant(pathKey, scan.ScopeKey))
                        throw new ArgumentException("Directory cost records must be within the scan scope.", nameof(costs));
                    if (cost.Entries < 0 || cost.Duration < TimeSpan.Zero)
                        throw new ArgumentException("Directory cost estimates cannot be negative.", nameof(costs));
                    key.Value = pathKey;
                    path.Value = normalized;
                    entries.Value = cost.Entries;
                    duration.Value = cost.Duration.Ticks;
                    recorded.Value = cost.RecordedUtc.UtcTicks;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, DirectoryScanCost>> GetDirectoryCostsAsync(
        string rootPath, string scopePath, DeferralPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var rootKey = PathRules.Key(rootPath);
        var scopeKey = PathRules.Key(scopePath);
        if (scopeKey != rootKey && !IsStrictDescendant(scopeKey, rootKey))
            throw new ArgumentException("The history scope must be within its root.", nameof(scopePath));
        policy.Validate(rootPath);
        EnsureReadableIndex();
        var costs = new Dictionary<string, DirectoryScanCost>(PathRules.Comparer);
        if (policy.HistoricalEntryThreshold is null && policy.HistoricalDurationThreshold is null)
            return costs;
        await using var connection = await OpenAsync(true, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (await ValidateSchemaAsync(connection, cancellationToken, transaction).ConfigureAwait(false) < 2)
            return costs;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var thresholds = new List<string>();
        if (policy.HistoricalEntryThreshold is { } entryThreshold)
        {
            thresholds.Add("entries >= $entries");
            command.Parameters.AddWithValue("$entries", entryThreshold);
        }
        if (policy.HistoricalDurationThreshold is { } durationThreshold)
        {
            thresholds.Add("duration_ticks >= $duration");
            command.Parameters.AddWithValue("$duration", durationThreshold.Ticks);
        }
        command.CommandText = $"""
            SELECT full_path, entries, duration_ticks, recorded_ticks FROM directory_costs
            WHERE root_key=$root AND (path_key=$scope OR (path_key >= $lower AND path_key < $upper))
                AND ({string.Join(" OR ", thresholds)})
            ORDER BY entries DESC, duration_ticks DESC, path_key COLLATE BINARY LIMIT 4096;
            """;
        command.Parameters.AddWithValue("$root", rootKey);
        command.Parameters.AddWithValue("$scope", scopeKey);
        var prefix = DescendantPrefix(scopeKey);
        command.Parameters.AddWithValue("$lower", prefix);
        command.Parameters.AddWithValue("$upper", PrefixUpperBound(prefix));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var cost = new DirectoryScanCost(reader.GetString(0), reader.GetInt64(1),
                TimeSpan.FromTicks(reader.GetInt64(2)), new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero));
            costs[cost.Path] = cost;
        }
        return costs;
    }

    public async Task PublishAsync(ScanReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(report.PendingScopes);
        if (!Enum.IsDefined(report.Status))
            throw new ArgumentException("Unknown scan status.", nameof(report));
        if (report.Status == ScanStatus.Deferred && report.PendingScopes.Count == 0)
            throw new ArgumentException("A deferred report must identify at least one pending scope.", nameof(report));
        if (report.Status == ScanStatus.Completed && report.PendingScopes.Count != 0)
            throw new ArgumentException("A completed report cannot contain pending scopes.", nameof(report));
        if (report.Status == ScanStatus.Cancelled)
        {
            await DiscardAsync(report.ScanId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireInitializedWriter();
            await using var connection = await OpenAsync(false, cancellationToken).ConfigureAwait(false);
            var scan = await ReadScanAsync(connection, report.ScanId, cancellationToken).ConfigureAwait(false);
            if (scan.RootKey != PathRules.Key(report.RootPath) || scan.ScopeKey != PathRules.Key(report.ScopePath))
                throw new ArgumentException("The report does not match the staged scan root and scope.", nameof(report));

            await using var transaction = connection.BeginTransaction();
            await StagePendingReportAsync(connection, transaction, report, scan, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO entries(root_key, path_key, full_path, name, name_search,
                    parent_path, kind, size_bytes, created_ticks, modified_ticks)
                SELECT $root, path_key, full_path, name, name_search,
                    parent_path, kind, size_bytes, created_ticks, modified_ticks
                FROM staging WHERE scan_id=$scan
                ON CONFLICT(root_key, path_key) DO UPDATE SET
                    full_path=excluded.full_path, name=excluded.name, name_search=excluded.name_search,
                    parent_path=excluded.parent_path, kind=excluded.kind, size_bytes=excluded.size_bytes,
                    created_ticks=excluded.created_ticks, modified_ticks=excluded.modified_ticks
                WHERE entries.full_path IS NOT excluded.full_path OR entries.name IS NOT excluded.name
                    OR entries.parent_path IS NOT excluded.parent_path OR entries.kind IS NOT excluded.kind
                    OR entries.size_bytes IS NOT excluded.size_bytes
                    OR entries.created_ticks IS NOT excluded.created_ticks
                    OR entries.modified_ticks IS NOT excluded.modified_ticks;
                """;
            command.Parameters.AddWithValue("$root", scan.RootKey);
            command.Parameters.AddWithValue("$scan", report.ScanId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            command.CommandText = """
                INSERT INTO directory_costs(root_key, path_key, full_path, entries, duration_ticks, recorded_ticks)
                SELECT $root, path_key, full_path, entries, duration_ticks, recorded_ticks
                FROM staged_costs WHERE scan_id=$scan
                ON CONFLICT(root_key, path_key) DO UPDATE SET full_path=excluded.full_path,
                    entries=excluded.entries, duration_ticks=excluded.duration_ticks,
                    recorded_ticks=excluded.recorded_ticks;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (report.Status is ScanStatus.Completed or ScanStatus.Deferred)
            {
                // Only a complete enumeration establishes absence. Preserve the scope directory
                // itself: an available scope directory is upserted above; inability to open
                // the scope must never be interpreted as its deletion.
                var prefix = DescendantPrefix(scan.ScopeKey);
                await SweepUnseenAsync(connection, transaction, report.ScanId, scan,
                    cancellationToken).ConfigureAwait(false);
                command.CommandText = """
                    DELETE FROM pending_scopes WHERE root_key=$root
                        AND (path_key=$scope OR (path_key >= $prefix AND path_key < $upper));
                    """;
                command.Parameters.AddWithValue("$prefix", prefix);
                command.Parameters.AddWithValue("$upper", PrefixUpperBound(prefix));
                command.Parameters.AddWithValue("$scope", scan.ScopeKey);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            command.CommandText = """
                INSERT INTO pending_scopes(root_key, path_key, scope_path, lower_bound, upper_bound,
                    reason, estimated_entries, estimated_duration_ticks, deferred_ticks)
                SELECT $root, path_key, scope_path, lower_bound, upper_bound,
                    reason, estimated_entries, estimated_duration_ticks, deferred_ticks
                FROM staged_pending WHERE scan_id=$scan
                ON CONFLICT(root_key, path_key) DO UPDATE SET scope_path=excluded.scope_path,
                    lower_bound=excluded.lower_bound, upper_bound=excluded.upper_bound,
                    reason=excluded.reason, estimated_entries=excluded.estimated_entries,
                    estimated_duration_ticks=excluded.estimated_duration_ticks, deferred_ticks=excluded.deferred_ticks;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            command.CommandText = """
                UPDATE roots SET last_scan_id=$scan, last_scope_path=$scopePath,
                    last_status=$status, last_published_ticks=$published,
                    last_entries=$count, last_errors=$errors WHERE root_key=$root;
                DELETE FROM scans WHERE scan_id=$scan;
                """;
            command.Parameters.AddWithValue("$scopePath", PathRules.Normalize(report.ScopePath));
            command.Parameters.AddWithValue("$status", (int)report.Status);
            command.Parameters.AddWithValue("$published", DateTimeOffset.UtcNow.UtcTicks);
            command.Parameters.AddWithValue("$count", report.Progress.Entries);
            command.Parameters.AddWithValue("$errors", report.Progress.ErrorCount);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DiscardAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireInitializedWriter();
            await using var connection = await OpenAsync(false, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM scans WHERE scan_id=$scan;";
            command.Parameters.AddWithValue("$scan", scanId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (query.Limit is < 1 or > 1000 || query.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Limit must be 1–1000 and offset must be nonnegative.");
        if (query.Kind is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentException("Unknown entry kind.", nameof(query));
        ValidateDateRange(query.CreatedFromUtc, query.CreatedBeforeUtc);
        ValidateDateRange(query.ModifiedFromUtc, query.ModifiedBeforeUtc);
        EnsureReadableIndex();

        await using var connection = await OpenAsync(true, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var version = await ValidateSchemaAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        var hasPending = false;
        if (version >= 2)
        {
            await using var pendingCommand = connection.CreateCommand();
            pendingCommand.Transaction = transaction;
            pendingCommand.CommandText = query.RootPath is null
                ? "SELECT 1 FROM pending_scopes LIMIT 1;"
                : "SELECT 1 FROM pending_scopes WHERE root_key=$root LIMIT 1;";
            if (query.RootPath is not null)
                pendingCommand.Parameters.AddWithValue("$root", PathRules.Key(query.RootPath));
            hasPending = await pendingCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var predicates = new List<string>();
        var normalizedName = query.NameContains?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(normalizedName))
        {
            // FTS produces candidates; INSTR verifies the literal Unicode substring, without
            // treating a user's *, quotation marks, %, or _ as query syntax or wildcards.
            if (normalizedName.EnumerateRunes().Count() >= 3)
            {
                predicates.Add("e.id IN (SELECT rowid FROM entry_names WHERE entry_names MATCH $match)");
                command.Parameters.AddWithValue("$match", "\"" + normalizedName.Replace("\"", "\"\"") + "\"");
            }
            predicates.Add("instr(e.name_search, $name) > 0");
            command.Parameters.AddWithValue("$name", normalizedName);
        }

        if (query.RootPath is not null)
        {
            predicates.Add("e.root_key=$root");
            command.Parameters.AddWithValue("$root", PathRules.Key(query.RootPath));
        }
        if (query.Kind is not null)
        {
            predicates.Add("e.kind=$kind");
            command.Parameters.AddWithValue("$kind", (int)query.Kind.Value);
        }
        AddDatePredicate(command, predicates, "created_ticks", ">=", "$createdFrom", query.CreatedFromUtc);
        AddDatePredicate(command, predicates, "created_ticks", "<", "$createdBefore", query.CreatedBeforeUtc);
        AddDatePredicate(command, predicates, "modified_ticks", ">=", "$modifiedFrom", query.ModifiedFromUtc);
        AddDatePredicate(command, predicates, "modified_ticks", "<", "$modifiedBefore", query.ModifiedBeforeUtc);
        var where = predicates.Count == 0 ? "" : "WHERE " + string.Join(" AND ", predicates);
        var coverage = version < 2 ? "0" : """
            EXISTS(SELECT 1 FROM pending_scopes p WHERE p.root_key=e.root_key
                AND (e.path_key=p.path_key OR (e.path_key >= p.lower_bound AND e.path_key < p.upper_bound)))
            """;
        command.CommandText = $"""
            SELECT e.full_path, e.name, e.parent_path, e.kind, e.size_bytes,
                e.created_ticks, e.modified_ticks, {coverage} FROM entries e {where}
            ORDER BY e.name_search COLLATE BINARY, e.full_path COLLATE BINARY, e.root_key COLLATE BINARY
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        command.Parameters.AddWithValue("$offset", query.Offset);
        var entries = new List<IndexedEntry>(query.Limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new IndexedEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                (EntryKind)reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero)) { CoveragePending = reader.GetBoolean(7) });
        }

        var hasMore = entries.Count > query.Limit;
        if (hasMore)
            entries.RemoveAt(entries.Count - 1);
        return new SearchResult(entries.AsReadOnly(), hasMore) { HasPendingScopes = hasPending };
    }

    public async Task<PendingResult> ListPendingAsync(PendingQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 1000 || query.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Limit must be 1–1000 and offset must be nonnegative.");
        EnsureReadableIndex();
        await using var connection = await OpenAsync(true, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (await ValidateSchemaAsync(connection, cancellationToken, transaction).ConfigureAwait(false) < 2)
            return new PendingResult([], false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var where = query.RootPath is null ? "" : "WHERE p.root_key=$root";
        command.CommandText = $"""
            SELECT r.root_path, p.scope_path, p.reason, p.estimated_entries,
                p.estimated_duration_ticks, p.deferred_ticks
            FROM pending_scopes p JOIN roots r ON r.root_key=p.root_key {where}
            ORDER BY p.root_key COLLATE BINARY, p.path_key COLLATE BINARY LIMIT $limit OFFSET $offset;
            """;
        if (query.RootPath is not null)
            command.Parameters.AddWithValue("$root", PathRules.Key(query.RootPath));
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        command.Parameters.AddWithValue("$offset", query.Offset);
        var pending = new List<PendingScope>(query.Limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pending.Add(new PendingScope(reader.GetString(0), reader.GetString(1),
                (DeferralReason)reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : TimeSpan.FromTicks(reader.GetInt64(4)),
                new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero)));
        }
        var hasMore = pending.Count > query.Limit;
        if (hasMore)
            pending.RemoveAt(pending.Count - 1);
        return new PendingResult(pending.AsReadOnly(), hasMore);
    }

    public async ValueTask DisposeAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_writerLease is { } lease)
            {
                _writerLease = null;
                await lease.LockFile.DisposeAsync().ConfigureAwait(false);
                _writerSemaphore.Release();
            }
            // Release this database's idle native connections, including Windows file handles.
            // Other databases' connection pools are unaffected.
            foreach (var readOnly in new[] { false, true })
            {
                using var connection = new SqliteConnection(ConnectionString(readOnly));
                SqliteConnection.ClearPool(connection);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReleaseWriterAsync(WriterLease lease)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_writerLease, lease))
            {
                _writerLease = null;
                await lease.LockFile.DisposeAsync().ConfigureAwait(false);
                _writerSemaphore.Release();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void RequireWriter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writerLease is null)
            throw new InvalidOperationException("Acquire a writer lease before modifying the index.");
    }

    private void RequireInitializedWriter()
    {
        RequireWriter();
        if (_writerLease is not { Initialized: true })
            throw new InvalidOperationException("Initialize the index before staging a scan.");
        ValidateDatabaseLocation();
    }

    private void EnsureReadableIndex()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(DatabasePath))
            throw new FileNotFoundException("No index has been built. Run a scan first.", DatabasePath);
        ValidateDatabaseLocation();
        ValidateOwnedHeader();
    }

    private void ValidateDatabaseLocation()
    {
        EnsureLocalDatabasePath();
        PathRules.EnsureNoReparseAncestors(DatabasePath);
        PathRules.EnsureNoReparseAncestors(DatabasePath + "-wal");
        PathRules.EnsureNoReparseAncestors(DatabasePath + "-shm");
    }

    private void EnsureLocalDatabasePath()
    {
        // SQLite's locking/WAL files require local storage. Network source folders
        // are supported; placing the index itself on a share is a different matter.
        if (!OperatingSystem.IsWindows())
            return; // Non-Windows network mounts cannot be reliably classified here.
        if (DatabasePath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("The SQLite index must be on a local disk, not a network share.", nameof(DatabasePath));

        var driveRoot = Path.GetPathRoot(DatabasePath);
        if (string.IsNullOrEmpty(driveRoot))
            return;
        DriveType driveType;
        try
        {
            driveType = new DriveInfo(driveRoot).DriveType;
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (ArgumentException) { return; }
        if (driveType == DriveType.Network)
            throw new ArgumentException("The SQLite index must be on a local disk, not a mapped network drive.", nameof(DatabasePath));
    }

    private static async Task EnsureNameUpdateTriggerAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name='entries_update';";
        var sql = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (sql?.Contains("AFTER UPDATE OF name_search", StringComparison.Ordinal) == true
            && sql.Contains("WHEN old.name_search IS NOT new.name_search", StringComparison.Ordinal))
            return;
        // Upgrade the original trigger without rebuilding the FTS index or changing rows.
        await ExecuteAsync(connection, "DROP TRIGGER IF EXISTS entries_update;", cancellationToken, transaction).ConfigureAwait(false);
        await ExecuteAsync(connection, NameUpdateTriggerSql, cancellationToken, transaction).ConfigureAwait(false);
    }

    private void ValidateOwnedHeader()
    {
        using var file = new FileStream(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[100];
        var bytesRead = file.Read(header);
        if (bytesRead != header.Length || !header[..16].SequenceEqual("SQLite format 3\0"u8)
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[68..72]) != ApplicationId)
            throw new InvalidDataException("The database path contains a file that is not a FindEverything index. Choose another path.");
    }

    private async Task<SqliteConnection> OpenAsync(bool readOnly, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString(readOnly));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, readOnly
                ? "PRAGMA query_only=ON; PRAGMA foreign_keys=ON;"
                : "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string ConnectionString(bool readOnly) => new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();

    private static async Task<int> ValidateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (version is < 1 or > SchemaVersion)
            throw new InvalidDataException($"Unsupported FindEverything index schema version {version}.");
        return version;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SweepUnseenAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid scanId, ScanMetadata scan, CancellationToken cancellationToken)
    {
        var gaps = await ReadUncoveredGapsAsync(connection, transaction, scanId, scan.ScopeKey,
            cancellationToken).ConfigureAwait(false);
        if (gaps.Count == 0)
            return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM entries WHERE root_key=$root
                AND path_key >= $lower COLLATE BINARY AND path_key < $upper COLLATE BINARY
                AND NOT EXISTS(SELECT 1 FROM staging s WHERE s.scan_id=$scan AND s.path_key=entries.path_key)
                AND NOT EXISTS(SELECT 1 FROM staged_pending p WHERE p.scan_id=$scan AND p.path_key=entries.path_key);
            DELETE FROM directory_costs WHERE root_key=$root
                AND path_key >= $lower COLLATE BINARY AND path_key < $upper COLLATE BINARY
                AND NOT EXISTS(SELECT 1 FROM entries e WHERE e.root_key=$root
                    AND e.path_key=directory_costs.path_key AND e.kind=1)
                AND NOT EXISTS(SELECT 1 FROM staged_pending p
                    WHERE p.scan_id=$scan AND p.path_key=directory_costs.path_key);
            """;
        command.Parameters.AddWithValue("$root", scan.RootKey);
        command.Parameters.AddWithValue("$scan", scanId.ToString("N"));
        var lowerParameter = command.Parameters.Add("$lower", SqliteType.Text);
        var upperParameter = command.Parameters.Add("$upper", SqliteType.Text);
        command.Prepare();
        foreach (var gap in gaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lowerParameter.Value = gap.Lower;
            upperParameter.Value = gap.Upper;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<BinaryInterval>> ReadUncoveredGapsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid scanId, string scopeKey, CancellationToken cancellationToken)
    {
        var prefix = DescendantPrefix(scopeKey);
        var cursor = new BinaryBoundary(prefix);
        var end = new BinaryBoundary(PrefixUpperBound(prefix));
        var gaps = new List<BinaryInterval>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT lower_bound, upper_bound FROM staged_pending WHERE scan_id=$scan
            ORDER BY lower_bound COLLATE BINARY, upper_bound COLLATE BINARY LIMIT 4097;
            """;
        command.Parameters.AddWithValue("$scan", scanId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++count > 4096)
                throw new InvalidDataException("The staged pending-scope limit was exceeded.");
            var lower = new BinaryBoundary(reader.GetString(0));
            var upper = new BinaryBoundary(reader.GetString(1));
            if (CompareBinary(upper, cursor) <= 0)
                continue; // Already covered by a prior (possibly ancestral) interval.
            if (CompareBinary(lower, end) >= 0)
                break;
            if (CompareBinary(lower, cursor) > 0)
                gaps.Add(new BinaryInterval(cursor.Text, lower.Text));
            // Advancing to the maximum endpoint unions adjacent/overlapping ranges.
            cursor = CompareBinary(upper, end) >= 0 ? end : upper;
            if (CompareBinary(cursor, end) >= 0)
                break;
        }
        if (CompareBinary(cursor, end) < 0)
            gaps.Add(new BinaryInterval(cursor.Text, end.Text));
        return gaps;
    }

    // SQLite BINARY uses UTF-8 byte ordering. UTF-16 ordinal comparison would
    // misorder supplementary Unicode filenames relative to some BMP filenames.
    private static int CompareBinary(BinaryBoundary left, BinaryBoundary right) =>
        left.Bytes.AsSpan().SequenceCompareTo(right.Bytes);

    private sealed class BinaryBoundary(string text)
    {
        public string Text { get; } = text;
        public byte[] Bytes { get; } = Encoding.UTF8.GetBytes(text);
    }

    private sealed record BinaryInterval(string Lower, string Upper);

    private static async Task StagePendingReportAsync(SqliteConnection connection, SqliteTransaction transaction,
        ScanReport report, ScanMetadata scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report.PendingScopes);
        if (report.PendingScopes.Count > 4096)
            throw new ArgumentException("A report can contain at most 4096 pending scopes.", nameof(report));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO staged_pending(scan_id, path_key, scope_path, lower_bound, upper_bound,
                reason, estimated_entries, estimated_duration_ticks, deferred_ticks)
            VALUES($scan, $key, $path, $lower, $upper, $reason, $entries, $duration, $deferred)
            ON CONFLICT(scan_id, path_key) DO UPDATE SET scope_path=excluded.scope_path,
                lower_bound=excluded.lower_bound, upper_bound=excluded.upper_bound,
                reason=excluded.reason, estimated_entries=excluded.estimated_entries,
                estimated_duration_ticks=excluded.estimated_duration_ticks, deferred_ticks=excluded.deferred_ticks;
            """;
        command.Parameters.AddWithValue("$scan", report.ScanId.ToString("N"));
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var lower = command.Parameters.Add("$lower", SqliteType.Text);
        var upper = command.Parameters.Add("$upper", SqliteType.Text);
        var reason = command.Parameters.Add("$reason", SqliteType.Integer);
        var entries = command.Parameters.Add("$entries", SqliteType.Integer);
        var duration = command.Parameters.Add("$duration", SqliteType.Integer);
        var deferred = command.Parameters.Add("$deferred", SqliteType.Integer);
        command.Prepare();
        foreach (var pending in report.PendingScopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(pending);
            var pendingPath = PathRules.Normalize(pending.ScopePath);
            var pendingKey = PathRules.Key(pendingPath);
            if (PathRules.Key(pending.RootPath) != scan.RootKey
                || (pendingKey != scan.ScopeKey && !IsStrictDescendant(pendingKey, scan.ScopeKey)))
                throw new ArgumentException("Pending scopes must match the scan root and be within its scope.", nameof(report));
            if (!Enum.IsDefined(pending.Reason) || pending.EstimatedEntries is < 0
                || pending.EstimatedDuration is { } estimate && estimate < TimeSpan.Zero)
                throw new ArgumentException("A pending scope contains invalid estimates or reason.", nameof(report));
            var prefix = DescendantPrefix(pendingKey);
            key.Value = pendingKey;
            path.Value = pendingPath;
            lower.Value = prefix;
            upper.Value = PrefixUpperBound(prefix);
            reason.Value = (int)pending.Reason;
            entries.Value = (object?)pending.EstimatedEntries ?? DBNull.Value;
            duration.Value = pending.EstimatedDuration is { } time ? time.Ticks : DBNull.Value;
            deferred.Value = pending.DeferredUtc.UtcTicks;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ScanMetadata> ReadScanAsync(SqliteConnection connection, Guid scanId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT root_key, scope_key FROM scans WHERE scan_id=$scan;";
        command.Parameters.AddWithValue("$scan", scanId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The scan was not begun or has already been published/discarded.");
        return new ScanMetadata(reader.GetString(0), reader.GetString(1));
    }

    private static string DescendantPrefix(string scopeKey) =>
        Path.EndsInDirectorySeparator(scopeKey) ? scopeKey : scopeKey + Path.DirectorySeparatorChar;

    private static bool IsStrictDescendant(string pathKey, string scopeKey) =>
        pathKey != scopeKey && pathKey.StartsWith(DescendantPrefix(scopeKey), StringComparison.Ordinal);

    // Prefix always ends with an ASCII path separator: incrementing that final character
    // gives an exact binary upper bound, without LIKE wildcard or case-folding ambiguity.
    private static string PrefixUpperBound(string prefix) => prefix[..^1] + (char)(prefix[^1] + 1);

    private static void ValidateDateRange(DateTimeOffset? from, DateTimeOffset? before)
    {
        if (from is not null && before is not null && from.Value.UtcTicks >= before.Value.UtcTicks)
            throw new ArgumentException("A date range must have its inclusive start before its exclusive end.");
    }

    private static void AddDatePredicate(SqliteCommand command, List<string> predicates,
        string column, string operation, string parameter, DateTimeOffset? value)
    {
        if (value is null)
            return;
        predicates.Add($"e.{column}{operation}{parameter}");
        command.Parameters.AddWithValue(parameter, value.Value.UtcTicks);
    }

    private sealed record ScanMetadata(string RootKey, string ScopeKey);

    private sealed class WriterLease(SqliteIndexStore owner, FileStream lockFile) : IAsyncDisposable
    {
        public FileStream LockFile { get; } = lockFile;
        public bool Initialized { get; set; }
        public ValueTask DisposeAsync() => new(owner.ReleaseWriterAsync(this));
    }

    private const string SchemaSql = """
        PRAGMA application_id=1178949169;
        PRAGMA user_version=2;
        CREATE TABLE IF NOT EXISTS roots(
            root_key TEXT PRIMARY KEY COLLATE BINARY,
            root_path TEXT NOT NULL,
            last_scan_id TEXT, last_scope_path TEXT, last_status INTEGER,
            last_published_ticks INTEGER, last_entries INTEGER, last_errors INTEGER
        );
        CREATE TABLE IF NOT EXISTS scans(
            scan_id TEXT PRIMARY KEY,
            root_key TEXT NOT NULL REFERENCES roots(root_key),
            scope_key TEXT NOT NULL COLLATE BINARY,
            scope_path TEXT NOT NULL,
            started_ticks INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS staging(
            scan_id TEXT NOT NULL REFERENCES scans(scan_id) ON DELETE CASCADE,
            path_key TEXT NOT NULL COLLATE BINARY,
            full_path TEXT NOT NULL, name TEXT NOT NULL, name_search TEXT NOT NULL,
            parent_path TEXT NOT NULL, kind INTEGER NOT NULL CHECK(kind IN (0,1)),
            size_bytes INTEGER, created_ticks INTEGER NOT NULL, modified_ticks INTEGER NOT NULL,
            PRIMARY KEY(scan_id, path_key)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS entries(
            id INTEGER PRIMARY KEY,
            root_key TEXT NOT NULL REFERENCES roots(root_key) COLLATE BINARY,
            path_key TEXT NOT NULL COLLATE BINARY,
            full_path TEXT NOT NULL, name TEXT NOT NULL, name_search TEXT NOT NULL COLLATE BINARY,
            parent_path TEXT NOT NULL, kind INTEGER NOT NULL CHECK(kind IN (0,1)),
            size_bytes INTEGER, created_ticks INTEGER NOT NULL, modified_ticks INTEGER NOT NULL,
            UNIQUE(root_key, path_key)
        );
        CREATE INDEX IF NOT EXISTS entries_name_order ON entries(name_search, full_path, root_key);
        CREATE INDEX IF NOT EXISTS entries_created ON entries(created_ticks);
        CREATE INDEX IF NOT EXISTS entries_modified ON entries(modified_ticks);
        CREATE TABLE IF NOT EXISTS directory_costs(
            root_key TEXT NOT NULL REFERENCES roots(root_key) COLLATE BINARY,
            path_key TEXT NOT NULL COLLATE BINARY, full_path TEXT NOT NULL,
            entries INTEGER NOT NULL CHECK(entries >= 0),
            duration_ticks INTEGER NOT NULL CHECK(duration_ticks >= 0), recorded_ticks INTEGER NOT NULL,
            PRIMARY KEY(root_key, path_key)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS costs_entries ON directory_costs(root_key, entries);
        CREATE INDEX IF NOT EXISTS costs_duration ON directory_costs(root_key, duration_ticks);
        CREATE TABLE IF NOT EXISTS staged_costs(
            scan_id TEXT NOT NULL REFERENCES scans(scan_id) ON DELETE CASCADE,
            path_key TEXT NOT NULL COLLATE BINARY, full_path TEXT NOT NULL,
            entries INTEGER NOT NULL CHECK(entries >= 0),
            duration_ticks INTEGER NOT NULL CHECK(duration_ticks >= 0), recorded_ticks INTEGER NOT NULL,
            PRIMARY KEY(scan_id, path_key)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS pending_scopes(
            root_key TEXT NOT NULL REFERENCES roots(root_key) COLLATE BINARY,
            path_key TEXT NOT NULL COLLATE BINARY, scope_path TEXT NOT NULL,
            lower_bound TEXT NOT NULL COLLATE BINARY, upper_bound TEXT NOT NULL COLLATE BINARY,
            reason INTEGER NOT NULL, estimated_entries INTEGER, estimated_duration_ticks INTEGER,
            deferred_ticks INTEGER NOT NULL, PRIMARY KEY(root_key, path_key)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS staged_pending(
            scan_id TEXT NOT NULL REFERENCES scans(scan_id) ON DELETE CASCADE,
            path_key TEXT NOT NULL COLLATE BINARY, scope_path TEXT NOT NULL,
            lower_bound TEXT NOT NULL COLLATE BINARY, upper_bound TEXT NOT NULL COLLATE BINARY,
            reason INTEGER NOT NULL, estimated_entries INTEGER, estimated_duration_ticks INTEGER,
            deferred_ticks INTEGER NOT NULL, PRIMARY KEY(scan_id, path_key)
        ) WITHOUT ROWID;
        CREATE VIRTUAL TABLE IF NOT EXISTS entry_names USING fts5(
            name_search, content='entries', content_rowid='id', tokenize='trigram'
        );
        CREATE TRIGGER IF NOT EXISTS entries_insert AFTER INSERT ON entries BEGIN
            INSERT INTO entry_names(rowid, name_search) VALUES(new.id, new.name_search);
        END;
        CREATE TRIGGER IF NOT EXISTS entries_delete AFTER DELETE ON entries BEGIN
            INSERT INTO entry_names(entry_names, rowid, name_search) VALUES('delete', old.id, old.name_search);
        END;
        """;

    private const string NameUpdateTriggerSql = """
        CREATE TRIGGER entries_update AFTER UPDATE OF name_search ON entries
        WHEN old.name_search IS NOT new.name_search BEGIN
            INSERT INTO entry_names(entry_names, rowid, name_search) VALUES('delete', old.id, old.name_search);
            INSERT INTO entry_names(rowid, name_search) VALUES(new.id, new.name_search);
        END;
        """;
}
