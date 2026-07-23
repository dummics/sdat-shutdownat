using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sdat.Core.Commands;
using Sdat.Core.Diagnostics;
using Sdat.Core.Execution;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Core.Storage;
using Sdat.Windows.Hosting;
using Sdat.Windows.Migration;
using Sdat.Windows.Maintenance;
using Spectre.Console;

return await SdatCli.RunAsync(args);

internal static class SdatCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    static SdatCli()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var requestedJson = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        CliInvocation invocation;
        try
        {
            var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
            invocation = CliInvocationParser.Parse(
                args,
                suspendAlias: string.Equals(executableName, "ssat", StringComparison.OrdinalIgnoreCase),
                interactiveDefault: args.Length == 0 && IsInteractiveTerminal());
        }
        catch (Exception exception) when (exception is CliUsageException or FormatException or OverflowException)
        {
            if (requestedJson)
            {
                WriteJson(MachineResponse<object>.Failed("parse", exception.GetType().Name, exception.Message));
            }
            else
            {
                Console.Error.WriteLine(exception.Message);
                Console.Error.WriteLine("Use 'sdat help' for examples.");
            }

            return 2;
        }

        if (invocation.Command == CliCommandType.Help)
        {
            PrintHelp();
            return 0;
        }

        if (invocation.Command == CliCommandType.Version)
        {
            if (invocation.Json)
            {
                WriteMachineSuccess("version", new { version = GetVersion() });
            }
            else
            {
                Console.WriteLine(GetVersion());
            }

            return 0;
        }

        if (invocation.Command == CliCommandType.Cancel)
        {
            TryAbortWindowsCountdown();
        }

        if (invocation.Command is CliCommandType.Update or CliCommandType.Uninstall)
        {
            try
            {
                return RunMaintenance(invocation);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                WriteError(exception, invocation.Json, invocation.Command.ToString().ToLowerInvariant());
                return 10;
            }
        }

        if (invocation.Command == CliCommandType.Ui)
        {
            try
            {
                var process = LaunchGraphicalCompanion();
                if (invocation.Json)
                {
                    WriteMachineSuccess("ui", new { launched = true, processId = process.Id });
                }
                else
                {
                    Console.WriteLine("Opened ShutdownAT.");
                }

                return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                WriteError(exception, invocation.Json, "ui");
                return 10;
            }
        }

        try
        {
            if (invocation.Command == CliCommandType.Preview)
            {
                return Preview(invocation);
            }

            var services = await CreateServicesAsync();
            var startup = services.StartupReconciliation;
            var reminderOffsets = (await services.Settings.LoadAsync()).ReminderOffsetsMinutes;

            if (invocation.Command == CliCommandType.TaskRun)
            {
                var result = await services.TaskInvocations.RunAsync(new TaskInvocation(
                    invocation.ScheduleId!.Value,
                    invocation.Revision!.Value,
                    invocation.TaskRole!.Value,
                    invocation.ReminderOffsetMinutes));
                if (invocation.Json)
                {
                    var error = result.Outcome == TaskInvocationOutcome.Failed
                        ? new MachineError("TaskInvocationFailed", result.Detail)
                        : null;
                    WriteMachineResponse(
                        "task-run",
                        result.Outcome != TaskInvocationOutcome.Failed,
                        result,
                        [],
                        error);
                }
                else if (result.Outcome == TaskInvocationOutcome.Failed)
                {
                    Console.Error.WriteLine(result.Detail);
                }

                return result.Outcome == TaskInvocationOutcome.Failed ? 10 : 0;
            }

            return invocation.Command switch
            {
                CliCommandType.Status => await ShowStatusAsync(
                    services.Schedules,
                    startup,
                    services.LegacyMigration,
                    services.StartupRecovery,
                    invocation.Json),
                CliCommandType.Schedule => await ScheduleAsync(services.ScheduleCommands, invocation),
                CliCommandType.Cancel => await CancelAsync(services.Coordinator, services.Schedules, invocation, reminderOffsets),
                CliCommandType.Skip => await SkipNextDailyAsync(services.DailySkips, invocation.Json),
                CliCommandType.Logs => await ShowLogsAsync(
                    services.Diagnostics,
                    Path.GetDirectoryName(services.StoreOptions.DatabasePath)!,
                    invocation.Json),
                CliCommandType.Reconcile => WriteReconciliation(startup, invocation.Json),
                CliCommandType.Health => await ShowHealthAsync(
                    services.Schedules,
                    startup,
                    services.LegacyMigration,
                    services.StartupRecovery,
                    invocation.Json),
                CliCommandType.Tui => await RunTuiAsync(services),
                _ => 2,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WriteError(exception, invocation.Json, invocation.Command.ToString().ToLowerInvariant());
            return 10;
        }
    }

    private static Task<SdatRuntime> CreateServicesAsync()
    {
        var applicationPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the SDAT executable path.");
        if (Path.GetFileName(applicationPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Scheduling requires a published SDAT executable; 'dotnet run' is intentionally not registered in Task Scheduler.");
        }

        var companionPath = Path.Combine(Path.GetDirectoryName(applicationPath)!, "SDAT.exe");
        var taskHostPath = File.Exists(companionPath) ? companionPath : applicationPath;
        return SdatRuntime.CreateAsync(taskHostPath);
    }

    private static bool IsInteractiveTerminal() =>
        Environment.UserInteractive &&
        !Console.IsInputRedirected &&
        !Console.IsOutputRedirected &&
        AnsiConsole.Profile.Capabilities.Interactive;

    internal static Process LaunchGraphicalCompanion()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the SDAT executable path.");
        var installDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("Cannot resolve the SDAT installation directory.");
        var companionPath = Path.Combine(installDirectory, "SDAT.exe");
        if (!File.Exists(companionPath))
        {
            throw new FileNotFoundException(
                "ShutdownAT is not available beside the CLI executable. Reinstall the complete package.",
                companionPath);
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = companionPath,
            WorkingDirectory = installDirectory,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Windows did not start ShutdownAT.");
    }

    private static int RunMaintenance(CliInvocation invocation)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the SDAT executable path.");
        var launcher = new MaintenanceLauncher(Path.GetDirectoryName(executablePath)!);
        var result = invocation.Command == CliCommandType.Update
            ? launcher.StartUpdate()
            : launcher.StartUninstall(invocation.KeepData);
        if (invocation.Json)
        {
            WriteMachineSuccess(invocation.Command.ToString().ToLowerInvariant(), result);
        }
        else
        {
            Console.WriteLine(result.Detail);
        }

        return 0;
    }

    private static async Task<int> ShowStatusAsync(
        IScheduleRepository repository,
        ReconciliationReport reconciliation,
        LegacyMigrationResult legacyMigration,
        DatabaseRecoveryResult? startupRecovery,
        bool json)
    {
        var schedules = await repository.ListAsync();
        if (json)
        {
            var warnings = GetReconciliationWarnings(reconciliation)
                .Concat(GetLegacyMigrationWarnings(legacyMigration))
                .Concat(GetRecoveryWarnings(startupRecovery))
                .ToArray();
            WriteMachineSuccess(
                "status",
                new { schedules, reconciliation, legacyMigration, startupRecovery },
                warnings,
                reconciliation.IsHealthy && legacyMigration.Status != LegacyMigrationStatus.Failed);
            return reconciliation.IsHealthy && legacyMigration.Status != LegacyMigrationStatus.Failed ? 0 : 3;
        }

        if (schedules.Count == 0)
        {
            Console.WriteLine("No active ShutdownAT schedules.");
        }
        else
        {
            foreach (var schedule in schedules)
            {
                Console.WriteLine(FormatSchedule(schedule));
            }
        }

        WriteReconciliationWarning(reconciliation);
        WriteLegacyMigrationWarnings(legacyMigration);
        WriteRecoveryWarning(startupRecovery);
        return reconciliation.IsHealthy && legacyMigration.Status != LegacyMigrationStatus.Failed ? 0 : 3;
    }

    private static async Task<int> ScheduleAsync(
        ScheduleCommandService scheduleCommands,
        CliInvocation invocation)
    {
        var now = DateTimeOffset.UtcNow;
        var timeZone = TimeZoneInfo.Local;
        var prepared = new ScheduleInputService().Prepare(
            invocation.TimeExpression!,
            invocation.ScheduleKind,
            invocation.Action,
            invocation.KeepDaily,
            now,
            timeZone);

        var result = await scheduleCommands.SetAsync(prepared.Draft);
        if (invocation.Json)
        {
            WriteMachineSuccess(
                "schedule",
                result,
                GetMutationWarnings(result),
                result.IsFullyApplied);
        }
        else
        {
            Console.WriteLine($"Scheduled: {FormatSchedule(result.Mutation.Schedule)}");
            if (result.AutomaticDailySkip is not null)
            {
                Console.WriteLine(
                    $"Skipped the overlapping daily occurrence at {result.AutomaticDailySkip.Request.ExecuteDueAt.ToLocalTime():yyyy-MM-dd HH:mm}.");
            }

            WriteMutationWarnings(result.Mutation);
            WriteDailySkipWarnings(result.AutomaticDailySkip);
        }

        return result.IsFullyApplied ? 0 : 3;
    }

    private static int Preview(CliInvocation invocation)
    {
        var prepared = new ScheduleInputService().Prepare(
            invocation.TimeExpression!,
            invocation.ScheduleKind,
            invocation.Action,
            invocation.KeepDaily,
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Local);
        if (invocation.Json)
        {
            WriteMachineSuccess("preview", prepared);
        }
        else
        {
            var when = prepared.Draft.Kind == ScheduleKind.OneTime
                ? prepared.Draft.TargetAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : $"daily at {prepared.Draft.DailyAt:HH:mm}";
            Console.WriteLine($"Would schedule {prepared.Draft.Action} {when}. No state was changed.");
        }

        return 0;
    }

    private static async Task<int> CancelAsync(
        ScheduleCoordinator coordinator,
        IScheduleRepository repository,
        CliInvocation invocation,
        IReadOnlyList<int> reminderOffsets)
    {
        var schedules = await repository.ListAsync();
        var targets = schedules
            .Where(schedule => invocation.CancelAll || schedule.Kind == ScheduleKind.OneTime)
            .OrderBy(schedule => schedule.Kind)
            .ToArray();
        var results = new List<ScheduleMutationResult>();

        foreach (var target in targets)
        {
            results.Add(await coordinator.CancelAsync(target.Kind, reminderOffsets));
        }

        if (invocation.Json)
        {
            var warnings = results.SelectMany(GetMutationWarnings).ToArray();
            WriteMachineSuccess(
                "cancel",
                new { cancelled = results.Select(result => result.Schedule), results },
                warnings,
                results.All(result => result.IsFullyApplied));
        }
        else if (results.Count == 0)
        {
            Console.WriteLine("Nothing to cancel.");
        }
        else
        {
            Console.WriteLine(invocation.CancelAll ? "Cancelled all ShutdownAT schedules." : "Cancelled the one-time schedule.");
            foreach (var result in results)
            {
                WriteMutationWarnings(result);
            }
        }

        return results.All(result => result.IsFullyApplied) ? 0 : 3;
    }

    private static async Task<int> ShowHealthAsync(
        IScheduleRepository repository,
        ReconciliationReport reconciliation,
        LegacyMigrationResult legacyMigration,
        DatabaseRecoveryResult? startupRecovery,
        bool json)
    {
        var store = await repository.CheckHealthAsync();
        if (json)
        {
            var warnings = GetReconciliationWarnings(reconciliation).ToList();
            warnings.AddRange(GetLegacyMigrationWarnings(legacyMigration));
            warnings.AddRange(GetRecoveryWarnings(startupRecovery));
            if (!store.CanExecutePowerActions)
            {
                warnings.Add(new MachineWarning("StoreUnhealthy", store.Detail));
            }

            WriteMachineSuccess(
                "health",
                new { store, reconciliation, legacyMigration, startupRecovery },
                warnings,
                store.CanExecutePowerActions && reconciliation.IsHealthy &&
                legacyMigration.Status != LegacyMigrationStatus.Failed);
        }
        else
        {
            Console.WriteLine($"Database: {store.Status} — {store.Detail}");
            Console.WriteLine($"Task Scheduler projection: {(reconciliation.IsHealthy ? "Healthy" : "Degraded")}");
            WriteReconciliationWarning(reconciliation);
            WriteLegacyMigrationWarnings(legacyMigration);
            WriteRecoveryWarning(startupRecovery);
        }

        return store.CanExecutePowerActions && reconciliation.IsHealthy &&
               legacyMigration.Status != LegacyMigrationStatus.Failed ? 0 : 3;
    }

    private static async Task<int> SkipNextDailyAsync(DailySkipCoordinator coordinator, bool json)
    {
        var result = await coordinator.RequestNextAsync();
        if (json)
        {
            var warnings = result.BackupFailure is null
                ? Array.Empty<MachineWarning>()
                : [new MachineWarning("BackupFailed", result.BackupFailure)];
            WriteMachineSuccess("skip", result, warnings, result.IsFullyPersisted);
        }
        else
        {
            Console.WriteLine(
                $"Skipped the daily action due {result.Request.ExecuteDueAt.ToLocalTime():yyyy-MM-dd HH:mm}.");
            if (result.BackupFailure is not null)
            {
                Console.Error.WriteLine($"Warning: skip saved, but backup failed: {result.BackupFailure}");
            }
        }

        return result.IsFullyPersisted ? 0 : 3;
    }

    private static async Task<int> ShowLogsAsync(
        IDiagnosticLogReader diagnostics,
        string dataDirectory,
        bool json)
    {
        var events = await diagnostics.ReadRecentAsync();
        if (json)
        {
            WriteMachineSuccess("logs", new { dataDirectory, events });
            return 0;
        }

        Console.WriteLine($"ShutdownAT data: {dataDirectory}");
        if (events.Count == 0)
        {
            Console.WriteLine("No diagnostic events recorded yet.");
            return 0;
        }

        foreach (var entry in events)
        {
            Console.WriteLine(
                $"{entry.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} [{entry.Severity}] {entry.Message}");
        }

        return 0;
    }

    private static async Task<int> RunTuiAsync(SdatRuntime services)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return await ShowStatusAsync(
                services.Schedules,
                await services.Coordinator.ReconcileAsync((await services.Settings.LoadAsync()).ReminderOffsetsMinutes),
                services.LegacyMigration,
                services.StartupRecovery,
                json: false);
        }

        return await TerminalApp.RunAsync(services);
    }

    private static int WriteReconciliation(ReconciliationReport report, bool json)
    {
        if (json)
        {
            WriteMachineSuccess(
                "reconcile",
                report,
                GetReconciliationWarnings(report),
                report.IsHealthy);
        }
        else
        {
            Console.WriteLine(
                $"Reconciled {report.DesiredCount} task(s): {report.CreatedOrUpdatedCount} repaired, {report.RemovedCount} removed.");
            WriteReconciliationWarning(report);
        }

        return report.IsHealthy ? 0 : 3;
    }

    private static string FormatSchedule(ScheduleSnapshot schedule)
    {
        var when = schedule.Kind == ScheduleKind.OneTime
            ? schedule.TargetAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : $"daily at {schedule.DailyAt:HH:mm}";
        return $"{schedule.Kind}: {schedule.Action} {when} (revision {schedule.Revision})";
    }

    private static void WriteMutationWarnings(ScheduleMutationResult result)
    {
        if (result.BackupFailure is not null)
        {
            Console.Error.WriteLine($"Warning: schedule saved, but backup failed: {result.BackupFailure}");
        }

        WriteReconciliationWarning(result.Reconciliation);
    }

    private static void WriteReconciliationWarning(ReconciliationReport report)
    {
        foreach (var failure in report.Failures)
        {
            Console.Error.WriteLine($"Warning: {failure.Operation} {failure.TaskName}: {failure.Detail}");
        }
    }

    private static void WriteError(Exception exception, bool json, string operation)
    {
        if (json)
        {
            WriteMachineResponse<object>(
                operation,
                false,
                null,
                [],
                new MachineError(exception.GetType().Name, exception.Message));
        }
        else
        {
            Console.Error.WriteLine($"ShutdownAT error: {exception.Message}");
        }
    }

    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteMachineSuccess<T>(
        string operation,
        T result,
        IReadOnlyList<MachineWarning>? warnings = null,
        bool success = true) =>
        WriteJson(MachineResponse<T>.Succeeded(operation, result, warnings, success));

    private static void WriteMachineResponse<T>(
        string operation,
        bool success,
        T? result,
        IReadOnlyList<MachineWarning> warnings,
        MachineError? error) =>
        WriteJson(new MachineResponse<T>(
            MachineResponse<T>.CurrentSchemaVersion,
            operation,
            success,
            result,
            warnings,
            error));

    private static IReadOnlyList<MachineWarning> GetMutationWarnings(ScheduleMutationResult result)
    {
        var warnings = GetReconciliationWarnings(result.Reconciliation).ToList();
        if (result.BackupFailure is not null)
        {
            warnings.Add(new MachineWarning("BackupFailed", result.BackupFailure));
        }

        return warnings;
    }

    private static IReadOnlyList<MachineWarning> GetMutationWarnings(ScheduleCommandResult result)
    {
        var warnings = GetMutationWarnings(result.Mutation).ToList();
        if (result.AutomaticDailySkip?.BackupFailure is not null)
        {
            warnings.Add(new MachineWarning("DailySkipBackupFailed", result.AutomaticDailySkip.BackupFailure));
        }

        return warnings;
    }

    private static void WriteDailySkipWarnings(DailySkipResult? result)
    {
        if (result?.BackupFailure is not null)
        {
            Console.Error.WriteLine($"Warning: daily skip was saved, but its backup failed: {result.BackupFailure}");
        }
    }

    private static IReadOnlyList<MachineWarning> GetReconciliationWarnings(ReconciliationReport report) =>
        report.Failures
            .Select(failure => new MachineWarning(
                "SchedulerProjectionFailed",
                $"{failure.Operation} {failure.TaskName}: {failure.Detail}"))
            .ToArray();

    private static IReadOnlyList<MachineWarning> GetLegacyMigrationWarnings(LegacyMigrationResult migration) =>
        migration.Warnings
            .Select(warning => new MachineWarning("LegacyMigration", warning))
            .ToArray();

    private static IReadOnlyList<MachineWarning> GetRecoveryWarnings(DatabaseRecoveryResult? recovery) =>
        recovery is null
            ? []
            : [new MachineWarning("DatabaseRecovered", $"Restored verified backup: {recovery.RestoredBackupPath}")];

    private static void WriteLegacyMigrationWarnings(LegacyMigrationResult migration)
    {
        foreach (var warning in migration.Warnings)
        {
            Console.Error.WriteLine($"Warning: {warning}");
        }
    }

    private static void WriteRecoveryWarning(DatabaseRecoveryResult? recovery)
    {
        if (recovery is not null)
        {
            Console.Error.WriteLine($"Warning: restored the local database from {recovery.RestoredBackupPath}");
        }
    }

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    internal static void TryAbortWindowsCountdown()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = "/a",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(2000);
        }
        catch
        {
            // Cancellation of SDAT state still proceeds; there may be no active Windows countdown.
        }
    }

    private static void PrintHelp() => Console.WriteLine(
        """
        ShutdownAT (SDAT) — local Windows power scheduling

          sdat 36m                 schedule a one-time shutdown
          sdat preview --time 36m preview without changing state
          sdat schedule --time 36m --action shutdown
          sdat 23:41              schedule at a local clock time
          sdat daily 02:30        schedule a daily shutdown
          ssat 45m                schedule a one-time suspend
          sdat 01:30 -Restart     schedule a restart
          sdat status             show active schedules
          sdat                    open the TUI in an interactive terminal; otherwise show status
          sdat cancel [all]       cancel one-time or all schedules
          sdat skip               skip the next daily action once
          sdat logs               show recent diagnostic history
          sdat update             install the latest verified release
          sdat uninstall          remove SDAT (--keep-data preserves runtime data)
          sdat reconcile          repair Task Scheduler from SQLite
          sdat health             check database and scheduler state
          sdat tui                open the interactive terminal UI
          sdat ui                 open the ShutdownAT Windows app

        Options: -p/--daily, -k/--keep-daily, -Suspend, -Restart, --dry-run, --json
        Legacy aliases: -a (cancel one-time), -aa (cancel all), -s (skip daily once)
        """);

}
