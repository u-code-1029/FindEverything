using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FindEverything.Engine;
using FindEverything.Server.Api;
using FindEverything.Server.Models;
using Xunit;

namespace FindEverything.Server.Tests;

public sealed class ServerApiTests
{
    [Fact]
    public async Task AnUnconfiguredServerHasNoDefaultCredentialsOrAnonymousSearch()
    {
        using var workspace = new ServerWorkspace();
        await using var factory = new ServerTestFactory(workspace, credentialsConfigured: false);
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task SearchAndAdministrationRequireAuthenticationAndAdministratorRole()
    {
        using var workspace = new ServerWorkspace();
        await using var factory = new ServerTestFactory(workspace);
        using var anonymous = factory.Client(null);
        using var reader = factory.Client(ServerTestFactory.ReaderKey);
        using var invalid = factory.Client("wrong-token");
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        using var unauthenticated = await anonymous.GetAsync("/api/profiles");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.True(unauthenticated.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.GetAsync("/api/profiles")).StatusCode);
        using var forbidden = await reader.GetAsync("/api/admin/profiles");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.True(forbidden.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/admin/profiles", workspace.ProfileDefinition(), ServerTestFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task ReadersOnlySeeGrantedProfilesAndRevocationImmediatelyBlocksAllViews()
    {
        using var workspace = new ServerWorkspace();
        workspace.WriteFile("private-secret.txt");
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        using var reader = factory.Client(ServerTestFactory.ReaderKey);
        using var outsider = factory.Client(ServerTestFactory.OutsiderKey);
        var granted = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition("Granted"));
        var privateProfile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition("Hidden") with { ReaderIds = [] });
        using var listResponse = await reader.GetAsync("/api/profiles");
        var listJson = await listResponse.Content.ReadAsStringAsync();
        Assert.Contains(granted.Id.ToString(), listJson);
        Assert.DoesNotContain(privateProfile.Id.ToString(), listJson);
        Assert.DoesNotContain(workspace.SourcePath, listJson);
        Assert.DoesNotContain("readerIds", listJson);
        foreach (var suffix in new[] { "search", "status", "export?format=json" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/profiles/{granted.Id}/{suffix}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/profiles/{privateProfile.Id}/{suffix}")).StatusCode);
        }
        var result = await EnqueueAsync(admin, granted.Id);
        Assert.Equal(JobStatus.Completed, (await ServerTestFactory.WaitForJobAsync(admin, result.Job.Id)).Status);
        Assert.Contains("private-secret.txt", await reader.GetStringAsync($"/api/profiles/{granted.Id}/search"));
        using var revoke = await admin.PutAsJsonAsync($"/api/admin/profiles/{granted.Id}", new ProfileUpdate(granted.Revision, granted.Definition with { ReaderIds = [] }), ServerTestFactory.Json);
        Assert.True(revoke.IsSuccessStatusCode);
        foreach (var suffix in new[] { "search", "status", "export?format=csv" })
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/profiles/{granted.Id}/{suffix}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/admin/jobs/{result.Job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/admin/profiles/{granted.Id}/pending")).StatusCode);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\Data")]
    public async Task ProfileSourceEscapeIsRejectedWithoutEnqueueing(string relativeRoot)
    {
        using var workspace = new ServerWorkspace();
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        using var response = await admin.PostAsJsonAsync("/api/admin/profiles", workspace.ProfileDefinition() with { RelativeRoot = relativeRoot }, ServerTestFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvalidRegexAndUnknownSourceCannotBecomeProfiles()
    {
        using var workspace = new ServerWorkspace();
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        var invalidRegex = workspace.ProfileDefinition() with
        {
            Options = ServerWorkspace.FastOptions with { ExcludedDirectoryNameRegexes = [new DirectoryNameRegex { Pattern = "[" }] }
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/profiles", invalidRegex, ServerTestFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/profiles", workspace.ProfileDefinition() with { SourceId = "unknown" }, ServerTestFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task StaleProfileRevisionReturnsConflict()
    {
        using var workspace = new ServerWorkspace();
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition());
        var update = new ProfileUpdate(1, profile.Definition with { Name = "new" });
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/profiles/{profile.Id}", update, ServerTestFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/admin/profiles/{profile.Id}", update, ServerTestFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task JobRunsRealScannerAndSearchRemainsAvailableWhenSourceIsOffline()
    {
        using var workspace = new ServerWorkspace();
        workspace.WriteFile("Archive/report-large.txt", new string('x', 20));
        workspace.WriteFile("Archive/report-small.txt", "x");
        workspace.WriteFile("Cache/ignored.txt");
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        using var reader = factory.Client(ServerTestFactory.ReaderKey);
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition() with
        {
            Options = ServerWorkspace.FastOptions with
            {
                ExcludedDirectoryNameRegexes = [new DirectoryNameRegex { Pattern = "cache", IgnoreCase = true, MatchMode = RegexMatchMode.Full }]
            },
            Output = new OutputDefinition
            {
                Columns = [OutputColumn.Name, OutputColumn.SizeBytes], SortBy = EntrySortField.Size,
                SortDirection = SortDirection.Descending, DefaultPageSize = 1
            }
        });
        var queued = await EnqueueAsync(admin, profile.Id);
        var finished = await ServerTestFactory.WaitForJobAsync(admin, queued.Job.Id);
        Assert.Equal(JobStatus.Completed, finished.Status);
        Assert.Equal(ExclusionReason.DirectoryNameRegex, Assert.Single(finished.Report!.Diagnostics.ExcludedPaths).Reason);
        using var searchResponse = await reader.PostAsJsonAsync($"/api/profiles/{profile.Id}/search", new ProfileSearchRequest
        {
            SearchText = "Archive report", Kind = EntryKind.File, MinSizeBytes = 2
        }, ServerTestFactory.Json);
        var found = await ServerTestFactory.ReadAsync<ProfileSearchResponse>(searchResponse);
        Assert.Equal(1, found.TotalCount);
        var entry = Assert.Single(found.Entries);
        Assert.Equal("report-large.txt", ((JsonElement)entry["name"]!).GetString());
        Assert.Equal(20, ((JsonElement)entry["sizeBytes"]!).GetInt64());
        Assert.Equal(2, entry.Count);
        using var statusResponse = await reader.GetAsync($"/api/profiles/{profile.Id}/status");
        var status = await ServerTestFactory.ReadAsync<PublicIndexStatus>(statusResponse);
        Assert.True(status.HasIndex);
        Assert.Equal(ScanStatus.Completed, status.LastStatus);
        Directory.Move(workspace.SourcePath, Path.Combine(workspace.BasePath, "offline"));
        using var offlineResponse = await reader.GetAsync($"/api/profiles/{profile.Id}/search?kind=file&searchText=report&limit=10");
        var offline = await ServerTestFactory.ReadAsync<ProfileSearchResponse>(offlineResponse);
        Assert.Equal(2, offline.TotalCount);
        Assert.Equal(2, offline.Entries.Count);
    }

    [Fact]
    public async Task ExportUsesConfiguredColumnsAndOnlyRequestedPageWithSafeCsvCells()
    {
        using var workspace = new ServerWorkspace();
        workspace.WriteFile("=SUM(1,2).txt");
        workspace.WriteFile("z-last.txt");
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition() with
        {
            Output = new OutputDefinition { Columns = [OutputColumn.Name], DefaultPageSize = 1 }
        });
        var queued = await EnqueueAsync(admin, profile.Id);
        Assert.Equal(JobStatus.Completed, (await ServerTestFactory.WaitForJobAsync(admin, queued.Job.Id)).Status);
        using var csvResponse = await admin.GetAsync($"/api/profiles/{profile.Id}/export?format=csv&limit=1");
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType!.MediaType);
        var csv = await csvResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"'=SUM(1,2).txt\"", csv);
        Assert.DoesNotContain("z-last", csv);
        Assert.DoesNotContain(workspace.SourcePath, csv);
        var json = await admin.GetStringAsync($"/api/profiles/{profile.Id}/export?format=json&limit=1&offset=1");
        Assert.Contains("z-last.txt", json);
        Assert.DoesNotContain("=SUM(1,2).txt", json);
        Assert.DoesNotContain("fullPath", json);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/profiles/{profile.Id}/export?format=csv&limit=1001")).StatusCode);
    }

    [Fact]
    public async Task SearchCannotChooseAnotherSourceOrUnboundedPagination()
    {
        using var workspace = new ServerWorkspace();
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition());
        using var rootOverride = await admin.PostAsJsonAsync($"/api/profiles/{profile.Id}/search", new { RootPath = workspace.BasePath });
        Assert.Equal(HttpStatusCode.BadRequest, rootOverride.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/profiles/{profile.Id}/search?rootPath=outside")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/profiles/{profile.Id}/search?limit=1001")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/profiles/{profile.Id}/search?offset=-1")).StatusCode);
    }

