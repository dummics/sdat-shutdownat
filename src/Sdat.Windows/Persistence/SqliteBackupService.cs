using Microsoft.Data.Sqlite;
using Sdat.Core.Operations;
using Sdat.Core.Storage;

namespace Sdat.Windows.Persistence;

public sealed class SqliteBackupService(
    SqliteStoreOptions options,
    TimeProvider? timeProvider = null) : IStateBackup
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<string> CreateVerifiedBackupAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        Directory.CreateDirectory(options.BackupDirectory);

        var backupPath = Path.Combine(
            options.BackupDirectory,
            $"sdat-{_timeProvider.GetUtcNow():yyyyMMddTHHmmssfffZ}.db");

        await using (var source = await SqliteSchema.OpenAsync(options, cancellationToken).ConfigureAwait(false))
        await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(destination);
        }

        await using (var verification = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()))
        {
            await verification.OpenAsync(cancellationToken).ConfigureAwait(false);
            var health = await SqliteSchema.CheckHealthAsync(verification, cancellationToken).ConfigureAwait(false);
            if (health.Status != StoreHealthStatus.Healthy)
            {
                File.Delete(backupPath);
                throw new InvalidDataException($"SQLite backup verification failed: {health.Detail}");
            }
        }

        EnforceRetention();
        return backupPath;
    }

    private void EnforceRetention()
    {
        var backups = new DirectoryInfo(options.BackupDirectory)
            .EnumerateFiles("sdat-*.db", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(options.BackupRetentionCount);

        foreach (var backup in backups)
        {
            backup.Delete();
        }
    }
}
