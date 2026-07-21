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
