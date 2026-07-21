using Sdat.Core.Operations;
using Sdat.Core.Settings;

namespace Sdat.Core.Scheduling;

public sealed record ScheduleCommandResult(
    ScheduleMutationResult Mutation,
    DailySkipResult? AutomaticDailySkip)
{
    public bool IsFullyApplied =>
        Mutation.IsFullyApplied && (AutomaticDailySkip?.IsFullyPersisted ?? true);
}

public sealed class ScheduleCommandService(
    ScheduleCoordinator coordinator,
    IScheduleRepository schedules,
    DailySkipCoordinator dailySkips,
    IAppSettingsRepository settingsRepository,
    IOperationLock operationLock,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ScheduleCommandResult> SetAsync(
        ScheduleDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var mutation = await coordinator
            .SetUnderAcquiredLockAsync(draft, settings.ReminderOffsetsMinutes, cancellationToken)
            .ConfigureAwait(false);
        if (draft.Kind != ScheduleKind.OneTime || draft.KeepDaily ||
            settings.DailyOverlapWindowMinutes == 0)
        {
            return new ScheduleCommandResult(mutation, null);
        }

        var daily = (await schedules.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(schedule => schedule.Kind == ScheduleKind.Daily);
        if (daily is null)
        {
            return new ScheduleCommandResult(mutation, null);
        }

        var dailyDueAt = DailyScheduleOccurrenceResolver.GetNextExecution(daily, _timeProvider.GetUtcNow());
        var distance = (draft.TargetAt!.Value.ToUniversalTime() - dailyDueAt).Duration();
        if (distance > TimeSpan.FromMinutes(settings.DailyOverlapWindowMinutes))
        {
            return new ScheduleCommandResult(mutation, null);
        }

        var skip = await dailySkips
            .RequestExactUnderAcquiredLockAsync(daily.Id, daily.Revision, dailyDueAt, cancellationToken)
            .ConfigureAwait(false);
        return new ScheduleCommandResult(mutation, skip);
    }
}
