using FindEverything.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FindEverything.Engine.Tests;

public sealed class PendingScanTests
{
    private static ScanOptions Defer(DeferralPolicy policy) => TestWorkspace.FastOptions with { Deferral = policy };
    private static ScanRequest Request(TestWorkspace workspace, ScanOptions options, string? scope = null, bool onDemand = false) =>
        new(workspace.SourcePath) { Options = options, ScopePath = scope, OnDemand = onDemand };

    [Fact]
    public async Task ExplicitDeferredDirectoriesPersistAndAreVisibleEvenWhenSearchHasNoMatches()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("expensive/deeper/hidden.txt");
        workspace.WriteFile("path-deferred/deeper/hidden.txt");
        var ordinary = workspace.WriteFile("ordinary.txt");
        var options = Defer(new DeferralPolicy
        {
            DirectoryNames = ["expensive"], Paths = [Path.Combine(workspace.SourcePath, "path-deferred")]
        });
        await using (var store = new SqliteIndexStore(workspace.DatabasePath))
        {
            var report = await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(Request(workspace, options));
            Assert.Equal(ScanStatus.Deferred, report.Status);
            Assert.Empty(report.Errors);
            Assert.Equal(2, report.PendingScopes.Count);
            Assert.All(report.PendingScopes, pending => Assert.Equal(DeferralReason.ExplicitRule, pending.Reason));
            Assert.Equal(ordinary, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);
            var folders = await store.SearchAsync(new SearchQuery { Kind = EntryKind.Directory });
            Assert.Equal(2, folders.Entries.Count);
            Assert.All(folders.Entries, entry => Assert.True(entry.CoveragePending));
            Assert.True(folders.HasPendingScopes);
            var noMatches = await store.SearchAsync(new SearchQuery { NameContains = "never-present" });
            Assert.Empty(noMatches.Entries);
            Assert.True(noMatches.HasPendingScopes);
        }

