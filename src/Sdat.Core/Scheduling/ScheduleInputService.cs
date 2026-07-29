using Sdat.Core.TimeExpressions;

namespace Sdat.Core.Scheduling;

public sealed record PreparedSchedule(ScheduleDraft Draft, ResolvedTimeExpression ResolvedTime);

public sealed record ScheduleInputPreview(
    bool IsValid,
    PowerActionType Action,
    ScheduleKind Kind,
    DateTimeOffset? TargetAt,
    TimeOnly? DailyAt,
    TimeExpressionKind? ExpressionKind,
    int? DurationSeconds,
    bool RollsToNextDay,
    ScheduleInputErrorCode? ErrorCode);

public sealed class ScheduleInputService(TimeExpressionParser? parser = null)
{
    private readonly TimeExpressionParser _parser = parser ?? new TimeExpressionParser();

    public ScheduleInputPreview Preview(
        string expression,
        ScheduleKind kind,
        PowerActionType action,
        bool keepDaily,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        try
        {
            var prepared = Prepare(expression, kind, action, keepDaily, now, timeZone);
            DateTimeOffset? targetLocal = prepared.Draft.TargetAt is null
                ? null
                : TimeZoneInfo.ConvertTime(prepared.Draft.TargetAt.Value, timeZone);
            var nowLocal = TimeZoneInfo.ConvertTime(now, timeZone);
            return new ScheduleInputPreview(
                true,
                action,
                kind,
                prepared.Draft.TargetAt,
                prepared.Draft.DailyAt,
                prepared.ResolvedTime.Kind,
                prepared.ResolvedTime.DurationSeconds,
                kind == ScheduleKind.OneTime &&
                prepared.ResolvedTime.Kind == TimeExpressionKind.Absolute &&
                targetLocal?.Date > nowLocal.Date,
                null);
        }
        catch (TimeExpressionParseException exception)
        {
            return new ScheduleInputPreview(
                false,
                action,
                kind,
                null,
                null,
                null,
                null,
                false,
                exception.ErrorCode);
        }
    }

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
            throw new TimeExpressionParseException(
                ScheduleInputErrorCode.RelativeDailyNotAllowed,
                "Daily schedules require a clock time such as 02:30.");
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
