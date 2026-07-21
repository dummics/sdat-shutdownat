using Microsoft.Data.Sqlite;
using Sdat.Core.Storage;

namespace Sdat.Windows.Persistence;

public sealed class SqliteRecoveryService(
    SqliteStoreOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<StoreHealth> CheckCurrentAsync(
        bool full = false,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (!File.Exists(options.DatabasePath))
        {
            return new StoreHealth(StoreHealthStatus.Unavailable, "Database file does not exist.");
        }

        try
        {
            await using var connection = await OpenReadOnlyAsync(options.DatabasePath, cancellationToken)
                .ConfigureAwait(false);
            return await SqliteSchema.CheckHealthAsync(connection, cancellationToken, full).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new StoreHealth(StoreHealthStatus.Unavailable, exception.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListVerifiedBackupsAsync(
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (!Directory.Exists(options.BackupDirectory))
        {
            return [];
        }

        var verified = new List<string>();
        foreach (var path in Directory.EnumerateFiles(options.BackupDirectory, "sdat-*.db")
                     .OrderByDescending(path => path, StringComparer.Ordinal))
        {
            if (await IsCompatibleBackupAsync(path, cancellationToken).ConfigureAwait(false))
            {
                verified.Add(path);
            }
        }

        return verified;
    }

    public async Task<DatabaseRecoveryResult> RestoreLatestVerifiedBackupAsync(
        bool allowHealthyOverwrite = false,
        CancellationToken cancellationToken = default)
    {
        var currentHealth = await CheckCurrentAsync(full: true, cancellationToken).ConfigureAwait(false);
        if (currentHealth.Status == StoreHealthStatus.Healthy && !allowHealthyOverwrite)
        {
            throw new InvalidOperationException("The current SDAT database is healthy; restore was not allowed.");
        }

        var backups = await ListVerifiedBackupsAsync(cancellationToken).ConfigureAwait(false);
        var backupPath = backups.FirstOrDefault()
            ?? throw new InvalidDataException("No compatible verified SDAT backup is available.");

        var databaseDirectory = Path.GetDirectoryName(options.DatabasePath)!;
        Directory.CreateDirectory(databaseDirectory);
        var candidatePath = Path.Combine(databaseDirectory, $"sdat.restore-{Guid.NewGuid():N}.db");
        File.Copy(backupPath, candidatePath, overwrite: false);

        try
        {
            if (!await IsCompatibleBackupAsync(candidatePath, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The restore candidate failed compatibility verification.");
            }

            SqliteConnection.ClearAllPools();
            var evidenceDirectory = MoveCurrentDatabaseToEvidence();

            try
            {
                File.Move(candidatePath, options.DatabasePath, overwrite: false);
                var restoredHealth = await CheckCurrentAsync(full: true, cancellationToken).ConfigureAwait(false);
                if (restoredHealth.Status != StoreHealthStatus.Healthy)
                {
                    throw new InvalidDataException($"Restored database is not healthy: {restoredHealth.Detail}");
                }

                return new DatabaseRecoveryResult(backupPath, evidenceDirectory, restoredHealth);
            }
            catch
            {
                RollBackEvidence(evidenceDirectory);
                throw;
            }
        }
        finally
        {
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }
        }
    }

    private async Task<bool> IsCompatibleBackupAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
            var health = await SqliteSchema.CheckHealthAsync(connection, cancellationToken, full: true)
                .ConfigureAwait(false);
            if (health.Status != StoreHealthStatus.Healthy)
            {
                return false;
            }

            var version = await SqliteSchema.GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            return version is >= 1 and <= SqliteSchema.CurrentVersion;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? MoveCurrentDatabaseToEvidence()
    {
        var existingFiles = GetDatabaseFiles().Where(File.Exists).ToArray();
        if (existingFiles.Length == 0)
        {
            return null;
        }

        var evidenceDirectory = Path.Combine(
            options.BackupDirectory,
            "recovery-evidence",
            _timeProvider.GetUtcNow().ToString("yyyyMMddTHHmmssfffZ"));
        Directory.CreateDirectory(evidenceDirectory);

        foreach (var path in existingFiles)
        {
            File.Move(path, Path.Combine(evidenceDirectory, Path.GetFileName(path)), overwrite: false);
        }

        return evidenceDirectory;
    }

    private void RollBackEvidence(string? evidenceDirectory)
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(options.DatabasePath))
        {
            var failedPath = Path.Combine(
                options.BackupDirectory,
                $"failed-restore-{_timeProvider.GetUtcNow():yyyyMMddTHHmmssfffZ}.db");
            File.Move(options.DatabasePath, failedPath, overwrite: false);
        }

        if (evidenceDirectory is null || !Directory.Exists(evidenceDirectory))
        {
            return;
        }

        foreach (var evidencePath in Directory.EnumerateFiles(evidenceDirectory))
        {
            var originalPath = Path.Combine(
                Path.GetDirectoryName(options.DatabasePath)!,
                Path.GetFileName(evidencePath));
            File.Move(evidencePath, originalPath, overwrite: false);
        }
    }

    private IEnumerable<string> GetDatabaseFiles()
    {
        yield return options.DatabasePath;
        yield return $"{options.DatabasePath}-wal";
        yield return $"{options.DatabasePath}-shm";
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
