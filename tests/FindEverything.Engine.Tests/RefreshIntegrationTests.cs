using FindEverything.Engine;
using Xunit;

namespace FindEverything.Engine.Tests;

public sealed class RefreshIntegrationTests
{
    [Fact]
    public async Task CompletedFullRefreshRemovesDeletedPathsAndReplacesRenamedPaths()
    {
        using var workspace = new TestWorkspace();
        var original = workspace.WriteFile("original.txt");
        workspace.WriteFile("removed-folder/old.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        var renamed = Path.Combine(workspace.SourcePath, "renamed.txt");
        File.Move(original, renamed);
        Directory.Delete(Path.Combine(workspace.SourcePath, "removed-folder"), recursive: true);
        workspace.WriteFile("new-folder/new.txt");

        var report = await engine.ScanAsync(workspace.Request());

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "original" })).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "old.txt" })).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "removed-folder" })).Entries);
        Assert.Equal(renamed, Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "renamed.txt" })).Entries).FullPath);
        Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "new.txt" })).Entries);
    }

    [Fact]
    public async Task SubtreeRefreshRemovesOnlyStaleEntriesWithinItsScope()
    {
        using var workspace = new TestWorkspace();
        var staleInScope = workspace.WriteFile("area-a/old.txt");
        var sibling = workspace.WriteFile("area-b/keep-until-refreshed.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        File.Delete(staleInScope);
        File.Delete(sibling);
        var fresh = workspace.WriteFile("area-a/fresh.txt");

        var report = await engine.ScanAsync(workspace.Request(Path.Combine(workspace.SourcePath, "area-a")));

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "old.txt" })).Entries);
        Assert.Equal(fresh, Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "fresh.txt" })).Entries).FullPath);
        Assert.Equal(sibling, Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "keep-until-refreshed" })).Entries).FullPath);
    }

    [Fact]
    public async Task SubtreeRefreshUpdatesScopeDirectoryMetadataAndPreservesSiblings()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("scope/child.txt");
        var sibling = workspace.WriteFile("sibling/keep.txt");
        var scope = Path.Combine(workspace.SourcePath, "scope");
        Directory.SetLastWriteTimeUtc(scope, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        var updated = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        Directory.SetLastWriteTimeUtc(scope, updated.UtcDateTime);
        File.Delete(sibling);

        var report = await engine.ScanAsync(workspace.Request(scope));

        Assert.Equal(ScanStatus.Completed, report.Status);
        var scopeEntry = Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.Directory, NameContains = "scope" })).Entries);
        Assert.Equal(scope, scopeEntry.FullPath);
        Assert.Equal(updated, scopeEntry.ModifiedUtc);
        Assert.Equal(sibling, Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "keep.txt" })).Entries).FullPath);
        var rootFiles = await store.SearchAsync(new SearchQuery { RootPath = workspace.SourcePath, Kind = EntryKind.File });
        Assert.Equal(2, rootFiles.Entries.Count);
    }

    [Fact]
    public async Task SubtreePathWithSqlWildcardsDoesNotDeleteSimilarSiblingPaths()
    {
        using var workspace = new TestWorkspace();
        var stale = workspace.WriteFile("area%_/stale.txt");
        var sibling = workspace.WriteFile("areaXX/keep.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        File.Delete(stale);
        File.Delete(sibling);

        await engine.ScanAsync(workspace.Request(Path.Combine(workspace.SourcePath, "area%_")));

        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "stale.txt" })).Entries);
        Assert.Equal(sibling, Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "keep.txt" })).Entries).FullPath);
    }

    [Fact]
    public async Task SeparateRootsCanShareAnIndexWithoutDeletingEachOthersEntries()
    {
        using var workspace = new TestWorkspace();
        var first = workspace.WriteFile("first-root.txt");
        var secondRoot = workspace.SourcePath + "-other";
        Directory.CreateDirectory(secondRoot);
        var second = Path.Combine(secondRoot, "second-root.txt");
        File.WriteAllText(second, "other root");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        await engine.ScanAsync(new ScanRequest(secondRoot) { Options = TestWorkspace.FastOptions });
        File.Delete(first);
        await engine.ScanAsync(workspace.Request());

        Assert.Empty((await store.SearchAsync(new SearchQuery { RootPath = workspace.SourcePath })).Entries);
        Assert.Equal(second, Assert.Single((await store.SearchAsync(new SearchQuery { RootPath = secondRoot })).Entries).FullPath);
    }

    [Fact]
    public async Task DeepFileChangesAreObservedEvenWhenAncestorDirectoryTimestampsAreUnchanged()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.WriteFile("level-one/level-two/deep.txt");
        var originalTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var modifiedTime = originalTime.AddDays(1);
        File.SetLastWriteTimeUtc(file, originalTime);
        var ancestors = new[]
        {
            workspace.SourcePath,
            Path.Combine(workspace.SourcePath, "level-one"),
            Path.GetDirectoryName(file)!
        };
        var originalDirectoryTimes = ancestors.Select(Directory.GetLastWriteTimeUtc).ToArray();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        await File.AppendAllTextAsync(file, "Updated deep file contents");
        File.SetLastWriteTimeUtc(file, modifiedTime);
        for (var index = 0; index < ancestors.Length; index++)
            Directory.SetLastWriteTimeUtc(ancestors[index], originalDirectoryTimes[index]);

        await engine.ScanAsync(workspace.Request());

        Assert.Equal(originalDirectoryTimes, ancestors.Select(Directory.GetLastWriteTimeUtc).ToArray());
        var entry = Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "deep.txt" })).Entries);
        Assert.Equal(new DateTimeOffset(modifiedTime), entry.ModifiedUtc);
        Assert.Equal(new FileInfo(file).Length, entry.SizeBytes);
    }

    [Fact]
    public async Task SearchesKeepPreviousIndexVisibleWhileNewScanBatchesAreStaged()
    {
        using var workspace = new TestWorkspace();
        var original = workspace.WriteFile("original.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var replacement = Path.Combine(workspace.SourcePath, "replacement.txt");
        var scanner = new ScriptedScanner(async (request, id, write, progress, ct) =>
        {
            await write([ScriptedScanner.FileEntry(replacement)], ct);
            staged.SetResult();
            await releaseScan.Task.WaitAsync(ct);
            return new ScanReport(id, request.RootPath, request.RootPath, ScanStatus.Completed,
                new ScanProgress(1, 0, 0, 0, 0, TimeSpan.Zero), []);
        });
        var scan = new IndexingEngine(scanner, store).ScanAsync(workspace.Request(), cancellationToken: deadline.Token);
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(original, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);
            Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "replacement" })).Entries);
        }
        finally
        {
            releaseScan.TrySetResult();
            await scan;
        }

        Assert.Equal(replacement, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);
    }

    [Fact]
    public async Task ReinitializingSameWriterLeasePreservesActiveStagedScan()
    {
        using var workspace = new TestWorkspace();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await using var writer = await store.AcquireWriterAsync();
        await store.InitializeAsync();
        var scanId = Guid.NewGuid();
        var file = ScriptedScanner.FileEntry(Path.Combine(workspace.SourcePath, "staged.txt"));
        await store.BeginScanAsync(scanId, workspace.SourcePath, workspace.SourcePath);
        await store.StageAsync(scanId, [file]);

        await store.InitializeAsync();
        await store.PublishAsync(new ScanReport(scanId, workspace.SourcePath, workspace.SourcePath,
            ScanStatus.Completed, new ScanProgress(1, 0, 0, 0, 0, TimeSpan.Zero), []));

        Assert.Equal(file.FullPath, Assert.Single((await store.SearchAsync(new SearchQuery())).Entries).FullPath);
    }

    [Fact]
    public async Task NewWriterLeaseDiscardsAbandonedStagingAndPreservesPublishedIndex()
    {
        using var workspace = new TestWorkspace();
        var original = workspace.WriteFile("original.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());
        var orphanId = Guid.NewGuid();
        await using (var writer = await store.AcquireWriterAsync())
        {
            await store.InitializeAsync();
            await store.BeginScanAsync(orphanId, workspace.SourcePath, workspace.SourcePath);
            await store.StageAsync(orphanId, [ScriptedScanner.FileEntry(Path.Combine(workspace.SourcePath, "orphan.txt"))]);
        }

        await using (var nextWriter = await store.AcquireWriterAsync())
        {
            await store.InitializeAsync();
            var orphanReport = new ScanReport(orphanId, workspace.SourcePath, workspace.SourcePath,
                ScanStatus.Completed, new ScanProgress(1, 0, 0, 0, 0, TimeSpan.Zero), []);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.PublishAsync(orphanReport));
            Assert.Equal(original, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);
        }

        Assert.Equal(ScanStatus.Completed, (await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request())).Status);
    }

    [Fact]
    public async Task PartialRefreshUpsertsObservedEntriesButNeverDeletesUnseenEntries()
    {
        using var workspace = new TestWorkspace();
        var old = workspace.WriteFile("temporarily-inaccessible.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());
        File.Delete(old);
        var observed = Path.Combine(workspace.SourcePath, "observed.txt");
        var scanner = new ScriptedScanner(async (request, id, write, progress, ct) =>
        {
            await write([ScriptedScanner.FileEntry(observed)], ct);
            var state = new ScanProgress(1, 0, 0, 0, 1, TimeSpan.Zero);
            progress?.Report(state);
            return new ScanReport(id, request.RootPath, request.ScopePath ?? request.RootPath,
                ScanStatus.Partial, state, [new ScanError(request.RootPath, "Simulated access failure")]);
        });

        var report = await new IndexingEngine(scanner, store).ScanAsync(workspace.Request());

        Assert.Equal(ScanStatus.Partial, report.Status);
        var entries = (await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.FullPath == old);
        Assert.Contains(entries, entry => entry.FullPath == observed);
    }

    [Fact]
    public async Task CancellationAfterStagingPublishesNothingAndRetainsPreviousIndex()
    {
        using var workspace = new TestWorkspace();
        var original = workspace.WriteFile("original.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());
        using var cancellation = new CancellationTokenSource();
        var staged = Path.Combine(workspace.SourcePath, "must-not-publish.txt");
        var scanner = new ScriptedScanner(async (request, id, write, progress, ct) =>
        {
            await write([ScriptedScanner.FileEntry(staged)], ct);
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return new ScanReport(id, request.RootPath, request.RootPath, ScanStatus.Completed,
                new ScanProgress(1, 0, 0, 0, 0, TimeSpan.Zero), []);
        });

        try
        {
            var report = await new IndexingEngine(scanner, store).ScanAsync(workspace.Request(), cancellationToken: cancellation.Token);
            Assert.Equal(ScanStatus.Cancelled, report.Status);
        }
        catch (OperationCanceledException) { }

        Assert.Equal(original, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "must-not-publish" })).Entries);
        // A later scan must be able to acquire the writer and replace its own staging data.
        Assert.Equal(ScanStatus.Completed, (await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request())).Status);
    }

    [Fact]
    public async Task FirstCancelledScanLeavesNoSearchableEntries()
    {
        using var workspace = new TestWorkspace();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var scanner = new ScriptedScanner(async (request, id, write, progress, ct) =>
        {
            await write([ScriptedScanner.FileEntry(Path.Combine(request.RootPath, "staged.txt"))], ct);
            return new ScanReport(id, request.RootPath, request.RootPath, ScanStatus.Cancelled,
                new ScanProgress(1, 0, 0, 0, 0, TimeSpan.Zero), []);
        });

        var report = await new IndexingEngine(scanner, store).ScanAsync(workspace.Request());

        Assert.Equal(ScanStatus.Cancelled, report.Status);
        Assert.Empty((await store.SearchAsync(new SearchQuery())).Entries);
    }

    [Fact]
    public async Task UnexpectedScannerFailureDoesNotPublishStagedRowsAndReleasesWriter()
    {
        using var workspace = new TestWorkspace();
        var original = workspace.WriteFile("original.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());
        var scanner = new ScriptedScanner(async (request, id, write, progress, ct) =>
        {
            await write([ScriptedScanner.FileEntry(Path.Combine(request.RootPath, "staged.txt"))], ct);
            throw new InvalidOperationException("Simulated scanner failure after a batch");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new IndexingEngine(scanner, store).ScanAsync(workspace.Request()));

        Assert.Equal(original, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);
        Assert.Equal(ScanStatus.Completed, (await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request())).Status);
    }

    [Fact]
    public async Task DifferentStoresForSameDatabaseCannotWriteConcurrently()
    {
        using var workspace = new TestWorkspace();
        await using var firstStore = new SqliteIndexStore(workspace.DatabasePath);
        await using var secondStore = new SqliteIndexStore(workspace.DatabasePath);
        var firstWriter = await firstStore.AcquireWriterAsync();
        Task<IAsyncDisposable>? secondAttempt = null;
        var secondRejected = false;
        try
        {
            await firstStore.InitializeAsync();
            secondAttempt = secondStore.AcquireWriterAsync();
            await Task.Delay(100);
            if (secondAttempt.IsCompleted)
            {
                try
                {
                    await using var unexpectedWriter = await secondAttempt;
                    Assert.Fail("A second writer acquired the same database while the first lease was held.");
                }
                catch (IOException) { secondRejected = true; }
            }
        }
        finally { await firstWriter.DisposeAsync(); }

        await using var nextWriter = secondRejected
            ? await secondStore.AcquireWriterAsync()
            : await secondAttempt!.WaitAsync(TimeSpan.FromSeconds(5));
        await secondStore.InitializeAsync();
    }
}
