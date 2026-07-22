using Sdat.Core.Diagnostics;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Windows.Hosting;
using Sdat.Windows.Migration;
using Spectre.Console;

internal sealed class TerminalApp
{
    private readonly SdatRuntime _services;
    private ReconciliationReport _reconciliation;
    private TerminalNotice? _notice;

    private TerminalApp(SdatRuntime services)
    {
        _services = services;
        _reconciliation = services.StartupReconciliation;
    }

    public static Task<int> RunAsync(SdatRuntime services) =>
        new TerminalApp(services).RunCoreAsync();

    private async Task<int> RunCoreAsync()
    {
        try
        {
            while (true)
            {
                ClearAndWriteHeader("Overview");
                await WriteOverviewAsync();

                var action = AnsiConsole.Prompt(
                    new SelectionPrompt<MainAction>()
                        .Title("[grey]What would you like to do?[/]")
                        .PageSize(7)
                        .UseConverter(MainActionLabel)
                        .AddChoices(Enum.GetValues<MainAction>()));

                switch (action)
                {
                    case MainAction.Schedule:
                        await ScheduleAsync();
                        break;
                    case MainAction.Manage:
                        await ManageSchedulesAsync();
                        break;
                    case MainAction.Diagnostics:
                        await ShowDiagnosticsAsync();
                        break;
                    case MainAction.OpenWindowsApp:
                        SdatCli.LaunchGraphicalCompanion();
                        return 0;
                    case MainAction.Refresh:
                        await RefreshAndRepairAsync();
                        break;
                    case MainAction.Exit:
                        return 0;
                    default:
                        throw new InvalidOperationException("Unknown terminal action.");
                }
            }
        }
        finally
        {
            AnsiConsole.Clear();
        }
    }

