using System.Globalization;
using System.Text.RegularExpressions;

namespace Sdat.Core.TimeExpressions;

public sealed partial class TimeExpressionParser
{
    private const string MissingMessage =
        "Missing time value. Use 2330, 23:30, 2h, 3.5h, 45m, mezzora, or 180s.";
    private const string InvalidMessage =
        "Invalid time format. Use HHMM/HH:MM or a duration like 2h, 45m, 180s.";

    public ResolvedTimeExpression Resolve(
        string value,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(timeZone);

        var raw = value.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new TimeExpressionParseException(MissingMessage);
        }

        var durationSeconds = TryResolveDurationSeconds(raw);
        if (durationSeconds is not null)
        {
            var targetUtc = now.ToUniversalTime().AddSeconds(durationSeconds.Value);
            if (targetUtc.Second > 0 || targetUtc.Millisecond > 0 || targetUtc.Microsecond > 0)
            {
                targetUtc = targetUtc
                    .AddMinutes(1)
                    .AddSeconds(-targetUtc.Second)
                    .AddTicks(-(targetUtc.Ticks % TimeSpan.TicksPerSecond));
            }

            var relativeTarget = TimeZoneInfo.ConvertTime(targetUtc, timeZone);
            return new ResolvedTimeExpression(
                TimeExpressionKind.Relative,
                raw,
                relativeTarget,
                durationSeconds,
                $"in {FormatRemaining(relativeTarget, now)}");
        }

        var time = ParseClockTime(raw);
        var nowLocal = TimeZoneInfo.ConvertTime(now, timeZone);
        var localDate = nowLocal.Date;
        var target = ResolveLocalDateTime(localDate, time, timeZone);
        if (target < now)
        {
            target = ResolveLocalDateTime(localDate.AddDays(1), time, timeZone);
        }

        return new ResolvedTimeExpression(
            TimeExpressionKind.Absolute,
            raw,
            target,
            null,
            $"at {time:HH:mm}");
    }

    public int? TryResolveDurationSeconds(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var raw = value.Trim().ToLowerInvariant();
        var compact = WhitespaceRegex().Replace(raw, string.Empty);

        if (compact is "mezzora" or "mezz'ora" or "mezzaora" or "mezzhour" or "halfhour" ||
            HalfHourRegex().IsMatch(raw))
        {
            return 1800;
        }

        var matches = DurationPartRegex().Matches(raw);
        if (matches.Count == 0)
        {
            return null;
        }

        var consumed = string.Concat(matches.Select(match => match.Value));
        if (!string.Equals(WhitespaceRegex().Replace(consumed, string.Empty), compact, StringComparison.Ordinal))
        {
            return null;
        }

        double seconds = 0;
        foreach (Match match in matches)
        {
            var amountText = match.Groups["amount"].Value.Replace(',', '.');
            var amount = double.Parse(amountText, CultureInfo.InvariantCulture);
            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            seconds += unit switch
            {
                "h" or "hr" or "hrs" or "hour" or "hours" or "ora" or "ore" => amount * 3600,
                "m" or "min" or "mins" or "minute" or "minutes" => amount * 60,
                _ => amount,
            };
        }

        var rounded = checked((int)Math.Round(seconds, MidpointRounding.AwayFromZero));
        if (rounded <= 0)
        {
            throw new TimeExpressionParseException("Duration must be greater than zero.");
        }

        return rounded;
    }

    public static string FormatRemaining(DateTimeOffset target, DateTimeOffset now)
    {
        var remaining = target.ToUniversalTime() - now.ToUniversalTime();
        if (remaining.TotalSeconds <= 0)
        {
            return "now";
        }

        var totalMinutes = (int)Math.Ceiling(remaining.TotalMinutes);
        if (totalMinutes <= 1)
        {
            return "<1m";
        }

        var days = totalMinutes / 1440;
        var hours = totalMinutes % 1440 / 60;
        var minutes = totalMinutes % 60;
        return days > 0
            ? $"{days}d {hours}h {minutes}m"
            : hours > 0
                ? $"{hours}h {minutes}m"
                : $"{totalMinutes}m";
    }

    private static TimeOnly ParseClockTime(string value)
    {
        var match = RawClockRegex().Match(value);
        if (!match.Success)
        {
            throw new TimeExpressionParseException(InvalidMessage);
        }

        var compact = match.Groups["compact"];
        var hours = compact.Success
            ? int.Parse(compact.Value.PadLeft(4, '0')[..2], CultureInfo.InvariantCulture)
            : int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minutes = compact.Success
            ? int.Parse(compact.Value.PadLeft(4, '0')[2..], CultureInfo.InvariantCulture)
            : int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59)
        {
            throw new TimeExpressionParseException($"Invalid time: {value}");
        }

        return new TimeOnly(hours, minutes);
    }

    private static DateTimeOffset ResolveLocalDateTime(
        DateTime localDate,
        TimeOnly time,
        TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localDate.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            throw new TimeExpressionParseException(
                $"The local time {local:yyyy-MM-dd HH:mm} does not exist because of a daylight-saving transition.");
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(mezz|mezza|half)\s*(ora|hour)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HalfHourRegex();

    [GeneratedRegex(
        @"(?<amount>\d+(?:[\.,]\d+)?)\s*(?<unit>minutes|minute|seconds|secondi|second|hours|hour|mins|secs|hrs|min|sec|ora|ore|hr|h|m|s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPartRegex();

    [GeneratedRegex(
        @"^(?:(?<hour>\d{1,2}):(?<minute>\d{2})|(?<compact>\d{3,4}))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RawClockRegex();
}
