using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Sdat.Core.Commands;
using Sdat.Core.Execution;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Core.TimeExpressions;
using Sdat.Windows.Concurrency;
using Sdat.Windows.Execution;
using Sdat.Windows.Notifications;
using Sdat.Windows.Persistence;
using Sdat.Windows.Scheduling;
using Spectre.Console;

return await SdatCli.RunAsync(args);

internal static class SdatCli
{
    private static readonly int[] DefaultReminderOffsets = [2];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        CliInvocation invocation;
        try
        {
            var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
            invocation = CliInvocationParser.Parse(
                args,
                suspendAlias: string.Equals(executableName, "ssat", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is CliUsageException or FormatException or OverflowException)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Use 'sdat help' for examples.");
            return 2;
        }

        if (invocation.Command == CliCommandType.Help)
        {
            PrintHelp();
            return 0;
        }

        if (invocation.Command == CliCommandType.Version)
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        if (invocation.Command == CliCommandType.Cancel)
        {
            TryAbortWindowsCountdown();
        }

        try
        {
            var services = CreateServices();
            var startup = await services.Coordinator.InitializeAndReconcileAsync(DefaultReminderOffsets);

            if (invocation.Command == CliCommandType.TaskRun)
            {
                var result = await services.TaskInvocationCoordinator.RunAsync(new TaskInvocation(
                    invocation.ScheduleId!.Value,
                    invocation.Revision!.Value,
                    invocation.TaskRole!.Value,
                    invocation.ReminderOffsetMinutes));
                if (invocation.Json)
                {
                    WriteJson(result);
                }
                else if (result.Outcome == TaskInvocationOutcome.Failed)
                {
                    Console.Error.WriteLine(result.Detail);
                }

                return result.Outcome == TaskInvocationOutcome.Failed ? 10 : 0;
            }

            return invocation.Command switch
            {
                CliCommandType.Status => await ShowStatusAsync(services.Repository, startup, invocation.Json),
                CliCommandType.Schedule => await ScheduleAsync(services.Coordinator, invocation),
                CliCommandType.Cancel => await CancelAsync(services.Coordinator, services.Repository, invocation),
                CliCommandType.Reconcile => WriteReconciliation(startup, invocation.Json),
                CliCommandType.Health => await ShowHealthAsync(services.Repository, startup, invocation.Json),
                CliCommandType.Tui => await RunTuiAsync(services),
                _ => 2,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WriteError(exception, invocation.Json);
            return 10;
        }
    }

    private static Services CreateServices()
    {
        var options = SqliteStoreOptions.CreateDefault();
        var repository = new SqliteScheduleRepository(options);
        var applicationPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the SDAT executable path.");
        if (Path.GetFileName(applicationPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Scheduling requires a published SDAT executable; 'dotnet run' is intentionally not registered in Task Scheduler.");
        }

        var projection = new WindowsTaskSchedulerProjection(applicationPath);
        var reconciler = new SchedulerReconciler(repository, projection, new ScheduleTaskPlanner());
        var backup = new SqliteBackupService(options);
        var operationLock = new FileOperationLock(options.OperationLockPath);
        var coordinator = new ScheduleCoordinator(
            repository,
            backup,
            reconciler,
            operationLock);
        var taskInvocationCoordinator = new TaskInvocationCoordinator(
            repository,
            new SqliteTaskExecutionLedger(options),
            new WindowsPowerActionExecutor(),
            new WindowsReminderNotifier(),
            new DurableExecutionFinalizer(backup, reconciler, DefaultReminderOffsets),
            operationLock);
        return new Services(repository, coordinator, taskInvocationCoordinator);
    }

    private static async Task<int> ShowStatusAsync(
        IScheduleRepository repository,
        ReconciliationReport reconciliation,
        bool json)
    {
        var schedules = await repository.ListAsync();
        if (json)
        {
            WriteJson(new { schedules, reconciliation });
            return reconciliation.IsHealthy ? 0 : 3;
        }

        if (schedules.Count == 0)
        {
            Console.WriteLine("No active SDAT schedules.");
        }
        else
        {
            foreach (var schedule in schedules)
            {
                Console.WriteLine(FormatSchedule(schedule));
            }
        }

        WriteReconciliationWarning(reconciliation);
        return reconciliation.IsHealthy ? 0 : 3;
    }

    private static async Task<int> ScheduleAsync(ScheduleCoordinator coordinator, CliInvocation invocation)
    {
        var expression = invocation.TimeExpression!;
        var now = DateTimeOffset.UtcNow;
        var timeZone = TimeZoneInfo.Local;
        var resolved = new TimeExpressionParser().Resolve(expression, now, timeZone);

        ScheduleDraft draft;
        if (invocation.ScheduleKind == ScheduleKind.Daily)
        {
            if (resolved.Kind != TimeExpressionKind.Absolute)
            {
                throw new CliUsageException("Daily schedules require a clock time such as 02:30.");
            }

            draft = ScheduleDraft.Daily(
                invocation.Action,
                TimeOnly.FromDateTime(resolved.Target.LocalDateTime),
                timeZone.Id);
        }
        else
        {
            draft = ScheduleDraft.OneTime(invocation.Action, resolved.Target, timeZone.Id, invocation.KeepDaily);
        }

        var result = await coordinator.SetAsync(draft, DefaultReminderOffsets);
        if (invocation.Json)
        {
            WriteJson(result);
        }
        else
        {
            Console.WriteLine($"Scheduled: {FormatSchedule(result.Schedule)}");
            WriteMutationWarnings(result);
        }

        return result.IsFullyApplied ? 0 : 3;
    }

    private static async Task<int> CancelAsync(
        ScheduleCoordinator coordinator,
        IScheduleRepository repository,
        CliInvocation invocation)
    {
        var schedules = await repository.ListAsync();
        var targets = schedules
            .Where(schedule => invocation.CancelAll || schedule.Kind == ScheduleKind.OneTime)
            .OrderBy(schedule => schedule.Kind)
            .ToArray();
        var results = new List<ScheduleMutationResult>();

        foreach (var target in targets)
        {
            results.Add(await coordinator.CancelAsync(target.Kind, DefaultReminderOffsets));
        }

        if (invocation.Json)
        {
            WriteJson(new { cancelled = results.Select(result => result.Schedule), results });
        }
        else if (results.Count == 0)
        {
            Console.WriteLine("Nothing to cancel.");
        }
        else
        {
            Console.WriteLine(invocation.CancelAll ? "Cancelled all SDAT schedules." : "Cancelled the one-time schedule.");
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
        bool json)
    {
        var store = await repository.CheckHealthAsync();
        if (json)
        {
            WriteJson(new { store, reconciliation });
        }
        else
        {
            Console.WriteLine($"Database: {store.Status} — {store.Detail}");
            Console.WriteLine($"Task Scheduler projection: {(reconciliation.IsHealthy ? "Healthy" : "Degraded")}");
            WriteReconciliationWarning(reconciliation);
        }

        return store.CanExecutePowerActions && reconciliation.IsHealthy ? 0 : 3;
    }

    private static async Task<int> RunTuiAsync(Services services)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return await ShowStatusAsync(
                services.Repository,
                await services.Coordinator.ReconcileAsync(DefaultReminderOffsets),
                json: false);
        }

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("SDAT").Color(Color.CornflowerBlue));
            await WriteTuiStatusAsync(services.Repository);
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]Choose an action[/]")
                    .PageSize(7)
                    .AddChoices(
                        "Schedule once",
                        "Schedule daily",
                        "Cancel one-time",
                        "Cancel all",
                        "Refresh",
                        "Exit"));

            if (action == "Exit")
            {
                AnsiConsole.Clear();
                return 0;
            }

            if (action == "Refresh")
            {
                await services.Coordinator.ReconcileAsync(DefaultReminderOffsets);
                continue;
            }

            try
            {
                if (action.StartsWith("Schedule", StringComparison.Ordinal))
                {
                    var powerAction = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Power action")
                            .AddChoices("Shutdown", "Suspend", "Restart"));
                    var expression = AnsiConsole.Ask<string>(
                        action == "Schedule daily"
                            ? "Clock time [grey](for example 02:30)[/]:"
                            : "When [grey](for example 36m or 23:41)[/]:");
                    var invocation = new CliInvocation(
                        CliCommandType.Schedule,
                        expression,
                        action == "Schedule daily" ? ScheduleKind.Daily : ScheduleKind.OneTime,
                        Enum.Parse<PowerActionType>(powerAction),
                        false,
                        false,
                        false,
                        null,
                        null,
                        null,
                        null);
                    var exitCode = await ScheduleAsync(services.Coordinator, invocation);
                    ShowTuiResult(exitCode == 0 ? "Schedule saved." : "Schedule saved with warnings.", exitCode == 0);
                }
                else
                {
                    var cancelAll = action == "Cancel all";
                    if (AnsiConsole.Confirm(
                            cancelAll ? "Cancel every active schedule?" : "Cancel the one-time schedule?",
                            defaultValue: false))
                    {
                        TryAbortWindowsCountdown();
                        var invocation = new CliInvocation(
                            CliCommandType.Cancel,
                            null,
                            ScheduleKind.OneTime,
                            PowerActionType.Shutdown,
                            cancelAll,
                            false,
                            false,
                            null,
                            null,
                            null,
                            null);
                        var exitCode = await CancelAsync(services.Coordinator, services.Repository, invocation);
                        ShowTuiResult(exitCode == 0 ? "Cancellation complete." : "Cancelled with warnings.", exitCode == 0);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[red]Could not complete the action:[/] {Markup.Escape(exception.Message)}");
                AnsiConsole.WriteLine("Press any key to continue.");
                Console.ReadKey(intercept: true);
            }
        }
    }

    private static async Task WriteTuiStatusAsync(IScheduleRepository repository)
    {
        var schedules = await repository.ListAsync();
        if (schedules.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No active schedules.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Slot")
            .AddColumn("Action")
            .AddColumn("When");
        foreach (var schedule in schedules)
        {
            table.AddRow(
                schedule.Kind == ScheduleKind.OneTime ? "Once" : "Daily",
                schedule.Action.ToString(),
                schedule.Kind == ScheduleKind.OneTime
                    ? schedule.TargetAt!.Value.ToLocalTime().ToString("ddd HH:mm")
                    : schedule.DailyAt!.Value.ToString("HH:mm"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void ShowTuiResult(string message, bool success)
    {
        AnsiConsole.MarkupLine(success
            ? $"[green]{Markup.Escape(message)}[/]"
            : $"[yellow]{Markup.Escape(message)}[/]");
        AnsiConsole.WriteLine("Press any key to continue.");
        Console.ReadKey(intercept: true);
    }

    private static int WriteReconciliation(ReconciliationReport report, bool json)
    {
        if (json)
        {
            WriteJson(report);
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

    private static void WriteError(Exception exception, bool json)
    {
        if (json)
        {
            WriteJson(new { error = exception.GetType().Name, message = exception.Message });
        }
        else
        {
            Console.Error.WriteLine($"SDAT error: {exception.Message}");
        }
    }

    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    private static void TryAbortWindowsCountdown()
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
        SDAT — local Windows power scheduling

          sdat 36m                 schedule a one-time shutdown
          sdat 23:41              schedule at a local clock time
          sdat daily 02:30        schedule a daily shutdown
          ssat 45m                schedule a one-time suspend
          sdat 01:30 -Restart     schedule a restart
          sdat status             show active schedules
          sdat cancel [all]       cancel one-time or all schedules
          sdat reconcile          repair Task Scheduler from SQLite
          sdat health             check database and scheduler state
          sdat tui                open the interactive terminal UI

        Options: -p/--daily, -k/--keep-daily, -Suspend, -Restart, --json
        Legacy aliases: -a (cancel one-time), -aa (cancel all)
        """);

    private sealed record Services(
        IScheduleRepository Repository,
        ScheduleCoordinator Coordinator,
        TaskInvocationCoordinator TaskInvocationCoordinator);
}
