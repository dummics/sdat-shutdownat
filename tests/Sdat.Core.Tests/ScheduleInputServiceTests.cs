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
        var exception = Assert.Throws<TimeExpressionParseException>(() =>
            new ScheduleInputService().Prepare(
                "2h",
                ScheduleKind.Daily,
                PowerActionType.Shutdown,
                keepDaily: false,
                Now,
                TimeZoneInfo.Utc));

        Assert.Equal(ScheduleInputErrorCode.RelativeDailyNotAllowed, exception.ErrorCode);
    }

    [Fact]
    public void Relative_preview_reuses_the_prepared_schedule_without_side_effects()
    {
        var result = new ScheduleInputService().Preview(
            "1h30m",
            ScheduleKind.OneTime,
            PowerActionType.Shutdown,
            keepDaily: false,
            Now,
            TimeZoneInfo.Utc);

        Assert.True(result.IsValid);
        Assert.Equal(Now.AddMinutes(90), result.TargetAt);
        Assert.Equal(TimeExpressionKind.Relative, result.ExpressionKind);
        Assert.Equal(5400, result.DurationSeconds);
        Assert.False(result.RollsToNextDay);
    }

    [Fact]
    public void Past_absolute_preview_reports_the_next_day()
    {
        var result = new ScheduleInputService().Preview(
            "19:30",
            ScheduleKind.OneTime,
            PowerActionType.Restart,
            keepDaily: false,
            Now,
            TimeZoneInfo.Utc);

        Assert.True(result.IsValid);
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 19, 30, 0, TimeSpan.Zero), result.TargetAt);
        Assert.True(result.RollsToNextDay);
    }

    [Fact]
    public void Daily_preview_exposes_the_resolved_clock_time()
    {
        var result = new ScheduleInputService().Preview(
            "0230",
            ScheduleKind.Daily,
            PowerActionType.Suspend,
            keepDaily: false,
            Now,
            TimeZoneInfo.Utc);

        Assert.True(result.IsValid);
        Assert.Equal(new TimeOnly(2, 30), result.DailyAt);
        Assert.Equal(TimeExpressionKind.Absolute, result.ExpressionKind);
    }

    [Theory]
    [InlineData("", ScheduleInputErrorCode.MissingValue)]
    [InlineData("random", ScheduleInputErrorCode.InvalidFormat)]
    [InlineData("25:00", ScheduleInputErrorCode.InvalidClockTime)]
    [InlineData("0m", ScheduleInputErrorCode.NonPositiveDuration)]
    public void Invalid_preview_returns_a_stable_error_code(
        string input,
        ScheduleInputErrorCode expectedError)
    {
        var result = new ScheduleInputService().Preview(
            input,
            ScheduleKind.OneTime,
            PowerActionType.Shutdown,
            keepDaily: false,
            Now,
            TimeZoneInfo.Utc);

        Assert.False(result.IsValid);
        Assert.Equal(expectedError, result.ErrorCode);
    }
}
