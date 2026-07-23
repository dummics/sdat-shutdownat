using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sdat.Core.Settings;

namespace Sdat.Windows.Persistence;

public static class SqliteLanguagePreferenceReader
{
    private const string SettingsKey = "app";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ReadOrSystemDefault(SqliteStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!File.Exists(options.DatabasePath))
        {
            return UiLanguagePreference.System;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value_json FROM settings WHERE setting_key = $key;";
            command.Parameters.AddWithValue("$key", SettingsKey);
            var value = Convert.ToString(command.ExecuteScalar());
            if (string.IsNullOrWhiteSpace(value))
            {
                return UiLanguagePreference.System;
            }

            return (JsonSerializer.Deserialize<AppSettings>(value, JsonOptions) ?? new AppSettings())
                .Validate()
                .PreferredLanguage;
        }
        catch (SqliteException)
        {
            return UiLanguagePreference.System;
        }
        catch (JsonException)
        {
            return UiLanguagePreference.System;
        }
        catch (InvalidDataException)
        {
            return UiLanguagePreference.System;
        }
        catch (ArgumentException)
        {
            return UiLanguagePreference.System;
        }
    }
}
