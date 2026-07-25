using Sdat.Core.Scheduling;
using Sdat.Core.Settings;

namespace Sdat.Core.Operations;

public sealed class ScheduleCoordinator(
    IScheduleRepository repository,
    IStateBackup backup,
    SchedulerReconciler reconciler,
    IOperationLock operationLock,
    TimeProvider? timeProvider = null,
    IRuntimeSafetyPolicy? safetyPolicy = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IRuntimeSafetyPolicy _safetyPolicy =
        safetyPolicy ?? NormalRuntimeSafetyPolicy.Instance;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReconciliationReport> InitializeAndReconcileAsync(
        IReadOnlyList<int> reminderOffsetsMinutes,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
        return await ReconcileSafelyAsync(reminderOffsetsMinutes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduleMutationResult> SetAsync(
        ScheduleDraft draft,
        IReadOnlyList<int> reminderOffsetsMinutes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await SetUnderAcquiredLockAsync(draft, reminderOffsetsMinutes, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<ScheduleMutationResult> SetUnderAcquiredLockAsync(
        ScheduleDraft draft,
        IReadOnlyList<int> reminderOffsetsMinutes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await EnsureRealSchedulingAllowedAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);

        var existing = (await repository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(schedule => schedule.Kind == draft.Kind);
        var schedule = existing is null
            ? await repository.CreateAsync(draft, cancellationToken).ConfigureAwait(false)
            : await repository.UpdateAsync(existing.Id, existing.Revision, draft, cancellationToken)
                .ConfigureAwait(false);

        return await FinishMutationAsync(schedule, reminderOffsetsMinutes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduleMutationResult> CancelAsync(
        ScheduleKind kind,
        IReadOnlyList<int> reminderOffsetsMinutes,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);

        var existing = (await repository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(schedule => schedule.Kind == kind)
            ?? throw new KeyNotFoundException($"No active {kind} schedule exists.");
        var schedule = await repository.CancelAsync(existing.Id, existing.Revision, cancellationToken)
            .ConfigureAwait(false);

        return await FinishMutationAsync(schedule, reminderOffsetsMinutes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduleMutationResult> CancelExactAsync(
        Guid scheduleId,
        long expectedRevision,
        IReadOnlyList<int> reminderOffsetsMinutes,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
        var schedule = await repository
            .CancelAsync(scheduleId, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
        return await FinishMutationAsync(schedule, reminderOffsetsMinutes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduleMutationResult> UpdateExactAsync(
        Guid scheduleId,
        long expectedRevision,
        ScheduleDraft draft,
        IReadOnlyList<int> reminderOffsetsMinutes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await EnsureRealSchedulingAllowedAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
        var schedule = await repository
            .UpdateAsync(scheduleId, expectedRevision, draft, cancellationToken)
            .ConfigureAwait(false);
        return await FinishMutationAsync(schedule, reminderOffsetsMinutes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReconciliationReport> ReconcileAsync(
        IReadOnlyList<int> reminderOffsetsMinutes,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
        return await ReconcileSafelyAsync(reminderOffsetsMinutes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScheduleMutationResult> FinishMutationAsync(
        ScheduleSnapshot schedule,
        IReadOnlyList<int> reminderOffsetsMinutes,
        CancellationToken cancellationToken)
    {
        string? backupReference = null;
        string? backupFailure = null;
        try
        {
            backupReference = await backup.CreateVerifiedBackupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            backupFailure = exception.Message;
        }

        var reconciliation = await ReconcileSafelyAsync(reminderOffsetsMinutes, cancellationToken)
            .ConfigureAwait(false);
        return new ScheduleMutationResult(schedule, backupReference, backupFailure, reconciliation);
    }

    private async Task EnsureHealthyAsync(CancellationToken cancellationToken)
    {
        var health = await repository.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!health.CanExecutePowerActions)
        {
            throw new ScheduleStoreUnavailableException(
                $"The SDAT database is not healthy. Power scheduling is disabled: {health.Detail}");
        }
    }

    private async Task<ReconciliationReport> ReconcileSafelyAsync(
        IReadOnlyList<int> reminderOffsetsMinutes,
        CancellationToken cancellationToken)
    {
        if (await _safetyPolicy.IsTestModeAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReconciliationReport.TestModeSuppressed;
        }

        try
        {
            return await reconciler
                .ReconcileAsync(reminderOffsetsMinutes, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ReconciliationReport(
                0,
                0,
                0,
                [new ReconciliationFailure("SDAT_*", "Reconcile", exception.Message)]);
        }
    }

    private async Task EnsureRealSchedulingAllowedAsync(CancellationToken cancellationToken)
    {
        if (await _safetyPolicy.IsTestModeAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TestModeScheduleBlockedException();
        }
    }
}
