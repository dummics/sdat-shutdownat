using Microsoft.Windows.AppNotifications;
using Sdat.Core.Execution;
using Sdat.Core.Scheduling;

namespace Sdat.Windows.Notifications;

public sealed class WindowsReminderNotifier : ITaskReminderNotifier
{
    public Task<ReminderDeliveryResult> ShowAsync(
        ScheduleSnapshot schedule,
        int offsetMinutes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            try
            {
                manager.Register();
                manager.Show(BuildNotification(schedule, offsetMinutes));
            }
            finally
            {
                try
                {
                    manager.Unregister();
                }
                catch
                {
                    // The original registration/show result remains authoritative.
                }

                manager.NotificationInvoked -= OnNotificationInvoked;
            }

            return Task.FromResult(ReminderDeliveryResult.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(ReminderDeliveryResult.Failed(
                exception.GetType().Name,
                exception.Message));
        }
    }

    internal static AppNotification BuildNotification(ScheduleSnapshot schedule, int offsetMinutes) =>
        new(BuildPayload(schedule, offsetMinutes));

    internal static string BuildPayload(ScheduleSnapshot schedule, int offsetMinutes)
    {
        var actionText = schedule.Action switch
        {
            PowerActionType.Shutdown => "shut down",
            PowerActionType.Restart => "restart",
            PowerActionType.Suspend => "suspend",
            _ => "run a power action",
        };
        var when = schedule.Kind == ScheduleKind.OneTime
            ? schedule.TargetAt!.Value.ToLocalTime().ToString("HH:mm")
            : schedule.DailyAt!.Value.ToString("HH:mm");
        var title = System.Security.SecurityElement.Escape(
            $"PC will {actionText} in {offsetMinutes} minute{(offsetMinutes == 1 ? string.Empty : "s")}");
        var detail = System.Security.SecurityElement.Escape(
            $"Scheduled for {when}. Save your work or cancel the action.");
        var scheduleId = schedule.Id.ToString("D");
        var revision = schedule.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"""
            <toast scenario="reminder">
              <visual>
                <binding template="ToastGeneric">
                  <text>{title}</text>
                  <text>{detail}</text>
                </binding>
              </visual>
              <actions>
                <action content="Cancel" arguments="action=cancel&amp;scheduleId={scheduleId}&amp;revision={revision}" activationType="foreground" />
                <action content="Open ShutdownAT" arguments="action=open&amp;scheduleId={scheduleId}" activationType="foreground" />
              </actions>
            </toast>
            """;
    }

    private static void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        // Foreground activation is handled by the companion composition root.
    }
}
