using Sdat.Core.Execution;
using Sdat.Core.Diagnostics;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
using Sdat.Core.Storage;
using Sdat.Windows.Concurrency;
using Sdat.Windows.Diagnostics;
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
    ScheduleCommandService ScheduleCommands,
    DailySkipCoordinator DailySkips,
    IDiagnosticLogReader Diagnostics,
    RollingFileAppLogger Logger,
    LocalDiagnosticReportWriter DiagnosticReports,
    WindowsReminderNotifier ReminderNotifications,
    TaskInvocationCoordinator TaskInvocations,
    AppSettings CurrentSettings,
    DatabaseRecoveryResult? StartupRecovery,
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
        var operationLock = new FileOperationLock(options.OperationLockPath);
        var backup = new SqliteBackupService(options);
        var initialization = await new SqliteStoreInitializer(
                options,
                schedules,
                new SqliteRecoveryService(options),
                backup,
                operationLock)
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
        var settingsRepository = new SqliteAppSettingsRepository(options);
        var settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        var safetyPolicy = new SettingsRuntimeSafetyPolicy(settingsRepository);
        var logger = new RollingFileAppLogger(options, settingsRepository);
        var taskPrefix = Environment.GetEnvironmentVariable("SDAT_TASK_PREFIX");
        var projection = new WindowsTaskSchedulerProjection(
            taskHostPath,
            string.IsNullOrWhiteSpace(taskPrefix) ? "SDAT_" : taskPrefix);
        var reconciler = new SchedulerReconciler(schedules, projection, new ScheduleTaskPlanner());
        var coordinator = new ScheduleCoordinator(
            schedules,
            backup,
            reconciler,
            operationLock,
            safetyPolicy: safetyPolicy);
        var dailySkips = new DailySkipCoordinator(
            schedules,
            new SqliteDailySkipStore(options),
            backup,
            operationLock);
        var scheduleCommands = new ScheduleCommandService(
            coordinator,
            schedules,
            dailySkips,
            settingsRepository,
            operationLock);
        var legacyRoot = Environment.GetEnvironmentVariable("SDAT_LEGACY_ROOT");
        if (string.IsNullOrWhiteSpace(legacyRoot))
        {
            legacyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SDAT",
                "legacy-v1");
        }

        var legacyMigration = settings.IsTestMode
            ? new LegacyMigrationResult(
                LegacyMigrationStatus.SuppressedByTestMode,
                0,
                ["Legacy import is paused while safe test mode is active."])
            : await new LegacyV1MigrationService(
                    new LegacyV1Source(legacyRoot, new WindowsLegacyTaskReader()),
                    new SqliteLegacyImportJournal(options),
                    coordinator,
                    dailySkips,
                    settings.ReminderOffsetsMinutes)
                .MigrateAsync(cancellationToken)
                .ConfigureAwait(false);
        var startup = settings.IsTestMode
            ? ReconciliationReport.TestModeSuppressed
            : legacyMigration.Status == LegacyMigrationStatus.Failed
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
        var reminderNotifications = new WindowsReminderNotifier();
        var taskInvocations = new TaskInvocationCoordinator(
            schedules,
            new SqliteTaskExecutionLedger(options),
            new SimulationAwarePowerActionExecutor(
                settingsRepository,
                new WindowsPowerActionExecutor(),
                logger),
            reminderNotifications,
            new DurableExecutionFinalizer(backup, reconciler, settings.ReminderOffsetsMinutes),
            operationLock,
            safetyPolicy: safetyPolicy);

        try
        {
            await logger.WriteAsync(
                    AppLogLevel.Debug,
                    nameof(SdatRuntime),
                    "Runtime initialized.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Logging is diagnostic-only and must never prevent safe startup.
        }

        var diagnostics = new SqliteDiagnosticLogReader(options);
        return new SdatRuntime(
            options,
            schedules,
            settingsRepository,
            coordinator,
            scheduleCommands,
            dailySkips,
            diagnostics,
            logger,
            new LocalDiagnosticReportWriter(
                options,
                schedules,
                settingsRepository,
                diagnostics,
                logger),
            reminderNotifications,
            taskInvocations,
            settings,
            initialization.Recovery,
            legacyMigration,
            startup);
    }
}
