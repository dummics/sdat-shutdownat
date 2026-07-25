using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Windows.Concurrency;
using Sdat.Windows.Migration;
using Sdat.Windows.Persistence;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class LegacyV1MigrationServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-v1-migration-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Imports_once_then_uses_durable_completion_marker()
    {
        var legacyRoot = Path.Combine(_root, "legacy-v1");
        Directory.CreateDirectory(Path.Combine(legacyRoot, "data"));
        File.WriteAllText(
            Path.Combine(legacyRoot, "data", "state.json"),
            """
            {
              "Version": 1,
              "Volatile": { "ActionType": "restart" },
              "Permanent": { "ActionType": "shutdown" }
            }
            """);
        var taskReader = new FakeTaskReader(
            new LegacyTaskSnapshot(
                "SDAT_Volatile",
                ScheduleKind.OneTime,
                Now.AddHours(1),
                null,
                PowerActionType.Restart,
                true),
            new LegacyTaskSnapshot(
                "SDAT_Permanent",
                ScheduleKind.Daily,
                null,
                new TimeOnly(2, 0),
                PowerActionType.Shutdown,
                true));
        var options = new SqliteStoreOptions
        {
            DatabasePath = Path.Combine(_root, "data", "sdat.db"),
            BackupDirectory = Path.Combine(_root, "backups"),
        };
        var repository = new SqliteScheduleRepository(options, new FixedTimeProvider(Now));
        await repository.InitializeAsync();
        var projection = new FakeProjection();
        var backup = new SqliteBackupService(options, new FixedTimeProvider(Now));
        var operationLock = new FileOperationLock(options.OperationLockPath);
        var coordinator = new ScheduleCoordinator(
            repository,
            backup,
            new SchedulerReconciler(repository, projection, new ScheduleTaskPlanner()),
            operationLock,
            new FixedTimeProvider(Now));
        var service = new LegacyV1MigrationService(
            new LegacyV1Source(legacyRoot, taskReader, new FixedTimeProvider(Now)),
            new SqliteLegacyImportJournal(options, new FixedTimeProvider(Now)),
            coordinator,
            new DailySkipCoordinator(
                repository,
                new SqliteDailySkipStore(options, new FixedTimeProvider(Now)),
                backup,
                operationLock,
                new FixedTimeProvider(Now)),
            [2]);

        var first = await service.MigrateAsync();
        var second = await service.MigrateAsync();
        var schedules = await repository.ListAsync();

        Assert.Equal(LegacyMigrationStatus.Imported, first.Status);
        Assert.Equal(2, first.ImportedScheduleCount);
        Assert.Equal(LegacyMigrationStatus.AlreadyCompleted, second.Status);
        Assert.Equal(2, schedules.Count);
        Assert.Contains(schedules, schedule =>
            schedule.Kind == ScheduleKind.OneTime && schedule.Action == PowerActionType.Restart);
        Assert.Contains(schedules, schedule =>
            schedule.Kind == ScheduleKind.Daily && schedule.DailyAt == new TimeOnly(2, 0));
        Assert.NotEmpty(projection.Tasks);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeTaskReader(params LegacyTaskSnapshot[] tasks) : ILegacyTaskReader
    {
        public Task<LegacyTaskSnapshot?> ReadAsync(
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LegacyTaskSnapshot?>(tasks.SingleOrDefault(task => task.Name == taskName));

        public Task RemoveAsync(string taskName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeProjection : ITaskSchedulerProjection
    {
        public Dictionary<string, SchedulerTaskSnapshot> Tasks { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<SchedulerTaskSnapshot>> ListOwnedAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchedulerTaskSnapshot>>(Tasks.Values.ToArray());

        public Task UpsertAsync(
            SchedulerTaskDefinition definition,
            CancellationToken cancellationToken = default)
        {
            Tasks[definition.TaskName] = new SchedulerTaskSnapshot(definition.TaskName, definition.Fingerprint);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string taskName, CancellationToken cancellationToken = default)
        {
            Tasks.Remove(taskName);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
