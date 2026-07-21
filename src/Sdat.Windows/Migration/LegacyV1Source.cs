using System.Text.Json;
using Microsoft.Win32.TaskScheduler;
using Sdat.Core.Scheduling;
using AsyncTask = System.Threading.Tasks.Task;
using Task = System.Threading.Tasks.Task;

namespace Sdat.Windows.Migration;

public sealed record LegacyTaskSnapshot(
    string Name,
    ScheduleKind Kind,
    DateTimeOffset? RunAt,
    TimeOnly? DailyAt,
    PowerActionType? Action,
    bool IsVerifiedLegacy);

public interface ILegacyTaskReader
{
    Task<LegacyTaskSnapshot?> ReadAsync(string taskName, CancellationToken cancellationToken = default);

    Task RemoveAsync(string taskName, CancellationToken cancellationToken = default);
}

public sealed record LegacyV1Plan(
    bool SourceFound,
    bool IsValid,
    string SourcePath,
    IReadOnlyList<ScheduleDraft> Schedules,
    bool SkipNextDaily,
    IReadOnlyList<string> ObsoleteTaskNames,
    IReadOnlyList<string> Warnings);

public sealed class LegacyV1Source(
    string legacyRoot,
    ILegacyTaskReader taskReader,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<LegacyV1Plan> ReadAsync(CancellationToken cancellationToken = default)
    {
        var statePath = Path.Combine(Path.GetFullPath(legacyRoot), "data", "state.json");
        if (!File.Exists(statePath))
        {
            return new LegacyV1Plan(false, true, statePath, [], false, [], []);
        }

        LegacyState state;
        try
        {
            await using var stream = File.OpenRead(statePath);
            state = await JsonSerializer.DeserializeAsync<LegacyState>(
                        stream,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                        cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new InvalidDataException("The v1 state file is empty.");
            if (state.Version != 1)
            {
                throw new InvalidDataException($"Unsupported v1 state version: {state.Version}.");
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new LegacyV1Plan(true, false, statePath, [], false, [], [$"Legacy state was preserved but not imported: {exception.Message}"]);
        }

        var schedules = new List<ScheduleDraft>();
        var obsolete = new List<string>();
        var warnings = new List<string>();
        var isValid = true;
        var timeZone = TimeZoneInfo.Local;
        var oneTime = await taskReader.ReadAsync("SDAT_Volatile", cancellationToken).ConfigureAwait(false);
        if (oneTime is not null)
        {
            if (!oneTime.IsVerifiedLegacy)
            {
                isValid = false;
                warnings.Add("Task SDAT_Volatile was not recognized as an SDAT v1 task and was left untouched.");
            }
            else
            {
                var action = ParseAction(state.Volatile?.ActionType) ?? oneTime.Action;
                if (action is null)
                {
                    isValid = false;
                    warnings.Add("Task SDAT_Volatile has no trustworthy power action and was left untouched.");
                }
                else if (oneTime.RunAt is not null && oneTime.RunAt > _timeProvider.GetUtcNow())
                {
                    schedules.Add(ScheduleDraft.OneTime(action.Value, oneTime.RunAt.Value, timeZone.Id));
                }
                else
                {
                    obsolete.Add(oneTime.Name);
                }
            }
        }

        var daily = await taskReader.ReadAsync("SDAT_Permanent", cancellationToken).ConfigureAwait(false);
        if (daily is not null)
        {
            if (!daily.IsVerifiedLegacy)
            {
                isValid = false;
                warnings.Add("Task SDAT_Permanent was not recognized as an SDAT v1 task and was left untouched.");
            }
            else
            {
                var action = ParseAction(state.Permanent?.ActionType) ?? daily.Action;
                if (action is null || daily.DailyAt is null)
                {
                    isValid = false;
                    warnings.Add("Task SDAT_Permanent has incomplete legacy metadata and was left untouched.");
                }
                else
                {
                    schedules.Add(ScheduleDraft.Daily(action.Value, daily.DailyAt.Value, timeZone.Id));
                }
            }
        }

        var skipNextDaily = schedules.Any(schedule => schedule.Kind == ScheduleKind.Daily) &&
                            DateTimeOffset.TryParse(state.SuspendPermanentUntil, out var suspendedUntil) &&
                            suspendedUntil > _timeProvider.GetUtcNow();
        return new LegacyV1Plan(true, isValid, statePath, schedules, skipNextDaily, obsolete, warnings);
    }

    public async Task RemoveObsoleteTasksAsync(
        LegacyV1Plan plan,
        CancellationToken cancellationToken = default)
    {
        foreach (var taskName in plan.ObsoleteTaskNames)
        {
            await taskReader.RemoveAsync(taskName, cancellationToken).ConfigureAwait(false);
        }
    }

    private static PowerActionType? ParseAction(string? value) =>
        Enum.TryParse<PowerActionType>(value, true, out var action) && Enum.IsDefined(action)
            ? action
            : null;

    private sealed record LegacyState(
        int Version,
        LegacySlot? Volatile,
        LegacySlot? Permanent,
        string? SuspendPermanentUntil = null);

    private sealed record LegacySlot(string? ActionType);
}

public sealed class WindowsLegacyTaskReader : ILegacyTaskReader
{
    public Task<LegacyTaskSnapshot?> ReadAsync(
        string taskName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = new TaskService();
        using var task = service.GetTask(taskName);
        if (task is null)
        {
            return AsyncTask.FromResult<LegacyTaskSnapshot?>(null);
        }

        var definition = task.Definition;
        var action = definition.Actions.Count == 1 ? definition.Actions[0] as ExecAction : null;
        var command = action is null ? string.Empty : $"{action.Path} {action.Arguments}";
        var isVerified = command.Contains("RunHidden.vbs", StringComparison.OrdinalIgnoreCase) &&
                         command.Contains("shutdownat.ps1", StringComparison.OrdinalIgnoreCase);
        PowerActionType? powerAction = command.Contains("-Restart", StringComparison.OrdinalIgnoreCase)
            ? PowerActionType.Restart
            : command.Contains("-Suspend", StringComparison.OrdinalIgnoreCase)
                ? PowerActionType.Suspend
                : isVerified
                    ? PowerActionType.Shutdown
                    : null;
        var trigger = definition.Triggers.Count == 1 ? definition.Triggers[0] : null;
        var kind = trigger is DailyTrigger ? ScheduleKind.Daily : ScheduleKind.OneTime;
        DateTimeOffset? runAt = trigger is TimeTrigger
            ? ResolveLocal(trigger.StartBoundary)
            : null;
        TimeOnly? dailyAt = trigger is DailyTrigger
            ? TimeOnly.FromDateTime(trigger.StartBoundary)
            : null;
        return AsyncTask.FromResult<LegacyTaskSnapshot?>(new LegacyTaskSnapshot(
            taskName,
            kind,
            runAt,
            dailyAt,
            powerAction,
            isVerified));
    }

    public Task RemoveAsync(string taskName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = new TaskService();
        service.RootFolder.DeleteTask(taskName, exceptionOnNotExists: false);
        return AsyncTask.CompletedTask;
    }

    private static DateTimeOffset ResolveLocal(DateTime value)
    {
        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        var timeZone = TimeZoneInfo.Local;
        if (timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