    private async Task WriteOverviewAsync()
    {
        var schedules = await _services.Schedules.ListAsync();
        var health = await _services.Schedules.CheckHealthAsync();

        WriteNotice();

        var summary = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn(string.Empty).NoWrap())
            .AddColumn(new TableColumn(string.Empty));
        summary.AddRow("[grey]Database[/]", StatusMarkup(health.Status.ToString(), health.CanExecutePowerActions));
        summary.AddRow(
            "[grey]Task Scheduler[/]",
            StatusMarkup(_reconciliation.IsHealthy ? "Healthy" : "Needs attention", _reconciliation.IsHealthy));
        summary.AddRow("[grey]Active schedules[/]", schedules.Count.ToString());
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        WriteSchedules(schedules);
        AnsiConsole.MarkupLine("[grey]Tip: enter a time directly for the fastest path, for example `sdat 36m`.[/]");
        AnsiConsole.WriteLine();
    }

    private async Task ScheduleAsync()
    {
        ClearAndWriteHeader("New schedule");
        var kindChoice = AnsiConsole.Prompt(
            new SelectionPrompt<ScheduleKindChoice>()
                .Title("[grey]How often?[/]")
                .UseConverter(ScheduleKindLabel)
                .AddChoices(Enum.GetValues<ScheduleKindChoice>()));
        if (kindChoice == ScheduleKindChoice.Back)
        {
            return;
        }

        var kind = kindChoice == ScheduleKindChoice.Daily ? ScheduleKind.Daily : ScheduleKind.OneTime;
        var action = AnsiConsole.Prompt(
            new SelectionPrompt<PowerActionType>()
                .Title("[grey]Power action[/]")
                .UseConverter(PowerActionLabel)
                .AddChoices(PowerActionType.Shutdown, PowerActionType.Suspend, PowerActionType.Restart));
        var expression = AnsiConsole.Prompt(
            new TextPrompt<string>(kind == ScheduleKind.Daily
                ? "Clock time [grey](for example 02:30)[/]:"
                : "When [grey](for example 36m or 23:41)[/]:"));

        var keepDaily = false;
        if (kind == ScheduleKind.OneTime &&
            (await _services.Schedules.ListAsync()).Any(schedule => schedule.Kind == ScheduleKind.Daily))
        {
            keepDaily = AnsiConsole.Confirm(
                "Keep the daily occurrence if it overlaps this one-time schedule?",
                defaultValue: false);
        }

        try
        {
            var prepared = new ScheduleInputService().Prepare(
                expression,
                kind,
                action,
                keepDaily,
                DateTimeOffset.UtcNow,
                TimeZoneInfo.Local);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[grey]Preview[/]").LeftJustified());
            AnsiConsole.MarkupLine($"  {Markup.Escape(FormatDraft(prepared.Draft))}");
            if (!AnsiConsole.Confirm("Save this schedule?", defaultValue: true))
            {
                _notice = TerminalNotice.Information("No changes were made.");
                return;
            }

            var result = await _services.ScheduleCommands.SetAsync(prepared.Draft);
            var message = $"Saved {FormatSchedule(result.Mutation.Schedule)}.";
            if (result.AutomaticDailySkip is not null)
            {
                message += $" The overlapping daily occurrence at " +
                           $"{result.AutomaticDailySkip.Request.ExecuteDueAt.ToLocalTime():yyyy-MM-dd HH:mm} was skipped.";
            }

            _reconciliation = result.Mutation.Reconciliation;
            _notice = result.IsFullyApplied
                ? TerminalNotice.Success(message)
                : TerminalNotice.Warning(message + " Review Diagnostics for recovery warnings.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notice = TerminalNotice.Error(exception.Message);
        }
    }

    private async Task ManageSchedulesAsync()
    {
        while (true)
        {
            ClearAndWriteHeader("Active schedules");
            var schedules = await _services.Schedules.ListAsync();
            WriteNotice();
            WriteSchedules(schedules);

            var choices = new List<ManageAction>();
            if (schedules.Any(schedule => schedule.Kind == ScheduleKind.OneTime))
            {
                choices.Add(ManageAction.CancelOneTime);
            }

            if (schedules.Any(schedule => schedule.Kind == ScheduleKind.Daily))
            {
                choices.Add(ManageAction.SkipDaily);
                choices.Add(ManageAction.CancelDaily);
            }

            if (schedules.Count > 1)
            {
                choices.Add(ManageAction.CancelAll);
            }

            choices.Add(ManageAction.Back);
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<ManageAction>()
                    .Title("[grey]Schedule actions[/]")
                    .PageSize(7)
                    .UseConverter(ManageActionLabel)
                    .AddChoices(choices));

            switch (action)
            {
                case ManageAction.CancelOneTime:
                    await CancelAsync([ScheduleKind.OneTime], "Cancel the one-time schedule?");
                    break;
                case ManageAction.SkipDaily:
                    await SkipNextDailyAsync();
                    break;
                case ManageAction.CancelDaily:
                    await CancelAsync([ScheduleKind.Daily], "Cancel the daily schedule?");
                    break;
                case ManageAction.CancelAll:
                    await CancelAsync(
                        schedules.Select(schedule => schedule.Kind).Distinct().ToArray(),
                        "Cancel every active schedule?");
                    break;
                case ManageAction.Back:
                    return;
                default:
                    throw new InvalidOperationException("Unknown schedule action.");
            }
        }
    }

    private async Task CancelAsync(IReadOnlyList<ScheduleKind> kinds, string prompt)
    {
        if (!AnsiConsole.Confirm(prompt, defaultValue: false))
        {
            _notice = TerminalNotice.Information("No changes were made.");
            return;
        }

        try
        {
            if (kinds.Contains(ScheduleKind.OneTime))
            {
                SdatCli.TryAbortWindowsCountdown();
            }

            var offsets = (await _services.Settings.LoadAsync()).ReminderOffsetsMinutes;
            var results = new List<ScheduleMutationResult>();
            foreach (var kind in kinds)
            {
                results.Add(await _services.Coordinator.CancelAsync(kind, offsets));
            }

            _reconciliation = results[^1].Reconciliation;
            _notice = results.All(result => result.IsFullyApplied)
                ? TerminalNotice.Success(kinds.Count > 1 ? "All schedules were cancelled." : "Schedule cancelled.")
                : TerminalNotice.Warning("Schedule state changed, but Windows task repair needs attention.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notice = TerminalNotice.Error(exception.Message);
        }
    }

    private async Task SkipNextDailyAsync()
    {
        try
        {
            var result = await _services.DailySkips.RequestNextAsync();
            var message = $"Skipped the daily occurrence due {result.Request.ExecuteDueAt.ToLocalTime():yyyy-MM-dd HH:mm}.";
            _notice = result.IsFullyPersisted
                ? TerminalNotice.Success(message)
                : TerminalNotice.Warning(message + " The backup could not be refreshed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notice = TerminalNotice.Error(exception.Message);
        }
    }

    private async Task ShowDiagnosticsAsync()
    {
        while (true)
        {
            ClearAndWriteHeader("Diagnostics");
            WriteNotice();
            await WriteHealthAsync();
            await WriteRecentActivityAsync();

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<DiagnosticsAction>()
                    .Title("[grey]Diagnostics actions[/]")
                    .UseConverter(DiagnosticsActionLabel)
                    .AddChoices(Enum.GetValues<DiagnosticsAction>()));
            if (action == DiagnosticsAction.Back)
            {
                return;
            }

            if (action == DiagnosticsAction.Refresh)
            {
                _notice = TerminalNotice.Information("Diagnostics refreshed.");
                continue;
            }

            try
            {
                var offsets = (await _services.Settings.LoadAsync()).ReminderOffsetsMinutes;
                _reconciliation = await _services.Coordinator.ReconcileAsync(offsets);
                _notice = _reconciliation.IsHealthy
                    ? TerminalNotice.Success(
                        $"Task Scheduler reconciled: {_reconciliation.CreatedOrUpdatedCount} repaired, " +
                        $"{_reconciliation.RemovedCount} removed.")
                    : TerminalNotice.Warning("Task Scheduler reconciliation completed with failures.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _notice = TerminalNotice.Error(exception.Message);
            }
        }
    }

    private async Task RefreshAndRepairAsync()
    {
        try
        {
            var offsets = (await _services.Settings.LoadAsync()).ReminderOffsetsMinutes;
            _reconciliation = await _services.Coordinator.ReconcileAsync(offsets);
            _notice = _reconciliation.IsHealthy
                ? TerminalNotice.Success("Status refreshed and the Task Scheduler projection is healthy.")
                : TerminalNotice.Warning("Status refreshed, but Task Scheduler repair reported failures.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notice = TerminalNotice.Error(exception.Message);
        }
    }

    private async Task WriteHealthAsync()
    {
        var store = await _services.Schedules.CheckHealthAsync();
        var health = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Area")
            .AddColumn("Status")
            .AddColumn("Detail");
        health.AddRow(
            "SQLite",
            StatusMarkup(store.Status.ToString(), store.CanExecutePowerActions),
            Markup.Escape(store.Detail));
        health.AddRow(
            "Task Scheduler",
            StatusMarkup(_reconciliation.IsHealthy ? "Healthy" : "Degraded", _reconciliation.IsHealthy),
            Markup.Escape(
                $"{_reconciliation.DesiredCount} desired; {_reconciliation.Failures.Count} failure(s)"));
        health.AddRow(
            "Legacy migration",
            StatusMarkup(
                _services.LegacyMigration.Status.ToString(),
                _services.LegacyMigration.Status != LegacyMigrationStatus.Failed),
            Markup.Escape($"{_services.LegacyMigration.ImportedScheduleCount} imported"));
        health.AddRow(
            "Startup recovery",
            _services.StartupRecovery is null ? "[grey]Not needed[/]" : "[yellow]Recovered[/]",
            Markup.Escape(_services.StartupRecovery?.RestoredBackupPath ?? "Primary database opened normally"));
        AnsiConsole.Write(health);
        AnsiConsole.MarkupLine($"[grey]Database: {Markup.Escape(_services.StoreOptions.DatabasePath)}[/]");
        AnsiConsole.WriteLine();
    }

    private async Task WriteRecentActivityAsync()
    {
        var events = await _services.Diagnostics.ReadRecentAsync(limit: 8);
        AnsiConsole.Write(new Rule("[grey]Recent activity[/]").LeftJustified());
        if (events.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No diagnostic events recorded yet.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var activity = new Table()
            .Border(TableBorder.None)
            .AddColumn(new TableColumn("When").NoWrap())
            .AddColumn("Level")
            .AddColumn("Source")
            .AddColumn("Message");
        foreach (var entry in events)
        {
            activity.AddRow(
                entry.OccurredAt.ToLocalTime().ToString("MM-dd HH:mm"),
                SeverityMarkup(entry.Severity),
                Markup.Escape(entry.Source),
                Markup.Escape(entry.Message));
        }

        AnsiConsole.Write(activity);
        AnsiConsole.WriteLine();
    }

    private static void WriteSchedules(IReadOnlyList<ScheduleSnapshot> schedules)
    {
        AnsiConsole.Write(new Rule("[grey]Schedules[/]").LeftJustified());
        if (schedules.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No active schedules.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Type")
            .AddColumn("Action")
            .AddColumn("When");
        foreach (var schedule in schedules.OrderBy(schedule => schedule.Kind))
        {
            table.AddRow(
                schedule.Kind == ScheduleKind.OneTime ? "Once" : "Daily",
                PowerActionLabel(schedule.Action),
                Markup.Escape(FormatWhen(schedule)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void ClearAndWriteHeader(string section)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(
            new Rule($"[bold deepskyblue1]ShutdownAT[/] [grey]· {Markup.Escape(section)}[/]")
                .LeftJustified());
        AnsiConsole.MarkupLine("[grey]Local Windows power scheduling · CLI command: sdat[/]");
        AnsiConsole.WriteLine();
    }

    private void WriteNotice()
    {
        if (_notice is null)
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"[{_notice.Color}]{Markup.Escape(_notice.Label)}:[/] {Markup.Escape(_notice.Message)}");
        AnsiConsole.WriteLine();
        _notice = null;
    }

    private static string FormatDraft(ScheduleDraft draft) => draft.Kind == ScheduleKind.OneTime
        ? $"{PowerActionLabel(draft.Action)} once at {draft.TargetAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
        : $"{PowerActionLabel(draft.Action)} daily at {draft.DailyAt:HH:mm}";

    private static string FormatSchedule(ScheduleSnapshot schedule) =>
        $"{PowerActionLabel(schedule.Action)} {FormatWhen(schedule)}";

    private static string FormatWhen(ScheduleSnapshot schedule) => schedule.Kind == ScheduleKind.OneTime
        ? $"once at {schedule.TargetAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
        : $"daily at {schedule.DailyAt:HH:mm}";

    private static string StatusMarkup(string value, bool healthy) =>
        healthy ? $"[green]{Markup.Escape(value)}[/]" : $"[yellow]{Markup.Escape(value)}[/]";

    private static string SeverityMarkup(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Information => "[blue]Info[/]",
        DiagnosticSeverity.Warning => "[yellow]Warning[/]",
        DiagnosticSeverity.Error => "[red]Error[/]",
        _ => Markup.Escape(severity.ToString()),
    };

    private static string PowerActionLabel(PowerActionType action) => action switch
    {
        PowerActionType.Shutdown => "Shut down",
        PowerActionType.Suspend => "Suspend",
        PowerActionType.Restart => "Restart",
        _ => action.ToString(),
    };

    private static string MainActionLabel(MainAction action) => action switch
    {
        MainAction.Schedule => "Schedule a power action",
        MainAction.Manage => "Manage active schedules",
        MainAction.Diagnostics => "Diagnostics and health",
        MainAction.OpenWindowsApp => "Open the ShutdownAT Windows app",
        MainAction.Refresh => "Refresh and repair status",
        MainAction.Exit => "Exit",
        _ => action.ToString(),
    };

    private static string ScheduleKindLabel(ScheduleKindChoice choice) => choice switch
    {
        ScheduleKindChoice.OneTime => "One-time",
        ScheduleKindChoice.Daily => "Daily",
        ScheduleKindChoice.Back => "Back",
        _ => choice.ToString(),
    };

    private static string ManageActionLabel(ManageAction action) => action switch
    {
        ManageAction.CancelOneTime => "Cancel the one-time schedule",
        ManageAction.SkipDaily => "Skip the next daily occurrence",
        ManageAction.CancelDaily => "Cancel the daily schedule",
        ManageAction.CancelAll => "Cancel all schedules",
        ManageAction.Back => "Back",
        _ => action.ToString(),
    };

    private static string DiagnosticsActionLabel(DiagnosticsAction action) => action switch
    {
        DiagnosticsAction.Reconcile => "Repair Task Scheduler from SQLite",
        DiagnosticsAction.Refresh => "Refresh",
        DiagnosticsAction.Back => "Back",
        _ => action.ToString(),
    };

    private enum MainAction
    {
        Schedule,
        Manage,
        Diagnostics,
        OpenWindowsApp,
        Refresh,
        Exit,
    }

    private enum ScheduleKindChoice
    {
        OneTime,
        Daily,
        Back,
    }

    private enum ManageAction
    {
        CancelOneTime,
        SkipDaily,
        CancelDaily,
        CancelAll,
        Back,
    }

    private enum DiagnosticsAction
    {
        Reconcile,
        Refresh,
        Back,
    }

    private sealed record TerminalNotice(string Label, string Color, string Message)
    {
        public static TerminalNotice Success(string message) => new("Success", "green", message);

        public static TerminalNotice Information(string message) => new("Info", "blue", message);

        public static TerminalNotice Warning(string message) => new("Warning", "yellow", message);

        public static TerminalNotice Error(string message) => new("Error", "red", message);
    }
}
