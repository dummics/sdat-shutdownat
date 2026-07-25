using Microsoft.Data.Sqlite;
using Sdat.Windows.Persistence;

namespace Sdat.Windows.Migration;

public sealed class SqliteLegacyImportJournal(
    SqliteStoreOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> IsCompletedAsync(
        string importKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM legacy_imports WHERE import_key = $key;";
        command.Parameters.AddWithValue("$key", importKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task MarkCompletedAsync(
        string importKey,
        string sourcePath,
        string detailJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO legacy_imports(import_key, imported_utc, source_path, detail_json)
            VALUES($key, $importedUtc, $sourcePath, $detailJson);
            """;
        command.Parameters.AddWithValue("$key", importKey);
        command.Parameters.AddWithValue("$importedUtc", _timeProvider.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$detailJson", detailJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
