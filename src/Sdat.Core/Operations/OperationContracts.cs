using Sdat.Core.Scheduling;

namespace Sdat.Core.Operations;

public interface IOperationLock
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}

public interface IStateBackup
{
    Task<string> CreateVerifiedBackupAsync(CancellationToken cancellationToken = default);
}

public sealed record ScheduleMutationResult(
    ScheduleSnapshot Schedule,
    string? BackupReference,
    string? BackupFailure,
    ReconciliationReport Reconciliation)
{
    public bool IsFullyApplied => BackupFailure is null && Reconciliation.IsHealthy;
}

public sealed class ScheduleStoreUnavailableException(string message) : InvalidOperationException(message);
