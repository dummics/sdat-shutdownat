using Microsoft.Data.Sqlite;
using Sdat.Core.Operations;
using Sdat.Core.Storage;

namespace Sdat.Windows.Persistence;

public sealed record StoreInitializationResult(DatabaseRecoveryResult? Recovery)
{
    public bool WasRecovered => Recovery is not null;
}

public sealed class SqliteStoreInitializer(
    SqliteStoreOptions options,
    SqliteScheduleRepository repository,
    SqliteRecoveryService recovery,
    IStateBackup backup,
    IOperationLock operationLock)
{
    public async Task<StoreInitializationResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(options.DatabasePath))
        {
            var existingBackups = await recovery.ListVerifiedBackupsAsync(cancellationToken).ConfigureAwait(false);
            if (existingBackups.Count > 0)
            {
                return await RestoreAndInitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return new StoreInitializationResult(null);
        }

        var verifiedBackups = await recovery.ListVerifiedBackupsAsync(cancellationToken).ConfigureAwait(false);
        var preflightHealth = await recovery.CheckCurrentAsync(full: true, cancellationToken).ConfigureAwait(false);
        var schemaVersion = preflightHealth.Status == StoreHealthStatus.Healthy
            ? await recovery.GetCurrentSchemaVersionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (verifiedBackups.Count > 0 &&
            (preflightHealth.Status != StoreHealthStatus.Healthy || schemaVersion == 0))
        {
            return await RestoreAndInitializeAsync(
                    cancellationToken,
                    allowHealthyOverwrite: schemaVersion == 0)
                .ConfigureAwait(false);
        }

        if (schemaVersion is > 0 and < SqliteSchema.CurrentVersion)
        {
            // Preserve the exact pre-migration database. If verification fails, migration must not start.
            await backup.CreateVerifiedBackupAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return new StoreInitializationResult(null);
        }
        catch (Exception initializationException) when (
            initializationException is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (preflightHealth.Status == StoreHealthStatus.Healthy)
            {
                // A healthy database can still be intentionally unsupported, for example a forward schema version.
                throw;
            }

            try
            {
                return await RestoreAndInitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception recoveryException) when (recoveryException is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    $"The SDAT database could not be initialized or recovered. Initial failure: {initializationException.Message}",
                    recoveryException);
            }
        }
    }

    private async Task<StoreInitializationResult> RestoreAndInitializeAsync(
        CancellationToken cancellationToken,
        bool allowHealthyOverwrite = false)
    {
        var result = await recovery
            .RestoreLatestVerifiedBackupAsync(allowHealthyOverwrite, cancellationToken)
            .ConfigureAwait(false);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return new StoreInitializationResult(result);
    }
}
