using FindEverything.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FindEverything.Engine.Tests;

public sealed class ScanSafetyTests
{
    [Fact]
    public async Task ExclusionsPruneDirectoriesAndSpecificPathsBeforeTraversal()
    {
        using var workspace = new TestWorkspace();
        var retained = workspace.WriteFile("documents/keep.txt");
        workspace.WriteFile("cache/deeper/secret.txt");
        workspace.WriteFile("archive/deeper/secret.txt");
        workspace.WriteFile("documents/unneeded.tmp");
        var blocked = Path.Combine(workspace.SourcePath, "cache");
        UnixFileMode? previousMode = null;
        if (OperatingSystem.IsLinux())
        {
            previousMode = File.GetUnixFileMode(blocked);
            File.SetUnixFileMode(blocked, UnixFileMode.None);
        }
        try
        {
            await using var store = new SqliteIndexStore(workspace.DatabasePath);
            var options = TestWorkspace.FastOptions with
            {
                ExcludedDirectoryNames = ["cache"],
                ExcludedPaths = [Path.Combine(workspace.SourcePath, "archive")],
                ExcludedFilePatterns = ["*.tmp"]
            };
            var report = await new IndexingEngine(new FileSystemMetadataScanner(), store)
                .ScanAsync(new ScanRequest(workspace.SourcePath) { Options = options });

            Assert.Equal(ScanStatus.Completed, report.Status);
            Assert.Empty(report.Errors);
            Assert.True(report.Progress.ExcludedEntries >= 3);
            Assert.Equal(retained, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);
            Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "cache" })).Entries);
            Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "archive" })).Entries);
        }
        finally
        {
            if (OperatingSystem.IsLinux() && previousMode is { } mode) File.SetUnixFileMode(blocked, mode);
        }
    }

    [Fact]
    public async Task SubtreeRefreshCannotBypassAnExcludedAncestor()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("keep.txt");
        workspace.WriteFile("cache/deeper/secret.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        var options = TestWorkspace.FastOptions with { ExcludedDirectoryNames = ["cache"] };
        await engine.ScanAsync(new ScanRequest(workspace.SourcePath) { Options = options });
        var request = new ScanRequest(workspace.SourcePath)
        {
            ScopePath = Path.Combine(workspace.SourcePath, "cache", "deeper"), Options = options
        };

        try { await engine.ScanAsync(request); }
        catch (ArgumentException) { /* Rejecting an excluded scope is also safe. */ }

        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "secret" })).Entries);
        Assert.Single((await store.SearchAsync(new SearchQuery { NameContains = "keep.txt" })).Entries);
    }

    [LinuxFact]
    public async Task LinuxDirectorySymlinkIsNotFollowedIntoOutsideTree()
    {
        using var workspace = new TestWorkspace();
        var outside = Path.Combine(workspace.BasePath, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside-secret.txt"), "external data");
        Directory.CreateSymbolicLink(Path.Combine(workspace.SourcePath, "link"), outside);
        workspace.WriteFile("keep.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);

        var report = await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.True(report.Progress.SkippedLinks >= 1);
        Assert.Empty((await store.SearchAsync(new SearchQuery { NameContains = "outside-secret" })).Entries);
        Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries);
    }

    [Fact]
    public async Task IndexInsideSourceIsRejectedBeforeDatabaseDirectoryOrFilesAreCreated()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("keep.txt");
        var indexDirectory = Path.Combine(workspace.SourcePath, "new-index");
        await using var store = new SqliteIndexStore(Path.Combine(indexDirectory, "metadata.db"));
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.ScanAsync(workspace.Request()));

        Assert.False(Directory.Exists(indexDirectory));
        Assert.Equal([Path.Combine(workspace.SourcePath, "keep.txt")], Directory.GetFiles(workspace.SourcePath, "*", SearchOption.AllDirectories));
    }

    [LinuxFact]
    public async Task LinuxDatabasePathAliasingSourceThroughSymlinkIsRejectedBeforeWrites()
    {
        using var workspace = new TestWorkspace();
        var alias = Path.Combine(workspace.BasePath, "source-alias");
        Directory.CreateSymbolicLink(alias, workspace.SourcePath);
        await using var store = new SqliteIndexStore(Path.Combine(alias, "metadata.db"));
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);

        await Assert.ThrowsAnyAsync<IOException>(() => engine.ScanAsync(workspace.Request()));

        Assert.Empty(Directory.GetFiles(workspace.SourcePath, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedExistingDatabaseFileIsRejectedWithoutChangingItsBytes(bool sqlite)
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.DatabasePath)!);
        if (sqlite)
        {
            await using var unrelated = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = workspace.DatabasePath, Pooling = false
            }.ToString());
            await unrelated.OpenAsync();
            await using var command = unrelated.CreateCommand();
            command.CommandText = "CREATE TABLE valuable(data TEXT); INSERT INTO valuable VALUES('preserve this data');";
            await command.ExecuteNonQueryAsync();
        }
        else await File.WriteAllTextAsync(workspace.DatabasePath, "Important unrelated file contents.");
        var original = await File.ReadAllBytesAsync(workspace.DatabasePath);
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        await using var writer = await store.AcquireWriterAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync());

        Assert.Equal(original, await File.ReadAllBytesAsync(workspace.DatabasePath));
    }

    [WindowsFact]
    public async Task WindowsNetworkDatabasePathsAreRejectedBeforeAccess()
    {
        foreach (var path in new[] { @"\\server\share\metadata.db", @"\\?\UNC\server\share\metadata.db" })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            {
                await using var store = new SqliteIndexStore(path);
                await using var writer = await store.AcquireWriterAsync();
                await store.InitializeAsync();
            });
        }
    }

    [WindowsTheory]
    [InlineData(@"\\?\")]
    [InlineData(@"\\.\")]
    [InlineData(@"\??\")]
    public async Task WindowsDeviceNamespaceCannotAliasIndexIntoSource(string devicePrefix)
    {
        using var workspace = new TestWorkspace();
        var file = workspace.WriteFile("valuable.txt", "preserve contents");
        var devicePath = devicePrefix + Path.Combine(workspace.SourcePath, "metadata.db");

        Assert.ThrowsAny<ArgumentException>(() => PathRules.Normalize(devicePath));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            await using var store = new SqliteIndexStore(devicePath);
            await new IndexingEngine(new FileSystemMetadataScanner(), store).ScanAsync(workspace.Request());
        });

        Assert.Equal([file], Directory.GetFiles(workspace.SourcePath, "*", SearchOption.AllDirectories));
        Assert.Equal("preserve contents", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task MissingSourceNeverErasesLastSuccessfulIndex()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.WriteFile("keep.txt");
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        await engine.ScanAsync(workspace.Request());
        Directory.Move(workspace.SourcePath, Path.Combine(workspace.BasePath, "disconnected-source"));

        try
        {
            var report = await engine.ScanAsync(workspace.Request());
            Assert.NotEqual(ScanStatus.Completed, report.Status);
        }
        catch (DirectoryNotFoundException) { }

        Assert.Equal(file, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);
    }
}
