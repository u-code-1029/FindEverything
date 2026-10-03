using System.Globalization;
using System.Text.Json;
using FindEverything.Cli;
using FindEverything.Engine;
using Xunit;

namespace FindEverything.Engine.Tests;

// Program owns the process console and cancellation handler.
[CollectionDefinition("CLI console", DisableParallelization = true)]
public sealed class CliConsoleCollection;

[Collection("CLI console")]
public sealed class CliConfigurationTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{\"excludedDirectoryNames\":null}")]
    [InlineData("{\"excludedDirectoryNameRegexes\":null}")]
    [InlineData("{\"excludedDirectoryNameRegexes\":[null]}")]
    [InlineData("{\"excludedDirectoryNameRegexes\":[{}]}")]
    [InlineData("{\"excludedDirectoryNameRegexes\":[{\"pattern\":\"[\"}]}")]
    [InlineData("{\"excludedDirectoryNameRegexes\":[{\"pattern\":\"cache\",\"matchMode\":\"wrong\"}]}")]
    [InlineData("{\"excludedDirectoryNameRegexes\":[{\"pattern\":\"cache\",\"matchMode\":0}]}")]
    [InlineData("{\"excludedDirectoryNameRegexes\":[{\"pattern\":\"cache\",\"ignoreCase\":\"true\"}]}")]
    [InlineData("{\"excludeDirectoryNames\":[\"cache\"]}")]
    [InlineData("{\"excludedDirectoryNames\":[\"cache\"],\"excludedDirectoryNames\":[]}")]
    [InlineData("{\"deferral\":{\"paths\":null}}")]
    [InlineData("{\"deferral\":null}")]
    [InlineData("{\"excludedPaths\":[\"../outside\"]}")]
    [InlineData("{\"maxRecordedExclusions\":-1}")]
    public async Task InvalidConfigurationFailsBeforeDatabaseCreation(string json)
    {
        using var workspace = new TestWorkspace();
        var config = Path.Combine(workspace.BasePath, "scan.json");
        await File.WriteAllTextAsync(config, json);

        var result = await RunAsync("scan", "--root", workspace.SourcePath, "--database", workspace.DatabasePath, "--config", config);

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("Error:", result.Error);
        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace.DatabasePath)));
    }

    [Fact]
    public async Task ConfigPathsResolveBesideFileAndCliListsAppendToEveryPolicy()
    {
        using var workspace = new TestWorkspace();
        var kept = workspace.WriteFile("keep.txt");
        workspace.WriteFile("config-name/hidden.txt");
        workspace.WriteFile("cli-name/hidden.txt");
        workspace.WriteFile("config-path/hidden.txt");
        workspace.WriteFile("cli-path/hidden.txt");
        workspace.WriteFile("cache-123/hidden.txt");
        workspace.WriteFile("prefix-temp-suffix/hidden.txt");
        workspace.WriteFile("cli-full/hidden.txt");
        workspace.WriteFile("prefix-cli-partial/hidden.txt");
        workspace.WriteFile("file.config");
        workspace.WriteFile("file.cli");
        workspace.WriteFile("config-pending/hidden.txt");
        workspace.WriteFile("cli-pending/hidden.txt");
        workspace.WriteFile("config-pending-path/hidden.txt");
        workspace.WriteFile("cli-pending-path/hidden.txt");
        var config = Path.Combine(workspace.BasePath, "scan.json");
        await File.WriteAllTextAsync(config, """
            {
              "excludedDirectoryNames": ["config-name"],
              "excludedPaths": ["source/config-path"],
              "excludedFilePatterns": ["*.config"],
              "excludedDirectoryNameRegexes": [
                { "pattern": "cache-\\d+", "matchMode": "full" },
                { "pattern": "TEMP", "matchMode": "partial", "ignoreCase": true }
              ],
              "maxEntriesPerSecond": 1,
              "directoryDelay": "00:00:10",
              "deferral": { "directoryNames": ["config-pending"], "paths": ["source/config-pending-path"] }
            }
            """);
        var args = new[] { "scan", "--root", workspace.SourcePath, "--database", workspace.DatabasePath,
            "--config", config, "--exclude-dir", "cli-name", "--exclude-path", Path.Combine(workspace.SourcePath, "cli-path"),
            "--exclude-file", "*.cli", "--exclude-dir-regex", "cli-full", "--exclude-dir-regex-partial", "cli-partial",
            "--defer-dir", "cli-pending", "--defer-path", Path.Combine(workspace.SourcePath, "cli-pending-path"),
            "--max-entries-per-second", "1000000", "--directory-delay-ms", "0" };

        var result = await RunAsync(args);

        Assert.Equal(3, result.Code);
        using var report = JsonDocument.Parse(result.Output);
        Assert.Equal("deferred", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(10, report.RootElement.GetProperty("progress").GetProperty("excludedEntries").GetInt64());
        Assert.Equal(4, report.RootElement.GetProperty("pendingScopes").GetArrayLength());
        Assert.Equal(10, report.RootElement.GetProperty("diagnostics").GetProperty("excludedPaths").GetArrayLength());
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        Assert.Equal(kept, Assert.Single((await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries).FullPath);

        var onDemand = await RunAsync([.. args, "--on-demand"]);
        Assert.Equal(0, onDemand.Code);
        Assert.Empty((await store.ListPendingAsync(new PendingQuery())).Entries);
        Assert.Equal(5, (await store.SearchAsync(new SearchQuery { Kind = EntryKind.File })).Entries.Count);
        Assert.Equal(0, (await RunAsync("pending", "--database", workspace.DatabasePath)).Code);
        Assert.Equal(0, (await RunAsync("search", "--database", workspace.DatabasePath, "--name", "keep")).Code);
    }

    [Fact]
    public async Task RegexFlagsAreRepeatableAndInvalidInputPreservesExistingIndex()
    {
        using var workspace = new TestWorkspace();
        var keep = workspace.WriteFile("keep.txt");
        workspace.WriteFile("cache/hidden.txt");
        workspace.WriteFile("temp/hidden.txt");
        var args = new[] { "scan", "--root", workspace.SourcePath, "--database", workspace.DatabasePath,
            "--exclude-dir-regex", "cache", "--exclude-dir-regex", "temp", "--directory-delay-ms", "0" };
        Assert.Equal(0, (await RunAsync(args)).Code);
        var invalid = await RunAsync([.. args, "--exclude-dir-regex-partial", "["]);
        Assert.Equal(1, invalid.Code);
        await using var store = new SqliteIndexStore(workspace.DatabasePath);
        Assert.Equal(keep, Assert.Single((await store.SearchAsync(new SearchQuery())).Entries).FullPath);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var code = await Program.Main(args);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
