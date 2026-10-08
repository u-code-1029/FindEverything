using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace FindEverything.Engine;

// One policy implementation for traversal and filesystem-free refresh planning.
internal sealed class ExclusionMatcher
{
    private readonly HashSet<string> _paths;
    private readonly HashSet<string> _names;
    private readonly (DirectoryNameRegex Rule, Regex Regex)[] _regexes;
    private readonly string[] _filePatterns;
    private readonly Queue<ScanError> _errors = new();
    private bool _matchTimedOut;

    public ExclusionMatcher(ScanOptions options)
    {
        _paths = new(options.ExcludedPaths.Select(PathRules.Normalize), PathRules.Comparer);
        _names = new(options.ExcludedDirectoryNames, PathRules.Comparer);
        _regexes = options.ExcludedDirectoryNameRegexes.Select(rule => (rule, Compile(rule))).ToArray();
        _filePatterns = options.ExcludedFilePatterns.ToArray();
    }

    internal static Regex Compile(DirectoryNameRegex rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Pattern);
        if (!Enum.IsDefined(rule.MatchMode))
            throw new ArgumentException("Directory regex matchMode must be full or partial.");
        if (rule.TimeoutMilliseconds is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rule.TimeoutMilliseconds),
                "Directory regex timeout must be between 1 and 10000 milliseconds.");
        }
        var options = RegexOptions.CultureInvariant;
        if (rule.IgnoreCase ?? OperatingSystem.IsWindows())
            options |= RegexOptions.IgnoreCase;
        // Turn on whitespace mode only at the end so a trailing (?x) line comment
        // cannot swallow our closing group/anchor. The newline never becomes a literal.
        var pattern = rule.MatchMode == RegexMatchMode.Full ? $"\\A(?:{rule.Pattern}(?x)\n)\\z" : rule.Pattern;
        return new Regex(pattern, options, TimeSpan.FromMilliseconds(rule.TimeoutMilliseconds));
    }

    public ExcludedEntry? Match(string path, string name, EntryKind kind)
    {
        // Parent directories have already been checked; match only this enumerated path.
        if (_paths.TryGetValue(path, out var excludedPath))
            return new(path, kind, ExclusionReason.Path, excludedPath);
        if (kind == EntryKind.Directory)
            return MatchName(path, name);
        foreach (var pattern in _filePatterns)
            if (FileSystemName.MatchesSimpleExpression(pattern, name, OperatingSystem.IsWindows()))
                return new(path, kind, ExclusionReason.FilePattern, pattern);
        return null;
    }

    public ExcludedEntry? MatchScope(string scope, string root)
    {
        // Explicit paths take precedence over name rules, even on an ancestor.
        for (string? current = scope; current is not null; current = Parent(current, root))
            if (_paths.TryGetValue(current, out var excludedPath))
                return new(scope, EntryKind.Directory, ExclusionReason.Path, excludedPath);
        for (string? current = scope; current is not null; current = Parent(current, root))
        {
            if (MatchName(current, Path.GetFileName(current)) is { } match)
                return match with { Path = scope };
            if (_matchTimedOut)
                return null;
        }
        return null;
    }

    private ExcludedEntry? MatchName(string path, string name)
    {
        _matchTimedOut = false;
        if (_names.TryGetValue(name, out var excludedName))
            return new(path, EntryKind.Directory, ExclusionReason.DirectoryName, excludedName);
        foreach (var (rule, regex) in _regexes)
        {
            try
            {
                if (regex.IsMatch(name))
                    return new(path, EntryKind.Directory, ExclusionReason.DirectoryNameRegex, rule.Pattern);
            }
            catch (RegexMatchTimeoutException exception)
            {
                _matchTimedOut = true;
                _errors.Enqueue(new ScanError(
                    path,
                    $"Directory-name exclusion regex '{rule.Pattern}' exceeded its "
                    + $"{exception.MatchTimeout.TotalMilliseconds:0} ms timeout; traversal continued."));
                // Match the profile runtime's fail-open behavior. Do not evaluate later
                // exclusion rules after one pathological pattern times out.
                return null;
            }
        }
        return null;
    }

    public void DrainErrors(Action<ScanError> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        while (_errors.TryDequeue(out var error))
            report(error);
    }

    private static string? Parent(string path, string root) =>
        PathRules.Comparer.Equals(path, root) ? null : Path.GetDirectoryName(path);
}
