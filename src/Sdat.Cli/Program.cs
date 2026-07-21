using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Sdat.Core.Commands;
using Sdat.Core.Execution;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Windows.Hosting;
using Spectre.Console;

return await SdatCli.RunAsync(args);

internal static class SdatCli
{
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
                CliCommandType.Status => await ShowStatusAsync(services.Schedules, startup, invocation.Json),
                CliCommandType.Schedule => await ScheduleAsync(services.Coordinator, invocation, reminderOffsets),
                CliCommandType.Cancel => await CancelAsync(services.Coordinator, services.Schedules, invocation, reminderOffsets),
                CliCommandType.Skip => await SkipNextDailyAsync(services.DailySkips, invocation.Json),
                CliCommandType.Reconcile => WriteReconciliation(startup, invocation.Json),
                CliCommandType.Health => await ShowHealthAsync(services.Schedules, startup, invocation.Json),
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

    private static async Task<int> ScheduleAsync(
        ScheduleCoordinator coordinator,
        CliInvocation invocation,
        IReadOnlyList<int> reminderOffsets)
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

        var result = await coordinator.SetAsync(prepared.Draft, reminderOffsets);
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

    private static async Task<int> SkipNextDailyAsync(DailySkipCoordinator coordinator, bool json)
    {
        var result = await coordinator.RequestNextAsync();
        if (json)
        {
            WriteJson(result);
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

    private static async Task<int> RunTuiAsync(SdatRuntime services)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return await ShowStatusAsync(
                services.Schedules,
                await services.Coordinator.ReconcileAsync((await services.Settings.LoadAsync()).ReminderOffsetsMinutes),
                json: false);
        }

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("SDAT").Color(Color.CornflowerBlue));
            await WriteTuiStatusAsync(services.Schedules);
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
                await services.Coordinator.ReconcileAsync((await services.Settings.LoadAsync()).ReminderOffsetsMinutes);
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
                    var offsets = (await services.Settings.LoadAsync()).ReminderOffsetsMinutes;
                    var exitCode = await ScheduleAsync(services.Coordinator, invocation, offsets);
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
                        var offsets = (await services.Settings.LoadAsync()).ReminderOffsetsMinutes;
                        var exitCode = await CancelAsync(services.Coordinator, services.Schedules, invocation, offsets);
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
          sdat skip               skip the next daily action once
          sdat reconcile          repair Task Scheduler from SQLite
          sdat health             check database and scheduler state
          sdat tui                open the interactive terminal UI

        Options: -p/--daily, -k/--keep-daily, -Suspend, -Restart, --json
        Legacy aliases: -a (cancel one-time), -aa (cancel all)
        """);

}
