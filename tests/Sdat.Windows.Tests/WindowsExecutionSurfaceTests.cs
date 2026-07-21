using Sdat.Core.Scheduling;
using Sdat.Windows.Execution;
using Sdat.Windows.Notifications;
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
}
