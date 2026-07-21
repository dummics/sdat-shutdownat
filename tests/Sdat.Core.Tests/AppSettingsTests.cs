using Sdat.Core.Settings;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Validation_normalizes_offsets()
    {
        var settings = new AppSettings { ReminderOffsetsMinutes = [2, 10, 2, 5] }.Validate();

        Assert.Equal([10, 5, 2], settings.ReminderOffsetsMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Invalid_offset_is_rejected(int offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AppSettings { ReminderOffsetsMinutes = [offset] }.Validate());
    }

    [Fact]
    public void Daily_overlap_defaults_to_two_hours()
    {
        Assert.Equal(120, new AppSettings().Validate().DailyOverlapWindowMinutes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void Invalid_daily_overlap_window_is_rejected(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AppSettings { DailyOverlapWindowMinutes = minutes }.Validate());
    }
}
