using System.Security.Cryptography;
using System.Text;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;

namespace Sdat.Core.Execution;

public sealed class TaskInvocationCoordinator(
    IScheduleRepository repository,
    ITaskExecutionLedger ledger,
    IPowerActionExecutor powerActionExecutor,
    ITaskReminderNotifier reminderNotifier,
    IOneTimeExecutionFinalizer oneTimeExecutionFinalizer,
    IOperationLock operationLock,
    TimeProvider? timeProvider = null,
    TimeSpan? earlyTolerance = null,
    TimeSpan? lateTolerance = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _earlyTolerance = earlyTolerance ?? TimeSpan.FromMinutes(1);
    private readonly TimeSpan _lateTolerance = lateTolerance ?? TimeSpan.FromMinutes(15);

    public async Task<TaskInvocationResult> RunAsync(
        TaskInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var health = await repository.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!health.CanExecutePowerActions)
        {
            return new TaskInvocationResult(
                TaskInvocationOutcome.Failed,
                $"Database health blocks execution: {health.Detail}");
        }

        var schedule = await repository.GetAsync(invocation.ScheduleId, cancellationToken).ConfigureAwait(false);
        if (schedule is null || schedule.Status != ScheduleStatus.Active || schedule.Revision != invocation.Revision)
        {
            return new TaskInvocationResult(TaskInvocationOutcome.IgnoredStale, "Schedule is missing, inactive, or superseded.");
        }

        if (invocation.Role == SchedulerTaskRole.Reminder && invocation.ReminderOffsetMinutes is not (>= 1 and <= 1440))
        {
            return new TaskInvocationResult(TaskInvocationOutcome.Failed, "Reminder offset is missing or invalid.");
        }

        var now = _timeProvider.GetUtcNow();
        var dueAt = ResolveDueAt(schedule, invocation, now);
        if (now < dueAt - _earlyTolerance)
        {
            return new TaskInvocationResult(TaskInvocationOutcome.IgnoredEarly, "Invocation arrived before its safety window.");
        }

        var isLate = now > dueAt + _lateTolerance;
        var occurrenceId = CreateOccurrenceId(invocation, dueAt);
        var claim = new OccurrenceClaim(
            occurrenceId,
            invocation,
            dueAt,
            isLate ? OccurrenceOutcome.Skipped : OccurrenceOutcome.Pending,
            ResolveExecuteDueAt(schedule, invocation, dueAt));
        var claimResult = await ledger.TryClaimAsync(claim, cancellationToken).ConfigureAwait(false);
        if (claimResult == OccurrenceClaimResult.Stale)
        {
            return new TaskInvocationResult(TaskInvocationOutcome.IgnoredStale, "Schedule changed before it could be claimed.");
        }

        if (claimResult == OccurrenceClaimResult.AlreadyHandled)
        {
            return new TaskInvocationResult(TaskInvocationOutcome.IgnoredDuplicate, "Occurrence was already handled.", occurrenceId);
        }

        if (claimResult == OccurrenceClaimResult.SkippedByRequest)
        {
            return new TaskInvocationResult(
                TaskInvocationOutcome.SkippedByRequest,
                "The daily occurrence was skipped by request.",
                occurrenceId);
        }

        var consumesOneTime = schedule.Kind == ScheduleKind.OneTime &&
                              invocation.Role == SchedulerTaskRole.Execute;
        string? finalizationWarning = null;
        if (consumesOneTime)
        {
            try
            {
                finalizationWarning = await oneTimeExecutionFinalizer.FinalizeAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (!isLate)
                {
                    await ledger
                        .FailAsync(occurrenceId, exception.GetType().Name, exception.Message, cancellationToken)
                        .ConfigureAwait(false);
                }

                return new TaskInvocationResult(
                    TaskInvocationOutcome.Failed,
                    $"One-time state could not be finalized safely: {exception.Message}",
                    occurrenceId);
            }
        }

        if (isLate)
        {
            return new TaskInvocationResult(
                TaskInvocationOutcome.SkippedLate,
                AppendWarning("Occurrence exceeded the allowed lateness.", finalizationWarning),
                occurrenceId);
        }

        try
        {
            if (invocation.Role == SchedulerTaskRole.Reminder)
            {
                await reminderNotifier
                    .ShowAsync(schedule, invocation.ReminderOffsetMinutes!.Value, cancellationToken)
                    .ConfigureAwait(false);
                await ledger.CompleteAsync(occurrenceId, cancellationToken).ConfigureAwait(false);
                return new TaskInvocationResult(TaskInvocationOutcome.ReminderShown, "Reminder displayed.", occurrenceId);
            }

            await powerActionExecutor.ExecuteAsync(schedule.Action, cancellationToken).ConfigureAwait(false);
            await ledger.CompleteAsync(occurrenceId, cancellationToken).ConfigureAwait(false);
            return new TaskInvocationResult(
                TaskInvocationOutcome.Executed,
                AppendWarning("Power action accepted by Windows.", finalizationWarning),
                occurrenceId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ledger
                .FailAsync(occurrenceId, exception.GetType().Name, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            return new TaskInvocationResult(TaskInvocationOutcome.Failed, exception.Message, occurrenceId);
        }
    }

    private static string AppendWarning(string detail, string? warning) =>
        warning is null ? detail : $"{detail} Warning: {warning}";

    private static DateTimeOffset ResolveDueAt(
        ScheduleSnapshot schedule,
        TaskInvocation invocation,
        DateTimeOffset now)
    {
        var offset = invocation.Role == SchedulerTaskRole.Reminder
            ? TimeSpan.FromMinutes(invocation.ReminderOffsetMinutes!.Value)
            : TimeSpan.Zero;
        if (schedule.Kind == ScheduleKind.OneTime)
        {
            return schedule.TargetAt!.Value - offset;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var effectiveTime = schedule.DailyAt!.Value.ToTimeSpan() - offset;
        while (effectiveTime < TimeSpan.Zero)
        {
            effectiveTime += TimeSpan.FromDays(1);
        }

        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        return Enumerable.Range(-1, 3)
            .Select(dayOffset => ResolveLocal(localNow.Date.AddDays(dayOffset), effectiveTime, timeZone))
            .OrderBy(candidate => Math.Abs((candidate - now).Ticks))
            .First();
    }

    private static DateTimeOffset ResolveLocal(DateTime date, TimeSpan time, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.Add(time), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static DateTimeOffset ResolveExecuteDueAt(
        ScheduleSnapshot schedule,
        TaskInvocation invocation,
        DateTimeOffset invocationDueAt)
    {
        if (schedule.Kind == ScheduleKind.OneTime)
        {
            return schedule.TargetAt!.Value;
        }

        if (invocation.Role == SchedulerTaskRole.Execute)
        {
            return invocationDueAt;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var reminderLocalDate = TimeZoneInfo.ConvertTime(invocationDueAt, timeZone).Date;
        var reminderClock = schedule.DailyAt!.Value.ToTimeSpan() -
                            TimeSpan.FromMinutes(invocation.ReminderOffsetMinutes!.Value);
        var executeDate = reminderClock < TimeSpan.Zero
            ? reminderLocalDate.AddDays(1)
            : reminderLocalDate;
        return ResolveLocal(executeDate, schedule.DailyAt.Value.ToTimeSpan(), timeZone);
    }

    private static Guid CreateOccurrenceId(TaskInvocation invocation, DateTimeOffset dueAt)
    {
        var canonical = $"{invocation.ScheduleId:D}|{invocation.Revision}|{invocation.Role}|{dueAt:O}|{invocation.ReminderOffsetMinutes}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }
}
