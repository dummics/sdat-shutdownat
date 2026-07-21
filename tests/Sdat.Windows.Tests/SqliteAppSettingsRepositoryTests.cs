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
        });
        var loaded = await repository.LoadAsync();

        Assert.Equal([10, 2], saved.ReminderOffsetsMinutes);
        Assert.Equal(saved.ReminderOffsetsMinutes, loaded.ReminderOffsetsMinutes);
        Assert.Equal(saved.CriticalOverlayEnabled, loaded.CriticalOverlayEnabled);
        Assert.Equal(saved.StartCompanionAtLogin, loaded.StartCompanionAtLogin);
        Assert.Equal(45, loaded.DailyOverlapWindowMinutes);
        Assert.Equal("Shift+F12", loaded.PaletteHotkey);
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
