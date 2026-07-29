using System.Globalization;
using Sdat.Core.Scheduling;
using Sdat.Core.TimeExpressions;

namespace Sdat.App;

internal static class SchedulePreviewFormatter
{
    public static string Format(
        ScheduleInputPreview preview,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        if (!preview.IsValid)
        {
            return FormatError(preview.ErrorCode);
        }

        var action = AppText.PowerAction(preview.Action);
        if (preview.Kind == ScheduleKind.Daily)
        {
            return AppText.Format(
                "SchedulePreviewDaily",
                "{0} every day at {1}",
                action,
                preview.DailyAt?.ToString("HH:mm", CultureInfo.CurrentUICulture));
        }

        var target = TimeZoneInfo.ConvertTime(preview.TargetAt!.Value, timeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var remaining = FormatRemaining(target, localNow);
        if (target.Date == localNow.Date)
        {
            return AppText.Format(
                "SchedulePreviewToday",
                "{0} today at {1} · in {2}",
                action,
                target.ToString("HH:mm", CultureInfo.CurrentUICulture),
                remaining);
        }

        if (target.Date == localNow.Date.AddDays(1))
        {
            return AppText.Format(
                "SchedulePreviewTomorrow",
                "{0} tomorrow at {1} · in {2}",
                action,
                target.ToString("HH:mm", CultureInfo.CurrentUICulture),
                remaining);
        }

        return AppText.Format(
            "SchedulePreviewDate",
            "{0} on {1} at {2} · in {3}",
            action,
            target.ToString("d", CultureInfo.CurrentUICulture),
            target.ToString("HH:mm", CultureInfo.CurrentUICulture),
            remaining);
    }

    public static string FormatButton(ScheduleInputPreview preview)
    {
        if (!preview.IsValid)
        {
            return AppText.Get("ScheduleButtonDefault", "Schedule");
        }

        var time = preview.Kind == ScheduleKind.Daily
            ? preview.DailyAt?.ToString("HH:mm", CultureInfo.CurrentUICulture)
            : preview.TargetAt?.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentUICulture);
        return AppText.Format(
            preview.Kind == ScheduleKind.Daily
                ? "ScheduleButtonDaily"
                : "ScheduleButtonAt",
            preview.Kind == ScheduleKind.Daily
                ? "{0} daily · {1}"
                : "{0} · {1}",
            AppText.PowerAction(preview.Action),
            time);
    }

    public static string FormatError(ScheduleInputErrorCode? errorCode) => errorCode switch
    {
        ScheduleInputErrorCode.MissingValue =>
            AppText.Get("ScheduleErrorMissingValue", "Enter a time, for example 36m or 23:41."),
        ScheduleInputErrorCode.InvalidClockTime =>
            AppText.Get("ScheduleErrorInvalidClock", "Enter a valid time between 00:00 and 23:59."),
        ScheduleInputErrorCode.NonPositiveDuration =>
            AppText.Get("ScheduleErrorPositiveDuration", "Enter a duration greater than zero."),
        ScheduleInputErrorCode.RelativeDailyNotAllowed =>
            AppText.Get("ScheduleErrorDailyClock", "Daily schedules need a clock time, for example 02:30."),
        ScheduleInputErrorCode.NonexistentLocalTime =>
            AppText.Get("ScheduleErrorDaylightSaving", "That local time does not exist because the clock changes on that day."),
        _ =>
            AppText.Get("ScheduleErrorInvalidFormat", "Use a duration such as 36m, or a time such as 23:41."),
    };

    private static string FormatRemaining(DateTimeOffset target, DateTimeOffset now)
        => FormatRemaining(target.ToUniversalTime() - now.ToUniversalTime());

    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalSeconds <= 0)
        {
            return AppText.Get("ScheduleRemainingNow", "now");
        }

        var totalMinutes = (int)Math.Ceiling(remaining.TotalMinutes);
        if (totalMinutes <= 1)
        {
            return AppText.Get("ScheduleRemainingLessThanMinute", "less than a minute");
        }

        var days = totalMinutes / 1440;
        var hours = totalMinutes % 1440 / 60;
        var minutes = totalMinutes % 60;
        var parts = new List<string>(3);
        if (days > 0)
        {
            parts.Add(AppText.Format("ScheduleRemainingDays", "{0} d", days));
        }
        if (hours > 0)
        {
            parts.Add(AppText.Format("ScheduleRemainingHours", "{0} h", hours));
        }
        if (minutes > 0)
        {
            parts.Add(AppText.Format("ScheduleRemainingMinutes", "{0} min", minutes));
        }

        return string.Join(" ", parts);
    }
}
