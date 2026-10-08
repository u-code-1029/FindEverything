using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FindEverything.Server.Security;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AuthSettings settings) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization;
        if (header.Count == 0) return Task.FromResult(AuthenticateResult.NoResult());
        if (header.Count != 1 || header[0] is not { } value ||
            !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.Fail("Bearer authentication is required."));
        var token = value[7..];
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsWhiteSpace))
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        ApiKeyUser? matched = null;
        foreach (var user in settings.Users)
            if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(user.TokenSha256))) matched = user;
        if (matched is null) return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, matched.Subject),
            new(ClaimTypes.Name, matched.Subject)
        };
        if (matched.IsAdministrator) claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
