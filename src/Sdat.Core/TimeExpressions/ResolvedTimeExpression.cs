namespace Sdat.Core.TimeExpressions;

public enum TimeExpressionKind
{
    Relative,
    Absolute,
}

public sealed record ResolvedTimeExpression(
    TimeExpressionKind Kind,
    string Raw,
    DateTimeOffset Target,
    int? DurationSeconds,
    string Label);

public sealed class TimeExpressionParseException(string message) : FormatException(message);
