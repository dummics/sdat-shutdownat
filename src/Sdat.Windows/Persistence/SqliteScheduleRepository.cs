using System.Globalization;
using Microsoft.Data.Sqlite;
using Sdat.Core.Scheduling;
using Sdat.Core.Storage;

namespace Sdat.Windows.Persistence;

public sealed class SqliteScheduleRepository(
    SqliteStoreOptions options,
    TimeProvider? timeProvider = null) : IScheduleRepository
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await SqliteSchema.MigrateAsync(connection, _timeProvider, cancellationToken).ConfigureAwait(false);

        var health = await SqliteSchema.CheckHealthAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!health.CanExecutePowerActions)
        {
            throw new InvalidDataException($"SDAT database is not healthy: {health.Detail}");
        }
    }

    public async Task<ScheduleSnapshot> CreateAsync(
        ScheduleDraft draft,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var snapshot = new ScheduleSnapshot(
            Guid.NewGuid(),
            1,
            draft.Kind,
            draft.Action,
            draft.TargetAt?.ToUniversalTime(),
            draft.DailyAt,
            draft.TimeZoneId,
            draft.KeepDaily,
            ScheduleStatus.Active,
            now,
            now);

        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        try
        {
            await InsertScheduleAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
            await AppendOperationAsync(connection, transaction, "CreateSchedule", snapshot, "Success", cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return snapshot;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ScheduleConflictException($"An active {draft.Kind} schedule already exists.");
        }
    }

    public async Task<ScheduleSnapshot> UpdateAsync(
        Guid id,
        long expectedRevision,
        ScheduleDraft draft,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var existing = await GetAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Schedule {id} was not found.");

        var updated = existing with
        {
            Revision = expectedRevision + 1,
            Kind = draft.Kind,
            Action = draft.Action,
            TargetAt = draft.TargetAt?.ToUniversalTime(),
            DailyAt = draft.DailyAt,
            TimeZoneId = draft.TimeZoneId,
            KeepDaily = draft.KeepDaily,
            Status = ScheduleStatus.Active,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE schedules
            SET revision = $newRevision,
                kind = $kind,
                action = $action,
                target_utc = $targetUtc,
                daily_time = $dailyTime,
                time_zone_id = $timeZoneId,
                keep_daily = $keepDaily,
                status = $status,
                updated_utc = $updatedUtc
            WHERE id = $id AND revision = $expectedRevision AND status = 'Active';
            """;
        AddScheduleParameters(command, updated);
        command.Parameters.AddWithValue("$newRevision", updated.Revision);
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new ScheduleConflictException($"Schedule {id} changed before this update could be applied.");
        }

        await AppendOperationAsync(connection, transaction, "UpdateSchedule", updated, "Success", cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
        return updated;
    }

    public async Task<ScheduleSnapshot> CancelAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var existing = await GetAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Schedule {id} was not found.");
        var cancelled = existing with
        {
            Revision = expectedRevision + 1,
            Status = ScheduleStatus.Cancelled,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE schedules
            SET revision = $newRevision, status = 'Cancelled', updated_utc = $updatedUtc
            WHERE id = $id AND revision = $expectedRevision AND status = 'Active';
            """;
        command.Parameters.AddWithValue("$newRevision", cancelled.Revision);
        command.Parameters.AddWithValue("$updatedUtc", cancelled.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new ScheduleConflictException($"Schedule {id} changed before it could be cancelled.");
        }

        await AppendOperationAsync(connection, transaction, "CancelSchedule", cancelled, "Success", cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
        return cancelled;
    }

    public async Task<ScheduleSnapshot?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        return await GetAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduleSnapshot>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeInactive
            ? "SELECT * FROM schedules ORDER BY created_utc;"
            : "SELECT * FROM schedules WHERE status = 'Active' ORDER BY created_utc;";

        var schedules = new List<ScheduleSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            schedules.Add(ReadSchedule(reader));
        }

        return schedules;
    }

    public async Task<StoreHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false);
            return await SqliteSchema.CheckHealthAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new StoreHealth(StoreHealthStatus.Unavailable, exception.Message);
        }
    }

    private static async Task InsertScheduleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schedules(
                id, revision, kind, action, target_utc, daily_time, time_zone_id,
                keep_daily, status, created_utc, updated_utc)
            VALUES(
                $id, $revision, $kind, $action, $targetUtc, $dailyTime, $timeZoneId,
                $keepDaily, $status, $createdUtc, $updatedUtc);
            """;
        AddScheduleParameters(command, snapshot);
        command.Parameters.AddWithValue("$revision", snapshot.Revision);
        command.Parameters.AddWithValue("$createdUtc", snapshot.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ScheduleSnapshot?> GetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM schedules WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSchedule(reader)
            : null;
    }

    private static ScheduleSnapshot ReadSchedule(SqliteDataReader reader)
    {
        var kind = Enum.Parse<ScheduleKind>(reader.GetString(reader.GetOrdinal("kind")));
        var targetOrdinal = reader.GetOrdinal("target_utc");
        var dailyOrdinal = reader.GetOrdinal("daily_time");

        return new ScheduleSnapshot(
            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            reader.GetInt64(reader.GetOrdinal("revision")),
            kind,
            Enum.Parse<PowerActionType>(reader.GetString(reader.GetOrdinal("action"))),
            reader.IsDBNull(targetOrdinal)
                ? null
                : DateTimeOffset.Parse(reader.GetString(targetOrdinal), CultureInfo.InvariantCulture),
            reader.IsDBNull(dailyOrdinal)
                ? null
                : TimeOnly.ParseExact(reader.GetString(dailyOrdinal), "HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            reader.GetString(reader.GetOrdinal("time_zone_id")),
            reader.GetBoolean(reader.GetOrdinal("keep_daily")),
            Enum.Parse<ScheduleStatus>(reader.GetString(reader.GetOrdinal("status"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_utc")), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_utc")), CultureInfo.InvariantCulture));
    }

    private static void AddScheduleParameters(SqliteCommand command, ScheduleSnapshot snapshot)
    {
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$kind", snapshot.Kind.ToString());
        command.Parameters.AddWithValue("$action", snapshot.Action.ToString());
        command.Parameters.AddWithValue("$targetUtc", snapshot.TargetAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$dailyTime",
            snapshot.DailyAt?.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$timeZoneId", snapshot.TimeZoneId);
        command.Parameters.AddWithValue("$keepDaily", snapshot.KeepDaily ? 1 : 0);
        command.Parameters.AddWithValue("$status", snapshot.Status.ToString());
        command.Parameters.AddWithValue("$updatedUtc", snapshot.UpdatedAt.ToString("O"));
    }

    private static async Task AppendOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operation,
        ScheduleSnapshot snapshot,
        string outcome,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operations(
                occurred_utc, operation, schedule_id, schedule_revision, outcome, detail_json)
            VALUES($occurredUtc, $operation, $scheduleId, $scheduleRevision, $outcome, NULL);
            """;
        command.Parameters.AddWithValue("$occurredUtc", snapshot.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$scheduleId", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$scheduleRevision", snapshot.Revision);
        command.Parameters.AddWithValue("$outcome", outcome);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
