using Sdat.Core.Scheduling;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class DailyScheduleOccurrenceResolverTests
{
    [Fact]
    public void Selects_todays_occurrence_when_it_is_still_upcoming()
    {
        var now = new DateTimeOffset(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);
        var schedule = CreateDaily(new TimeOnly(21, 15));

        var due = DailyScheduleOccurrenceResolver.GetNextExecution(schedule, now);

        Assert.Equal(new DateTimeOffset(2026, 7, 21, 21, 15, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void Selects_tomorrows_occurrence_after_todays_time()
    {
        var now = new DateTimeOffset(2026, 7, 21, 22, 0, 0, TimeSpan.Zero);
        var schedule = CreateDaily(new TimeOnly(21, 15));

        var due = DailyScheduleOccurrenceResolver.GetNextExecution(schedule, now);

        Assert.Equal(new DateTimeOffset(2026, 7, 22, 21, 15, 0, TimeSpan.Zero), due);
    }

    private static ScheduleSnapshot CreateDaily(TimeOnly time) => new(
        Guid.NewGuid(),
        1,
        ScheduleKind.Daily,
        PowerActionType.Shutdown,
        null,
        time,
        "UTC",
        false,
        ScheduleStatus.Active,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}
