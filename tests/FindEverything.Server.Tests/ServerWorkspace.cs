using FindEverything.Engine;
using FindEverything.Server;
using FindEverything.Server.Models;

namespace FindEverything.Server.Tests;

internal sealed class ServerWorkspace : IDisposable
{
    public string BasePath { get; } = Path.Combine(Path.GetTempPath(), "FindEverything-server-tests", Guid.NewGuid().ToString("N"));
    public string SourcePath => Path.Combine(BasePath, "source");
    public string DataPath => Path.Combine(BasePath, "data");
    public ServerOptions Options { get; }

    public ServerWorkspace()
    {
        Directory.CreateDirectory(SourcePath);
        Options = new ServerOptions
        {
            DataDirectory = DataPath,
            Sources = [new SourceDefinition { Id = "primary", RootPath = SourcePath }],
            SchedulerPollSeconds = 3600,
            MaxQueuedJobs = 2
        };
    }

    public ProfileDefinition ProfileDefinition(string name = "Shared drive") => new()
    {
        Name = name,
        SourceId = "primary",
        Options = FastOptions,
        ReaderIds = ["reader"]
    };

    public string WriteFile(string relativePath, string contents = "indexed")
    {
        var path = Path.Combine(SourcePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

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
