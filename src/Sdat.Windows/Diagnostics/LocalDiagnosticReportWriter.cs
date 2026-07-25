using System.Text;
using Sdat.Core.Diagnostics;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
using Sdat.Windows.Persistence;

namespace Sdat.Windows.Diagnostics;

public sealed class LocalDiagnosticReportWriter(
    SqliteStoreOptions options,
    IScheduleRepository schedules,
    IAppSettingsRepository settingsRepository,
    IDiagnosticLogReader diagnostics,
    IAppLogger logger)
{
    public async Task<string> WriteAsync(
        string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        var health = await schedules.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var activeSchedules = await schedules.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var events = await diagnostics.ReadRecentAsync(50, cancellationToken).ConfigureAwait(false);
        var report = new StringBuilder()
            .AppendLine("ShutdownAT diagnostic report")
            .AppendLine($"Created UTC: {DateTimeOffset.UtcNow:O}")
            .AppendLine($"Version: {applicationVersion}")
            .AppendLine($"Operating system: {Environment.OSVersion}")
            .AppendLine($"Database health: {health.Status} - {health.Detail}")
            .AppendLine($"Active schedules: {activeSchedules.Count}")
            .AppendLine($"Developer mode: {settings.DeveloperModeEnabled}")
            .AppendLine($"Safe test mode: {settings.IsTestMode}")
            .AppendLine($"Logging level: {settings.LogLevel}")
            .AppendLine($"Database: {options.DatabasePath}")
            .AppendLine()
            .AppendLine("Recent activity:");
        foreach (var entry in events)
        {
            report.AppendLine(
                $"{entry.OccurredAt:O} [{entry.Severity}] {entry.Source}: {entry.Message}");
        }

        Directory.CreateDirectory(options.DataDirectory);
        await File.WriteAllTextAsync(
                options.DiagnosticReportPath,
                report.ToString(),
                cancellationToken)
            .ConfigureAwait(false);
        await logger.WriteAsync(
                AppLogLevel.Information,
                nameof(LocalDiagnosticReportWriter),
                "Created a local diagnostic report.",
                cancellationToken)
            .ConfigureAwait(false);
        return options.DiagnosticReportPath;
    }
}
