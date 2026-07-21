namespace Sdat.Core.Diagnostics;

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
