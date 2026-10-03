using System.Text.RegularExpressions;
using FindEverything.Engine;
using Xunit;

namespace FindEverything.Engine.Tests;

public sealed class ExclusionDiagnosticsTests
{
    [Theory]
    [InlineData(RegexMatchMode.Full, 1)]
    [InlineData(RegexMatchMode.Partial, 3)]
    public async Task RegexPrunesDirectoryNamesOnlyAtEveryDepth(RegexMatchMode mode, int excluded)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("cache/deep/hidden.txt");
        workspace.WriteFile("project/prefix-cache/deep/hidden.txt");
        workspace.WriteFile("cache-suffix/hidden.txt");
        workspace.WriteFile("cache.txt");
        workspace.WriteFile("documents/keep.txt");
        var options = TestWorkspace.FastOptions with
        {
            ExcludedDirectoryNameRegexes = [new() { Pattern = "cache", MatchMode = mode }]
        };
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var report = await new IndexingEngine(new FileSystemMetadataScanner(), store)
            .ScanAsync(workspace.Request() with { Options = options });

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Equal(excluded, report.Progress.ExcludedEntries);
        Assert.Equal(excluded, report.Diagnostics.Exclusions.DirectoryNameRegexes);
        Assert.Equal(excluded, report.Diagnostics.Exclusions.Directories);
        Assert.Equal(0, report.Diagnostics.Exclusions.Files);
        Assert.All(report.Diagnostics.ExcludedPaths, item => Assert.Equal("cache", item.Rule));
        var files = (await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries;
        Assert.Contains(files, item => item.Name == "cache.txt");
        Assert.Equal(mode == RegexMatchMode.Full ? 4 : 2, files.Count);
        Assert.DoesNotContain(files, item => PathRules.IsWithin(item.FullPath, Path.Combine(workspace.SourcePath, "cache")));
    }

