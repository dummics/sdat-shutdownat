using Sdat.Core.Settings;
using Sdat.Windows.Persistence;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class SqliteAppSettingsRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-settings-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Defaults_are_returned_until_settings_are_saved()
    {
        var options = CreateOptions();
        await new SqliteScheduleRepository(options).InitializeAsync();
        var repository = new SqliteAppSettingsRepository(options);

        var settings = await repository.LoadAsync();

        Assert.Equal([2], settings.ReminderOffsetsMinutes);
        Assert.True(settings.CriticalOverlayEnabled);
        Assert.False(settings.StartCompanionAtLogin);
        Assert.Equal(120, settings.DailyOverlapWindowMinutes);
        Assert.Equal("Ctrl+Alt+S", settings.PaletteHotkey);
        Assert.Equal(UiLanguagePreference.System, settings.PreferredLanguage);
    }

    [Fact]
    public async Task Settings_round_trip_in_normalized_form()
    {
        var options = CreateOptions();
        await new SqliteScheduleRepository(options).InitializeAsync();
        var repository = new SqliteAppSettingsRepository(options);

        var saved = await repository.SaveAsync(new AppSettings
        {
            ReminderOffsetsMinutes = [2, 10, 2],
            CriticalOverlayEnabled = false,
            StartCompanionAtLogin = true,
            DailyOverlapWindowMinutes = 45,
            PaletteHotkey = "shift+f12",
            PreferredLanguage = "it",
        });
        var loaded = await repository.LoadAsync();

        Assert.Equal([10, 2], saved.ReminderOffsetsMinutes);
        Assert.Equal(saved.ReminderOffsetsMinutes, loaded.ReminderOffsetsMinutes);
        Assert.Equal(saved.CriticalOverlayEnabled, loaded.CriticalOverlayEnabled);
        Assert.Equal(saved.StartCompanionAtLogin, loaded.StartCompanionAtLogin);
        Assert.Equal(45, loaded.DailyOverlapWindowMinutes);
        Assert.Equal("Shift+F12", loaded.PaletteHotkey);
        Assert.Equal(UiLanguagePreference.Italian, loaded.PreferredLanguage);
    }

    [Fact]
    public async Task Early_language_reader_uses_saved_preference_without_changing_the_store()
    {
        var options = CreateOptions();
        await new SqliteScheduleRepository(options).InitializeAsync();
        var repository = new SqliteAppSettingsRepository(options);
        await repository.SaveAsync(new AppSettings { PreferredLanguage = UiLanguagePreference.English });

        var preference = SqliteLanguagePreferenceReader.ReadOrSystemDefault(options);

        Assert.Equal(UiLanguagePreference.English, preference);
    }

    [Fact]
    public void Early_language_reader_falls_back_to_system_when_the_database_does_not_exist()
    {
        var preference = SqliteLanguagePreferenceReader.ReadOrSystemDefault(CreateOptions());

        Assert.Equal(UiLanguagePreference.System, preference);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SqliteStoreOptions CreateOptions() => new()
    {
        DatabasePath = Path.Combine(_root, "data", "sdat.db"),
        BackupDirectory = Path.Combine(_root, "backups"),
    };
}
