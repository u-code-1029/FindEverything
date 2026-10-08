using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FindEverything.Engine;
using FindEverything.Server;
using FindEverything.Server.Models;
using FindEverything.Server.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FindEverything.Server.Tests;

internal sealed class ServerTestFactory(ServerWorkspace workspace, IMetadataScanner? scanner = null, bool credentialsConfigured = true) : WebApplicationFactory<global::Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public const string AdminKey = "test-admin-token-generated-only-for-isolated-in-memory-test-host-001";
    public const string ReaderKey = "test-reader-token-generated-only-for-isolated-in-memory-test-host-002";
    public const string OutsiderKey = "test-outsider-token-generated-only-for-isolated-in-memory-test-host-003";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FindEverything:DataDirectory"] = workspace.DataPath,
            ["FindEverything:Sources:0:Id"] = "primary",
            ["FindEverything:Sources:0:RootPath"] = workspace.SourcePath,
            ["FindEverything:MaxQueuedJobs"] = workspace.Options.MaxQueuedJobs.ToString(),
            ["FindEverything:SchedulerPollSeconds"] = "3600",
            ["Authentication:Mode"] = "ApiKey",
            ["Authentication:Users:0:Subject"] = "admin",
            ["Authentication:Users:0:TokenSha256"] = Hash(AdminKey),
            ["Authentication:Users:0:IsAdministrator"] = "true",
            ["Authentication:Users:1:Subject"] = "reader",
            ["Authentication:Users:1:TokenSha256"] = Hash(ReaderKey),
            ["Authentication:Users:1:IsAdministrator"] = "false",
            ["Authentication:Users:2:Subject"] = "outsider",
            ["Authentication:Users:2:TokenSha256"] = Hash(OutsiderKey),
            ["Authentication:Users:2:IsAdministrator"] = "false"
        }));
        builder.ConfigureServices(services =>
        {
            // Minimal hosting reads singleton configuration before these factory
            // callbacks. Replace the configured instances as well as configuration.
            services.RemoveAll<ServerOptions>();
            services.AddSingleton(workspace.Options);
            services.RemoveAll<AuthSettings>();
            services.AddSingleton(new AuthSettings
            {
                Mode = "ApiKey",
                Users = credentialsConfigured ?
                [
                    new ApiKeyUser { Subject = "admin", TokenSha256 = Hash(AdminKey), IsAdministrator = true },
                    new ApiKeyUser { Subject = "reader", TokenSha256 = Hash(ReaderKey) },
                    new ApiKeyUser { Subject = "outsider", TokenSha256 = Hash(OutsiderKey) }
                ] : []
            });
            if (scanner is not null)
            {
                services.RemoveAll<IMetadataScanner>();
                services.AddSingleton(scanner);
            }
        });
    }

    public HttpClient Client(string? token = AdminKey)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidOperationException("The response body was empty.");
    }

    public static async Task<ScanProfile> CreateProfileAsync(HttpClient client, ProfileDefinition definition)
    {
        using var response = await client.PostAsJsonAsync("/api/admin/profiles", definition, Json);
        return await ReadAsync<ScanProfile>(response);
    }

    public static async Task<ScanJob> WaitForJobAsync(HttpClient client, Guid jobId, Func<ScanJob, bool>? predicate = null)
    {
        predicate ??= job => job.Status is not JobStatus.Queued and not JobStatus.Running;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await client.GetAsync($"/api/admin/jobs/{jobId}", timeout.Token);
            var job = await ReadAsync<ScanJob>(response);
            if (predicate(job)) return job;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