        // Listing saved pending work must not reopen the source or create it after a disconnect.
        Directory.Move(workspace.SourcePath, Path.Combine(workspace.BasePath, "disconnected-source"));
        await using var reopened = new SqliteIndexStore(workspace.DatabasePath);
        var saved = await reopened.ListPendingAsync(new PendingQuery { RootPath = workspace.SourcePath });
        Assert.Equal(2, saved.Entries.Count);
        Assert.False(saved.HasMore);
        Assert.False(Directory.Exists(workspace.SourcePath));
        Assert.All(saved.Entries, pending => Assert.Equal(workspace.SourcePath, pending.RootPath));
    }

    [LinuxFact]
    public async Task DeferredDirectoryRulesPruneBeforeOpeningDeniedDirectories()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var workspace = new TestWorkspace();
        workspace.WriteFile("named/deeper/hidden.txt");
        workspace.WriteFile("by-path/deeper/hidden.txt");
        workspace.WriteFile("ordinary.txt");
        var blocked = new[] { Path.Combine(workspace.SourcePath, "named"), Path.Combine(workspace.SourcePath, "by-path") };
        var modes = blocked.Select(File.GetUnixFileMode).ToArray();
        foreach (var directory in blocked) File.SetUnixFileMode(directory, UnixFileMode.None);
        try
        {
            await using var store = new SqliteIndexStore(workspace.DatabasePath);
            var options = Defer(new DeferralPolicy { DirectoryNames = ["named"], Paths = [blocked[1]] });
            var report = await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(Request(workspace, options));

            Assert.Equal(ScanStatus.Deferred, report.Status);
            Assert.Empty(report.Errors);
            Assert.Equal(2, report.PendingScopes.Count);
            Assert.Equal(1, report.Progress.Directories);
            Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "hidden.txt" })).Entries);
        }
        finally
        {
            for (var index = 0; index < blocked.Length; index++) File.SetUnixFileMode(blocked[index], modes[index]);
        }
    }

    [Fact]
    public async Task ListingPendingWithoutAnIndexDoesNotCreateDatabaseOrSourceDirectories()
    {
        using var workspace = new TestWorkspace();
        Directory.Delete(workspace.SourcePath);
        await using var store = new SqliteIndexStore(workspace.DatabasePath);

        await Assert.ThrowsAnyAsync<FileNotFoundException>(() => store.ListPendingAsync(new PendingQuery { RootPath = workspace.SourcePath }));

        Assert.False(Directory.Exists(workspace.SourcePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace.DatabasePath)));
    }

    [Fact]
    public async Task DeferredRefreshPreservesOldRowsWithinPendingButDeletesStaleRowsOutsideIt()
    {
        using var workspace = new TestWorkspace();
        var retained = workspace.WriteFile("expensive/stale-inside.txt");
        var removed = workspace.WriteFile("stale-outside.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        File.Delete(retained);
        File.Delete(removed);
        workspace.WriteFile("expensive/new-inside.txt");

        var options = Defer(new DeferralPolicy { DirectoryNames = ["expensive"] });
        await engine.ScanAsync(Request(workspace, options));

        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "stale-outside" })).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "new-inside" })).Entries);
        var oldEntry = Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "stale-inside" })).Entries);
        Assert.Equal(retained, oldEntry.FullPath);
        Assert.True(oldEntry.CoveragePending);
    }

    [Fact]
    public async Task OnDemandCompletesDeferredScopeUpdatesAdditionsAndDeletionsAndKeepsPermanentExclusions()
    {
        using var workspace = new TestWorkspace();
        var old = workspace.WriteFile("expensive/old.txt");
        workspace.WriteFile("expensive/cache/secret.txt");
        var options = Defer(new DeferralPolicy { DirectoryNames = ["expensive", "cache", "nested"] }) with
        {
            ExcludedDirectoryNames = ["cache"], ExcludedFilePatterns = ["*.tmp"]
        };
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(Request(workspace, options with { Deferral = new() }));
        File.Delete(old);
        var fresh = workspace.WriteFile("expensive/nested/new.txt");
        workspace.WriteFile("expensive/discard.tmp");
        await engine.ScanAsync(Request(workspace, options));
        Assert.Single((await store.ListPendingAsync(new PendingQuery())).Entries);

        var report = await engine.ScanAsync(Request(workspace, options, Path.Combine(workspace.SourcePath, "expensive"), onDemand: true));

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Empty(report.PendingScopes);
        Assert.Empty((await store.ListPendingAsync(new PendingQuery())).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "old.txt" })).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "secret" })).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "discard" })).Entries);
        var result = await store.SearchAsync(new SearchQuery { Kind = EntryKind.File });
        Assert.Equal(fresh, Assert.Single(result.Entries).FullPath);
        Assert.False(result.HasPendingScopes);
        Assert.False(result.Entries[0].CoveragePending);
    }

    [Fact]
    public async Task CompletingChildCannotClearPendingAncestorOrSimilarlyNamedSibling()
    {
        using var workspace = new TestWorkspace();
        var child = workspace.WriteFile("foo/child/inside.txt");
        workspace.WriteFile("foobar/hidden.txt");
        var options = Defer(new DeferralPolicy { DirectoryNames = ["foo", "foobar"] });
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(Request(workspace, options));

        await engine.ScanAsync(Request(workspace, options, Path.Combine(workspace.SourcePath, "foo", "child"), onDemand: true));

        var pending = (await store.ListPendingAsync(new PendingQuery())).Entries;
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, entry => entry.ScopePath == Path.Combine(workspace.SourcePath, "foo"));
        Assert.Contains(pending, entry => entry.ScopePath == Path.Combine(workspace.SourcePath, "foobar"));
        Assert.Equal(child, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);
        Assert.True(Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).CoveragePending);

        await engine.ScanAsync(Request(workspace, options, Path.Combine(workspace.SourcePath, "foo"), onDemand: true));
        Assert.Equal(Path.Combine(workspace.SourcePath, "foobar"), Assert.Single((await store.ListPendingAsync(new PendingQuery())).Entries).ScopePath);
    }

    [Theory]
    [InlineData(ScanStatus.Cancelled)]
    [InlineData(ScanStatus.Partial)]
    public async Task CancelledOrPartialScanCannotClearPriorPendingCoverage(ScanStatus status)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("expensive/hidden.txt");
        var scope = Path.Combine(workspace.SourcePath, "expensive");
        var options = Defer(new DeferralPolicy { DirectoryNames = ["expensive"] });
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(Request(workspace, options));
        var before = (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray();
        var scanner = new ScriptedScanner(async (request, id, write, progress, ct) =>
        {
            await write([ScriptedScanner.FileEntry(Path.Combine(scope, "observed.txt"))], ct);
            return new ScanReport(id, request.RootPath, request.ScopePath!, status,
                new ScanProgress(1, 0, 0, 0, status == ScanStatus.Partial ? 1 : 0, TimeSpan.Zero),
                status == ScanStatus.Partial ? [new ScanError(scope, "Simulated connection failure")] : []);
        });

        await new IndexingEngine(scanner, store).ScanAsync(Request(workspace, options, scope, onDemand: true));

        Assert.Equal(before, (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray());
        var observed = await store.SearchAsync(new SearchQuery { NameContains = "observed" });
        if (status == ScanStatus.Cancelled) Assert.Empty(observed.Entries);
        else Assert.True(Assert.Single(observed.Entries).CoveragePending);
        Assert.True(observed.HasPendingScopes);
    }

    [Fact]
    public async Task FailedScannerLeavesPendingStateUnchanged()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("expensive/hidden.txt");
        var options = Defer(new DeferralPolicy { DirectoryNames = ["expensive"] });
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(Request(workspace, options));
        var before = (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray();
        var scanner = new ScriptedScanner(async (request, id, write, progress, ct) =>
        {
            await write([ScriptedScanner.FileEntry(Path.Combine(request.RootPath, "unpublished.txt"))], ct);
            throw new IOException("Simulated scanner failure");
        });

        await Assert.ThrowsAsync<IOException>(() => new IndexingEngine(scanner, store).ScanAsync(Request(workspace, options, onDemand: true)));

        Assert.Equal(before, (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray());
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "unpublished" })).Entries);
    }

    [Fact]
    public async Task MissingSourceDuringOnDemandRefreshRetainsPendingState()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("expensive/hidden.txt");
        var options = Defer(new DeferralPolicy { DirectoryNames = ["expensive"] });
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(Request(workspace, options));
        var before = (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray();
        Directory.Move(workspace.SourcePath, Path.Combine(workspace.BasePath, "disconnected-source"));

        var report = await engine.ScanAsync(Request(workspace, options, onDemand: true));

        Assert.Equal(ScanStatus.Partial, report.Status);
        Assert.Equal(before, (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedTrainingScanLetsHistoryDeferCostlyChildButNeverRoot(bool useDuration)
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 5; index++) workspace.WriteFile($"expensive/entry-{index}.txt");
        workspace.WriteFile("ordinary.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        Assert.Equal(ScanStatus.Completed, (await engine.ScanAsync(workspace.Request())).Status);
        var policy = useDuration
            ? new DeferralPolicy { HistoricalDurationThreshold = TimeSpan.FromTicks(1) }
            : new DeferralPolicy { HistoricalEntryThreshold = 2 };
        var costs = await store.GetDirectoryCostsAsync(workspace.SourcePath, workspace.SourcePath, policy);
        Assert.Contains(costs.Values, cost => cost.Path == Path.Combine(workspace.SourcePath, "expensive") && cost.Entries >= 5);

        var report = await engine.ScanAsync(Request(workspace, Defer(policy)));

        Assert.Equal(ScanStatus.Deferred, report.Status);
        var pending = Assert.Single(report.PendingScopes);
        Assert.Equal(Path.Combine(workspace.SourcePath, "expensive"), pending.ScopePath);
        Assert.Equal(useDuration ? DeferralReason.HistoricalDuration : DeferralReason.HistoricalEntryCount, pending.Reason);
        Assert.Equal(1, report.Progress.Directories);
        Assert.False(Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "ordinary.txt" })).Entries).CoveragePending);

        var onDemandOptions = Defer(policy with
        {
            DirectoryNames = ["expensive"], EntryBudget = 1, TimeBudget = TimeSpan.FromTicks(1)
        });
        var completed = await engine.ScanAsync(Request(workspace, onDemandOptions, pending.ScopePath, onDemand: true));
        Assert.Equal(ScanStatus.Completed, completed.Status);
        Assert.Empty((await store.ListPendingAsync(new PendingQuery())).Entries);
        Assert.Equal(5, (await store.SearchAsync(new SearchQuery { NameContains = "entry-" })).Entries.Count);
    }

    [Fact]
    public async Task GlobalEntryBudgetDefersRootAndProtectsAllPreviouslyUnseenRows()
    {
        using var workspace = new TestWorkspace();
        var deleted = workspace.WriteFile("deleted-but-unverified.txt");
        for (var index = 0; index < 20; index++) workspace.WriteFile($"entry-{index:00}.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        File.Delete(deleted);
        var options = Defer(new DeferralPolicy { EntryBudget = 1 });

        var report = await engine.ScanAsync(Request(workspace, options));

        Assert.Equal(ScanStatus.Deferred, report.Status);
        var pending = Assert.Single(report.PendingScopes);
        Assert.Equal(workspace.SourcePath, pending.ScopePath);
        Assert.Equal(DeferralReason.EntryBudget, pending.Reason);
        Assert.True(Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "deleted-but-unverified" })).Entries).CoveragePending);
        Assert.True((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries.Count >= 21);

        Assert.Equal(ScanStatus.Completed, (await engine.ScanAsync(Request(workspace, options, onDemand: true))).Status);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "deleted-but-unverified" })).Entries);
        Assert.Empty((await store.ListPendingAsync(new PendingQuery())).Entries);
    }

    [Fact]
    public async Task ExhaustedTimeBudgetDefersRootWithoutDeletingPriorIndex()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("valuable.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());

        var report = await engine.ScanAsync(Request(workspace, Defer(new DeferralPolicy { TimeBudget = TimeSpan.FromTicks(1) })));

        Assert.Equal(ScanStatus.Deferred, report.Status);
        Assert.Equal(DeferralReason.TimeBudget, Assert.Single(report.PendingScopes).Reason);
        Assert.Equal(workspace.SourcePath, report.PendingScopes[0].ScopePath);
        Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "valuable" })).Entries);
    }

    [Fact]
    public async Task PendingLimitCollapsesToRootAndProtectsUnverifiedOldEntries()
    {
        using var workspace = new TestWorkspace();
        var old = workspace.WriteFile("first/old.txt");
        workspace.WriteFile("second/hidden.txt");
        workspace.WriteFile("third/hidden.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        File.Delete(old);
        var options = Defer(new DeferralPolicy
        {
            DirectoryNames = ["first", "second", "third"], MaxPendingScopes = 1
        });

        var report = await engine.ScanAsync(Request(workspace, options));

        Assert.Equal(ScanStatus.Deferred, report.Status);
        var pending = Assert.Single(report.PendingScopes);
        Assert.Equal(workspace.SourcePath, pending.ScopePath);
        Assert.Equal(DeferralReason.PendingLimit, pending.Reason);
        Assert.Equal(workspace.SourcePath, Assert.Single((await store.ListPendingAsync(new PendingQuery())).Entries).ScopePath);
        Assert.True(Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "old.txt" })).Entries).CoveragePending);
    }

    [Fact]
    public async Task SuccessfulParentRescanRemovesDeletedDeferredFolderAndItsPendingRecord()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("expensive/old.txt");
        workspace.WriteFile("ordinary.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        var options = Defer(new DeferralPolicy { DirectoryNames = ["expensive"] });
        await engine.ScanAsync(Request(workspace, options));
        Assert.Single((await store.ListPendingAsync(new PendingQuery())).Entries);
        Directory.Delete(Path.Combine(workspace.SourcePath, "expensive"), recursive: true);

        var report = await engine.ScanAsync(Request(workspace, options));

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Empty((await store.ListPendingAsync(new PendingQuery())).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "old.txt" })).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "expensive" })).Entries);
        Assert.False((await store.SearchAsync(new SearchQuery())).HasPendingScopes);
    }

    [Fact]
    public async Task CompletingPendingPathWithSqlWildcardsDoesNotClearSiblingPendingCoverage()
    {
        using var workspace = new TestWorkspace();
        var first = workspace.WriteFile("area%_/first.txt");
        var sibling = workspace.WriteFile("areaXX/sibling.txt");
        var firstScope = Path.GetDirectoryName(first)!;
        var siblingScope = Path.GetDirectoryName(sibling)!;
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        var options = Defer(new DeferralPolicy { Paths = [firstScope, siblingScope] });
        await engine.ScanAsync(Request(workspace, options));

        await engine.ScanAsync(Request(workspace, options, firstScope, onDemand: true));

        Assert.Equal(siblingScope, Assert.Single((await store.ListPendingAsync(new PendingQuery())).Entries).ScopePath);
        Assert.False(Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "first.txt" })).Entries).CoveragePending);
        Assert.True(Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "sibling.txt" })).Entries).CoveragePending);
    }

    [Fact]
    public async Task SubtreeBudgetProtectsUnseenRowsWithinScopeAndDoesNotMarkSiblingsPending()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("branch/first.txt");
        var old = workspace.WriteFile("branch/unverified.txt");
        workspace.WriteFile("sibling/ordinary.txt");
        var scope = Path.Combine(workspace.SourcePath, "branch");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        File.Delete(old);

        var report = await engine.ScanAsync(Request(workspace, Defer(new DeferralPolicy { EntryBudget = 1 }), scope));

        Assert.Equal(ScanStatus.Deferred, report.Status);
        Assert.Equal(scope, Assert.Single(report.PendingScopes).ScopePath);
        Assert.True(Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "unverified.txt" })).Entries).CoveragePending);
        Assert.False(Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "ordinary.txt" })).Entries).CoveragePending);
    }

    [Fact]
    public async Task DeferredSiblingNamePrefixesDoNotProtectUnrelatedPathsFromDeletion()
    {
        using var workspace = new TestWorkspace();
        var first = workspace.WriteFile("foo/retained-first.txt");
        var second = workspace.WriteFile("foo-bar/retained-second.txt");
        var unrelated = workspace.WriteFile("foo-neighbor/remove.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        File.Delete(first);
        File.Delete(second);
        File.Delete(unrelated);

        var report = await engine.ScanAsync(Request(workspace,
            Defer(new DeferralPolicy { DirectoryNames = ["foo", "foo-bar"] })));

        Assert.Equal(ScanStatus.Deferred, report.Status);
        Assert.Equal(2, report.PendingScopes.Count);
        var remaining = (await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries;
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, entry => entry.FullPath == first && entry.CoveragePending);
        Assert.Contains(remaining, entry => entry.FullPath == second && entry.CoveragePending);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "remove.txt" })).Entries);
    }

    [Fact]
    public async Task DeferredUnicodeScopesUseSqliteBinaryOrderWhenSweepingUnprotectedGaps()
    {
        using var workspace = new TestWorkspace();
        const string firstName = "\uE000-deferred";
        const string secondName = "\U00010000-deferred";
        const string gapName = "\uF000-unprotected-gap";
        var first = workspace.WriteFile(Path.Combine(firstName, "retained-first.txt"));
        var second = workspace.WriteFile(Path.Combine(secondName, "retained-second.txt"));
        workspace.WriteFile(Path.Combine(gapName, "remove-gap.txt"));
        var before = workspace.WriteFile("a-remove-before.txt");
        var after = workspace.WriteFile("\U00010001-remove-after.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        File.Delete(first);
        File.Delete(second);
        File.Delete(before);
        File.Delete(after);
        Directory.Delete(Path.Combine(workspace.SourcePath, gapName), recursive: true);

        var report = await engine.ScanAsync(Request(workspace,
            Defer(new DeferralPolicy { DirectoryNames = [firstName, secondName] })));

        Assert.Equal(ScanStatus.Deferred, report.Status);
        var remaining = (await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries;
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, entry => entry.FullPath == first && entry.CoveragePending);
        Assert.Contains(remaining, entry => entry.FullPath == second && entry.CoveragePending);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "remove" })).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { Kind = EntryKind.Directory, NameContains = "unprotected-gap" })).Entries);
    }

    [Fact]
    public async Task OverlappingPendingParentAndChildProtectTheirUnionOnly()
    {
        using var workspace = new TestWorkspace();
        var childFile = workspace.WriteFile("parent/child/retained-child.txt");
        var siblingFile = workspace.WriteFile("parent/sibling/retained-sibling.txt");
        var outside = workspace.WriteFile("outside/remove.txt");
        var parent = Path.Combine(workspace.SourcePath, "parent");
        var child = Path.Combine(parent, "child");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());
        File.Delete(childFile);
        File.Delete(siblingFile);
        File.Delete(outside);
        var scanner = new ScriptedScanner(async (request, id, write, progress, ct) =>
        {
            var directoryRows = new[] { parent, child }.Select(path => new IndexedEntry(
                path, Path.GetFileName(path), Path.GetDirectoryName(path)!, EntryKind.Directory,
                null, Directory.GetCreationTimeUtc(path), Directory.GetLastWriteTimeUtc(path))).ToArray();
            await write(directoryRows, ct);
            return new ScanReport(id, request.RootPath, request.RootPath, ScanStatus.Deferred,
                new ScanProgress(2, 1, 0, 0, 0, TimeSpan.Zero), [])
            {
                PendingScopes = new[] { parent, child }.Select(path => new PendingScope(
                    request.RootPath, path, DeferralReason.ExplicitRule, null, null, DateTimeOffset.UtcNow)).ToArray()
            };
        });

        await new IndexingEngine(scanner, store).ScanAsync(workspace.Request());

        var result = await store.SearchAsync(new SearchQuery { Kind = EntryKind.File });
        Assert.True(result.HasPendingScopes);
        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, entry => entry.FullPath == childFile && entry.CoveragePending);
        Assert.Contains(result.Entries, entry => entry.FullPath == siblingFile && entry.CoveragePending);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "remove.txt" })).Entries);
    }

    [Fact]
    public async Task PendingPaginationAndRootFilterDoNotMixSeparateRoots()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("first/hidden.txt");
        workspace.WriteFile("second/hidden.txt");
        workspace.WriteFile("third/hidden.txt");
        var otherRoot = workspace.SourcePath + "-other";
        Directory.CreateDirectory(Path.Combine(otherRoot, "fourth"));
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        var options = Defer(new DeferralPolicy { DirectoryNames = ["first", "second", "third", "fourth"] });
        await engine.ScanAsync(Request(workspace, options));
        await engine.ScanAsync(new ScanRequest(otherRoot) { Options = options });

        var first = await store.ListPendingAsync(new PendingQuery { RootPath = workspace.SourcePath, Limit = 2 });
        var last = await store.ListPendingAsync(new PendingQuery { RootPath = workspace.SourcePath, Limit = 2, Offset = 2 });
        Assert.True(first.HasMore);
        Assert.False(last.HasMore);
        Assert.Equal(3, first.Entries.Concat(last.Entries).Select(entry => entry.ScopePath).Distinct().Count());
        Assert.All(first.Entries.Concat(last.Entries), entry => Assert.Equal(workspace.SourcePath, entry.RootPath));
        Assert.Single((await store.ListPendingAsync(new PendingQuery { RootPath = otherRoot })).Entries);
        Assert.True((await store.SearchAsync(new SearchQuery { RootPath = workspace.SourcePath, NameContains = "absent" })).HasPendingScopes);
        Assert.False((await store.SearchAsync(new SearchQuery { RootPath = Path.Combine(workspace.BasePath, "never-indexed") })).HasPendingScopes);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1001, 0)]
    [InlineData(1, -1)]
    public async Task InvalidPendingPaginationIsRejected(int limit, int offset)
    {
        using var workspace = new TestWorkspace();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListPendingAsync(new PendingQuery { Limit = limit, Offset = offset }));
    }

    public static TheoryData<DeferralPolicy> InvalidPolicies => new()
    {
        new DeferralPolicy { HistoricalEntryThreshold = 0 },
        new DeferralPolicy { HistoricalDurationThreshold = TimeSpan.Zero },
        new DeferralPolicy { EntryBudget = 0 },
        new DeferralPolicy { TimeBudget = TimeSpan.Zero },
        new DeferralPolicy { MaxPendingScopes = 0 },
        new DeferralPolicy { MaxPendingScopes = 4097 },
        new DeferralPolicy { DirectoryNames = ["parent/child"] }
    };

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public async Task InvalidDeferralPolicyIsRejectedBeforeIndexCreation(DeferralPolicy policy)
    {
        using var workspace = new TestWorkspace();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => new IndexingEngine(new FileSystemMetadataScanner(), store)
            .ScanAsync(Request(workspace, Defer(policy))));

        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace.DatabasePath)));
    }

    [Fact]
    public async Task DeferredPathOutsideRootIsRejectedEvenWhenItsNameSharesRootPrefix()
    {
        using var workspace = new TestWorkspace();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var options = Defer(new DeferralPolicy { Paths = [workspace.SourcePath + "-other"] });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => new IndexingEngine(new FileSystemMetadataScanner(), store)
            .ScanAsync(Request(workspace, options)));

        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace.DatabasePath)));
    }

    [Theory]
    [InlineData("completed-with-pending")]
    [InlineData("deferred-without-pending")]
    [InlineData("outside-scope")]
    [InlineData("wrong-root")]
    [InlineData("negative-entries")]
    [InlineData("negative-duration")]
    [InlineData("unknown-reason")]
    public async Task InvalidPendingPublicationCannotAlterPreviousIndexOrPendingState(string invalidCase)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("expensive/hidden.txt");
        workspace.WriteFile("ordinary.txt");
        var scope = Path.Combine(workspace.SourcePath, "expensive");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var options = Defer(new DeferralPolicy { DirectoryNames = ["expensive"] });
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(Request(workspace, options));
        var beforePending = (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray();
        var beforePaths = (await store.SearchAsync(new SearchQuery())).Entries.Select(entry => entry.FullPath).ToArray();
        await using var writer = await store.AcquireWriterAsync();
        await store.InitializeAsync();
        var id = Guid.NewGuid();
        await store.BeginScanAsync(id, workspace.SourcePath, scope);
        await store.StageAsync(id, [ScriptedScanner.FileEntry(Path.Combine(scope, "unpublished.txt"))]);
        var pending = new PendingScope(workspace.SourcePath, scope, DeferralReason.ExplicitRule,
            null, null, DateTimeOffset.UtcNow);
        pending = invalidCase switch
        {
            "outside-scope" => pending with { ScopePath = Path.Combine(workspace.SourcePath, "sibling") },
            "wrong-root" => pending with { RootPath = workspace.SourcePath + "-other" },
            "negative-entries" => pending with { EstimatedEntries = -1 },
            "negative-duration" => pending with { EstimatedDuration = TimeSpan.FromTicks(-1) },
            "unknown-reason" => pending with { Reason = (DeferralReason)int.MaxValue },
            _ => pending
        };
        var report = new ScanReport(id, workspace.SourcePath, scope,
            invalidCase == "completed-with-pending" ? ScanStatus.Completed : ScanStatus.Deferred,
            new ScanProgress(1, 0, 0, 0, 0, TimeSpan.Zero), [])
        {
            PendingScopes = invalidCase == "deferred-without-pending" ? [] : [pending]
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.PublishAsync(report));

        Assert.Equal(beforePending, (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray());
        Assert.Equal(beforePaths, (await store.SearchAsync(new SearchQuery())).Entries.Select(entry => entry.FullPath).ToArray());
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "unpublished" })).Entries);
    }

    [Fact]
    public async Task ReadOnlyLegacyIndexThenMigrationPreservesExistingDataAndSupportsPendingScopes()
    {
        using var workspace = new TestWorkspace();
        var existing = workspace.WriteFile("legacy-report.txt");
        workspace.WriteFile("expensive/hidden.txt");
        await CreateLegacyIndexAsync(workspace, existing);
        var before = await File.ReadAllBytesAsync(workspace.DatabasePath);
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        Assert.Equal(existing, Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "legacy-report" })).Entries).FullPath);
        Assert.Empty((await store.ListPendingAsync(new PendingQuery())).Entries);
        Assert.Empty(await store.GetDirectoryCostsAsync(workspace.SourcePath, workspace.SourcePath, new DeferralPolicy { HistoricalEntryThreshold = 1 }));
        Assert.Equal(before, await File.ReadAllBytesAsync(workspace.DatabasePath));

        await using (var writer = await store.AcquireWriterAsync())
        {
            await store.InitializeAsync();
            Assert.Equal(existing, Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "legacy-report" })).Entries).FullPath);
        }
        var options = Defer(new DeferralPolicy { DirectoryNames = ["expensive"] });
        var report = await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(Request(workspace, options));
        Assert.Equal(ScanStatus.Deferred, report.Status);
        Assert.Single((await store.ListPendingAsync(new PendingQuery())).Entries);
        Assert.Equal(existing, Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "legacy-report" })).Entries).FullPath);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, await version.ExecuteScalarAsync());
    }

    private static async Task CreateLegacyIndexAsync(TestWorkspace workspace, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.DatabasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.DatabasePath, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var schema = connection.CreateCommand();
        schema.CommandText = LegacySchemaSql;
        await schema.ExecuteNonQueryAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO roots(root_key, root_path) VALUES($root, $rootPath);
            INSERT INTO entries(root_key, path_key, full_path, name, name_search, parent_path, kind,
                size_bytes, created_ticks, modified_ticks)
                VALUES($root, $pathKey, $path, $name, $nameSearch, $parent, 0, 13, $time, $time);
            """;
        insert.Parameters.AddWithValue("$root", PathRules.Key(workspace.SourcePath));
        insert.Parameters.AddWithValue("$rootPath", workspace.SourcePath);
        insert.Parameters.AddWithValue("$pathKey", PathRules.Key(file));
        insert.Parameters.AddWithValue("$path", file);
        insert.Parameters.AddWithValue("$name", Path.GetFileName(file));
        insert.Parameters.AddWithValue("$nameSearch", Path.GetFileName(file).ToLowerInvariant());
        insert.Parameters.AddWithValue("$parent", workspace.SourcePath);
        insert.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.UtcTicks);
        await insert.ExecuteNonQueryAsync();
    }

    // The released schema-1 shape, independently created to exercise real migration.
    private const string LegacySchemaSql = """
        PRAGMA application_id=1178949169;
        PRAGMA user_version=1;
        CREATE TABLE roots(
            root_key TEXT PRIMARY KEY COLLATE BINARY, root_path TEXT NOT NULL,
            last_scan_id TEXT, last_scope_path TEXT, last_status INTEGER,
            last_published_ticks INTEGER, last_entries INTEGER, last_errors INTEGER);
        CREATE TABLE scans(
            scan_id TEXT PRIMARY KEY, root_key TEXT NOT NULL REFERENCES roots(root_key),
            scope_key TEXT NOT NULL COLLATE BINARY, scope_path TEXT NOT NULL, started_ticks INTEGER NOT NULL);
        CREATE TABLE staging(
            scan_id TEXT NOT NULL REFERENCES scans(scan_id) ON DELETE CASCADE,
            path_key TEXT NOT NULL COLLATE BINARY, full_path TEXT NOT NULL, name TEXT NOT NULL,
            name_search TEXT NOT NULL, parent_path TEXT NOT NULL, kind INTEGER NOT NULL CHECK(kind IN (0,1)),
            size_bytes INTEGER, created_ticks INTEGER NOT NULL, modified_ticks INTEGER NOT NULL,
            PRIMARY KEY(scan_id, path_key)) WITHOUT ROWID;
        CREATE TABLE entries(
            id INTEGER PRIMARY KEY, root_key TEXT NOT NULL REFERENCES roots(root_key) COLLATE BINARY,
            path_key TEXT NOT NULL COLLATE BINARY, full_path TEXT NOT NULL, name TEXT NOT NULL,
            name_search TEXT NOT NULL COLLATE BINARY, parent_path TEXT NOT NULL,
            kind INTEGER NOT NULL CHECK(kind IN (0,1)), size_bytes INTEGER,
            created_ticks INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, UNIQUE(root_key, path_key));
        CREATE INDEX entries_name_order ON entries(name_search, full_path, root_key);
        CREATE INDEX entries_created ON entries(created_ticks);
        CREATE INDEX entries_modified ON entries(modified_ticks);
        CREATE VIRTUAL TABLE entry_names USING fts5(
            name_search, content='entries', content_rowid='id', tokenize='trigram');
        CREATE TRIGGER entries_insert AFTER INSERT ON entries BEGIN
            INSERT INTO entry_names(rowid, name_search) VALUES(new.id, new.name_search);
        END;
        CREATE TRIGGER entries_delete AFTER DELETE ON entries BEGIN
            INSERT INTO entry_names(entry_names, rowid, name_search) VALUES('delete', old.id, old.name_search);
        END;
        CREATE TRIGGER entries_update AFTER UPDATE OF name_search ON entries
        WHEN old.name_search IS NOT new.name_search BEGIN
            INSERT INTO entry_names(entry_names, rowid, name_search) VALUES('delete', old.id, old.name_search);
            INSERT INTO entry_names(rowid, name_search) VALUES(new.id, new.name_search);
        END;
        """;
}
