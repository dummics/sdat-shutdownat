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
}