    [Theory]
    [InlineData("cache|cache-long", "cache-long", true)]
    [InlineData("cache", "cache-long", false)]
    [InlineData("임시_[0-9]+", "임시_123", true)]
    [InlineData("cache", "cache\n", false)]
    [InlineData("(?x)cache # trailing comment", "cache", true)]
    [InlineData("cache#", "cache#", true)]
    public void FullMatchAnchorsAlternativesUnicodeAndEndOfName(string pattern, string name, bool excluded)
    {
        var root = Path.Combine(Path.GetTempPath(), "regex-root");
        var scopes = RefreshPlanner.Plan(root, [Path.Combine(root, name)], new ScanOptions
        {
            ExcludedDirectoryNameRegexes = [new() { Pattern = pattern }]
        });
        Assert.Equal(excluded ? 0 : 1, scopes.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void RegexCaseCanFollowHostOrBeExplicit(bool? ignoreCase)
    {
        var root = Path.Combine(Path.GetTempPath(), "regex-root");
        var scopes = RefreshPlanner.Plan(root, [Path.Combine(root, "CACHE")], new ScanOptions
        {
            ExcludedDirectoryNameRegexes = [new() { Pattern = "cache", IgnoreCase = ignoreCase }]
        });
        Assert.Equal((ignoreCase ?? OperatingSystem.IsWindows()) ? 0 : 1, scopes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedAndOnDemandRequestsCannotBypassRegexAncestors(bool rootExcluded)
    {
        using var workspace = new TestWorkspace();
        var scope = Path.Combine(workspace.SourcePath, "cache-123", "missing");
        var rule = new DirectoryNameRegex { Pattern = rootExcluded ? "source" : @"cache-\d+" };
        var options = TestWorkspace.FastOptions with { ExcludedDirectoryNameRegexes = [rule] };
        Assert.Empty(RefreshPlanner.Plan(workspace.SourcePath, [scope], options));
        var report = await new FileSystemMetadataScanner().ScanAsync(workspace.Request(scope) with
        {
            Options = options, OnDemand = true
        }, Guid.NewGuid(), (_, _) => throw new InvalidOperationException("No batch expected."));

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Equal(0, report.Progress.Directories);
        Assert.Equal(0, report.Progress.Entries);
        Assert.Equal(1, report.Progress.ExcludedEntries);
        Assert.Equal(scope, Assert.Single(report.Diagnostics.ExcludedPaths).Path);
        Assert.Empty(report.Diagnostics.RepeatedDirectoryNames);
    }

    public static TheoryData<ScanOptions> InvalidOptions => new()
    {
        TestWorkspace.FastOptions with { ExcludedDirectoryNameRegexes = [new() { Pattern = "[" }] },
        TestWorkspace.FastOptions with { ExcludedDirectoryNameRegexes = [new() { Pattern = " " }] },
        TestWorkspace.FastOptions with { ExcludedDirectoryNameRegexes = [new() { Pattern = "cache", MatchMode = (RegexMatchMode)99 }] },
        TestWorkspace.FastOptions with { ExcludedDirectoryNameRegexes = [null!] },
        TestWorkspace.FastOptions with { ExcludedDirectoryNameRegexes = null! },
        TestWorkspace.FastOptions with { MaxRecordedExclusions = -1 },
        TestWorkspace.FastOptions with { MaxTrackedDirectoryNames = -1 }
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task InvalidRulesFailBeforeIndexCreationAndEvenWithEmptyPlan(ScanOptions options)
    {
        using var workspace = new TestWorkspace();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new IndexingEngine(new FileSystemMetadataScanner(), store)
            .ScanAsync(workspace.Request() with { Options = options }));
        Assert.ThrowsAny<ArgumentException>(() => RefreshPlanner.Plan(workspace.SourcePath, [], options));
        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace.DatabasePath)));
    }

    [Fact]
    public async Task RegexTimeoutDiscardsStagedRowsAndPreservesIndexAndPending()
    {
        using var workspace = new TestWorkspace();
        var old = workspace.WriteFile("work/old.txt");
        workspace.WriteFile("archive/hidden.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request() with { Options = TestWorkspace.FastOptions with
        {
            Deferral = new() { DirectoryNames = ["archive"] }
        }});
        var before = (await store.SearchAsync(new SearchQuery())).Entries.ToArray();
        var pending = (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray();
        File.Delete(old);
        Directory.CreateDirectory(Path.Combine(workspace.SourcePath, "work", new string('a', 200) + "!"));
        var options = TestWorkspace.FastOptions with
        {
            BatchSize = 1,
            ExcludedDirectoryNameRegexes = [new() { Pattern = "(a+)+$" }]
        };

        await Assert.ThrowsAsync<RegexMatchTimeoutException>(() => engine.ScanAsync(
            workspace.Request(Path.Combine(workspace.SourcePath, "work")) with { Options = options, OnDemand = true }));

        Assert.Equal(before, (await store.SearchAsync(new SearchQuery())).Entries.ToArray());
        Assert.Equal(pending, (await store.ListPendingAsync(new PendingQuery())).Entries.ToArray());
        Assert.Equal(ScanStatus.Completed, (await engine.ScanAsync(workspace.Request() with { OnDemand = true })).Status);
    }

    [Fact]
    public async Task ExclusionPriorityAndRepeatedNamesCountOnlyObservedDirectories()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("a/cache/cache/not-observed.txt");
        workspace.WriteFile("b/cache/not-observed.txt");
        workspace.WriteFile("c/cache/not-observed.txt");
        workspace.WriteFile("generated-12/not-observed.txt");
        workspace.WriteFile("omit.tmp");
        workspace.WriteFile("explicit.tmp");
        workspace.WriteFile("keep.txt");
        var options = TestWorkspace.FastOptions with
        {
            ExcludedPaths = [Path.Combine(workspace.SourcePath, "a", "cache"), Path.Combine(workspace.SourcePath, "explicit.tmp")],
            ExcludedDirectoryNames = ["cache", "cache"],
            ExcludedDirectoryNameRegexes = [new() { Pattern = "cache|generated-.*" }],
            ExcludedFilePatterns = ["*.tmp"], MaxRecordedExclusions = 2
        };
        var scanner = new FileSystemMetadataScanner();
        var report = await scanner.ScanAsync(workspace.Request() with { Options = options }, Guid.NewGuid(), (_, _) => Task.CompletedTask);

        Assert.Equal(new ExclusionStatistics(2, 2, 1, 1, 4, 2), report.Diagnostics.Exclusions);
        Assert.Equal(6, report.Progress.ExcludedEntries);
        Assert.Equal(2, report.Diagnostics.ExcludedPaths.Count);
        Assert.Equal(4, report.Diagnostics.OmittedExcludedPaths);
        var repeated = Assert.Single(report.Diagnostics.RepeatedDirectoryNames);
        Assert.Equal("cache", repeated.Name);
        Assert.Equal(3, repeated.Count);
        Assert.Equal(3, repeated.SamplePaths.Count);
        Assert.Equal(0, report.Diagnostics.UntrackedDirectoryNameOccurrences);

        var next = await scanner.ScanAsync(workspace.Request(Path.Combine(workspace.SourcePath, "b")), Guid.NewGuid(), (_, _) => Task.CompletedTask);
        Assert.Empty(next.Diagnostics.ExcludedPaths);
        Assert.Empty(next.Diagnostics.RepeatedDirectoryNames);
        Assert.Equal(new ExclusionStatistics(0, 0, 0, 0, 0, 0), next.Diagnostics.Exclusions);
    }

