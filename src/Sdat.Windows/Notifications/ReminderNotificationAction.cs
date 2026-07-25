using System.Globalization;

namespace Sdat.Windows.Notifications;

public enum ReminderNotificationActionKind
{
    Unknown,
    Open,
    Cancel,
}

public sealed record ReminderNotificationAction(
    ReminderNotificationActionKind Kind,
    Guid? ScheduleId,
    long? Revision);

public static class ReminderNotificationActionParser
{
    public static ReminderNotificationAction Parse(string value)
    {
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            arguments[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
        }

        var kind = arguments.GetValueOrDefault("action")?.ToLowerInvariant() switch
        {
            "cancel" => ReminderNotificationActionKind.Cancel,
            "open" => ReminderNotificationActionKind.Open,
            _ => ReminderNotificationActionKind.Unknown,
        };
        Guid? scheduleId = Guid.TryParse(arguments.GetValueOrDefault("scheduleId"), out var parsedId)
            ? parsedId
            : null;
        long? revision = long.TryParse(
            arguments.GetValueOrDefault("revision"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedRevision)
            ? parsedRevision
            : null;
        return new ReminderNotificationAction(kind, scheduleId, revision);
    }
}
