using Sdat.Core.Scheduling;

namespace Sdat.Core.Commands;

public enum CliCommandType
{
    Status,
    Schedule,
    Preview,
    Cancel,
    Skip,
    Logs,
    Reconcile,
    Health,
    Help,
    Version,
    Tui,
    TaskRun,
}

public sealed record CliInvocation(
    CliCommandType Command,
    string? TimeExpression,
    ScheduleKind ScheduleKind,
    PowerActionType Action,
    bool CancelAll,
    bool KeepDaily,
    bool Json,
    Guid? ScheduleId,
    long? Revision,
    SchedulerTaskRole? TaskRole,
    int? ReminderOffsetMinutes);

public sealed class CliUsageException(string message) : ArgumentException(message);

public static class CliInvocationParser
{
    public static CliInvocation Parse(IReadOnlyList<string> args, bool suspendAlias = false)
    {
        var action = suspendAlias ? PowerActionType.Suspend : PowerActionType.Shutdown;
        var kind = ScheduleKind.OneTime;
        var keepDaily = false;
        var json = false;
        var dryRun = false;
        string? explicitTime = null;
        Guid? scheduleId = null;
        long? revision = null;
        SchedulerTaskRole? taskRole = null;
        int? reminderOffset = null;
        var positional = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            switch (token.ToLowerInvariant())
            {
                case "-p" or "--daily":
                    kind = ScheduleKind.Daily;
                    break;
                case "-k" or "-keepdaily" or "--keep-daily":
                    keepDaily = true;
                    break;
                case "-suspend" or "--suspend":
                    action = PowerActionType.Suspend;
                    break;
                case "-restart" or "--restart":
                    action = PowerActionType.Restart;
                    break;
                case "--shutdown":
                    action = PowerActionType.Shutdown;
                    break;
                case "--action":
                    if (!Enum.TryParse<PowerActionType>(ReadValue(args, ref index, token), true, out var parsedAction) ||
                        !Enum.IsDefined(parsedAction))
                    {
                        throw new CliUsageException("Action must be shutdown, suspend, or restart.");
                    }

                    action = parsedAction;
                    break;
                case "--time":
                    explicitTime = ReadValue(args, ref index, token);
                    break;
                case "-dryrun" or "--dry-run":
                    dryRun = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "-h" or "--help":
                    positional.Add("help");
                    break;
                case "-t" or "-tui" or "--tui":
                    positional.Add("tui");
                    break;
                case "-a":
                    positional.Add("cancel");
                    break;
                case "-aa":
                    positional.Add("cancel");
                    positional.Add("all");
                    break;
                case "-s":
                    positional.Add("skip");
                    break;
                case "--task-run":
                    positional.Add("--task-run");
                    break;
                case "--schedule-id":
                    scheduleId = Guid.Parse(ReadValue(args, ref index, token));
                    break;
                case "--revision":
                    revision = long.Parse(ReadValue(args, ref index, token), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--role":
                    if (!Enum.TryParse<SchedulerTaskRole>(ReadValue(args, ref index, token), true, out var parsedRole))
                    {
                        throw new CliUsageException("Task role must be execute or reminder.");
                    }

                    taskRole = parsedRole;
                    break;
                case "--reminder-offset":
                    reminderOffset = int.Parse(
                        ReadValue(args, ref index, token),
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    if (token.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new CliUsageException($"Unknown option: {token}");
                    }

                    positional.Add(token);
                    break;
            }
        }

        if (positional.Count == 0)
        {
            return explicitTime is null
                ? Create(CliCommandType.Status)
                : Create(dryRun ? CliCommandType.Preview : CliCommandType.Schedule, explicitTime);
        }

        var command = positional[0].ToLowerInvariant();
        return command switch
        {
            "status" => RequireCount(CliCommandType.Status, 1),
            "help" or "-h" or "--help" => RequireCount(CliCommandType.Help, 1),
            "version" or "--version" => RequireCount(CliCommandType.Version, 1),
            "t" or "tui" => RequireCount(CliCommandType.Tui, 1),
            "reconcile" => RequireCount(CliCommandType.Reconcile, 1),
            "health" => RequireCount(CliCommandType.Health, 1),
            "skip" => RequireCount(CliCommandType.Skip, 1),
            "logs" => RequireCount(CliCommandType.Logs, 1),
            "preview" when positional.Count == 1 && explicitTime is not null =>
                Create(CliCommandType.Preview, explicitTime),
            "preview" when positional.Count == 2 && explicitTime is null =>
                Create(CliCommandType.Preview, positional[1]),
            "preview" => throw new CliUsageException("Preview needs a time, for example: sdat preview --time 36m"),
            "schedule" when positional.Count == 1 && explicitTime is not null =>
                Create(dryRun ? CliCommandType.Preview : CliCommandType.Schedule, explicitTime),
            "schedule" when positional.Count == 2 && explicitTime is null =>
                Create(dryRun ? CliCommandType.Preview : CliCommandType.Schedule, positional[1]),
            "schedule" => throw new CliUsageException("Schedule needs a time, for example: sdat schedule --time 36m"),
            "daily" when positional.Count == 2 => Create(
                CliCommandType.Schedule,
                positional[1],
                ScheduleKind.Daily),
            "daily" => throw new CliUsageException("Daily needs one clock time, for example: sdat daily 02:30"),
            "cancel" when positional.Count is 1 or 2 &&
                          (positional.Count == 1 || positional[1].Equals("all", StringComparison.OrdinalIgnoreCase)) =>
                Create(CliCommandType.Cancel, cancelAll: positional.Count == 2),
            "cancel" => throw new CliUsageException("Use 'sdat cancel' or 'sdat cancel all'."),
            "--task-run" when positional.Count == 1 && scheduleId is not null && revision is not null &&
                              taskRole is not null =>
                Create(CliCommandType.TaskRun),
            "--task-run" => throw new CliUsageException("Incomplete internal task invocation."),
            _ when positional.Count == 1 =>
                Create(dryRun ? CliCommandType.Preview : CliCommandType.Schedule, positional[0], kind),
            _ => throw new CliUsageException("Too many arguments. Use 'sdat help' for examples."),
        };

        CliInvocation RequireCount(CliCommandType type, int count) => positional.Count == count
            ? Create(type)
            : throw new CliUsageException($"{command} does not accept additional arguments.");

        CliInvocation Create(
            CliCommandType type,
            string? timeExpression = null,
            ScheduleKind? scheduleKind = null,
            bool? cancelAll = null) => new(
            type,
            timeExpression,
            scheduleKind ?? kind,
            action,
            cancelAll ?? false,
            keepDaily,
            json,
            scheduleId,
            revision,
            taskRole,
            reminderOffset);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        index++;
        if (index >= args.Count)
        {
            throw new CliUsageException($"Missing value for {option}.");
        }

        return args[index];
    }
}