    [Fact]
    public async Task DiagnosticsCapsDoNotAffectTotalsOrTraversal()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("a/cache/hidden.txt");
        workspace.WriteFile("b/cache/hidden.txt");
        var report = await new FileSystemMetadataScanner().ScanAsync(workspace.Request() with
        {
            Options = TestWorkspace.FastOptions with
            {
                ExcludedDirectoryNames = ["cache"], MaxRecordedExclusions = 0, MaxTrackedDirectoryNames = 0
            }
        }, Guid.NewGuid(), (_, _) => Task.CompletedTask);

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Equal(2, report.Diagnostics.OmittedExcludedPaths);
        Assert.Equal(2, report.Diagnostics.Exclusions.DirectoryNames);
        Assert.Equal(4, report.Diagnostics.UntrackedDirectoryNameOccurrences);
        Assert.Equal(3, report.Progress.Directories);
        Assert.Empty(report.Diagnostics.ExcludedPaths);
        Assert.Empty(report.Diagnostics.RepeatedDirectoryNames);
    }

    [Fact]
    public async Task TrackedNameKeepsCountingAfterCapAndSamplesStayBounded()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("repeat/other/repeat/other/repeat/other/repeat/keep.txt");
        var report = await new FileSystemMetadataScanner().ScanAsync(workspace.Request() with
        {
            Options = TestWorkspace.FastOptions with { MaxTrackedDirectoryNames = 1 }
        }, Guid.NewGuid(), (_, _) => Task.CompletedTask);

        Assert.Equal(3, report.Diagnostics.UntrackedDirectoryNameOccurrences);
        var name = Assert.Single(report.Diagnostics.RepeatedDirectoryNames);
        Assert.Equal("repeat", name.Name);
        Assert.Equal(4, name.Count);
        Assert.Equal(3, name.SamplePaths.Count);
    }

    [Fact]
    public async Task DeferralAndOnDemandKeepRegexExclusionsAndExistingPendingSemantics()
    {
        using var workspace = new TestWorkspace();
        var old = workspace.WriteFile("archive/old.txt");
        workspace.WriteFile("archive/cache-1/hidden.txt");
        workspace.WriteFile("ordinary.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        File.Delete(old);
        var request = workspace.Request() with { Options = TestWorkspace.FastOptions with
        {
            ExcludedDirectoryNameRegexes = [new() { Pattern = "cache-.*" }],
            Deferral = new() { DirectoryNames = ["archive", "cache-1"] }
        }};

        var deferred = await engine.ScanAsync(request);
        Assert.Equal(ScanStatus.Deferred, deferred.Status);
        Assert.Equal(0, deferred.Progress.ExcludedEntries);
        Assert.True(Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "old.txt" })).Entries).CoveragePending);
        var completed = await engine.ScanAsync(request with { OnDemand = true, ScopePath = Path.Combine(workspace.SourcePath, "archive") });
        Assert.Equal(ScanStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Diagnostics.Exclusions.DirectoryNameRegexes);
        Assert.Empty((await store.ListPendingAsync(new PendingQuery())).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "old.txt" })).Entries);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "hidden.txt" })).Entries);
        Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "ordinary.txt" })).Entries);
    }
}
