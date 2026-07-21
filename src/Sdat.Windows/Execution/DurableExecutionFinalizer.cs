using Sdat.Core.Execution;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;

namespace Sdat.Windows.Execution;

public sealed class DurableExecutionFinalizer(
    IStateBackup backup,
    SchedulerReconciler reconciler,
    IReadOnlyList<int> reminderOffsetsMinutes,
    TimeProvider? timeProvider = null) : IOneTimeExecutionFinalizer
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<string?> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        await backup.CreateVerifiedBackupAsync(cancellationToken).ConfigureAwait(false);
        var report = await reconciler
            .ReconcileAsync(reminderOffsetsMinutes, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return report.IsHealthy
            ? null
            : string.Join(
                "; ",
                report.Failures.Select(failure => $"{failure.Operation} {failure.TaskName}: {failure.Detail}"));
    }
}
