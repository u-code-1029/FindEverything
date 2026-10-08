using System.Text.Json.Serialization;
using FindEverything.Engine;
using FindEverything.Server;
using FindEverything.Server.Api;
using FindEverything.Server.Persistence;
using FindEverything.Server.Security;
using FindEverything.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;

var builder = WebApplication.CreateBuilder(args);
var serverOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new();
var authentication = builder.Configuration.GetSection(AuthSettings.SectionName).Get<AuthSettings>() ?? new();
authentication.Validate();
builder.Services.AddSingleton(serverOptions);
builder.Services.AddSingleton(authentication);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServerPaths>();
builder.Services.AddSingleton<ProfileValidator>();
builder.Services.AddSingleton<ServerRepository>();
builder.Services.AddSingleton<IMetadataScanner, FileSystemMetadataScanner>();
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddHostedService(services => services.GetRequiredService<ScanCoordinator>());
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = StrictJsonMiddleware.MaximumBodyBytes);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.MaxDepth = 64;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase, allowIntegerValues: false));
});
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
if (authentication.Mode == "Negotiate")
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
else
    builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy("Administrator", policy =>
    policy.RequireAuthenticatedUser().RequireAssertion(context => SubjectAccess.IsAdministrator(context.User, authentication))));

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
foreach (var origin in origins)
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
        uri.GetLeftPart(UriPartial.Authority) != origin || !string.IsNullOrEmpty(uri.UserInfo))
        throw new ArgumentException("CORS origins must be explicit HTTP or HTTPS origins without paths.");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (origins.Length > 0)
    {
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()
            .WithExposedHeaders("X-Has-Index", "X-Has-More", "X-Has-Pending-Scopes", "X-Total-Count");
        if (authentication.Mode == "Negotiate") policy.AllowCredentials();
    }
}));

var app = builder.Build();
app.UseMiddleware<ApiErrorMiddleware>();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        // Integrated authentication is sent automatically by browsers. A custom header
        // makes mutations require an explicitly allowed CORS preflight or same-origin code.
        if (authentication.Mode == "Negotiate" &&
            context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE" &&
            context.Request.Headers["X-FindEverything-Request"] != "1")
            throw new BadHttpRequestException("A mutation request header is required.");
    }
    await next(context);
});
app.UseMiddleware<StrictJsonMiddleware>();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapServerApi();
app.Run();

public partial class Program;
