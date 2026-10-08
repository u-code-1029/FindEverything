using FindEverything.Engine;
using FindEverything.Server.Models;
using FindEverything.Server.Persistence;
using FindEverything.Server.Security;
using FindEverything.Server.Services;

namespace FindEverything.Server.Api;

public static class ServerEndpoints
{
    private static readonly string[] SearchParameters = ["searchText", "nameContains", "kind", "minSizeBytes", "maxSizeBytes",
        "createdFromUtc", "createdBeforeUtc", "modifiedFromUtc", "modifiedBeforeUtc", "sortBy", "sortDirection", "limit", "offset"];

    public static void MapServerApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/me", (HttpContext context, AuthSettings authentication) => Results.Ok(new
        {
            subject = SubjectAccess.Subject(context.User),
            isAdministrator = SubjectAccess.IsAdministrator(context.User, authentication)
        }));
        api.MapGet("/profiles", ListVisibleProfilesAsync);
        api.MapGet("/profiles/{id:guid}/status", GetStatusAsync);
        api.MapGet("/profiles/{id:guid}/search", async (Guid id, [AsParameters] ProfileSearchQuery search,
            HttpContext context, ServerRepository repository, ServerPaths paths, AuthSettings authentication, CancellationToken cancellationToken) =>
        {
            RejectUnknownQuery(context, SearchParameters);
            return await SearchAsync(id, search.ToRequest(), context, repository, paths, authentication, cancellationToken);
        });
        api.MapPost("/profiles/{id:guid}/search", SearchAsync);
        api.MapGet("/profiles/{id:guid}/export", ExportAsync);

        var admin = api.MapGroup("/admin").RequireAuthorization("Administrator");
        admin.MapGet("/sources", (ServerPaths paths) => Results.Ok(paths.Sources.Values));
        admin.MapGet("/profiles", async (int? limit, int? offset, ServerRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListProfilesAsync(limit ?? 100, offset ?? 0, ct)));
        admin.MapGet("/profiles/{id:guid}", async (Guid id, ServerRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetProfileAsync(id, ct) ?? throw new ServerNotFoundException("Profile not found.")));
        admin.MapPost("/profiles", async (ProfileDefinition definition, ServerRepository repository, CancellationToken ct) =>
        {
            var profile = await repository.CreateProfileAsync(definition, ct);
            return Results.Created($"/api/admin/profiles/{profile.Id}", profile);
        });
        admin.MapPut("/profiles/{id:guid}", async (Guid id, ProfileUpdate update, ServerRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.UpdateProfileAsync(id, update.ExpectedRevision, update.Definition, ct)));
        admin.MapDelete("/profiles/{id:guid}", async (Guid id, int expectedRevision, ServerRepository repository, CancellationToken ct) =>
        {
            await repository.DeleteProfileAsync(id, expectedRevision, ct);
            return Results.NoContent();
        });
        admin.MapGet("/schedules", async (int? limit, int? offset, ServerRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListSchedulesAsync(limit ?? 100, offset ?? 0, ct)));
        admin.MapGet("/schedules/{id:guid}", async (Guid id, ServerRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetScheduleAsync(id, ct) ?? throw new ServerNotFoundException("Schedule not found.")));
        admin.MapPost("/schedules", async (ScheduleDefinition definition, ServerRepository repository, TimeProvider clock, CancellationToken ct) =>
        {
            var schedule = await repository.CreateScheduleAsync(definition, NextDue(definition, clock), ct);
            return Results.Created($"/api/admin/schedules/{schedule.Id}", schedule);
        });
        admin.MapPut("/schedules/{id:guid}", async (Guid id, ScheduleUpdate update, ServerRepository repository, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await repository.UpdateScheduleAsync(id, update.ExpectedRevision, update.Definition, NextDue(update.Definition, clock), ct)));
        admin.MapDelete("/schedules/{id:guid}", async (Guid id, int expectedRevision, ServerRepository repository, CancellationToken ct) =>
        {
            await repository.DeleteScheduleAsync(id, expectedRevision, ct);
            return Results.NoContent();
        });
        admin.MapGet("/jobs", async (Guid? profileId, int? limit, int? offset, ServerRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListJobsAsync(profileId, limit ?? 100, offset ?? 0, ct)));
        admin.MapGet("/jobs/{id:guid}", async (Guid id, ServerRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetJobAsync(id, ct) ?? throw new ServerNotFoundException("Job not found.")));
        admin.MapPost("/jobs", async (EnqueueScanRequest request, HttpContext context, ScanCoordinator coordinator, CancellationToken ct) =>
        {
            var result = await coordinator.EnqueueAsync(request.ProfileId, request.RelativeScope, request.OnDemand,
                SubjectAccess.Subject(context.User)!, ct);
            return Results.Accepted($"/api/admin/jobs/{result.Job.Id}", result);
        });
        admin.MapPost("/profiles/{id:guid}/scan", async (Guid id, StartScanRequest request, HttpContext context, ScanCoordinator coordinator, CancellationToken ct) =>
        {
            var result = await coordinator.EnqueueAsync(id, request.RelativeScope, request.OnDemand, SubjectAccess.Subject(context.User)!, ct);
            return Results.Accepted($"/api/admin/jobs/{result.Job.Id}", result);
        });
        admin.MapPost("/jobs/{id:guid}/cancel", async (Guid id, ScanCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.CancelAsync(id, ct) ?? throw new ServerNotFoundException("Job not found.")));
        admin.MapGet("/profiles/{id:guid}/pending", ListPendingAsync);
    }

    private static async Task<IResult> ListVisibleProfilesAsync(HttpContext context, int? limit, int? offset,
        ServerRepository repository, AuthSettings authentication, CancellationToken cancellationToken)
    {
        RejectUnknownQuery(context, "limit", "offset");
        var pageSize = limit ?? 100;
        var skip = offset ?? 0;
        ValidatePagination(pageSize, skip);
        // Pagination counts authorized profiles only and never reveals other profile IDs or totals.
        var items = new List<PublicProfile>(pageSize + 1);
        var databaseOffset = 0;
        while (true)
        {
            var page = await repository.ListProfilesAsync(200, databaseOffset, cancellationToken);
            foreach (var profile in page.Items)
            {
                if (!SubjectAccess.CanRead(context.User, authentication, profile)) continue;
                if (skip > 0) { skip--; continue; }
                items.Add(new PublicProfile(profile.Id, profile.Definition.Name, profile.Definition.SourceId,
                    profile.Definition.Enabled, profile.Definition.Output));
                if (items.Count > pageSize) return Results.Ok(new PageResult<PublicProfile>(items.Take(pageSize).ToArray(), true));
            }
            if (!page.HasMore) break;
            databaseOffset = checked(databaseOffset + page.Items.Count);
        }
        return Results.Ok(new PageResult<PublicProfile>(items, false));
    }

    private static async Task<IResult> GetStatusAsync(Guid id, HttpContext context, ServerRepository repository,
        ServerPaths paths, AuthSettings authentication, CancellationToken cancellationToken)
    {
        RejectUnknownQuery(context);
        var profile = await ReadableProfileAsync(id, context, repository, authentication, cancellationToken);
        var root = paths.ResolveProfileRootForRead(profile.Definition);
        var databasePath = paths.GetIndexPath(id);
        IndexRootStatus? status = null;
        if (File.Exists(databasePath))
        {
            await using var store = new SqliteIndexStore(databasePath);
            status = await store.GetRootStatusAsync(root, cancellationToken);
        }
        return Results.Ok(new PublicIndexStatus(id, status?.LastPublishedUtc is not null, status?.LastScanId,
            status?.LastStatus, status?.LastPublishedUtc, status?.EntryCount ?? 0,
            status?.LastErrorCount ?? 0, status?.HasPendingScopes ?? false));
    }

    private static async Task<IResult> SearchAsync(Guid id, ProfileSearchRequest search, HttpContext context,
        ServerRepository repository, ServerPaths paths, AuthSettings authentication, CancellationToken cancellationToken)
    {
        var profile = await ReadableProfileAsync(id, context, repository, authentication, cancellationToken);
        var query = search.ToQuery(paths.ResolveProfileRootForRead(profile.Definition), profile.Definition.Output);
        var (result, hasIndex) = await ReadSearchAsync(paths.GetIndexPath(id), query, cancellationToken);
        return Results.Ok(new ProfileSearchResponse(id, hasIndex, result.Entries.Select(entry => ResultProjection.Project(entry, profile.Definition.Output)).ToArray(),
            result.TotalCount, result.HasMore, result.HasPendingScopes, query.Limit, query.Offset));
    }

    private static async Task<IResult> ExportAsync(Guid id, [AsParameters] ProfileSearchQuery search, string? format,
        HttpContext context, ServerRepository repository, ServerPaths paths, AuthSettings authentication, CancellationToken cancellationToken)
    {
        RejectUnknownQuery(context, [.. SearchParameters, "format"]);
        var profile = await ReadableProfileAsync(id, context, repository, authentication, cancellationToken);
        var exportFormat = format is null ? profile.Definition.Output.Format : format switch
        {
            "json" => OutputFormat.Json, "csv" => OutputFormat.Csv, _ => throw new ArgumentException("Unknown export format.")
        };
        var query = search.ToRequest().ToQuery(paths.ResolveProfileRootForRead(profile.Definition), profile.Definition.Output);
        var (result, hasIndex) = await ReadSearchAsync(paths.GetIndexPath(id), query, cancellationToken);
        if (exportFormat == OutputFormat.Json)
            return Results.Ok(new ProfileSearchResponse(id, hasIndex, result.Entries.Select(entry => ResultProjection.Project(entry, profile.Definition.Output)).ToArray(),
                result.TotalCount, result.HasMore, result.HasPendingScopes, query.Limit, query.Offset));
        context.Response.Headers["X-Has-Index"] = hasIndex ? "true" : "false";
        context.Response.Headers["X-Has-More"] = result.HasMore ? "true" : "false";
        context.Response.Headers["X-Has-Pending-Scopes"] = result.HasPendingScopes ? "true" : "false";
        context.Response.Headers["X-Total-Count"] = result.TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.File(ResultProjection.Csv(result.Entries, profile.Definition.Output), "text/csv; charset=utf-8", $"{id:N}.csv");
    }

    private static async Task<IResult> ListPendingAsync(Guid id, int? limit, int? offset, HttpContext context,
        ServerRepository repository, ServerPaths paths, CancellationToken cancellationToken)
    {
        RejectUnknownQuery(context, "limit", "offset");
        var profile = await repository.GetProfileAsync(id, cancellationToken) ?? throw new ServerNotFoundException("Profile not found.");
        var pageSize = limit ?? 100;
        var skip = offset ?? 0;
        ValidatePagination(pageSize, skip);
        var path = paths.GetIndexPath(id);
        if (!File.Exists(path)) return Results.Ok(new PendingResult([], false));
        await using var store = new SqliteIndexStore(path);
        return Results.Ok(await store.ListPendingAsync(new PendingQuery
        {
            RootPath = paths.ResolveProfileRootForRead(profile.Definition), Limit = pageSize, Offset = skip
        }, cancellationToken));
    }

    private static async Task<ScanProfile> ReadableProfileAsync(Guid id, HttpContext context,
        ServerRepository repository, AuthSettings authentication, CancellationToken cancellationToken)
    {
        var profile = await repository.GetProfileAsync(id, cancellationToken);
        if (profile is null || !SubjectAccess.CanRead(context.User, authentication, profile))
            throw new ServerNotFoundException("Profile not found.");
        return profile;
    }

    private static async Task<(SearchResult Result, bool HasIndex)> ReadSearchAsync(string databasePath,
        SearchQuery query, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath)) return (new SearchResult([], false), false);
        await using var store = new SqliteIndexStore(databasePath);
        var status = await store.GetRootStatusAsync(query.RootPath!, cancellationToken);
        if (status?.LastPublishedUtc is null) return (new SearchResult([], false), false);
        return (await store.SearchAsync(query, cancellationToken), true);
    }

    private static DateTimeOffset? NextDue(ScheduleDefinition definition, TimeProvider clock)
    {
        ScheduleCalculator.Validate(definition);
        var now = clock.GetUtcNow();
        if (definition.Enabled && definition.Kind == ScheduleKind.Once && definition.AnchorUtc <= now)
            throw new ArgumentException("Enabled one-time schedules must start in the future.");
        var due = definition.Enabled ? ScheduleCalculator.GetNextDueUtc(definition, now) : null;
        if (definition.Enabled && due is null) throw new ArgumentException("No future schedule occurrence is available.");
        return due;
    }

    private static void ValidatePagination(int limit, int offset)
    {
        if (limit is < 1 or > 1000 || offset < 0) throw new ArgumentException("Invalid pagination.");
    }

    private static void RejectUnknownQuery(HttpContext context, params string[] known)
    {
        if (context.Request.Query.Keys.Any(key => !known.Contains(key, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Unknown query parameter.");
    }
}
