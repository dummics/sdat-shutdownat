using Sdat.Core.Execution;
using Sdat.Core.Diagnostics;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
using Sdat.Windows.Concurrency;
using Sdat.Windows.Execution;
using Sdat.Windows.Migration;
using Sdat.Windows.Notifications;
using Sdat.Windows.Persistence;
using Sdat.Windows.Scheduling;

namespace Sdat.Windows.Hosting;

public sealed record SdatRuntime(
    SqliteStoreOptions StoreOptions,
    SqliteScheduleRepository Schedules,
    SqliteAppSettingsRepository Settings,
    ScheduleCoordinator Coordinator,
    DailySkipCoordinator DailySkips,
    IDiagnosticLogReader Diagnostics,
    TaskInvocationCoordinator TaskInvocations,
    AppSettings CurrentSettings,
    LegacyMigrationResult LegacyMigration,
    ReconciliationReport StartupReconciliation)
{
    public static async Task<SdatRuntime> CreateAsync(
        string taskHostPath,
        CancellationToken cancellationToken = default)
    {
        var dataRoot = Environment.GetEnvironmentVariable("SDAT_DATA_ROOT");
        var options = string.IsNullOrWhiteSpace(dataRoot)
            ? SqliteStoreOptions.CreateDefault()
            : SqliteStoreOptions.CreateAtRoot(dataRoot);
        var schedules = new SqliteScheduleRepository(options);
        await schedules.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var settingsRepository = new SqliteAppSettingsRepository(options);
        var settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        var taskPrefix = Environment.GetEnvironmentVariable("SDAT_TASK_PREFIX");
        var projection = new WindowsTaskSchedulerProjection(
            taskHostPath,
            string.IsNullOrWhiteSpace(taskPrefix) ? "SDAT_" : taskPrefix);
        var reconciler = new SchedulerReconciler(schedules, projection, new ScheduleTaskPlanner());
        var backup = new SqliteBackupService(options);
        var operationLock = new FileOperationLock(options.OperationLockPath);
        var coordinator = new ScheduleCoordinator(schedules, backup, reconciler, operationLock);
        var dailySkips = new DailySkipCoordinator(
            schedules,
            new SqliteDailySkipStore(options),
            backup,
            operationLock);
        await coordinator.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var legacyRoot = Environment.GetEnvironmentVariable("SDAT_LEGACY_ROOT");
        if (string.IsNullOrWhiteSpace(legacyRoot))
        {
            legacyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SDAT",
                "legacy-v1");
        }

        var legacyMigration = await new LegacyV1MigrationService(
                new LegacyV1Source(legacyRoot, new WindowsLegacyTaskReader()),
                new SqliteLegacyImportJournal(options),
                coordinator,
                dailySkips,
                settings.ReminderOffsetsMinutes)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        var startup = legacyMigration.Status == LegacyMigrationStatus.Failed
            ? new ReconciliationReport(
                0,
                0,
                0,
                [new ReconciliationFailure(
                    "SDAT v1",
                    "Migrate",
                    string.Join(" ", legacyMigration.Warnings))])
            : await coordinator
                .ReconcileAsync(settings.ReminderOffsetsMinutes, cancellationToken)
                .ConfigureAwait(false);
        var taskInvocations = new TaskInvocationCoordinator(
            schedules,
            new SqliteTaskExecutionLedger(options),
            new WindowsPowerActionExecutor(),
            new WindowsReminderNotifier(),
            new DurableExecutionFinalizer(backup, reconciler, settings.ReminderOffsetsMinutes),
            operationLock);

        return new SdatRuntime(
            options,
            schedules,
            settingsRepository,
            coordinator,
            dailySkips,
            new SqliteDiagnosticLogReader(options),
            taskInvocations,
            settings,
            legacyMigration,
            startup);
    }
}
