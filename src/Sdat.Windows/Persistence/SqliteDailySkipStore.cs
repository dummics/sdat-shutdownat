using Microsoft.Data.Sqlite;
using Sdat.Core.Scheduling;

namespace Sdat.Windows.Persistence;

public sealed class SqliteDailySkipStore(
    SqliteStoreOptions options,
    TimeProvider? timeProvider = null) : IDailySkipStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<DailySkipRequest> RequestAsync(
        Guid scheduleId,
        long scheduleRevision,
        DateTimeOffset executeDueAt,
        CancellationToken cancellationToken = default)
    {
        var requestedAt = _timeProvider.GetUtcNow();
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using (var expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = """
                UPDATE daily_skip_requests
                SET consumed_utc = $consumedUtc
                WHERE schedule_id = $scheduleId
                  AND consumed_utc IS NULL
                  AND execute_due_utc <> $executeDueUtc;
                """;
            expire.Parameters.AddWithValue("$consumedUtc", requestedAt.ToString("O"));
            expire.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D"));
            expire.Parameters.AddWithValue("$executeDueUtc", executeDueAt.ToUniversalTime().ToString("O"));
            await expire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO daily_skip_requests(
                schedule_id, schedule_revision, execute_due_utc, requested_utc, consumed_utc)
            SELECT id, revision, $executeDueUtc, $requestedUtc, NULL
            FROM schedules
            WHERE id = $scheduleId AND revision = $revision AND kind = 'Daily' AND status = 'Active'
            ON CONFLICT(schedule_id, schedule_revision, execute_due_utc)
            DO UPDATE SET requested_utc = excluded.requested_utc, consumed_utc = NULL;
            """;
        command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D"));
        command.Parameters.AddWithValue("$revision", scheduleRevision);
        command.Parameters.AddWithValue("$executeDueUtc", executeDueAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$requestedUtc", requestedAt.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ScheduleConflictException("The daily schedule changed before its next occurrence could be skipped.");
        }

        await using var operation = connection.CreateCommand();
        operation.Transaction = transaction;
        operation.CommandText = """
            INSERT INTO operations(
                occurred_utc, operation, schedule_id, schedule_revision, outcome, detail_json)
            VALUES($occurredUtc, 'RequestDailySkip', $scheduleId, $revision, 'Success', $detail);
            """;
        operation.Parameters.AddWithValue("$occurredUtc", requestedAt.ToString("O"));
        operation.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D"));
        operation.Parameters.AddWithValue("$revision", scheduleRevision);
        operation.Parameters.AddWithValue("$detail", $"{{\"executeDueUtc\":\"{executeDueAt.ToUniversalTime():O}\"}}");
        await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return new DailySkipRequest(scheduleId, scheduleRevision, executeDueAt.ToUniversalTime(), requestedAt);
    }
}
