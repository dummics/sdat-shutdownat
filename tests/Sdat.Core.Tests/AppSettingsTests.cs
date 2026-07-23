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

    [Fact]
    public void Language_defaults_to_the_Windows_preference()
    {
        Assert.Equal(UiLanguagePreference.System, new AppSettings().Validate().PreferredLanguage);
    }

    [Fact]
    public void Test_mode_requires_developer_mode()
    {
        var settings = new AppSettings
        {
            DeveloperModeEnabled = false,
            SimulationModeEnabled = true,
        }.Validate();

        Assert.False(settings.SimulationModeEnabled);
        Assert.False(settings.IsTestMode);
    }

    [Fact]
    public void Logging_defaults_to_information()
    {
        Assert.Equal(AppLogLevel.Information, new AppSettings().Validate().LogLevel);
    }

    [Theory]
    [InlineData("system", "system")]
    [InlineData("IT", "it-IT")]
    [InlineData("it-it", "it-IT")]
    [InlineData("EN", "en-US")]
    [InlineData("en-us", "en-US")]
    public void Supported_language_is_normalized(string input, string expected)
    {
        var settings = new AppSettings { PreferredLanguage = input }.Validate();

        Assert.Equal(expected, settings.PreferredLanguage);
    }

    [Fact]
    public void Unsupported_language_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AppSettings { PreferredLanguage = "fr-FR" }.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void Invalid_daily_overlap_window_is_rejected(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AppSettings { DailyOverlapWindowMinutes = minutes }.Validate());
    }

    [Theory]
    [InlineData("alt+control+s", "Ctrl+Alt+S", "S")]
    [InlineData("Shift+F12", "Shift+F12", "F12")]
    [InlineData("Win+1", "Win+1", "1")]
    public void Hotkey_is_parsed_and_normalized(string input, string expected, string expectedKey)
    {
        var hotkey = HotkeyGesture.Parse(input);

        Assert.Equal(expected, hotkey.ToString());
        Assert.Equal(expectedKey, hotkey.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("S")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Ctrl+S")]
    public void Invalid_hotkey_is_rejected(string input)
    {
        Assert.Throws<FormatException>(() => HotkeyGesture.Parse(input));
    }
}
