using System.Text;
using FindEverything.Engine;
using FindEverything.Server.Api;
using FindEverything.Server.Models;
using Xunit;

namespace FindEverything.Server.Tests;

public sealed class ResultProjectionTests
{
    [Theory]
    [InlineData("=SUM(1,2).txt", "\"'=SUM(1,2).txt\"")]
    [InlineData("+value.txt", "\"'+value.txt\"")]
    [InlineData("-value.txt", "\"'-value.txt\"")]
    [InlineData("@value.txt", "\"'@value.txt\"")]
    [InlineData("  =value.txt", "\"'  =value.txt\"")]
    [InlineData("\tvalue.txt", "\"'\tvalue.txt\"")]
    [InlineData("ordinary,comma.txt", "\"ordinary,comma.txt\"")]
    [InlineData("ordinary\"quote.txt", "\"ordinary\"\"quote.txt\"")]
    public void CsvQuotesValuesAndNeutralizesSpreadsheetFormulas(string name, string expectedCell)
    {
        var entry = new IndexedEntry("hidden/path", name, "hidden", EntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var output = new OutputDefinition { Columns = [OutputColumn.Name] };
        Assert.Equal("\"name\"\r\n" + expectedCell + "\r\n", Encoding.UTF8.GetString(ResultProjection.Csv([entry], output)));
    }
}
