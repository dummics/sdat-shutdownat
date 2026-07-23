namespace Sdat.Core.Scheduling;

public enum PowerActionType
{
    Shutdown,
    Suspend,
    Restart,
}

public enum ScheduleKind
{
    OneTime,
    Daily,
}

public enum ScheduleStatus
{
    Active,
    Cancelled,
    Completed,
}

public sealed record ScheduleDraft
{
    private ScheduleDraft(
        ScheduleKind kind,
        PowerActionType action,
        DateTimeOffset? targetAt,
        TimeOnly? dailyAt,
        string timeZoneId,
        bool keepDaily)
    {
        Kind = kind;
        Action = action;
        TargetAt = targetAt;
        DailyAt = dailyAt;
        TimeZoneId = timeZoneId;
        KeepDaily = keepDaily;
    }

    public ScheduleKind Kind { get; }

    public PowerActionType Action { get; }

    public DateTimeOffset? TargetAt { get; }

    public TimeOnly? DailyAt { get; }

    public string TimeZoneId { get; }

    public bool KeepDaily { get; }

    public static ScheduleDraft OneTime(
        PowerActionType action,
        DateTimeOffset targetAt,
        string timeZoneId,
        bool keepDaily = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        return new ScheduleDraft(ScheduleKind.OneTime, action, targetAt, null, timeZoneId, keepDaily);
    }

    public static ScheduleDraft Daily(PowerActionType action, TimeOnly dailyAt, string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        return new ScheduleDraft(ScheduleKind.Daily, action, null, dailyAt, timeZoneId, false);
    }
}

public sealed record ScheduleSnapshot(
    Guid Id,
    long Revision,
    ScheduleKind Kind,
    PowerActionType Action,
    DateTimeOffset? TargetAt,
    TimeOnly? DailyAt,
    string TimeZoneId,
    bool KeepDaily,
    ScheduleStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
