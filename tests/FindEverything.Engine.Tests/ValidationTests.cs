using FindEverything.Engine;
using Xunit;

namespace FindEverything.Engine.Tests;

public sealed class ValidationTests
{
    public static TheoryData<ScanOptions> InvalidOptions => new()
    {
        TestWorkspace.FastOptions with { BatchSize = 0 },
        TestWorkspace.FastOptions with { MaxDepth = -1 },
        TestWorkspace.FastOptions with { DirectoryDelay = TimeSpan.FromMilliseconds(-1) },
        TestWorkspace.FastOptions with { MaxRecordedErrors = -1 },
        TestWorkspace.FastOptions with { MaxEntriesPerSecond = -1 },
        TestWorkspace.FastOptions with { EnumerationBufferSize = 0 }
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task InvalidScanOptionsDoNotWriteAnIndex(ScanOptions options)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("keep.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.ScanAsync(new ScanRequest(workspace.SourcePath) { Options = options }));

        Assert.False(File.Exists(workspace.DatabasePath));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public async Task InvalidPaginationIsRejected(int limit, int offset)
    {
        using var workspace = new TestWorkspace();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await using var writer = await store.AcquireWriterAsync();
        await store.InitializeAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SearchAsync(new SearchQuery { Limit = limit, Offset = offset }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReversedDateRangesAreRejected(bool created)
    {
        using var workspace = new TestWorkspace();
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await using var writer = await store.AcquireWriterAsync();
        await store.InitializeAsync();
        var from = DateTimeOffset.UtcNow;
        var before = from.AddDays(-1);
        var query = created
            ? new SearchQuery { CreatedFromUtc = from, CreatedBeforeUtc = before }
            : new SearchQuery { ModifiedFromUtc = from, ModifiedBeforeUtc = before };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SearchAsync(query));
    }

    [Fact]
    public async Task ScopeOutsideRootIsRejectedBeforeDatabaseCreation()
    {
        using var workspace = new TestWorkspace();
        var outside = Path.Combine(workspace.BasePath, "outside");
        Directory.CreateDirectory(outside);
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.ScanAsync(workspace.Request(outside)));

        Assert.False(File.Exists(workspace.DatabasePath));
    }
}
