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

        try
        {
            await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return new StoreInitializationResult(null);
        }
        catch (Exception initializationException) when (
            initializationException is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var currentHealth = await recovery.CheckCurrentAsync(full: true, cancellationToken).ConfigureAwait(false);
            if (currentHealth.Status == StoreHealthStatus.Healthy)
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
        CancellationToken cancellationToken)
    {
        var result = await recovery
            .RestoreLatestVerifiedBackupAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return new StoreInitializationResult(result);
    }
}
