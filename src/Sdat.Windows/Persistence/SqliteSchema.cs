using Microsoft.Data.Sqlite;
using Sdat.Core.Storage;

namespace Sdat.Windows.Persistence;

internal static class SqliteSchema
{
    public const int CurrentVersion = 2;

    private const string MigrationOne = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            migration_id INTEGER PRIMARY KEY,
            applied_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS schedules (
            id TEXT PRIMARY KEY,
            revision INTEGER NOT NULL CHECK (revision > 0),
            kind TEXT NOT NULL CHECK (kind IN ('OneTime', 'Daily')),
            action TEXT NOT NULL CHECK (action IN ('Shutdown', 'Suspend', 'Restart')),
            target_utc TEXT NULL,
            daily_time TEXT NULL,
            time_zone_id TEXT NOT NULL,
            keep_daily INTEGER NOT NULL CHECK (keep_daily IN (0, 1)),
            status TEXT NOT NULL CHECK (status IN ('Active', 'Cancelled', 'Completed')),
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            CHECK (
                (kind = 'OneTime' AND target_utc IS NOT NULL AND daily_time IS NULL) OR
                (kind = 'Daily' AND target_utc IS NULL AND daily_time IS NOT NULL)
            )
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_schedules_active_kind
            ON schedules(kind)
            WHERE status = 'Active';

        CREATE TABLE IF NOT EXISTS occurrences (
            id TEXT PRIMARY KEY,
            schedule_id TEXT NOT NULL,
            schedule_revision INTEGER NOT NULL,
            occurrence_kind TEXT NOT NULL CHECK (occurrence_kind IN ('Execute', 'Reminder')),
            due_utc TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Pending', 'Completed', 'Skipped', 'Failed')),
            completed_utc TEXT NULL,
            error_code TEXT NULL,
            error_detail TEXT NULL,
            FOREIGN KEY (schedule_id) REFERENCES schedules(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS scheduler_bindings (
            schedule_id TEXT NOT NULL,
            schedule_revision INTEGER NOT NULL,
            task_role TEXT NOT NULL,
            task_name TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            health TEXT NOT NULL,
            last_reconciled_utc TEXT NOT NULL,
            PRIMARY KEY (schedule_id, task_role),
            FOREIGN KEY (schedule_id) REFERENCES schedules(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS settings (
            setting_key TEXT PRIMARY KEY,
            value_json TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS operations (
            operation_id INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_utc TEXT NOT NULL,
            operation TEXT NOT NULL,
            schedule_id TEXT NULL,
            schedule_revision INTEGER NULL,
            outcome TEXT NOT NULL,
            detail_json TEXT NULL
        );
        """;

    private const string MigrationTwo = """
        CREATE TABLE IF NOT EXISTS daily_skip_requests (
            schedule_id TEXT NOT NULL,
            schedule_revision INTEGER NOT NULL,
            execute_due_utc TEXT NOT NULL,
            requested_utc TEXT NOT NULL,
            consumed_utc TEXT NULL,
            PRIMARY KEY (schedule_id, schedule_revision, execute_due_utc),
            FOREIGN KEY (schedule_id) REFERENCES schedules(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_daily_skip_requests_pending
            ON daily_skip_requests(schedule_id, schedule_revision, execute_due_utc)
            WHERE consumed_utc IS NULL;
        """;

    public static async Task<SqliteConnection> OpenAsync(
        SqliteStoreOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task MigrateAsync(
        SqliteConnection connection,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var existingVersion = await GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (existingVersion > CurrentVersion)
        {
            throw new InvalidDataException(
                $"The SDAT database schema version {existingVersion} is newer than this app supports ({CurrentVersion}).");
        }

        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrationOne;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = MigrationTwo;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations(migration_id, applied_utc)
            VALUES (1, $appliedUtc);
            INSERT OR IGNORE INTO schema_migrations(migration_id, applied_utc)
            VALUES (2, $appliedUtc);
            PRAGMA user_version = 2;
            """;
        command.Parameters.AddWithValue("$appliedUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public static async Task<StoreHealth> CheckHealthAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        bool full = false)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = full ? "PRAGMA integrity_check;" : "PRAGMA quick_check;";
            var result = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)
                ? new StoreHealth(StoreHealthStatus.Healthy, "ok")
                : new StoreHealth(StoreHealthStatus.Corrupt, result ?? "quick_check returned no result");
        }
        catch (SqliteException exception)
        {
            return new StoreHealth(StoreHealthStatus.Unavailable, exception.Message);
        }
    }

    public static async Task<int> GetUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
}
