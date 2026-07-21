using Sdat.Core.Scheduling;

namespace Sdat.Core.Execution;

public enum OccurrenceOutcome
{
    Pending,
    Completed,
    Skipped,
    Failed,
}

public enum OccurrenceClaimResult
{
    Claimed,
    SkippedByRequest,
    Stale,
    AlreadyHandled,
}

public sealed record TaskInvocation(
    Guid ScheduleId,
    long Revision,
    SchedulerTaskRole Role,
    int? ReminderOffsetMinutes);

public sealed record OccurrenceClaim(
    Guid OccurrenceId,
    TaskInvocation Invocation,
    DateTimeOffset DueAt,
    OccurrenceOutcome InitialOutcome,
    DateTimeOffset? ExecuteDueAt = null);

public interface ITaskExecutionLedger
{
    Task<OccurrenceClaimResult> TryClaimAsync(
        OccurrenceClaim claim,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid occurrenceId, CancellationToken cancellationToken = default);

    Task FailAsync(
        Guid occurrenceId,
        string errorCode,
        string errorDetail,
        CancellationToken cancellationToken = default);
}

public interface IPowerActionExecutor
{
    Task ExecuteAsync(PowerActionType action, CancellationToken cancellationToken = default);
}

public interface ITaskReminderNotifier
{
    Task ShowAsync(
        ScheduleSnapshot schedule,
        int offsetMinutes,
        CancellationToken cancellationToken = default);
}

public interface IOneTimeExecutionFinalizer
{
    Task<string?> FinalizeAsync(CancellationToken cancellationToken = default);
}

public enum TaskInvocationOutcome
{
    Executed,
    ReminderShown,
    SkippedByRequest,
    SkippedLate,
    IgnoredStale,
    IgnoredDuplicate,
    IgnoredEarly,
    Failed,
}

public sealed record TaskInvocationResult(
    TaskInvocationOutcome Outcome,
    string Detail,
    Guid? OccurrenceId = null);
