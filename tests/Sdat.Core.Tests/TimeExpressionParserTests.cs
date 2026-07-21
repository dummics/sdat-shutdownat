using Sdat.Core.TimeExpressions;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class TimeExpressionParserTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private readonly TimeExpressionParser _parser = new();

    [Theory]
    [InlineData("90m", 5400)]
    [InlineData("3.5h", 12600)]
    [InlineData("1,5h", 5400)]
    [InlineData("1h30m", 5400)]
    [InlineData("mezzora", 1800)]
    [InlineData("mezza ora", 1800)]
    [InlineData("180s", 180)]
    public void Duration_inputs_match_the_legacy_contract(string value, int expectedSeconds)
    {
        Assert.Equal(expectedSeconds, _parser.TryResolveDurationSeconds(value));
    }

    [Theory]
    [InlineData("2330", 23, 30)]
    [InlineData("23:30", 23, 30)]
    [InlineData("930", 9, 30)]
    public void Absolute_inputs_are_normalized(string value, int expectedHour, int expectedMinute)
    {
        var now = new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero);

        var result = _parser.Resolve(value, now, Utc);

        Assert.Equal(TimeExpressionKind.Absolute, result.Kind);
        Assert.Equal(expectedHour, result.Target.Hour);
        Assert.Equal(expectedMinute, result.Target.Minute);
    }

    [Fact]
    public void Past_absolute_time_rolls_to_the_next_day()
    {
        var now = new DateTimeOffset(2026, 7, 21, 23, 0, 0, TimeSpan.Zero);

        var result = _parser.Resolve("22:30", now, Utc);

        Assert.Equal(new DateTimeOffset(2026, 7, 22, 22, 30, 0, TimeSpan.Zero), result.Target);
    }

    [Fact]
    public void Relative_target_rounds_up_to_the_next_minute()
    {
        var now = new DateTimeOffset(2026, 7, 21, 20, 0, 45, TimeSpan.Zero);

        var result = _parser.Resolve("90s", now, Utc);

        Assert.Equal(new DateTimeOffset(2026, 7, 21, 20, 3, 0, TimeSpan.Zero), result.Target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1x30")]
    [InlineData("25:00")]
    [InlineData("0m")]
    public void Invalid_inputs_are_rejected(string value)
    {
        Assert.Throws<TimeExpressionParseException>(() =>
            _parser.Resolve(value, DateTimeOffset.UtcNow, Utc));
    }

    [Fact]
    public void Remaining_time_uses_compact_legacy_format()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal("1h 38m", TimeExpressionParser.FormatRemaining(now.AddMinutes(98), now));
    }
}
