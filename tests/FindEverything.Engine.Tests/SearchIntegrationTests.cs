using FindEverything.Engine;
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
        Assert.Equal(5, (await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries.Count);
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
        Assert.Equal(12, first.Entries.Concat(second.Entries).Concat(third.Entries).Select(entry => entry.FullPath).Distinct().Count());
        Assert.Empty((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File, Offset = 12 })).Entries);
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
}
