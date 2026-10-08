using FindEverything.Server.Models;
using FindEverything.Server.Services;
using Xunit;

namespace FindEverything.Server.Tests;

public sealed class ScheduleCalculatorTests
{
    [Fact]
    public void OnceHasNoSecondOccurrence()
    {
        var anchor = Utc(2026, 1, 1, 10, 0);
        var definition = Schedule(ScheduleKind.Once, anchor);
        Assert.Equal(anchor, ScheduleCalculator.GetNextDueUtc(definition, anchor.AddDays(-1)));
        Assert.Null(ScheduleCalculator.GetNextDueUtc(definition, anchor));
        Assert.Null(ScheduleCalculator.GetNextDueUtc(definition, anchor.AddYears(1)));
    }

    [Fact]
    public void IntervalSkipsMissedRunsAndKeepsOriginalAnchor()
    {
        var anchor = Utc(2026, 1, 1, 10, 0);
        var definition = Schedule(ScheduleKind.Interval, anchor) with { IntervalMinutes = 60 };
        Assert.Equal(anchor, ScheduleCalculator.GetNextDueUtc(definition, anchor.AddTicks(-1)));
        Assert.Equal(anchor.AddHours(1), ScheduleCalculator.GetNextDueUtc(definition, anchor));
        Assert.Equal(Utc(2026, 2, 3, 15, 0), ScheduleCalculator.GetNextDueUtc(definition, Utc(2026, 2, 3, 14, 27)));
    }

    [Fact]
    public void DailySpringGapSkipsNonexistentLocalTime()
    {
        var definition = Schedule(ScheduleKind.Daily, Utc(2026, 1, 1, 0, 0)) with
        {
            LocalTime = new TimeOnly(2, 30), TimeZoneId = "America/New_York"
        };
        // March 8 has no 02:30. March 9 is EDT (UTC-04).
        Assert.Equal(Utc(2026, 3, 9, 6, 30), ScheduleCalculator.GetNextDueUtc(definition, Utc(2026, 3, 8, 0, 0)));
    }

    [Fact]
    public void DailyFallRepeatedHourRunsOnlyAtFirstOccurrence()
    {
        var definition = Schedule(ScheduleKind.Daily, Utc(2026, 1, 1, 0, 0)) with
        {
            LocalTime = new TimeOnly(1, 30), TimeZoneId = "America/New_York"
        };
        Assert.Equal(Utc(2026, 11, 1, 5, 30), ScheduleCalculator.GetNextDueUtc(definition, Utc(2026, 11, 1, 0, 0)));
        Assert.Equal(Utc(2026, 11, 2, 6, 30), ScheduleCalculator.GetNextDueUtc(definition, Utc(2026, 11, 1, 5, 45)));
    }

    [Fact]
    public void DailyDoesNotScheduleBeforeAnchorEvenOnSameLocalDay()
    {
        var definition = Schedule(ScheduleKind.Daily, Utc(2026, 1, 1, 12, 0)) with
        {
            LocalTime = new TimeOnly(10, 0), TimeZoneId = "UTC"
        };
        Assert.Equal(Utc(2026, 1, 2, 10, 0), ScheduleCalculator.GetNextDueUtc(definition, Utc(2025, 12, 30, 0, 0)));
    }

    [Fact]
    public void IntervalNearDateBoundsReturnsNoOccurrenceInsteadOfOverflowing()
    {
        var definition = Schedule(ScheduleKind.Interval, Utc(9999, 12, 31, 23, 59)) with { IntervalMinutes = 1 };
        Assert.Null(ScheduleCalculator.GetNextDueUtc(definition, definition.AnchorUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidIntervalCannotBeSaved(int minutes)
    {
        var definition = Schedule(ScheduleKind.Interval, Utc(2026, 1, 1, 0, 0)) with { IntervalMinutes = minutes };
        Assert.Throws<ArgumentException>(() => ScheduleCalculator.Validate(definition));
    }

    [Theory]
    [InlineData("Eastern Standard Time")]
    [InlineData("Unknown/Zone")]
    public void DailyRequiresPortableKnownTimeZone(string zone)
    {
        var definition = Schedule(ScheduleKind.Daily, Utc(2026, 1, 1, 0, 0)) with { LocalTime = new TimeOnly(1, 0), TimeZoneId = zone };
        Assert.Throws<ArgumentException>(() => ScheduleCalculator.Validate(definition));
    }

    private static ScheduleDefinition Schedule(ScheduleKind kind, DateTimeOffset anchor) => new()
    {
        ProfileId = Guid.NewGuid(), Name = "Scheduled scan", Kind = kind, AnchorUtc = anchor
    };

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
