using FindEverything.Engine;
using FindEverything.Server.Models;

namespace FindEverything.Server;

public sealed class ProfileValidator(ServerPaths paths)
{
    public ProfileDefinition Validate(ProfileDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 200) throw new ArgumentException("Profile name must contain 1 to 200 characters.");
        var root = paths.ResolveProfileRootForRead(definition);
        ArgumentNullException.ThrowIfNull(definition.Options);
        var options = definition.Options;
        ArgumentNullException.ThrowIfNull(options.ExcludedDirectoryNameRegexes);
        if (options.ExcludedDirectoryNameRegexes.Count > 100 || options.ExcludedDirectoryNameRegexes.Any(rule => rule is null || rule.Pattern is null || rule.Pattern.Length > 4096))
            throw new ArgumentException("At most 100 directory regexes of 4096 characters each are allowed.");
        ArgumentNullException.ThrowIfNull(options.ExcludedDirectoryNames);
        ArgumentNullException.ThrowIfNull(options.ExcludedPaths);
        ArgumentNullException.ThrowIfNull(options.ExcludedFilePatterns);
        ArgumentNullException.ThrowIfNull(options.Deferral);
        ArgumentNullException.ThrowIfNull(options.Deferral.DirectoryNames);
        ArgumentNullException.ThrowIfNull(options.Deferral.Paths);
        if (new[] { options.ExcludedDirectoryNames.Count, options.ExcludedPaths.Count, options.ExcludedFilePatterns.Count,
            options.Deferral.DirectoryNames.Count, options.Deferral.Paths.Count }.Any(count => count > 4096))
            throw new ArgumentException("Each filter list must contain at most 4096 items.");
        options = ResolveOptions(options, root);
        options.Validate(root);
        ArgumentNullException.ThrowIfNull(definition.Output);
        ArgumentNullException.ThrowIfNull(definition.Output.Columns);
        if (definition.Output.Columns.Count is < 1 or > 8 || definition.Output.Columns.Distinct().Count() != definition.Output.Columns.Count || definition.Output.Columns.Any(column => !Enum.IsDefined(column)))
            throw new ArgumentException("Output columns must be nonempty, unique known columns.");
        if (!Enum.IsDefined(definition.Output.SortBy) || !Enum.IsDefined(definition.Output.SortDirection) || !Enum.IsDefined(definition.Output.Format) || definition.Output.DefaultPageSize is < 1 or > 1000)
            throw new ArgumentException("Invalid output options.");
        ArgumentNullException.ThrowIfNull(definition.ReaderIds);
        if (definition.ReaderIds.Count > 1000) throw new ArgumentException("At most 1000 readers are allowed.");
        foreach (var id in definition.ReaderIds)
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl))
                throw new ArgumentException("Reader IDs must contain 1 to 256 characters and no control characters.");
        // Persist portable relative exclusion paths. Snapshots resolve them at queue time.
        return definition with { ReaderIds = definition.ReaderIds.Distinct(StringComparer.Ordinal).ToArray() };
    }

    public JobSnapshot CreateSnapshot(ScanProfile profile, string? relativeScope, bool onDemand)
    {
        Validate(profile.Definition);
        var root = paths.ResolveProfileRootForRead(profile.Definition);
        var scope = ServerPaths.ResolveRelative(root, relativeScope ?? ".", checkAncestors: false);
        var resolved = profile with { Definition = profile.Definition with { Options = ResolveOptions(profile.Definition.Options, root) } };
        return new JobSnapshot(resolved, root, scope, onDemand);
    }

    private static ScanOptions ResolveOptions(ScanOptions options, string root) => options with
    {
        ExcludedPaths = options.ExcludedPaths.Select(path => ServerPaths.ResolveRelative(root, path, checkAncestors: false)).ToArray(),
        Deferral = options.Deferral with { Paths = options.Deferral.Paths.Select(path => ServerPaths.ResolveRelative(root, path, checkAncestors: false)).ToArray() }
    };
}
