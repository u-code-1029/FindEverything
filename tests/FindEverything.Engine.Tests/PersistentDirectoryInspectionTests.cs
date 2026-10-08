using FindEverything.Engine;
using Xunit;

namespace FindEverything.Engine.Tests;

public sealed class PersistentDirectoryInspectionTests
{
    [Fact]
    public async Task ScanDistinguishesKeepingAPrunedDirectoryFromExcludingItsEntireSubtree()
    {
        using var workspace = new TestWorkspace();
        var hiddenFile = CanonicalTempPath(workspace.WriteFile("terminal/deep/hidden.txt"));
        var excludedFile = CanonicalTempPath(workspace.WriteFile(
            "deferred-excluded/deep/omitted.txt"));
        var retainedFile = CanonicalTempPath(workspace.WriteFile("sibling/leaf/keep.txt"));
        var sourcePath = CanonicalTempPath(workspace.SourcePath);
        var terminalPath = Path.Combine(sourcePath, "terminal");
        var excludedPath = Path.Combine(sourcePath, "deferred-excluded");
        var candidates = new List<DirectoryCandidate>();
        await using var store = new SqliteIndexStore(CanonicalTempPath(workspace.DatabasePath));
        var request = new ScanRequest(sourcePath)
        {
            Options = TestWorkspace.FastOptions with
            {
                Deferral = new DeferralPolicy
                {
                    DirectoryNames = ["deferred-excluded"],
                },
            },
            InspectDirectory = candidate =>
            {
                candidates.Add(candidate);
                if (PathRules.Comparer.Equals(candidate.FullPath, terminalPath))
                    return DirectoryTraversalDecision.SkipDescendants;
                return PathRules.Comparer.Equals(candidate.FullPath, excludedPath)
                    ? DirectoryTraversalDecision.ExcludeSubtree
                    : DirectoryTraversalDecision.Continue;
            },
        };

        var report = await new IndexingEngine(new FileSystemMetadataScanner(), store)
            .ScanAsync(request);
        var indexed = await store.SearchAsync(new SearchQuery
        {
            RootPath = sourcePath,
            Limit = 100,
        });

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Contains(candidates, candidate =>
            PathRules.Comparer.Equals(candidate.FullPath, sourcePath)
            && candidate.Depth == 0);
        Assert.Contains(candidates, candidate =>
            PathRules.Comparer.Equals(candidate.FullPath, terminalPath));
        Assert.Contains(candidates, candidate =>
            PathRules.Comparer.Equals(candidate.FullPath, excludedPath)
            && candidate.CoveragePending);
        Assert.DoesNotContain(candidates, candidate => candidate.Name == "deep");
        Assert.Contains(candidates, candidate => candidate.Name == "leaf");
        Assert.Contains(indexed.Entries, entry =>
            PathRules.Comparer.Equals(entry.FullPath, terminalPath)
            && entry.Kind == EntryKind.Directory);
        Assert.Contains(indexed.Entries, entry =>
            PathRules.Comparer.Equals(entry.FullPath, retainedFile));
        Assert.DoesNotContain(indexed.Entries, entry =>
            PathRules.Comparer.Equals(entry.FullPath, hiddenFile)
            || PathRules.Comparer.Equals(entry.FullPath, excludedPath)
            || PathRules.Comparer.Equals(entry.FullPath, excludedFile)
            || entry.Name == "deep");
        Assert.Empty(report.PendingScopes);
        Assert.Equal(1, report.Progress.ExcludedEntries);
    }

    [Fact]
    public async Task ScopedScanEmitsTheScopeDirectoryWhenInspectionPrunesItBeforeOpening()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("terminal/deep/hidden.txt");
        var sourcePath = CanonicalTempPath(workspace.SourcePath);
        var scopePath = Path.Combine(sourcePath, "terminal");
        var candidates = new List<DirectoryCandidate>();
        var indexed = new List<IndexedEntry>();
        var request = new ScanRequest(sourcePath)
        {
            ScopePath = scopePath,
            Options = TestWorkspace.FastOptions,
            InspectDirectory = candidate =>
            {
                candidates.Add(candidate);
                return DirectoryTraversalDecision.SkipDescendants;
            },
        };

        var report = await new FileSystemMetadataScanner().ScanAsync(
            request,
            Guid.NewGuid(),
            (batch, _) =>
            {
                indexed.AddRange(batch);
                return Task.CompletedTask;
            });

        var candidate = Assert.Single(candidates);
        Assert.True(PathRules.Comparer.Equals(scopePath, candidate.FullPath));
        var entry = Assert.Single(indexed);
        Assert.True(PathRules.Comparer.Equals(scopePath, entry.FullPath));
        Assert.Equal(EntryKind.Directory, entry.Kind);
        Assert.Equal(ScanStatus.Completed, report.Status);
    }

    private static string CanonicalTempPath(string path) =>
        OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + path
            : path;
}
