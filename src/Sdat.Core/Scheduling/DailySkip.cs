using Sdat.Core.Operations;

namespace Sdat.Core.Scheduling;

public sealed record DailySkipRequest(
    Guid ScheduleId,
    long ScheduleRevision,
    DateTimeOffset ExecuteDueAt,
    DateTimeOffset RequestedAt);

public sealed record DailySkipResult(
    DailySkipRequest Request,
    string? BackupReference,
    string? BackupFailure)
{
    public bool IsFullyPersisted => BackupFailure is null;
}

public interface IDailySkipStore
{
    Task<DailySkipRequest> RequestAsync(
        Guid scheduleId,
        long scheduleRevision,
        DateTimeOffset executeDueAt,
        CancellationToken cancellationToken = default);
}

public sealed class DailySkipCoordinator(
    IScheduleRepository schedules,
    IDailySkipStore skipStore,
    IStateBackup backup,
    IOperationLock operationLock,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<DailySkipResult> RequestNextAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var health = await schedules.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!health.CanExecutePowerActions)
        {
            throw new ScheduleStoreUnavailableException($"Database health blocks updates: {health.Detail}");
        }

        var daily = (await schedules.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(schedule => schedule.Kind == ScheduleKind.Daily);
        if (daily is null)
        {
            throw new InvalidOperationException("There is no active daily schedule to skip.");
        }

        var dueAt = DailyScheduleOccurrenceResolver.GetNextExecution(daily, _timeProvider.GetUtcNow());
        return await RequestCoreAsync(daily.Id, daily.Revision, dueAt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DailySkipResult> RequestExactAsync(
        Guid scheduleId,
        long scheduleRevision,
        DateTimeOffset executeDueAt,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await RequestExactUnderAcquiredLockAsync(
                scheduleId,
                scheduleRevision,
                executeDueAt,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<DailySkipResult> RequestExactUnderAcquiredLockAsync(
        Guid scheduleId,
        long scheduleRevision,
        DateTimeOffset executeDueAt,
        CancellationToken cancellationToken = default)
    {
        var health = await schedules.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!health.CanExecutePowerActions)
        {
            throw new ScheduleStoreUnavailableException($"Database health blocks updates: {health.Detail}");
        }

        return await RequestCoreAsync(
                scheduleId,
                scheduleRevision,
                executeDueAt,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DailySkipResult> RequestCoreAsync(
        Guid scheduleId,
        long scheduleRevision,
        DateTimeOffset executeDueAt,
        CancellationToken cancellationToken)
    {
        var request = await skipStore
            .RequestAsync(scheduleId, scheduleRevision, executeDueAt, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var backupReference = await backup.CreateVerifiedBackupAsync(cancellationToken).ConfigureAwait(false);
            return new DailySkipResult(request, backupReference, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DailySkipResult(request, null, exception.Message);
        }
    }
}

public static class DailyScheduleOccurrenceResolver
{
    public static DateTimeOffset GetNextExecution(ScheduleSnapshot schedule, DateTimeOffset now)
    {
        if (schedule.Kind != ScheduleKind.Daily || schedule.DailyAt is null)
        {
            throw new ArgumentException("A daily schedule is required.", nameof(schedule));
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        for (var dayOffset = 0; dayOffset <= 2; dayOffset++)
        {
            var candidate = ResolveLocal(localNow.Date.AddDays(dayOffset), schedule.DailyAt.Value, timeZone);
            if (candidate > now)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The next daily occurrence could not be resolved.");
    }

    private static DateTimeOffset ResolveLocal(DateTime date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
