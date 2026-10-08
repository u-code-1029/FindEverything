using System.Globalization;
using System.Text;
using FindEverything.Engine;
using FindEverything.Server.Models;

namespace FindEverything.Server.Api;

public static class ResultProjection
{
    public static IReadOnlyDictionary<string, object?> Project(IndexedEntry entry, OutputDefinition output) =>
        output.Columns.ToDictionary(ColumnName, column => Value(entry, column), StringComparer.Ordinal);

    public static byte[] Csv(IReadOnlyList<IndexedEntry> entries, OutputDefinition output)
    {
        var text = new StringBuilder();
        text.AppendJoin(',', output.Columns.Select(column => Cell(ColumnName(column)))).Append("\r\n");
        foreach (var entry in entries)
            text.AppendJoin(',', output.Columns.Select(column => Cell(Format(Value(entry, column))))).Append("\r\n");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static object? Value(IndexedEntry entry, OutputColumn column) => column switch
    {
        OutputColumn.Name => entry.Name,
        OutputColumn.FullPath => entry.FullPath,
        OutputColumn.ParentPath => entry.ParentPath,
        OutputColumn.Kind => entry.Kind.ToString().ToLowerInvariant(),
        OutputColumn.SizeBytes => entry.SizeBytes,
        OutputColumn.CreatedUtc => entry.CreatedUtc,
        OutputColumn.ModifiedUtc => entry.ModifiedUtc,
        OutputColumn.CoveragePending => entry.CoveragePending,
        _ => throw new ArgumentException("Unknown output column.")
    };

    private static string ColumnName(OutputColumn column) => column switch
    {
        OutputColumn.Name => "name", OutputColumn.FullPath => "fullPath", OutputColumn.ParentPath => "parentPath",
        OutputColumn.Kind => "kind", OutputColumn.SizeBytes => "sizeBytes", OutputColumn.CreatedUtc => "createdUtc",
        OutputColumn.ModifiedUtc => "modifiedUtc", OutputColumn.CoveragePending => "coveragePending",
        _ => throw new ArgumentException("Unknown output column.")
    };

    private static string Format(object? value) => value switch
    {
        null => "", DateTimeOffset instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false", IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string Cell(string value)
    {
        // Quoting alone does not stop spreadsheet formulas; neutralize dangerous leading characters.
        var leading = value.AsSpan().TrimStart(' ');
        if (!leading.IsEmpty && leading[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
