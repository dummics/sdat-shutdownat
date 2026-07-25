using System.Text.Json;
using Microsoft.Win32.TaskScheduler;
using Sdat.Core.Scheduling;
using Sdat.Windows.Migration;
using AsyncTask = System.Threading.Tasks.Task;
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

namespace Sdat.Windows.Scheduling;

public sealed class WindowsTaskSchedulerProjection : ITaskSchedulerProjection
{
    private const string RegistrationSource = "SDAT";
    private readonly string _applicationPath;
    private readonly string _workingDirectory;
    private readonly string _taskPrefix;

    public WindowsTaskSchedulerProjection(
        string applicationPath,
        string taskPrefix = "SDAT_")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskPrefix);

        if (!Path.IsPathFullyQualified(applicationPath))
        {
            throw new ArgumentException("The scheduled application path must be absolute.", nameof(applicationPath));
        }

        _applicationPath = Path.GetFullPath(applicationPath);
        _workingDirectory = Path.GetDirectoryName(_applicationPath)!;
        _taskPrefix = taskPrefix;
    }

    public System.Threading.Tasks.Task<IReadOnlyList<SchedulerTaskSnapshot>> ListOwnedAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = new TaskService();
        var snapshots = service.RootFolder.Tasks
            .Where(task => task.Name.StartsWith(_taskPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(task => string.Equals(
                task.Definition.RegistrationInfo.Source,
                RegistrationSource,
                StringComparison.Ordinal))
            .Select(CreateSnapshot)
            .ToArray();

        return AsyncTask.FromResult<IReadOnlyList<SchedulerTaskSnapshot>>(snapshots);
    }

    public AsyncTask UpsertAsync(
        SchedulerTaskDefinition definition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwnedName(definition.TaskName);

        using var service = new TaskService();
        using var existing = service.GetTask(definition.TaskName);
        if (existing is not null &&
            !string.Equals(
                existing.Definition.RegistrationInfo.Source,
                RegistrationSource,
                StringComparison.Ordinal) &&
            !IsVerifiedLegacy(existing))
        {
            throw new InvalidOperationException(
                $"Task '{definition.TaskName}' already exists and is not owned by SDAT. It was left untouched.");
        }

        var task = service.NewTask();
        task.RegistrationInfo.Source = RegistrationSource;
        task.RegistrationInfo.Description = "SDAT managed power schedule. Manual edits are repaired from local state.";
        task.RegistrationInfo.Documentation = JsonSerializer.Serialize(CreateManifest(definition));
        task.Principal.LogonType = TaskLogonType.InteractiveToken;
        task.Principal.RunLevel = TaskRunLevel.LUA;
        task.Settings.AllowDemandStart = true;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.StartWhenAvailable = true;
        task.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        task.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(5);

        task.Actions.Add(new ExecAction(_applicationPath, definition.Arguments, _workingDirectory));
        task.Triggers.Add(CreateTrigger(definition));

        service.RootFolder.RegisterTaskDefinition(
            definition.TaskName,
            task,
            TaskCreation.CreateOrUpdate,
            null,
            null,
            TaskLogonType.InteractiveToken);

        return AsyncTask.CompletedTask;
    }

    public AsyncTask RemoveAsync(string taskName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwnedName(taskName);

        using var service = new TaskService();
        using var existing = service.GetTask(taskName);
        if (existing is null)
        {
            return AsyncTask.CompletedTask;
        }

        if (!string.Equals(
                existing.Definition.RegistrationInfo.Source,
                RegistrationSource,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Task '{taskName}' is not owned by SDAT. It was left untouched.");
        }

        service.RootFolder.DeleteTask(taskName, exceptionOnNotExists: false);
        return AsyncTask.CompletedTask;
    }

    private SchedulerTaskSnapshot CreateSnapshot(ScheduledTask task)
    {
        var manifest = ParseManifest(task.Definition.RegistrationInfo.Documentation);
        var isValid = manifest is not null && DefinitionMatchesManifest(task, manifest);
        return new SchedulerTaskSnapshot(task.Name, isValid ? manifest!.Fingerprint : string.Empty);
    }

    private static bool IsVerifiedLegacy(ScheduledTask task) =>
        task.Definition.Actions.Count == 1 &&
        task.Definition.Actions[0] is ExecAction action &&
        LegacyTaskSignature.IsVerified(task.Name, action.Path, action.Arguments);

    private bool DefinitionMatchesManifest(ScheduledTask task, TaskManifest manifest)
    {
        if (!string.Equals(manifest.ApplicationPath, _applicationPath, StringComparison.OrdinalIgnoreCase) ||
            task.Definition.Actions.Count != 1 ||
            task.Definition.Actions[0] is not ExecAction action ||
            !string.Equals(action.Path, manifest.ApplicationPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(action.Arguments, manifest.Arguments, StringComparison.Ordinal) ||
            !string.Equals(action.WorkingDirectory, manifest.WorkingDirectory, StringComparison.OrdinalIgnoreCase) ||
            task.Definition.Triggers.Count != 1 ||
            !DefinitionMatchesRequiredSettings(
                task.Enabled,
                task.Definition.Principal.LogonType,
                task.Definition.Principal.RunLevel,
                task.Definition.Settings.AllowDemandStart,
                task.Definition.Settings.DisallowStartIfOnBatteries,
                task.Definition.Settings.StopIfGoingOnBatteries,
                task.Definition.Settings.StartWhenAvailable,
                task.Definition.Settings.MultipleInstances,
                task.Definition.Settings.ExecutionTimeLimit))
        {
            return false;
        }

        var trigger = task.Definition.Triggers[0];
        return manifest.TriggerKind switch
        {
            SchedulerTriggerKind.Once =>
                trigger is TimeTrigger &&
                manifest.RunAtLocal is not null &&
                AreClose(trigger.StartBoundary, manifest.RunAtLocal.Value),
            SchedulerTriggerKind.Daily =>
                trigger is DailyTrigger daily &&
                daily.DaysInterval == 1 &&
                manifest.DailyAt is not null &&
                TimeOnly.FromDateTime(trigger.StartBoundary) == manifest.DailyAt.Value,
            _ => false,
        };
    }

    private static Trigger CreateTrigger(SchedulerTaskDefinition definition) => definition.TriggerKind switch
    {
        SchedulerTriggerKind.Once => new TimeTrigger(definition.RunAt!.Value.LocalDateTime),
        SchedulerTriggerKind.Daily => new DailyTrigger
        {
            StartBoundary = DateTime.Today.Add(definition.DailyAt!.Value.ToTimeSpan()),
            DaysInterval = 1,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(definition)),
    };

    private TaskManifest CreateManifest(SchedulerTaskDefinition definition) => new(
        definition.Fingerprint,
        _applicationPath,
        _workingDirectory,
        definition.Arguments,
        definition.TriggerKind,
        definition.RunAt?.LocalDateTime,
        definition.DailyAt);

    private static TaskManifest? ParseManifest(string? documentation)
    {
        if (string.IsNullOrWhiteSpace(documentation))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TaskManifest>(documentation);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool AreClose(DateTime left, DateTime right) =>
        Math.Abs((left - right).TotalSeconds) < 1;

    internal static bool DefinitionMatchesRequiredSettings(
        bool enabled,
        TaskLogonType logonType,
        TaskRunLevel runLevel,
        bool allowDemandStart,
        bool disallowStartIfOnBatteries,
        bool stopIfGoingOnBatteries,
        bool startWhenAvailable,
        TaskInstancesPolicy multipleInstances,
        TimeSpan executionTimeLimit) =>
        enabled &&
        logonType == TaskLogonType.InteractiveToken &&
        runLevel == TaskRunLevel.LUA &&
        allowDemandStart &&
        !disallowStartIfOnBatteries &&
        !stopIfGoingOnBatteries &&
        startWhenAvailable &&
        multipleInstances == TaskInstancesPolicy.IgnoreNew &&
        executionTimeLimit == TimeSpan.FromMinutes(5);

    private void EnsureOwnedName(string taskName)
    {
        if (!taskName.StartsWith(_taskPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Task '{taskName}' is outside the SDAT-owned namespace.");
        }
    }

    private sealed record TaskManifest(
        string Fingerprint,
        string ApplicationPath,
        string WorkingDirectory,
        string Arguments,
        SchedulerTriggerKind TriggerKind,
        DateTime? RunAtLocal,
        TimeOnly? DailyAt);
}
