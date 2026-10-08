using FindEverything.Server.Models;

namespace FindEverything.Server.Services;

/// <summary>Computes future occurrences without enumerating missed executions.</summary>
public static class ScheduleCalculator
{
    public static void Validate(ScheduleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Enum.IsDefined(definition.Kind))
            throw new ArgumentException("Unknown schedule kind.");
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 200)
            throw new ArgumentException("Schedule name must contain 1 to 200 characters.");
        if (definition.ProfileId == Guid.Empty)
            throw new ArgumentException("A profile is required.");
        if (definition.AnchorUtc == default)
            throw new ArgumentException("An anchor UTC timestamp is required.");
        if (definition.AnchorUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("AnchorUtc must use UTC (offset zero).");
        switch (definition.Kind)
        {
            case ScheduleKind.Once:
                if (definition.IntervalMinutes is not null || definition.LocalTime is not null || definition.TimeZoneId is not null)
                    throw new ArgumentException("Once schedules only accept AnchorUtc.");
                break;
            case ScheduleKind.Interval:
                if (definition.IntervalMinutes is not > 0)
                    throw new ArgumentException("IntervalMinutes must be positive.");
                if (definition.LocalTime is not null || definition.TimeZoneId is not null)
                    throw new ArgumentException("Interval schedules use UTC rather than a local time zone.");
                break;
            case ScheduleKind.Daily:
                if (definition.IntervalMinutes is not null || definition.LocalTime is null || string.IsNullOrWhiteSpace(definition.TimeZoneId))
                    throw new ArgumentException("Daily schedules require LocalTime and an IANA time zone, without IntervalMinutes.");
                _ = FindTimeZone(definition.TimeZoneId);
                break;
        }
    }

    /// <summary>
    /// Finds the first occurrence strictly after the supplied time. Daily local
    /// times in a DST gap are skipped; repeated times use the earlier UTC instant.
    /// AnchorUtc is the minimum permitted occurrence for every schedule kind.
    /// </summary>
    public static DateTimeOffset? GetNextDueUtc(ScheduleDefinition definition, DateTimeOffset afterExclusive)
    {
        Validate(definition);
        var anchor = definition.AnchorUtc.ToUniversalTime();
        var after = afterExclusive.ToUniversalTime();
        if (definition.Kind == ScheduleKind.Once)
            return anchor > after ? anchor : null;
        if (definition.Kind == ScheduleKind.Interval)
        {
            if (anchor > after) return anchor;
            var stepTicks = TimeSpan.FromMinutes(definition.IntervalMinutes!.Value).Ticks;
            var elapsed = after.UtcTicks - anchor.UtcTicks;
            var occurrences = elapsed / stepTicks + 1;
            var remainingTicks = DateTimeOffset.MaxValue.UtcTicks - anchor.UtcTicks;
            if (occurrences > remainingTicks / stepTicks) return null;
            return anchor.AddTicks(occurrences * stepTicks);
        }

        var zone = FindTimeZone(definition.TimeZoneId!);
        var earliest = after >= anchor ? after : anchor;
        var local = TimeZoneInfo.ConvertTime(earliest, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        // A zone may skip a whole local date. Bound the calculation without a
        // per-missed-day loop; ordinary DST gaps need only the next local day.
        for (var day = 0; day < 370; day++)
        {
            var candidateLocal = date.ToDateTime(definition.LocalTime!.Value, DateTimeKind.Unspecified);
            if (!zone.IsInvalidTime(candidateLocal))
            {
                TimeSpan offset;
                if (zone.IsAmbiguousTime(candidateLocal))
                    offset = zone.GetAmbiguousTimeOffsets(candidateLocal).Max();
                else
                    offset = zone.GetUtcOffset(candidateLocal);
                try
                {
                    var candidate = new DateTimeOffset(candidateLocal, offset).ToUniversalTime();
                    if (candidate > after && candidate >= anchor) return candidate;
                }
                catch (ArgumentException) { /* UTC conversion exceeds DateTime bounds. */ }
            }
            if (date == DateOnly.MaxValue) return null;
            date = date.AddDays(1);
        }
        throw new ArgumentException("No valid daily occurrence could be found in the selected time zone.");
    }

    private static TimeZoneInfo FindTimeZone(string id)
    {
        if (!id.Contains('/') && id != "UTC" && id != "Etc/UTC")
            throw new ArgumentException("TimeZoneId must be an IANA time zone such as America/Chicago or UTC.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException error) { throw new ArgumentException("Unknown time zone.", nameof(id), error); }
        catch (InvalidTimeZoneException error) { throw new ArgumentException("Invalid time zone.", nameof(id), error); }
    }
}
