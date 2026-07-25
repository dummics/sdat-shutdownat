using Sdat.Core.Scheduling;
using Sdat.Windows.Execution;
using Sdat.Windows.Hosting;

namespace Sdat.App;

internal sealed record AppScheduleCancellationResult(
    bool ScheduleSettled,
    WindowsShutdownCancellationGuardResult<bool> Guard)
{
    public bool IsSafe => ScheduleSettled && Guard.WindowsStateConfirmed;

    public string? ErrorDetail =>
        Guard.StateError?.Message ??
        (Guard.WindowsStateConfirmed
            ? null
            : Guard.FinalAbort.Detail ?? $"shutdown.exe exited with code {Guard.FinalAbort.ExitCode}.");
}

internal static class AppScheduleCancellation
{
    public static async Task<AppScheduleCancellationResult> CancelAsync(
        SdatRuntime runtime,
        ScheduleSnapshot schedule,
        long? expectedRevision = null,
        ShutdownCountdownAbortResult? initialAbort = null,
        bool cancelScheduleState = true,
        CancellationToken cancellationToken = default)
    {
        var guard = await WindowsShutdownCancellationGuard.RunAsync(
            async token =>
            {
                if (!cancelScheduleState)
                {
                    return true;
                }

                var settings = await runtime.Settings.LoadAsync(token);
                await runtime.Coordinator.CancelExactAsync(
                    schedule.Id,
                    expectedRevision ?? schedule.Revision,
                    settings.ReminderOffsetsMinutes,
                    token);
                return true;
            },
            initialAbort,
            cancellationToken: cancellationToken);

        var scheduleSettled = guard.StateResult == true && guard.StateError is null;
        if (!scheduleSettled &&
            guard.StateError is ScheduleConflictException or KeyNotFoundException)
        {
            var latest = await runtime.Schedules.GetAsync(schedule.Id, cancellationToken);
            scheduleSettled = latest is null || latest.Status != ScheduleStatus.Active;
        }

        return new AppScheduleCancellationResult(scheduleSettled, guard);
    }
}
