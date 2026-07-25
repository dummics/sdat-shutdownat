using Sdat.Core.Settings;
using Sdat.Windows.Diagnostics;
using Sdat.Windows.Persistence;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class RollingFileAppLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sdat-file-log-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Ensure_file_exists_when_information_entries_are_filtered()
    {
        var options = CreateOptions();
        await new SqliteScheduleRepository(options).InitializeAsync();
        var settings = new SqliteAppSettingsRepository(options);
        await settings.SaveAsync(new AppSettings { LogLevel = AppLogLevel.Error });
        var logger = new RollingFileAppLogger(options, settings);

        await logger.EnsureFileExistsAsync();
        await logger.WriteAsync(AppLogLevel.Information, "Test", "filtered");

        Assert.True(File.Exists(options.LogPath));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(options.LogPath));
    }

    [Fact]
    public async Task Configured_level_filters_less_important_entries()
    {
        var options = CreateOptions();
        await new SqliteScheduleRepository(options).InitializeAsync();
        var settings = new SqliteAppSettingsRepository(options);
        await settings.SaveAsync(new AppSettings { LogLevel = AppLogLevel.Error });
        var logger = new RollingFileAppLogger(options, settings);

        await logger.WriteAsync(AppLogLevel.Information, "Test", "not written");
        await logger.WriteAsync(AppLogLevel.Error, "Test", "written");

        var content = await File.ReadAllTextAsync(options.LogPath);
        Assert.DoesNotContain("not written", content, StringComparison.Ordinal);
        Assert.Contains("[Error] Test: written", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_entries_are_kept_on_one_line()
    {
        var options = CreateOptions();
        await new SqliteScheduleRepository(options).InitializeAsync();
        var settings = new SqliteAppSettingsRepository(options);
        var logger = new RollingFileAppLogger(options, settings);

        await logger.WriteAsync(AppLogLevel.Information, "UI\r\nstatus", "first\r\nsecond");

        var lines = await File.ReadAllLinesAsync(options.LogPath);
        var line = Assert.Single(lines);
        Assert.Contains("UI  status: first  second", line, StringComparison.Ordinal);
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
