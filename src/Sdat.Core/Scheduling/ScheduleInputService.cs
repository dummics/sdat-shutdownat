using Sdat.Core.TimeExpressions;

namespace Sdat.Core.Scheduling;

public sealed record PreparedSchedule(ScheduleDraft Draft, ResolvedTimeExpression ResolvedTime);

public sealed class ScheduleInputService(TimeExpressionParser? parser = null)
{
    private readonly TimeExpressionParser _parser = parser ?? new TimeExpressionParser();

    public PreparedSchedule Prepare(
        string expression,
        ScheduleKind kind,
        PowerActionType action,
        bool keepDaily,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        var resolved = _parser.Resolve(expression, now, timeZone);
        if (kind == ScheduleKind.Daily && resolved.Kind != TimeExpressionKind.Absolute)
        {
            throw new TimeExpressionParseException("Daily schedules require a clock time such as 02:30.");
        }

        var draft = kind == ScheduleKind.Daily
            ? ScheduleDraft.Daily(
                action,
                TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(resolved.Target, timeZone).DateTime),
                timeZone.Id)
            : ScheduleDraft.OneTime(action, resolved.Target, timeZone.Id, keepDaily);
        return new PreparedSchedule(draft, resolved);
    }
}
