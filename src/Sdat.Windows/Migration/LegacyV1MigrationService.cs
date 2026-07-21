using System.Text.Json;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;

namespace Sdat.Windows.Migration;

public enum LegacyMigrationStatus
{
    NotFound,
    AlreadyCompleted,
    Imported,
    ImportedWithWarnings,
    Failed,
}

public sealed record LegacyMigrationResult(
    LegacyMigrationStatus Status,
    int ImportedScheduleCount,
    IReadOnlyList<string> Warnings);

public sealed class LegacyV1MigrationService(
    LegacyV1Source source,
    SqliteLegacyImportJournal journal,
    ScheduleCoordinator schedules,
    DailySkipCoordinator dailySkips,
    IReadOnlyList<int> reminderOffsetsMinutes)
{
    private const string ImportKey = "sdat-v1-default";

    public async Task<LegacyMigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (await journal.IsCompletedAsync(ImportKey, cancellationToken).ConfigureAwait(false))
        {
            return new LegacyMigrationResult(LegacyMigrationStatus.AlreadyCompleted, 0, []);
        }

        var plan = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!plan.SourceFound)
        {
            return new LegacyMigrationResult(LegacyMigrationStatus.NotFound, 0, []);
        }

        if (!plan.IsValid)
        {
            return new LegacyMigrationResult(LegacyMigrationStatus.Failed, 0, plan.Warnings);
        }

        var warnings = plan.Warnings.ToList();
        var imported = 0;
        try
        {
            foreach (var draft in plan.Schedules.OrderBy(schedule => schedule.Kind))
            {
                var result = await schedules.SetAsync(draft, reminderOffsetsMinutes, cancellationToken)
                    .ConfigureAwait(false);
                imported++;
                if (result.BackupFailure is not null)
                {
                    warnings.Add($"Imported {draft.Kind}, but its backup failed: {result.BackupFailure}");
                }

                warnings.AddRange(result.Reconciliation.Failures.Select(failure =>
                    $"{failure.Operation} {failure.TaskName}: {failure.Detail}"));
            }

            if (plan.SkipNextDaily)
            {
                var skip = await dailySkips.RequestNextAsync(cancellationToken).ConfigureAwait(false);
                if (skip.BackupFailure is not null)
                {
                    warnings.Add($"Imported the pending daily skip, but its backup failed: {skip.BackupFailure}");
                }
            }

            await source.RemoveObsoleteTasksAsync(plan, cancellationToken).ConfigureAwait(false);
            var detail = JsonSerializer.Serialize(new
            {
                importedScheduleCount = imported,
                importedDailySkip = plan.SkipNextDaily,
                obsoleteTasksRemoved = plan.ObsoleteTaskNames,
                warnings,
            });
            await journal.MarkCompletedAsync(ImportKey, plan.SourcePath, detail, cancellationToken)
                .ConfigureAwait(false);
            return new LegacyMigrationResult(
                warnings.Count == 0 ? LegacyMigrationStatus.Imported : LegacyMigrationStatus.ImportedWithWarnings,
                imported,
                warnings);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"Legacy migration will be retried: {exception.Message}");
            return new LegacyMigrationResult(LegacyMigrationStatus.Failed, imported, warnings);
        }
    }
}
