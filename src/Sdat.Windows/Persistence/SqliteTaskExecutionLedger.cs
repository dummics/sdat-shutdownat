using Microsoft.Data.Sqlite;
using Sdat.Core.Execution;
using Sdat.Core.Scheduling;

namespace Sdat.Windows.Persistence;

public sealed class SqliteTaskExecutionLedger(
    SqliteStoreOptions options,
    TimeProvider? timeProvider = null) : ITaskExecutionLedger
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<OccurrenceClaimResult> TryClaimAsync(
        OccurrenceClaim claim,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using var validate = connection.CreateCommand();
        validate.Transaction = transaction;
        validate.CommandText = "SELECT kind FROM schedules WHERE id = $id AND revision = $revision AND status = 'Active';";
        validate.Parameters.AddWithValue("$id", claim.Invocation.ScheduleId.ToString("D"));
        validate.Parameters.AddWithValue("$revision", claim.Invocation.Revision);
        var kindValue = await validate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (kindValue is null)
        {
            return OccurrenceClaimResult.Stale;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO occurrences(
                id, schedule_id, schedule_revision, occurrence_kind, due_utc, status,
                completed_utc, error_code, error_detail)
            VALUES(
                $id, $scheduleId, $revision, $kind, $dueUtc, $status,
                $completedUtc, NULL, NULL);
            """;
        insert.Parameters.AddWithValue("$id", claim.OccurrenceId.ToString("D"));
        insert.Parameters.AddWithValue("$scheduleId", claim.Invocation.ScheduleId.ToString("D"));
        insert.Parameters.AddWithValue("$revision", claim.Invocation.Revision);
        insert.Parameters.AddWithValue("$kind", claim.Invocation.Role.ToString());
        insert.Parameters.AddWithValue("$dueUtc", claim.DueAt.ToUniversalTime().ToString("O"));
        insert.Parameters.AddWithValue("$status", claim.InitialOutcome.ToString());
        insert.Parameters.AddWithValue(
            "$completedUtc",
            claim.InitialOutcome == OccurrenceOutcome.Skipped
                ? _timeProvider.GetUtcNow().ToString("O")
                : (object)DBNull.Value);
        if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return OccurrenceClaimResult.AlreadyHandled;
        }

        if (claim.Invocation.Role == SchedulerTaskRole.Execute &&
            string.Equals(Convert.ToString(kindValue), ScheduleKind.OneTime.ToString(), StringComparison.Ordinal))
        {
            await using var completeSchedule = connection.CreateCommand();
            completeSchedule.Transaction = transaction;
            completeSchedule.CommandText = """
                UPDATE schedules
                SET revision = revision + 1, status = 'Completed', updated_utc = $updatedUtc
                WHERE id = $id AND revision = $revision AND status = 'Active';
                """;
            completeSchedule.Parameters.AddWithValue("$updatedUtc", _timeProvider.GetUtcNow().ToString("O"));
            completeSchedule.Parameters.AddWithValue("$id", claim.Invocation.ScheduleId.ToString("D"));
            completeSchedule.Parameters.AddWithValue("$revision", claim.Invocation.Revision);
            if (await completeSchedule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return OccurrenceClaimResult.Stale;
            }
        }

        await using var operation = connection.CreateCommand();
        operation.Transaction = transaction;
        operation.CommandText = """
            INSERT INTO operations(
                occurred_utc, operation, schedule_id, schedule_revision, outcome, detail_json)
            VALUES($occurredUtc, 'ClaimOccurrence', $scheduleId, $revision, $outcome, NULL);
            """;
        operation.Parameters.AddWithValue("$occurredUtc", _timeProvider.GetUtcNow().ToString("O"));
        operation.Parameters.AddWithValue("$scheduleId", claim.Invocation.ScheduleId.ToString("D"));
        operation.Parameters.AddWithValue("$revision", claim.Invocation.Revision);
        operation.Parameters.AddWithValue("$outcome", claim.InitialOutcome.ToString());
        await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        transaction.Commit();
        return OccurrenceClaimResult.Claimed;
    }

    public Task CompleteAsync(Guid occurrenceId, CancellationToken cancellationToken = default) =>
        FinishAsync(occurrenceId, OccurrenceOutcome.Completed, null, null, cancellationToken);

    public Task FailAsync(
        Guid occurrenceId,
        string errorCode,
        string errorDetail,
        CancellationToken cancellationToken = default) =>
        FinishAsync(occurrenceId, OccurrenceOutcome.Failed, errorCode, errorDetail, cancellationToken);

    private async Task FinishAsync(
        Guid occurrenceId,
        OccurrenceOutcome outcome,
        string? errorCode,
        string? errorDetail,
        CancellationToken cancellationToken)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE occurrences
            SET status = $status, completed_utc = $completedUtc,
                error_code = $errorCode, error_detail = $errorDetail
            WHERE id = $id AND status = 'Pending';
            """;
        command.Parameters.AddWithValue("$status", outcome.ToString());
        command.Parameters.AddWithValue("$completedUtc", _timeProvider.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$errorCode", errorCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorDetail", errorDetail ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Pending occurrence {occurrenceId:D} was not found.");
        }
    }
}
