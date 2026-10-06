using FindEverything.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FindEverything.Engine.Tests;

public sealed class SearchIntegrationTests
{
    [Fact]
    public async Task ScanSupportsKoreanShortSubstringsAndTreatsSqlWildcardsAsLiteralNames()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("분기보고서2026.txt");
        workspace.WriteFile("분기보고서2025.txt");
        var literal = workspace.WriteFile("budget_100%'final.txt");
        workspace.WriteFile("budgetX100Zfinal.txt");
        workspace.WriteFile("평범한문서.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);

        var report = await engine.ScanAsync(workspace.Request());

        Assert.Equal(ScanStatus.Completed, report.Status);
        foreach (var substring in new[] { "보고서", "보고" })
        {
            var matches = await store.SearchAsync(new SearchQuery { NameContains = substring });
            Assert.Equal(2, matches.Entries.Count);
            Assert.All(matches.Entries, entry => Assert.Contains(substring, entry.Name));
        }
        var literalMatches = await store.SearchAsync(new SearchQuery { NameContains = "_100%'" });
        Assert.Equal(literal, Assert.Single(literalMatches.Entries).FullPath);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "'; DROP TABLE entries; --" })).Entries);
        var allFiles = await store.SearchAsync(new SearchQuery { Kind = EntryKind.File });
        Assert.Equal(5, allFiles.Entries.Count);
        Assert.Equal(5, allFiles.TotalCount);
    }

    [Fact]
    public async Task SearchTextMatchesEveryLiteralTermAcrossNameAndFullPath()
    {
        using var workspace = new TestWorkspace();
        var match = workspace.WriteFile("고객_100%'자료/분기-report.txt");
        workspace.WriteFile("고객_100%'자료/notes.txt");
        workspace.WriteFile("other/분기-report.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());

        var result = await store.SearchAsync(new SearchQuery
        {
            RootPath = workspace.SourcePath,
            SearchText = "고객_100%'   분기"
        });

        Assert.Equal(match, Assert.Single(result.Entries).FullPath);
        Assert.Equal(1, result.TotalCount);
        Assert.Empty((await store.SearchAsync(new SearchQuery
            { NameContains = "고객", Kind = EntryKind.File })).Entries);
        var literalPath = await store.SearchAsync(new SearchQuery
            { SearchText = "_100%'", Kind = EntryKind.File });
        Assert.Equal(2, literalPath.TotalCount);
        Assert.All(literalPath.Entries, entry => Assert.Contains("_100%'", entry.FullPath));
    }

    [Fact]
    public async Task SearchFiltersKindsDatesAndPaginatesWithoutDuplicates()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.SourcePath, "reports"));
        var before = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 12; index++)
        {
            var path = workspace.WriteFile($"report-{index:00}.txt");
            File.SetLastWriteTimeUtc(path, (index < 6 ? before : after).UtcDateTime);
        }
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());

        var dated = await store.SearchAsync(new SearchQuery
        {
            Kind = EntryKind.File, ModifiedFromUtc = before, ModifiedBeforeUtc = after
        });
        Assert.Equal(6, dated.Entries.Count);
        Assert.All(dated.Entries, entry => Assert.Equal(before, entry.ModifiedUtc));
        var directories = await store.SearchAsync(new SearchQuery { Kind = EntryKind.Directory, NameContains = "reports" });
        Assert.Equal(EntryKind.Directory, Assert.Single(directories.Entries).Kind);
        var creationFuture = await store.SearchAsync(new SearchQuery { CreatedFromUtc = DateTimeOffset.UtcNow.AddDays(1) });
        Assert.Empty(creationFuture.Entries);
        var creationBefore = await store.SearchAsync(new SearchQuery { Kind = EntryKind.File, CreatedBeforeUtc = DateTimeOffset.UtcNow.AddDays(1) });
        Assert.Equal(12, creationBefore.Entries.Count);

        var first = await store.SearchAsync(new SearchQuery { Kind = EntryKind.File, Limit = 5 });
        var second = await store.SearchAsync(new SearchQuery { Kind = EntryKind.File, Limit = 5, Offset = 5 });
        var third = await store.SearchAsync(new SearchQuery { Kind = EntryKind.File, Limit = 5, Offset = 10 });
        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(third.HasMore);
        Assert.Equal(12, first.TotalCount);
        Assert.Equal(12, second.TotalCount);
        Assert.Equal(12, third.TotalCount);
        Assert.Equal(12, first.Entries.Concat(second.Entries).Concat(third.Entries).Select(entry => entry.FullPath).Distinct().Count());
        var beyond = await store.SearchAsync(new SearchQuery { Kind = EntryKind.File, Offset = 12 });
        Assert.Empty(beyond.Entries);
        Assert.Equal(12, beyond.TotalCount);
    }

    [Fact]
    public async Task SearchFiltersInclusiveSizesDatesAndIsolatesRootStatus()
    {
        using var workspace = new TestWorkspace();
        var firstTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var secondTime = firstTime.AddDays(1);
        var thirdTime = secondTime.AddDays(1);
        var entries = new[]
        {
            Entry(workspace.SourcePath, "folder", EntryKind.Directory, null, firstTime, thirdTime),
            Entry(workspace.SourcePath, "folder/small.txt", EntryKind.File, 5, firstTime, firstTime),
            Entry(workspace.SourcePath, "middle.txt", EntryKind.File, 10, secondTime, secondTime),
            Entry(workspace.SourcePath, "large.txt", EntryKind.File, 15, thirdTime, thirdTime)
        };
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var report = await PublishAsync(store, workspace.SourcePath, entries);

        var filtered = await store.SearchAsync(new SearchQuery
        {
            RootPath = workspace.SourcePath,
            MinSizeBytes = 10,
            MaxSizeBytes = 15,
            CreatedFromUtc = secondTime,
            CreatedBeforeUtc = thirdTime.AddTicks(1),
            ModifiedFromUtc = secondTime,
            ModifiedBeforeUtc = thirdTime.AddTicks(1),
            SortBy = EntrySortField.Size
        });
        Assert.Equal(new long?[] { 10, 15 }, filtered.Entries.Select(entry => entry.SizeBytes));
        Assert.Equal(2, filtered.TotalCount);

        var otherRoot = Path.Combine(workspace.BasePath, "other-source");
        Directory.CreateDirectory(otherRoot);
        await PublishAsync(store, otherRoot,
            [Entry(otherRoot, "foreign.txt", EntryKind.File, 99, firstTime, firstTime)]);

        var rootOnly = await store.SearchAsync(new SearchQuery { RootPath = workspace.SourcePath });
        Assert.Equal(entries.Length, rootOnly.TotalCount);
        Assert.Equal(entries.Length + 1, (await store.SearchAsync(new SearchQuery())).TotalCount);
        var status = Assert.IsType<IndexRootStatus>(await store.GetRootStatusAsync(workspace.SourcePath));
        Assert.Equal(workspace.SourcePath, status.RootPath);
        Assert.Equal(report.ScanId, status.LastScanId);
        Assert.Equal(workspace.SourcePath, status.LastScopePath);
        Assert.Equal(ScanStatus.Completed, status.LastStatus);
        Assert.NotNull(status.LastPublishedUtc);
        Assert.Equal(entries.Length, status.EntryCount);
        Assert.Equal(0, status.LastErrorCount);
        Assert.False(status.HasPendingScopes);
        Assert.Null(await store.GetRootStatusAsync(Path.Combine(workspace.BasePath, "not-indexed")));
    }

    [Fact]
    public async Task EverySortDirectionHasStablePagingAndNullSizesAlwaysSortLast()
    {
        using var workspace = new TestWorkspace();
        var start = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            Entry(workspace.SourcePath, "zeta", EntryKind.Directory, null, start.AddDays(2), start),
            Entry(workspace.SourcePath, "b/alpha.txt", EntryKind.File, 30, start, start.AddDays(3)),
            Entry(workspace.SourcePath, "a/charlie.txt", EntryKind.File, 10, start.AddDays(3), start.AddDays(2)),
            Entry(workspace.SourcePath, "delta.txt", EntryKind.File, 20, start.AddDays(1), start.AddDays(1)),
            Entry(workspace.SourcePath, "a/bravo.txt", EntryKind.File, 20, start.AddDays(1), start.AddDays(1))
        };
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await PublishAsync(store, workspace.SourcePath, entries);

        foreach (var field in Enum.GetValues<EntrySortField>())
        foreach (var direction in Enum.GetValues<SortDirection>())
        {
            var query = new SearchQuery
            {
                RootPath = workspace.SourcePath,
                SortBy = field,
                SortDirection = direction,
                Limit = 100
            };
            var full = await store.SearchAsync(query);
            Assert.Equal(ExpectedOrder(entries, field, direction), full.Entries.Select(entry => entry.FullPath));
            Assert.Equal((long)entries.Length, full.TotalCount);

            var paged = new List<string>();
            for (var offset = 0; offset < entries.Length; offset += 2)
            {
                var page = await store.SearchAsync(query with { Limit = 2, Offset = offset });
                paged.AddRange(page.Entries.Select(entry => entry.FullPath));
                Assert.Equal((long)entries.Length, page.TotalCount);
                Assert.Equal(offset + page.Entries.Count < entries.Length, page.HasMore);
            }
            Assert.Equal(full.Entries.Select(entry => entry.FullPath), paged);
            Assert.Equal(entries.Length, paged.Distinct().Count());
            if (field == EntrySortField.Size)
                Assert.Null(full.Entries[^1].SizeBytes);
        }
    }

    [Fact]
    public async Task VersionTwoMigrationPreservesRowsAndRebuildsUnicodePathSearch()
    {
        using var workspace = new TestWorkspace();
        var legacyPath = Path.Combine(workspace.SourcePath, "ÄBC", "legacy-report.txt");
        await CreateVersionTwoIndexAsync(workspace, legacyPath);
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        Assert.Equal(legacyPath,
            Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "legacy" })).Entries).FullPath);
        var legacySearch = await store.SearchAsync(new SearchQuery { SearchText = "äbc legacy" });
        Assert.Equal(legacyPath, Assert.Single(legacySearch.Entries).FullPath);
        Assert.Equal(1, legacySearch.TotalCount);

        await using (var writer = await store.AcquireWriterAsync())
            await store.InitializeAsync();

        var migrated = await store.SearchAsync(new SearchQuery { SearchText = "äbc legacy" });
        Assert.Equal(legacyPath, Assert.Single(migrated.Entries).FullPath);
        Assert.Equal(1, migrated.TotalCount);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path_search, (SELECT user_version FROM pragma_user_version) FROM entries;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(legacyPath.ToLowerInvariant(), reader.GetString(0));
        Assert.Equal(3, reader.GetInt32(1));
    }

    [Fact]
    public async Task RootStatusIsAbsentUntilAFirstScanIsPublished()
    {
        using var workspace = new TestWorkspace();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var cancelledScanner = new ScriptedScanner((request, scanId, _, _, _) =>
            Task.FromResult(new ScanReport(scanId, request.RootPath, request.ScopePath!,
                ScanStatus.Cancelled, new ScanProgress(0, 0, 0, 0, 0, TimeSpan.Zero), [])));

        var cancelled = await new IndexingEngine(cancelledScanner, store).ScanAsync(workspace.Request());

        Assert.Equal(ScanStatus.Cancelled, cancelled.Status);
        Assert.Null(await store.GetRootStatusAsync(workspace.SourcePath));

        var failedRoot = Path.Combine(workspace.BasePath, "failed-source");
        Directory.CreateDirectory(failedRoot);
        var failedScanner = new ScriptedScanner((_, _, _, _, _) =>
            Task.FromException<ScanReport>(new IOException("Simulated scanner failure.")));
        await Assert.ThrowsAsync<IOException>(() => new IndexingEngine(failedScanner, store)
            .ScanAsync(new ScanRequest(failedRoot) { Options = TestWorkspace.FastOptions }));
        Assert.Null(await store.GetRootStatusAsync(failedRoot));
    }

    [Fact]
    public async Task ScanPublishesProgressAndDoesNotChangeSourceContentsOrTimestamps()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.WriteFile("valuable.txt", "These bytes must remain untouched.\n");
        File.SetLastWriteTimeUtc(file, new DateTime(2023, 4, 5, 6, 7, 8, DateTimeKind.Utc));
        var contents = await File.ReadAllBytesAsync(file);
        var created = File.GetCreationTimeUtc(file);
        var modified = File.GetLastWriteTimeUtc(file);
        var seen = new List<ScanProgress>();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);

        var report = await new IndexingEngine(new FileSystemMetadataScanner(), store)
            .ScanAsync(workspace.Request(), new InlineProgress<ScanProgress>(seen.Add));

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.NotEmpty(seen);
        Assert.True(report.Progress.Entries >= 1);
        Assert.Equal(contents, await File.ReadAllBytesAsync(file));
        Assert.Equal(created, File.GetCreationTimeUtc(file));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(file));
        Assert.Equal([file], Directory.GetFiles(workspace.SourcePath, "*", SearchOption.AllDirectories));
    }

    private static IndexedEntry Entry(string rootPath, string relativePath, EntryKind kind,
        long? size, DateTimeOffset created, DateTimeOffset modified)
    {
        var path = Path.Combine(rootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        return new IndexedEntry(path, Path.GetFileName(path), Path.GetDirectoryName(path)!,
            kind, size, created, modified);
    }

    private static async Task<ScanReport> PublishAsync(SqliteIndexStore store, string rootPath,
        IReadOnlyList<IndexedEntry> entries)
    {
        var scanner = new ScriptedScanner(async (request, scanId, writeBatch, _, cancellationToken) =>
        {
            await writeBatch(entries, cancellationToken);
            var progress = new ScanProgress(entries.Count,
                entries.Count(entry => entry.Kind == EntryKind.Directory), 0, 0, 0, TimeSpan.Zero);
            return new ScanReport(scanId, request.RootPath, request.ScopePath!, ScanStatus.Completed,
                progress, []);
        });
        return await new IndexingEngine(scanner, store).ScanAsync(new ScanRequest(rootPath)
        {
            Options = TestWorkspace.FastOptions
        });
    }

    private static IReadOnlyList<string> ExpectedOrder(IEnumerable<IndexedEntry> entries,
        EntrySortField field, SortDirection direction)
    {
        var ascending = direction == SortDirection.Ascending;
        IOrderedEnumerable<IndexedEntry> ordered = field switch
        {
            EntrySortField.Name when ascending => entries.OrderBy(
                entry => entry.Name.ToLowerInvariant(), StringComparer.Ordinal),
            EntrySortField.Name => entries.OrderByDescending(
                entry => entry.Name.ToLowerInvariant(), StringComparer.Ordinal),
            EntrySortField.Path when ascending => entries.OrderBy(
                entry => entry.FullPath.ToLowerInvariant(), StringComparer.Ordinal),
            EntrySortField.Path => entries.OrderByDescending(
                entry => entry.FullPath.ToLowerInvariant(), StringComparer.Ordinal),
            EntrySortField.Kind when ascending => entries.OrderBy(entry => entry.Kind),
            EntrySortField.Kind => entries.OrderByDescending(entry => entry.Kind),
            EntrySortField.Size when ascending => entries.OrderBy(entry => entry.SizeBytes is null)
                .ThenBy(entry => entry.SizeBytes.GetValueOrDefault()),
            EntrySortField.Size => entries.OrderBy(entry => entry.SizeBytes is null)
                .ThenByDescending(entry => entry.SizeBytes.GetValueOrDefault()),
            EntrySortField.Created when ascending => entries.OrderBy(entry => entry.CreatedUtc.UtcTicks),
            EntrySortField.Created => entries.OrderByDescending(entry => entry.CreatedUtc.UtcTicks),
            EntrySortField.Modified when ascending => entries.OrderBy(entry => entry.ModifiedUtc.UtcTicks),
            EntrySortField.Modified => entries.OrderByDescending(entry => entry.ModifiedUtc.UtcTicks),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        return ordered.ThenBy(entry => PathRules.Key(entry.FullPath), StringComparer.Ordinal)
            .Select(entry => entry.FullPath)
            .ToArray();
    }

    private static async Task CreateVersionTwoIndexAsync(TestWorkspace workspace, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.DatabasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.DatabasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA application_id=1178949169;
            PRAGMA user_version=2;
            CREATE TABLE roots(
                root_key TEXT PRIMARY KEY COLLATE BINARY, root_path TEXT NOT NULL,
                last_scan_id TEXT, last_scope_path TEXT, last_status INTEGER,
                last_published_ticks INTEGER, last_entries INTEGER, last_errors INTEGER);
            CREATE TABLE scans(
                scan_id TEXT PRIMARY KEY, root_key TEXT NOT NULL REFERENCES roots(root_key),
                scope_key TEXT NOT NULL COLLATE BINARY, scope_path TEXT NOT NULL,
                started_ticks INTEGER NOT NULL);
            CREATE TABLE staging(
                scan_id TEXT NOT NULL REFERENCES scans(scan_id) ON DELETE CASCADE,
                path_key TEXT NOT NULL COLLATE BINARY, full_path TEXT NOT NULL, name TEXT NOT NULL,
                name_search TEXT NOT NULL, parent_path TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK(kind IN (0,1)), size_bytes INTEGER,
                created_ticks INTEGER NOT NULL, modified_ticks INTEGER NOT NULL,
                PRIMARY KEY(scan_id, path_key)) WITHOUT ROWID;
            CREATE TABLE entries(
                id INTEGER PRIMARY KEY, root_key TEXT NOT NULL REFERENCES roots(root_key) COLLATE BINARY,
                path_key TEXT NOT NULL COLLATE BINARY, full_path TEXT NOT NULL, name TEXT NOT NULL,
                name_search TEXT NOT NULL COLLATE BINARY, parent_path TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK(kind IN (0,1)), size_bytes INTEGER,
                created_ticks INTEGER NOT NULL, modified_ticks INTEGER NOT NULL,
                UNIQUE(root_key, path_key));
            CREATE TABLE pending_scopes(
                root_key TEXT NOT NULL REFERENCES roots(root_key) COLLATE BINARY,
                path_key TEXT NOT NULL COLLATE BINARY, scope_path TEXT NOT NULL,
                lower_bound TEXT NOT NULL COLLATE BINARY, upper_bound TEXT NOT NULL COLLATE BINARY,
                reason INTEGER NOT NULL, estimated_entries INTEGER, estimated_duration_ticks INTEGER,
                deferred_ticks INTEGER NOT NULL, PRIMARY KEY(root_key, path_key)) WITHOUT ROWID;
            CREATE VIRTUAL TABLE entry_names USING fts5(
                name_search, content='entries', content_rowid='id', tokenize='trigram');
            CREATE TRIGGER entries_insert AFTER INSERT ON entries BEGIN
                INSERT INTO entry_names(rowid, name_search) VALUES(new.id, new.name_search);
            END;
            CREATE TRIGGER entries_delete AFTER DELETE ON entries BEGIN
                INSERT INTO entry_names(entry_names, rowid, name_search)
                    VALUES('delete', old.id, old.name_search);
            END;
            INSERT INTO roots(root_key, root_path) VALUES($root, $rootPath);
            INSERT INTO entries(root_key, path_key, full_path, name, name_search, parent_path,
                kind, size_bytes, created_ticks, modified_ticks)
            VALUES($root, $pathKey, $path, $name, $nameSearch, $parent, 0, 13, $time, $time);
            """;
        command.Parameters.AddWithValue("$root", PathRules.Key(workspace.SourcePath));
        command.Parameters.AddWithValue("$rootPath", workspace.SourcePath);
        command.Parameters.AddWithValue("$pathKey", PathRules.Key(path));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$name", Path.GetFileName(path));
        command.Parameters.AddWithValue("$nameSearch", Path.GetFileName(path).ToLowerInvariant());
        command.Parameters.AddWithValue("$parent", Path.GetDirectoryName(path)!);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.UtcTicks);
        await command.ExecuteNonQueryAsync();
    }
}