    [Fact]
    public async Task ScheduleCrudPreservesDefinitionAndRejectsStaleRevision()
    {
        using var workspace = new ServerWorkspace();
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition());
        var definition = new ScheduleDefinition
        {
            ProfileId = profile.Id, Name = "Hourly", Kind = ScheduleKind.Interval,
            AnchorUtc = DateTimeOffset.UtcNow.AddHours(1), IntervalMinutes = 60
        };
        using var create = await admin.PostAsJsonAsync("/api/admin/schedules", definition, ServerTestFactory.Json);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var schedule = await ServerTestFactory.ReadAsync<ScanSchedule>(create);
        Assert.Equal(definition.AnchorUtc, schedule.NextDueUtc);
        using var update = await admin.PutAsJsonAsync($"/api/admin/schedules/{schedule.Id}", new ScheduleUpdate(schedule.Revision, definition with { Enabled = false }), ServerTestFactory.Json);
        var disabled = await ServerTestFactory.ReadAsync<ScanSchedule>(update);
        Assert.False(disabled.Definition.Enabled);
        Assert.Null(disabled.NextDueUtc);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/schedules/{schedule.Id}?expectedRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/schedules/{schedule.Id}?expectedRevision={disabled.Revision}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/schedules/{schedule.Id}")).StatusCode);
    }

    [Fact]
    public async Task RelativeScopeEscapeAndDuplicateJsonAreRejected()
    {
        using var workspace = new ServerWorkspace();
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition());
        using var escaping = await admin.PostAsJsonAsync("/api/admin/jobs", new EnqueueScanRequest { ProfileId = profile.Id, RelativeScope = "../outside" }, ServerTestFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, escaping.StatusCode);
        using var duplicate = new StringContent("{\"searchText\":\"safe\",\"searchText\":\"other\"}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/profiles/{profile.Id}/search", duplicate)).StatusCode);
        var page = await admin.GetStringAsync("/api/admin/jobs");
        Assert.DoesNotContain(profile.Id.ToString(), page);
    }

    [Fact]
    public async Task DeferredScopeCanBeScannedOnDemandWhilePermanentExclusionsRemain()
    {
        using var workspace = new ServerWorkspace();
        workspace.WriteFile("Archive/wanted.txt");
        workspace.WriteFile("Archive/Cache/ignored.txt");
        await using var factory = new ServerTestFactory(workspace);
        using var admin = factory.Client();
        var profile = await ServerTestFactory.CreateProfileAsync(admin, workspace.ProfileDefinition() with
        {
            Options = ServerWorkspace.FastOptions with
            {
                Deferral = new DeferralPolicy { DirectoryNames = ["Archive"] }, ExcludedDirectoryNames = ["Cache"]
            }
        });
        var initial = await EnqueueAsync(admin, profile.Id);
        Assert.Equal(JobStatus.Deferred, (await ServerTestFactory.WaitForJobAsync(admin, initial.Job.Id)).Status);
        using var before = await admin.GetAsync($"/api/profiles/{profile.Id}/search?kind=file");
        var beforeResult = await ServerTestFactory.ReadAsync<ProfileSearchResponse>(before);
        Assert.True(beforeResult.HasPendingScopes);
        Assert.Empty(beforeResult.Entries);
        using var pending = await admin.GetAsync($"/api/admin/profiles/{profile.Id}/pending");
        Assert.Single((await ServerTestFactory.ReadAsync<PendingResult>(pending)).Entries);
        var demanded = await EnqueueAsync(admin, profile.Id, "Archive", onDemand: true);
        Assert.Equal(JobStatus.Completed, (await ServerTestFactory.WaitForJobAsync(admin, demanded.Job.Id)).Status);
        using var after = await admin.GetAsync($"/api/profiles/{profile.Id}/search?kind=file");
        var afterResult = await ServerTestFactory.ReadAsync<ProfileSearchResponse>(after);
        Assert.False(afterResult.HasPendingScopes);
        Assert.Single(afterResult.Entries);
        var text = await after.Content.ReadAsStringAsync();
        Assert.Contains("wanted.txt", text);
        Assert.DoesNotContain("ignored.txt", text);
    }

    internal static async Task<EnqueueResult> EnqueueAsync(HttpClient client, Guid profileId, string? relativeScope = null, bool onDemand = false)
    {
        using var response = await client.PostAsJsonAsync("/api/admin/jobs", new EnqueueScanRequest
        {
            ProfileId = profileId, RelativeScope = relativeScope, OnDemand = onDemand
        }, ServerTestFactory.Json);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await ServerTestFactory.ReadAsync<EnqueueResult>(response);
    }
}
