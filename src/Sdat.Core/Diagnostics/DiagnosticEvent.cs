using Sdat.Core.Settings;

namespace Sdat.Core.Diagnostics;

public interface IAppLogger
{
    Task WriteAsync(
        AppLogLevel level,
        string source,
        string message,
        CancellationToken cancellationToken = default);
}

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record DiagnosticEvent(
    DateTimeOffset OccurredAt,
    DiagnosticSeverity Severity,
    string Source,
    string Message,
    Guid? ScheduleId,
    long? ScheduleRevision);

public interface IDiagnosticLogReader
{
    Task<IReadOnlyList<DiagnosticEvent>> ReadRecentAsync(
        int limit = 20,
        CancellationToken cancellationToken = default);
}
