using Sdat.Windows.Notifications;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class ReminderNotificationActionParserTests
{
    [Fact]
    public void Cancel_action_preserves_the_exact_schedule_revision()
    {
        var scheduleId = Guid.NewGuid();

        var action = ReminderNotificationActionParser.Parse(
            $"action=cancel&scheduleId={scheduleId:D}&revision=17");

        Assert.Equal(ReminderNotificationActionKind.Cancel, action.Kind);
        Assert.Equal(scheduleId, action.ScheduleId);
        Assert.Equal(17, action.Revision);
    }

    [Fact]
    public void Open_action_does_not_become_a_cancellation()
    {
        var action = ReminderNotificationActionParser.Parse(
            "action=open&scheduleId=not-a-guid&revision=not-a-number");

        Assert.Equal(ReminderNotificationActionKind.Open, action.Kind);
        Assert.Null(action.ScheduleId);
        Assert.Null(action.Revision);
    }
}
