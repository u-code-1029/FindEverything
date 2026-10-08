namespace FindEverything.Server.Security;

public sealed class AuthSettings
{
    public const string SectionName = "Authentication";
    public string Mode { get; set; } = "ApiKey";
    public List<ApiKeyUser> Users { get; set; } = [];
    public List<string> AdminIds { get; set; } = [];

    public void Validate()
    {
        if (Mode is not ("ApiKey" or "Negotiate"))
            throw new ArgumentException("Authentication Mode must be ApiKey or Negotiate.");
        ArgumentNullException.ThrowIfNull(Users);
        ArgumentNullException.ThrowIfNull(AdminIds);
        foreach (var user in Users)
        {
            ValidateSubject(user.Subject);
            if (user.TokenSha256 is null || user.TokenSha256.Length != 64 || !user.TokenSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("TokenSha256 must contain a SHA-256 hexadecimal hash.");
        }
        foreach (var id in AdminIds) ValidateSubject(id);
        if (Users.Select(user => user.Subject).Distinct(StringComparer.Ordinal).Count() != Users.Count ||
            Users.Select(user => user.TokenSha256.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() != Users.Count)
            throw new ArgumentException("Authentication subjects and token hashes must be unique.");
    }

    private static void ValidateSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || subject.Any(char.IsControl))
            throw new ArgumentException("Subjects must contain 1 to 256 characters without control characters.");
    }
}

public sealed class ApiKeyUser
{
    public string Subject { get; set; } = "";
    public string TokenSha256 { get; set; } = "";
    public bool IsAdministrator { get; set; }
}
