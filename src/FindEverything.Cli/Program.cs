using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using FindEverything.Engine;

namespace FindEverything.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const string Usage = """
        FindEverything: read-only file metadata indexing and search.

        scan --root PATH --database PATH [options]
          --scope PATH                    Refresh only this subtree of the root.
          --exclude-dir NAME              Skip directories with this name; repeatable.
          --exclude-path PATH             Skip this path and its subtree; repeatable.
          --exclude-file PATTERN          Skip filenames matching a wildcard; repeatable.
          --defer-dir NAME                Leave this directory's contents pending; repeatable.
          --defer-path PATH               Leave this subtree pending; repeatable.
          --defer-over-entries N          Defer subtrees above a prior entry-count estimate.
          --defer-over-seconds N          Defer subtrees above a prior duration estimate.
          --entry-budget N                Stop normal traversal after this many entries.
          --time-budget-seconds N         Stop normal traversal after this many seconds.
          --on-demand                     Bypass deferral for this scan (no value).
          --max-entries-per-second N       Metadata processing limit (default: 2000).
          --directory-delay-ms N           Pause between directories (default: 5).

        search --database PATH [options]
          --name TEXT                     Case-insensitive substring of a filename.
          --root PATH                     Restrict results to one indexed root.
          --kind file|directory            Filter entry type.
          --modified-from ISO             Inclusive modification date lower bound.
          --modified-before ISO           Exclusive modification date upper bound.
          --created-from ISO              Inclusive creation date lower bound.
          --created-before ISO            Exclusive creation date upper bound.
          --limit N                       Results per page (default: 100).
          --offset N                      Results to skip (default: 0).

        pending --database PATH [options]
          --root PATH                     Restrict pending scopes to one indexed root.
          --limit N                       Results per page (default: 100).
          --offset N                      Results to skip (default: 0).

        Dates require an explicit UTC or offset, e.g. 2026-01-01T00:00:00Z.
        Paths may be absolute or relative to the current working directory.
        Exclusion and deferred paths must be inside the scan root. No temporary-file or hidden-file
        exclusion is automatic; choose exclusions explicitly. Directory links are skipped.
        Deferral is disabled unless configured. Historical estimates come from prior successful
        subtree scans; first scans with unknown costs need explicit deferral rules or budgets.
        Time budgets are cooperative and cannot interrupt a stalled SMB operation.
        On-demand scans retain permanent exclusions, rate limits, and link checks. The CLI does
        not persist exclusion policy: repeat the same exclusion options for every scan.
        Use a database on the indexer's local disk, outside the scanned source tree.
        Search and pending only query an existing index; neither scans nor creates a database.
        JSON results go to stdout; progress and errors go to stderr. Ctrl+C cancels.
        Exit codes: 0 successful, 1 invalid input/error, 2 partial scan, 3 deferred, 130 cancelled.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (args.Length == 0 || args is ["help"] || args.Contains("--help", StringComparer.Ordinal))
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return args[0] switch
            {
                "scan" => await ScanAsync(ParseOptions(args, ScanOptionNames, ScanFlagNames), cancellation.Token),
                "search" => await SearchAsync(ParseOptions(args, SearchOptionNames), cancellation.Token),
                "pending" => await PendingAsync(ParseOptions(args, PendingOptionNames), cancellation.Token),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Use --help for usage.")
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static readonly HashSet<string> ScanOptionNames = new(StringComparer.Ordinal)
    {
        "--root", "--database", "--scope", "--exclude-dir", "--exclude-path", "--exclude-file",
        "--max-entries-per-second", "--directory-delay-ms", "--defer-dir", "--defer-path",
        "--defer-over-entries", "--defer-over-seconds", "--entry-budget", "--time-budget-seconds", "--on-demand"
    };

    private static readonly HashSet<string> ScanFlagNames = new(StringComparer.Ordinal) { "--on-demand" };

    private static readonly HashSet<string> SearchOptionNames = new(StringComparer.Ordinal)
    {
        "--database", "--name", "--root", "--kind", "--modified-from", "--modified-before",
        "--created-from", "--created-before", "--limit", "--offset"
    };

    private static readonly HashSet<string> PendingOptionNames = new(StringComparer.Ordinal)
    {
        "--database", "--root", "--limit", "--offset"
    };

    private static Dictionary<string, List<string>> ParseOptions(string[] args, HashSet<string> allowed,
        HashSet<string>? flags = null)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (!allowed.Contains(name))
                throw new ArgumentException($"Unknown option '{name}'. Use --help for usage.");
            if (flags?.Contains(name) == true)
            {
                if (!options.TryAdd(name, []))
                    throw new ArgumentException($"Option '{name}' must appear only once.");
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option '{name}' requires a value.");
            var value = args[++index];
            if (options.TryGetValue(name, out var values))
            {
                if (name is not ("--exclude-dir" or "--exclude-path" or "--exclude-file" or "--defer-dir" or "--defer-path"))
                    throw new ArgumentException($"Option '{name}' must appear only once.");
                values.Add(value);
            }
            else
            {
                options.Add(name, [value]);
            }
        }
        return options;
    }

    private static async Task<int> ScanAsync(Dictionary<string, List<string>> options, CancellationToken cancellationToken)
    {
        var rootPath = RequiredPath(options, "--root");
        var databasePath = RequiredPath(options, "--database");
        var request = new ScanRequest(rootPath)
        {
            ScopePath = OptionalPath(options, "--scope"),
            OnDemand = options.ContainsKey("--on-demand"),
            Options = new ScanOptions
            {
                ExcludedDirectoryNames = RepeatedValues(options, "--exclude-dir"),
                ExcludedPaths = RepeatedValues(options, "--exclude-path").Select(Path.GetFullPath).ToArray(),
                ExcludedFilePatterns = RepeatedValues(options, "--exclude-file"),
                MaxEntriesPerSecond = Integer(options, "--max-entries-per-second", 2000, 1),
                DirectoryDelay = TimeSpan.FromMilliseconds(Integer(options, "--directory-delay-ms", 5, 0)),
                Deferral = new DeferralPolicy
                {
                    DirectoryNames = RepeatedValues(options, "--defer-dir"),
                    Paths = RepeatedValues(options, "--defer-path").Select(Path.GetFullPath).ToArray(),
                    HistoricalEntryThreshold = PositiveLong(options, "--defer-over-entries"),
                    HistoricalDurationThreshold = PositiveDuration(options, "--defer-over-seconds"),
                    EntryBudget = PositiveLong(options, "--entry-budget"),
                    TimeBudget = PositiveDuration(options, "--time-budget-seconds")
                }
            }
        };
        await using var store = new SqliteIndexStore(databasePath);
        var engine = new IndexingEngine(new FileSystemMetadataScanner(), store);
        var report = await engine.ScanAsync(request, new ConsoleScanProgress(), cancellationToken);
        Console.Out.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return report.Status switch
        {
            ScanStatus.Completed => 0,
            ScanStatus.Partial => 2,
            ScanStatus.Deferred => 3,
            ScanStatus.Cancelled => 130,
            _ => 1
        };
    }

    private static async Task<int> SearchAsync(Dictionary<string, List<string>> options, CancellationToken cancellationToken)
    {
        var databasePath = RequiredPath(options, "--database");
        var query = new SearchQuery
        {
            NameContains = OptionalValue(options, "--name"),
            RootPath = OptionalPath(options, "--root"),
            Kind = OptionalValue(options, "--kind") switch
            {
                null => null,
                "file" => EntryKind.File,
                "directory" => EntryKind.Directory,
                var invalid => throw new ArgumentException($"Invalid --kind '{invalid}'; use file or directory.")
            },
            CreatedFromUtc = Date(options, "--created-from"),
            CreatedBeforeUtc = Date(options, "--created-before"),
            ModifiedFromUtc = Date(options, "--modified-from"),
            ModifiedBeforeUtc = Date(options, "--modified-before"),
            Limit = Integer(options, "--limit", 100, 1),
            Offset = Integer(options, "--offset", 0, 0)
        };
        ValidateDateRange(query.CreatedFromUtc, query.CreatedBeforeUtc, "created");
        ValidateDateRange(query.ModifiedFromUtc, query.ModifiedBeforeUtc, "modified");

        // Deliberately do not initialize: a query must never create an empty index.
        await using var store = new SqliteIndexStore(databasePath);
        var result = await store.SearchAsync(query, cancellationToken);
        Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static async Task<int> PendingAsync(Dictionary<string, List<string>> options, CancellationToken cancellationToken)
    {
        var databasePath = RequiredPath(options, "--database");
        var query = new PendingQuery
        {
            RootPath = OptionalPath(options, "--root"),
            Limit = Integer(options, "--limit", 100, 1),
            Offset = Integer(options, "--offset", 0, 0)
        };

        // Listing pending work must never open a source directory or create an index.
        await using var store = new SqliteIndexStore(databasePath);
        var result = await store.ListPendingAsync(query, cancellationToken);
        Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static string? OptionalValue(Dictionary<string, List<string>> options, string name) =>
        options.TryGetValue(name, out var values) ? values[0] : null;

    private static string RequiredPath(Dictionary<string, List<string>> options, string name)
    {
        var value = OptionalValue(options, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Option '{name}' is required and must contain a path.");
        return Path.GetFullPath(value);
    }

    private static string? OptionalPath(Dictionary<string, List<string>> options, string name)
    {
        var value = OptionalValue(options, name);
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Option '{name}' must contain a path.");
        return Path.GetFullPath(value);
    }

    private static IReadOnlyList<string> RepeatedValues(Dictionary<string, List<string>> options, string name)
    {
        if (!options.TryGetValue(name, out var values))
            return [];
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Option '{name}' must not be empty.");
        return values;
    }

    private static int Integer(Dictionary<string, List<string>> options, string name, int defaultValue, int minimum)
    {
        var value = OptionalValue(options, name);
        if (value is null)
            return defaultValue;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
            throw new ArgumentException($"Option '{name}' must be an integer of at least {minimum}.");
        return parsed;
    }

    private static DateTimeOffset? Date(Dictionary<string, List<string>> options, string name)
    {
        var value = OptionalValue(options, name);
        if (value is null)
            return null;
        if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:\d{2})$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new ArgumentException($"Option '{name}' requires an ISO date with UTC or an offset, e.g. 2026-01-01T00:00:00Z.");
        }
        return parsed.ToUniversalTime();
    }

    private static long? PositiveLong(Dictionary<string, List<string>> options, string name)
    {
        var value = OptionalValue(options, name);
        if (value is null)
            return null;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new ArgumentException($"Option '{name}' must be a positive 64-bit integer.");
        return parsed;
    }

    private static TimeSpan? PositiveDuration(Dictionary<string, List<string>> options, string name)
    {
        var value = OptionalValue(options, name);
        if (value is null)
            return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds <= 0)
        {
            throw new ArgumentException($"Option '{name}' must be a positive finite number of seconds.");
        }
        TimeSpan duration;
        try
        {
            duration = TimeSpan.FromSeconds(seconds);
        }
        catch (OverflowException)
        {
            throw new ArgumentException($"Option '{name}' exceeds the supported duration range.");
        }
        if (duration <= TimeSpan.Zero)
            throw new ArgumentException($"Option '{name}' must be at least one clock tick (0.0000001 seconds).");
        return duration;
    }

    private static void ValidateDateRange(DateTimeOffset? from, DateTimeOffset? before, string prefix)
    {
        if (from.HasValue && before.HasValue && from.Value >= before.Value)
            throw new ArgumentException($"--{prefix}-from must be earlier than --{prefix}-before.");
    }

    private sealed class ConsoleScanProgress : IProgress<ScanProgress>
    {
        private long _lastTimestamp;

        public void Report(ScanProgress value)
        {
            var timestamp = Stopwatch.GetTimestamp();
            if (_lastTimestamp != 0 && Stopwatch.GetElapsedTime(_lastTimestamp, timestamp) < TimeSpan.FromSeconds(1))
                return;
            _lastTimestamp = timestamp;
            Console.Error.WriteLine(FormattableString.Invariant(
                $"entries={value.Entries} directories={value.Directories} excluded={value.ExcludedEntries} pending={value.PendingDirectories} skippedLinks={value.SkippedLinks} errors={value.ErrorCount} elapsed={value.Elapsed:c}"));
        }
    }
}
