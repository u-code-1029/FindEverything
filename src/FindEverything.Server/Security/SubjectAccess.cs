using System.Security.Claims;
using FindEverything.Server.Models;

namespace FindEverything.Server.Security;

public static class SubjectAccess
{
    public static string? Subject(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.Identity?.Name;

    public static bool IsAdministrator(ClaimsPrincipal principal, AuthSettings settings) =>
        principal.Identity?.IsAuthenticated == true &&
        ((settings.Mode == "ApiKey" && principal.IsInRole("Administrator")) ||
         settings.AdminIds.Contains(Subject(principal) ?? "", StringComparer.Ordinal));

    public static bool CanRead(ClaimsPrincipal principal, AuthSettings settings, ScanProfile profile) =>
        principal.Identity?.IsAuthenticated == true &&
        (IsAdministrator(principal, settings) ||
         profile.Definition.ReaderIds.Contains(Subject(principal) ?? "", StringComparer.Ordinal));
}
