using FindEverything.Engine;
using FindEverything.Server.Models;

namespace FindEverything.Server.Api;

// Query-string enum binding is case-sensitive in minimal APIs. Bind names as strings
// here and parse the same enum names used by the camel-case JSON contract.
public sealed class ProfileSearchQuery
{
    public string? SearchText { get; set; }
    public string? NameContains { get; set; }
    public string? Kind { get; set; }
    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }
    public DateTimeOffset? CreatedFromUtc { get; set; }
    public DateTimeOffset? CreatedBeforeUtc { get; set; }
    public DateTimeOffset? ModifiedFromUtc { get; set; }
    public DateTimeOffset? ModifiedBeforeUtc { get; set; }
    public string? SortBy { get; set; }
    public string? SortDirection { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }

    public ProfileSearchRequest ToRequest() => new()
    {
        SearchText = SearchText, NameContains = NameContains, Kind = Parse<EntryKind>(Kind),
        MinSizeBytes = MinSizeBytes, MaxSizeBytes = MaxSizeBytes,
        CreatedFromUtc = CreatedFromUtc, CreatedBeforeUtc = CreatedBeforeUtc,
        ModifiedFromUtc = ModifiedFromUtc, ModifiedBeforeUtc = ModifiedBeforeUtc,
        SortBy = Parse<EntrySortField>(SortBy), SortDirection = Parse<SortDirection>(SortDirection),
        Limit = Limit, Offset = Offset
    };

    private static T? Parse<T>(string? name) where T : struct, Enum
    {
        if (name is null) return null;
        if (!Enum.GetNames<T>().Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown search enum name.");
        return Enum.Parse<T>(name, ignoreCase: true);
    }
}

public sealed class ProfileSearchRequest
{
    public string? SearchText { get; set; }
    public string? NameContains { get; set; }
    public EntryKind? Kind { get; set; }
    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }
    public DateTimeOffset? CreatedFromUtc { get; set; }
    public DateTimeOffset? CreatedBeforeUtc { get; set; }
    public DateTimeOffset? ModifiedFromUtc { get; set; }
    public DateTimeOffset? ModifiedBeforeUtc { get; set; }
    public EntrySortField? SortBy { get; set; }
    public SortDirection? SortDirection { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }

    public SearchQuery ToQuery(string root, OutputDefinition output)
    {
        var query = new SearchQuery
        {
            RootPath = root, SearchText = SearchText, NameContains = NameContains, Kind = Kind,
            MinSizeBytes = MinSizeBytes, MaxSizeBytes = MaxSizeBytes,
            CreatedFromUtc = CreatedFromUtc, CreatedBeforeUtc = CreatedBeforeUtc,
            ModifiedFromUtc = ModifiedFromUtc, ModifiedBeforeUtc = ModifiedBeforeUtc,
            SortBy = SortBy ?? output.SortBy, SortDirection = SortDirection ?? output.SortDirection,
            Limit = Limit ?? output.DefaultPageSize, Offset = Offset ?? 0
        };
        if (query.Limit is < 1 or > 1000 || query.Offset < 0)
            throw new ArgumentException("Invalid pagination.");
        if (query.Kind is { } kind && !Enum.IsDefined(kind) ||
            !Enum.IsDefined(query.SortBy) || !Enum.IsDefined(query.SortDirection))
            throw new ArgumentException("Invalid search enum.");
        if (query.MinSizeBytes is < 0 || query.MaxSizeBytes is < 0 || query.MinSizeBytes > query.MaxSizeBytes)
            throw new ArgumentException("Invalid size range.");
        if (query.CreatedFromUtc >= query.CreatedBeforeUtc || query.ModifiedFromUtc >= query.ModifiedBeforeUtc)
            throw new ArgumentException("Invalid date range.");
        if (SearchText?.Length > 4096 || NameContains?.Length > 4096)
            throw new ArgumentException("Search text is too long.");
        if (SearchText?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 64)
            throw new ArgumentException("At most 64 search terms are allowed.");
        return query;
    }
}

public sealed record PublicProfile(Guid Id, string Name, string SourceId, bool Enabled, OutputDefinition Output);
public sealed record PublicIndexStatus(Guid ProfileId, bool HasIndex, Guid? LastScanId, ScanStatus? LastStatus,
    DateTimeOffset? LastPublishedUtc, long EntryCount, long LastErrorCount, bool HasPendingScopes);
public sealed record ProfileSearchResponse(Guid ProfileId, bool HasIndex,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Entries,
    long TotalCount, bool HasMore, bool HasPendingScopes, int Limit, int Offset);
public sealed record EnqueueScanRequest
{
    public required Guid ProfileId { get; init; }
    public string? RelativeScope { get; init; }
    public bool OnDemand { get; init; }
}
