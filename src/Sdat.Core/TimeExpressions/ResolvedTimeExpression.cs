namespace Sdat.Core.TimeExpressions;

public enum TimeExpressionKind
{
    Relative,
    Absolute,
}

public enum ScheduleInputErrorCode
{
    MissingValue,
    InvalidFormat,
    InvalidClockTime,
    NonPositiveDuration,
    RelativeDailyNotAllowed,
    NonexistentLocalTime,
}

public sealed record ResolvedTimeExpression(
    TimeExpressionKind Kind,
    string Raw,
    DateTimeOffset Target,
    int? DurationSeconds,
    string Label);

public sealed class TimeExpressionParseException(
    ScheduleInputErrorCode errorCode,
    string message) : FormatException(message)
{
    public TimeExpressionParseException(string message)
        : this(ScheduleInputErrorCode.InvalidFormat, message)
    {
    }

    public ScheduleInputErrorCode ErrorCode { get; } = errorCode;
}
