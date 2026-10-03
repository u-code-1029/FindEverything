using FindEverything.Engine;
using Xunit;

namespace FindEverything.Engine.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public string BasePath { get; } = Path.Combine(Path.GetTempPath(), "FindEverything-tests", Guid.NewGuid().ToString("N"));
    public string SourcePath => Path.Combine(BasePath, "source");
    public string DatabasePath => Path.Combine(BasePath, "index", "metadata.db");

    public TestWorkspace() => Directory.CreateDirectory(SourcePath);

    public string WriteFile(string relativePath, string contents = "test contents")
    {
        var path = Path.GetFullPath(Path.Combine(SourcePath, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public ScanRequest Request(string? scope = null) => new(SourcePath)
    {
        ScopePath = scope,
        Options = FastOptions
    };

    public static ScanOptions FastOptions => new()
    {
        DirectoryDelay = TimeSpan.Zero,
        MaxEntriesPerSecond = 1_000_000,
        BatchSize = 2
    };

    public void Dispose()
    {
        if (Directory.Exists(BasePath)) Directory.Delete(BasePath, recursive: true);
    }
}

internal sealed class ScriptedScanner(
    Func<ScanRequest, Guid, Func<IReadOnlyList<IndexedEntry>, CancellationToken, Task>,
        IProgress<ScanProgress>?, CancellationToken, Task<ScanReport>> script) : IMetadataScanner
{
    public Task<ScanReport> ScanAsync(ScanRequest request, Guid scanId,
        Func<IReadOnlyList<IndexedEntry>, CancellationToken, Task> writeBatch,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        script(request, scanId, writeBatch, progress, cancellationToken);

    public static IndexedEntry FileEntry(string path) => new(
        path, Path.GetFileName(path), Path.GetDirectoryName(path)!, EntryKind.File,
        10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "This test requires Linux symbolic link support.";
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "This test requires Windows path semantics.";
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "This test requires Windows path semantics.";
    }
}
