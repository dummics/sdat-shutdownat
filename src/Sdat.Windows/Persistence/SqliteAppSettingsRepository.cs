using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sdat.Core.Settings;

namespace Sdat.Windows.Persistence;

public sealed class SqliteAppSettingsRepository(
    SqliteStoreOptions options,
    TimeProvider? timeProvider = null) : IAppSettingsRepository
{
    private const string SettingsKey = "app";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM settings WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", SettingsKey);
        var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(value))
        {
            return new AppSettings();
        }

        try
        {
            return (JsonSerializer.Deserialize<AppSettings>(value, JsonOptions) ?? new AppSettings()).Validate();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored SDAT settings are not valid JSON.", exception);
        }
    }

    public async Task<AppSettings> SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validated = settings.Validate();
        var json = JsonSerializer.Serialize(validated, JsonOptions);
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(setting_key, value_json, updated_utc)
            VALUES($key, $value, $updatedUtc)
            ON CONFLICT(setting_key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", SettingsKey);
        command.Parameters.AddWithValue("$value", json);
        command.Parameters.AddWithValue("$updatedUtc", _timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return validated;
    }
}
