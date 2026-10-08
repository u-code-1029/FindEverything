using FindEverything.Engine;
using FindEverything.Server.Models;

namespace FindEverything.Server;

public sealed class ServerPaths
{
    public string DataDirectory { get; }
    public string ControlDatabasePath => Path.Combine(DataDirectory, "server.db");
    public IReadOnlyDictionary<string, SourceDefinition> Sources { get; }

    public ServerPaths(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxQueuedJobs is < 1 or > 10000) throw new ArgumentException("MaxQueuedJobs must be between 1 and 10000.");
        if (options.SchedulerPollSeconds is < 1 or > 3600) throw new ArgumentException("SchedulerPollSeconds must be between 1 and 3600.");
        DataDirectory = PathRules.Normalize(options.DataDirectory);
        PathRules.EnsureNoReparseAncestors(DataDirectory);
        if (OperatingSystem.IsWindows() && (DataDirectory.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(DataDirectory)!).DriveType == DriveType.Network))
            throw new ArgumentException("Server data must be on a local disk.");
        var sources = new Dictionary<string, SourceDefinition>(StringComparer.Ordinal);
        foreach (var source in options.Sources)
        {
            ValidateId(source.Id);
            var root = PathRules.Normalize(source.RootPath);
            // Source paths may be offline at startup. Live link checks happen immediately before scanning.
            if (PathRules.IsWithin(DataDirectory, root) || PathRules.IsWithin(root, DataDirectory))
                throw new ArgumentException("Server data and scan sources must not overlap.");
            if (!sources.TryAdd(source.Id, new SourceDefinition { Id = source.Id, RootPath = root }))
                throw new ArgumentException("Source IDs must be unique.");
        }
        Sources = sources;
    }

    public string ResolveProfileRoot(ProfileDefinition definition)
    {
        if (!Sources.TryGetValue(definition.SourceId, out var source)) throw new ArgumentException("Unknown source ID.");
        return ResolveRelative(source.RootPath, definition.RelativeRoot);
    }

    // Search paths are already registered/validated; reading the local index must work with an offline source.
    public string ResolveProfileRootForRead(ProfileDefinition definition)
    {
        if (!Sources.TryGetValue(definition.SourceId, out var source)) throw new ArgumentException("Unknown source ID.");
        return ResolveRelative(source.RootPath, definition.RelativeRoot, checkAncestors: false);
    }

    public string ResolveScope(ProfileDefinition definition, string? relativeScope) =>
        ResolveRelative(ResolveProfileRoot(definition), relativeScope ?? ".");

    public string GetIndexPath(Guid profileId) => Path.Combine(DataDirectory, "indexes", profileId.ToString("N") + ".db");

    public static string ResolveRelative(string root, string relative, bool checkAncestors = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.StartsWith('\\') || relative.StartsWith('/'))
            throw new ArgumentException("A source-relative path is required.");
        // Do not allow Windows separators to become literal filenames on Linux.
        var portable = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var resolved = PathRules.Normalize(Path.Combine(root, portable));
        if (!PathRules.IsWithin(resolved, root)) throw new ArgumentException("The path must remain within the registered source.");
        if (checkAncestors) PathRules.EnsureNoReparseAncestors(resolved);
        return resolved;
    }

    public Task<IAsyncDisposable> AcquireInstanceLeaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PathRules.EnsureNoReparseAncestors(DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        try
        {
            var lockPath = Path.Combine(DataDirectory, "server.lock");
            PathRules.EnsureNoReparseAncestors(lockPath);
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return Task.FromResult<IAsyncDisposable>(stream);
        }
        catch (IOException error) { throw new IOException("Another server may be using this data directory.", error); }
    }

    public static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("IDs must contain at most 100 ASCII letters, digits, dots, hyphens or underscores.");
    }
}
