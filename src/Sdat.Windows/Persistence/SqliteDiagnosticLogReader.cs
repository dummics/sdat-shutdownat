using System.Globalization;
using Microsoft.Data.Sqlite;
using Sdat.Core.Diagnostics;

namespace Sdat.Windows.Persistence;

public sealed class SqliteDiagnosticLogReader(SqliteStoreOptions options) : IDiagnosticLogReader
{
    public async Task<IReadOnlyList<DiagnosticEvent>> ReadRecentAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The diagnostic event limit must be between 1 and 200.");
        }

        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT occurred_utc, severity, source, message, schedule_id, schedule_revision
            FROM (
                SELECT
                    occurred_utc,
                    CASE
                        WHEN outcome = 'Failed' THEN 'Error'
                        WHEN outcome IN ('Skipped', 'Degraded') THEN 'Warning'
                        ELSE 'Information'
                    END AS severity,
                    operation AS source,
                    operation || ': ' || outcome AS message,
                    schedule_id,
                    schedule_revision
                FROM operations

                UNION ALL

                SELECT
                    COALESCE(completed_utc, due_utc) AS occurred_utc,
                    'Error' AS severity,
                    'Occurrence' AS source,
                    COALESCE(error_code || ': ' || error_detail, 'Scheduled occurrence failed') AS message,
                    schedule_id,
                    schedule_revision
                FROM occurrences
                WHERE status = 'Failed'
            )
            ORDER BY occurred_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<DiagnosticEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new DiagnosticEvent(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                Enum.Parse<DiagnosticSeverity>(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }

        return events;
    }
}
