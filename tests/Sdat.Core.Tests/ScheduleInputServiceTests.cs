using Sdat.Core.Scheduling;
using Sdat.Core.TimeExpressions;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class ScheduleInputServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Relative_input_prepares_one_time_draft()
    {
        var result = new ScheduleInputService().Prepare(
            "36m",
            ScheduleKind.OneTime,
            PowerActionType.Restart,
            keepDaily: true,
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal(Now.AddMinutes(36), result.Draft.TargetAt);
        Assert.Equal(PowerActionType.Restart, result.Draft.Action);
        Assert.True(result.Draft.KeepDaily);
    }

    [Fact]
    public void Absolute_input_prepares_daily_draft()
    {
        var result = new ScheduleInputService().Prepare(
            "02:30",
            ScheduleKind.Daily,
            PowerActionType.Suspend,
            keepDaily: false,
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal(new TimeOnly(2, 30), result.Draft.DailyAt);
        Assert.Equal(PowerActionType.Suspend, result.Draft.Action);
    }

    [Fact]
    public void Relative_daily_input_is_rejected()
    {
        Assert.Throws<TimeExpressionParseException>(() =>
            new ScheduleInputService().Prepare(
                "2h",
                ScheduleKind.Daily,
                PowerActionType.Shutdown,
                keepDaily: false,
                Now,
                TimeZoneInfo.Utc));
    }
}
