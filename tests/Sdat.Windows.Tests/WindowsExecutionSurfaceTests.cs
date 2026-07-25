using Sdat.Core.Scheduling;
using Sdat.Windows.Execution;
using Sdat.Windows.Migration;
using Sdat.Windows.Notifications;
using Sdat.Windows.Scheduling;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class WindowsExecutionSurfaceTests
{
    [Theory]
    [InlineData(PowerActionType.Shutdown, "/s")]
    [InlineData(PowerActionType.Restart, "/r")]
    public void Shutdown_command_preserves_native_thirty_second_countdown(
        PowerActionType action,
        string mode)
    {
        var startInfo = WindowsPowerActionExecutor.CreateShutdownStartInfo(action);

        Assert.EndsWith("shutdown.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([mode, "/f", "/t", "30"], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Theory]
    [InlineData(0, ShutdownCountdownAbortStatus.Aborted)]
    [InlineData(1116, ShutdownCountdownAbortStatus.NoCountdown)]
    [InlineData(5, ShutdownCountdownAbortStatus.Failed)]
    public void Shutdown_countdown_abort_reports_the_native_result(
        int exitCode,
        ShutdownCountdownAbortStatus expected)
    {
        var result = WindowsShutdownCountdownAborter.InterpretExitCode(exitCode);

        Assert.Equal(expected, result.Status);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("0", "1", "0", null)]
    [InlineData("1", "1", null, ShutdownCountdownAbortStatus.Aborted)]
    [InlineData("1", "1", "0", ShutdownCountdownAbortStatus.Aborted)]
    [InlineData("1", "0", "1116", ShutdownCountdownAbortStatus.NoCountdown)]
    [InlineData("1", "0", "5", ShutdownCountdownAbortStatus.Failed)]
    public void Launcher_preflight_is_reused_without_a_second_abort(
        string? attempted,
        string? succeeded,
        string? exitCode,
        ShutdownCountdownAbortStatus? expected)
    {
        var result = WindowsShutdownCountdownAborter.InterpretLauncherPreflight(
            attempted,
            succeeded,
            exitCode);

        Assert.Equal(expected, result?.Status);
    }

    [Fact]
    public async Task Cancellation_guard_aborts_a_countdown_started_during_state_mutation()
    {
        var abortCalls = 0;
        var taskStarted = false;
        Task<ShutdownCountdownAbortResult> Abort(CancellationToken _)
        {
            abortCalls++;
            return Task.FromResult(taskStarted
                ? new ShutdownCountdownAbortResult(ShutdownCountdownAbortStatus.Aborted)
                : new ShutdownCountdownAbortResult(ShutdownCountdownAbortStatus.NoCountdown));
        }

        var result = await WindowsShutdownCancellationGuard.RunAsync(
            _ =>
            {
                taskStarted = true;
                return Task.FromResult(true);
            },
            abortCountdown: Abort);

        Assert.Equal(2, abortCalls);
        Assert.True(result.WindowsStateConfirmed);
        Assert.True(result.WasCountdownAborted);
        Assert.Equal(ShutdownCountdownAbortStatus.Aborted, result.EffectiveAbort.Status);
        Assert.Null(result.EffectiveAbort.ExitCode);
    }

    [Fact]
    public async Task Cancellation_guard_preserves_the_exit_code_from_the_abort_that_succeeded()
    {
        var abortCalls = 0;
        Task<ShutdownCountdownAbortResult> Abort(CancellationToken _)
        {
            abortCalls++;
            return Task.FromResult(
                new ShutdownCountdownAbortResult(
                    ShutdownCountdownAbortStatus.NoCountdown,
                    1116));
        }

        var result = await WindowsShutdownCancellationGuard.RunAsync(
            _ => Task.FromResult(true),
            new ShutdownCountdownAbortResult(ShutdownCountdownAbortStatus.Aborted, 0),
            Abort);

        Assert.Equal(1, abortCalls);
        Assert.Equal(ShutdownCountdownAbortStatus.Aborted, result.EffectiveAbort.Status);
        Assert.Equal(0, result.EffectiveAbort.ExitCode);
    }

    [Fact]
    public async Task Cancellation_guard_runs_the_final_abort_when_state_mutation_fails()
    {
        var abortCalls = 0;
        Task<ShutdownCountdownAbortResult> Abort(CancellationToken _)
        {
            abortCalls++;
            return Task.FromResult(
                new ShutdownCountdownAbortResult(ShutdownCountdownAbortStatus.NoCountdown));
        }

        var result = await WindowsShutdownCancellationGuard.RunAsync<bool>(
            _ => throw new IOException("database unavailable"),
            abortCountdown: Abort);

        Assert.Equal(2, abortCalls);
        Assert.IsType<IOException>(result.StateError);
        Assert.True(result.WindowsStateConfirmed);
        Assert.False(result.StateResult);
    }

    [Fact]
    public void Reminder_notification_is_persistent_and_contains_cancel_action()
    {
        var schedule = new ScheduleSnapshot(
            Guid.Parse("6c8a2de4-f75d-4304-95d5-1761ecfd6eb5"),
            7,
            ScheduleKind.OneTime,
            PowerActionType.Shutdown,
            new DateTimeOffset(2026, 7, 21, 23, 41, 0, TimeSpan.FromHours(2)),
            null,
            "W. Europe Standard Time",
            false,
            ScheduleStatus.Active,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var payload = WindowsReminderNotifier.BuildPayload(schedule, 2);

        Assert.Contains("scenario=\"reminder\"", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cancel", payload, StringComparison.Ordinal);
        Assert.Contains("action=cancel", payload, StringComparison.Ordinal);
        Assert.Contains(schedule.Id.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test_notification_has_no_schedule_mutation_actions()
    {
        var payload = WindowsReminderNotifier.BuildTestPayload(
            "[TEST] ShutdownAT notification",
            "No schedule was created.");

        Assert.Contains("[TEST]", payload, StringComparison.Ordinal);
        Assert.Contains("duration=\"short\"", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scenario=\"reminder\"", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("action=cancel", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scheduleId", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SDAT_Volatile", "C:\\Windows\\System32\\wscript.exe", "//B //NoLogo \"C:\\SDAT\\lib\\RunHidden.vbs\" \"C:\\SDAT\\shutdownat.ps1\" -RunVolatile", true)]
    [InlineData("SDAT_Permanent", "wscript.exe", "//B //NoLogo \"C:\\SDAT\\lib\\RunHidden.vbs\" \"C:\\SDAT\\shutdownat.ps1\" -RunPermanent -Profile media -Suspend -DryRun", true)]
    [InlineData("SDAT_Volatile", "C:\\Windows\\System32\\notepad.exe", "RunHidden.vbs shutdownat.ps1", false)]
    [InlineData("SDAT_Volatile", "C:\\Windows\\System32\\wscript.exe", "unrelated.vbs", false)]
    [InlineData("SDAT_Permanent", "C:\\Windows\\System32\\wscript.exe", "//B //NoLogo \"C:\\Other\\lib\\RunHidden.vbs\" \"C:\\SDAT\\shutdownat.ps1\" -RunPermanent", false)]
    [InlineData("SDAT_Volatile", "wscript.exe", "//B //NoLogo \"C:\\SDAT\\lib\\RunHidden.vbs\" \"C:\\SDAT\\shutdownat.ps1\" -RunPermanent", false)]
    [InlineData("SDAT_Volatile_Reminder_0002", "wscript.exe", "//B //NoLogo \"C:\\SDAT\\lib\\RunHidden.vbs\" \"C:\\SDAT\\shutdownat.ps1\" -RunVolatile", false)]
    public void Legacy_task_takeover_requires_the_exact_v1_launcher_shape(
        string taskName,
        string applicationPath,
        string arguments,
        bool expected)
    {
        Assert.Equal(expected, LegacyTaskSignature.IsVerified(taskName, applicationPath, arguments));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Scheduler_projection_requires_an_enabled_task(bool enabled, bool expected)
    {
        Assert.Equal(
            expected,
            WindowsTaskSchedulerProjection.DefinitionMatchesRequiredSettings(
                enabled,
                Microsoft.Win32.TaskScheduler.TaskLogonType.InteractiveToken,
                Microsoft.Win32.TaskScheduler.TaskRunLevel.LUA,
                true,
                false,
                false,
                true,
                Microsoft.Win32.TaskScheduler.TaskInstancesPolicy.IgnoreNew,
                TimeSpan.FromMinutes(5)));
    }
}
