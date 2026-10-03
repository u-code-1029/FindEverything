using System.Text.Json;
using System.Text.Json.Serialization;
using FindEverything.Engine;

namespace FindEverything.Cli;

internal static class ScanConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static async Task<ScanOptions> LoadAsync(string? path, string root, CancellationToken cancellationToken)
    {
        if (path is null) return new ScanOptions();
        await using var stream = File.OpenRead(path);
        var options = await JsonSerializer.DeserializeAsync<ScanOptions>(stream, JsonOptions, cancellationToken)
            ?? throw new ArgumentException("Scan configuration must be a JSON object.");
        ArgumentNullException.ThrowIfNull(options.ExcludedPaths);
        ArgumentNullException.ThrowIfNull(options.Deferral);
        ArgumentNullException.ThrowIfNull(options.Deferral.Paths);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string Resolve(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            return Path.GetFullPath(value, directory);
        }
        options = options with
        {
            ExcludedPaths = options.ExcludedPaths.Select(Resolve).ToArray(),
            Deferral = options.Deferral with { Paths = options.Deferral.Paths.Select(Resolve).ToArray() }
        };
        options.Validate(root);
        return options;
    }
}
