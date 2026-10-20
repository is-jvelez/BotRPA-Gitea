using Bots.Infrastructure.Scheduling;
using Cronos;

namespace Bots.Infrastructure.Tests;

public sealed class CronosScheduleTests
{
    private const string Guayaquil = "America/Guayaquil"; // UTC-5, sin horario de verano

    private static DateTimeOffset Utc(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Next_occurrence_is_evaluated_in_the_configured_time_zone()
    {
        var schedule = new CronosSchedule("0 8 * * *", Guayaquil); // 8:00 en Guayaquil = 13:00 UTC

        var next = schedule.GetNextOccurrence(Utc(20, 12));

        Assert.Equal(Utc(20, 13), next);
        Assert.Equal(TimeSpan.Zero, next!.Value.Offset);
    }

    [Fact]
    public void Next_occurrence_is_strictly_after_from()
    {
        var schedule = new CronosSchedule("0 8 * * *", Guayaquil);

        Assert.Equal(Utc(21, 13), schedule.GetNextOccurrence(Utc(20, 13)));
    }

    [Fact]
    public void Occurrences_range_excludes_from_and_includes_to()
    {
        var schedule = new CronosSchedule("*/5 * * * *", Guayaquil);

        var occurrences = schedule.GetOccurrences(Utc(20, 10, 0), Utc(20, 10, 15));

        Assert.Equal([Utc(20, 10, 5), Utc(20, 10, 10), Utc(20, 10, 15)], occurrences);
        Assert.All(occurrences, o => Assert.Equal(TimeSpan.Zero, o.Offset));
    }

    [Fact]
    public void Daily_occurrences_across_days_in_the_time_zone()
    {
        var schedule = new CronosSchedule("0 8 * * *", Guayaquil);

        Assert.Equal([Utc(20, 13), Utc(21, 13), Utc(22, 13)], schedule.GetOccurrences(Utc(20, 0), Utc(22, 13)));
    }

    [Fact]
    public void Empty_or_inverted_range_returns_no_occurrences()
    {
        var schedule = new CronosSchedule("*/5 * * * *", Guayaquil);

        Assert.Empty(schedule.GetOccurrences(Utc(20, 10), Utc(20, 10)));
        Assert.Empty(schedule.GetOccurrences(Utc(20, 11), Utc(20, 10)));
    }

    [Fact]
    public void Occurrences_are_generated_lazily()
    {
        var schedule = new CronosSchedule("* * * * *", Guayaquil);

        // Un siglo de ocurrencias por minuto (~52 millones): solo se generan las que se consumen.
        var occurrences = schedule.GetOccurrences(Utc(20, 10), Utc(20, 10).AddYears(100));

        Assert.Equal([Utc(20, 10, 1), Utc(20, 10, 2), Utc(20, 10, 3)], occurrences.Take(3));
    }

    [Fact]
    public void Invalid_cron_expression_fails_fast() =>
        Assert.Throws<CronFormatException>(() => new CronosSchedule("cada 5 minutos", Guayaquil));

    [Fact]
    public void Unknown_time_zone_fails_fast() =>
        Assert.Throws<TimeZoneNotFoundException>(() => new CronosSchedule("*/5 * * * *", "America/NoExiste"));
}
