using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sdat.Core.Scheduling;

public enum SchedulerTaskRole
{
    Execute,
    Reminder,
}

public enum SchedulerTriggerKind
{
    Once,
    Daily,
}

public sealed record SchedulerTaskDefinition(
    string TaskName,
    SchedulerTaskRole Role,
    SchedulerTriggerKind TriggerKind,
    Guid ScheduleId,
    long ScheduleRevision,
    DateTimeOffset? RunAt,
    TimeOnly? DailyAt,
    string TimeZoneId,
    int? ReminderOffsetMinutes,
    string Arguments,
    string Fingerprint);

public sealed record SchedulerTaskSnapshot(string TaskName, string Fingerprint);

public interface ITaskSchedulerProjection
{
    Task<IReadOnlyList<SchedulerTaskSnapshot>> ListOwnedAsync(
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        SchedulerTaskDefinition definition,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string taskName, CancellationToken cancellationToken = default);
}

public sealed record ReconciliationFailure(string TaskName, string Operation, string Detail);

public sealed record ReconciliationReport(
    int DesiredCount,
    int CreatedOrUpdatedCount,
    int RemovedCount,
    IReadOnlyList<ReconciliationFailure> Failures,
    bool SuppressedByTestMode = false)
{
    public bool IsHealthy => Failures.Count == 0;

    public static ReconciliationReport TestModeSuppressed { get; } =
        new(0, 0, 0, [], true);
}

public sealed class ScheduleTaskPlanner
{
    public IReadOnlyList<SchedulerTaskDefinition> Plan(
        ScheduleSnapshot schedule,
        IEnumerable<int> reminderOffsetsMinutes,
        DateTimeOffset now)
    {
        if (schedule.Status != ScheduleStatus.Active)
        {
            return [];
        }

        var offsets = reminderOffsetsMinutes
            .Distinct()
            .OrderDescending()
            .ToArray();

        if (offsets.Length > 5 || offsets.Any(offset => offset is < 1 or > 1440))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminderOffsetsMinutes),
                "Reminder offsets must contain at most five unique values between 1 and 1440 minutes.");
        }

        var definitions = new List<SchedulerTaskDefinition>
        {
            CreateExecutionDefinition(schedule),
        };

        foreach (var offset in offsets)
        {
            var reminder = CreateReminderDefinition(schedule, offset, now);
            if (reminder is not null)
            {
                definitions.Add(reminder);
            }
        }

        return definitions;
    }

    private static SchedulerTaskDefinition CreateExecutionDefinition(ScheduleSnapshot schedule)
    {
        var baseName = GetBaseTaskName(schedule.Kind);
        return CreateDefinition(
            schedule,
            baseName,
            SchedulerTaskRole.Execute,
            schedule.Kind == ScheduleKind.OneTime ? SchedulerTriggerKind.Once : SchedulerTriggerKind.Daily,
            schedule.TargetAt,
            schedule.DailyAt,
            null);
    }

    private static SchedulerTaskDefinition? CreateReminderDefinition(
        ScheduleSnapshot schedule,
        int offsetMinutes,
        DateTimeOffset now)
    {
        DateTimeOffset? runAt = null;
        TimeOnly? dailyAt = null;

        if (schedule.Kind == ScheduleKind.OneTime)
        {
            runAt = schedule.TargetAt!.Value.AddMinutes(-offsetMinutes);
            if (runAt <= now)
            {
                return null;
            }
        }
        else
        {
            var reminderSpan = schedule.DailyAt!.Value.ToTimeSpan() - TimeSpan.FromMinutes(offsetMinutes);
            while (reminderSpan < TimeSpan.Zero)
            {
                reminderSpan += TimeSpan.FromDays(1);
            }

            dailyAt = TimeOnly.FromTimeSpan(reminderSpan);
        }

        return CreateDefinition(
            schedule,
            $"{GetBaseTaskName(schedule.Kind)}_Reminder_{offsetMinutes:0000}",
            SchedulerTaskRole.Reminder,
            schedule.Kind == ScheduleKind.OneTime ? SchedulerTriggerKind.Once : SchedulerTriggerKind.Daily,
            runAt,
            dailyAt,
            offsetMinutes);
    }

    private static SchedulerTaskDefinition CreateDefinition(
        ScheduleSnapshot schedule,
        string taskName,
        SchedulerTaskRole role,
        SchedulerTriggerKind triggerKind,
        DateTimeOffset? runAt,
        TimeOnly? dailyAt,
        int? reminderOffsetMinutes)
    {
        var arguments = FormattableString.Invariant(
            $"--task-run --role {role.ToString().ToLowerInvariant()} --schedule-id {schedule.Id:D} --revision {schedule.Revision}");
        if (reminderOffsetMinutes is not null)
        {
            arguments += FormattableString.Invariant($" --reminder-offset {reminderOffsetMinutes.Value}");
        }

        var canonical = string.Join(
            '|',
            taskName,
            role,
            triggerKind,
            schedule.Id.ToString("D"),
            schedule.Revision.ToString(CultureInfo.InvariantCulture),
            runAt?.ToUniversalTime().ToString("O") ?? string.Empty,
            dailyAt?.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) ?? string.Empty,
            schedule.TimeZoneId,
            reminderOffsetMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            arguments);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        return new SchedulerTaskDefinition(
            taskName,
            role,
            triggerKind,
            schedule.Id,
            schedule.Revision,
            runAt,
            dailyAt,
            schedule.TimeZoneId,
            reminderOffsetMinutes,
            arguments,
            fingerprint);
    }

    private static string GetBaseTaskName(ScheduleKind kind) =>
        kind == ScheduleKind.OneTime ? "SDAT_Volatile" : "SDAT_Permanent";
}

public sealed class SchedulerReconciler(
    IScheduleRepository repository,
    ITaskSchedulerProjection projection,
    ScheduleTaskPlanner planner)
{
    public async Task<ReconciliationReport> ReconcileAsync(
        IReadOnlyList<int> reminderOffsetsMinutes,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var schedules = await repository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var desired = schedules
            .SelectMany(schedule => planner.Plan(schedule, reminderOffsetsMinutes, now))
            .ToDictionary(task => task.TaskName, StringComparer.OrdinalIgnoreCase);
        var actual = (await projection.ListOwnedAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(task => task.TaskName, StringComparer.OrdinalIgnoreCase);

        var repaired = 0;
        var removed = 0;
        var failures = new List<ReconciliationFailure>();

        foreach (var definition in desired.Values.OrderBy(task => task.TaskName, StringComparer.OrdinalIgnoreCase))
        {
            if (actual.TryGetValue(definition.TaskName, out var existing) &&
                string.Equals(existing.Fingerprint, definition.Fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                await projection.UpsertAsync(definition, cancellationToken).ConfigureAwait(false);
                repaired++;
            }
            catch (Exception exception)
            {
                failures.Add(new ReconciliationFailure(definition.TaskName, "Upsert", exception.Message));
            }
        }

        if (failures.Count == 0)
        {
            foreach (var obsolete in actual.Keys
                         .Where(taskName => !desired.ContainsKey(taskName))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    await projection.RemoveAsync(obsolete, cancellationToken).ConfigureAwait(false);
                    removed++;
                }
                catch (Exception exception)
                {
                    failures.Add(new ReconciliationFailure(obsolete, "Remove", exception.Message));
                }
            }
        }

        return new ReconciliationReport(desired.Count, repaired, removed, failures);
    }
}
