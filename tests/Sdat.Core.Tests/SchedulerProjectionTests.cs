using Sdat.Core.Scheduling;
using Sdat.Core.Storage;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class SchedulerProjectionTests
{
    [Fact]
    public void One_time_plan_contains_execution_and_future_reminders()
    {
        var now = new DateTimeOffset(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);
        var schedule = CreateOneTime(now.AddMinutes(20));

        var tasks = new ScheduleTaskPlanner().Plan(schedule, [10, 2, 30], now);

        Assert.Equal(3, tasks.Count);
        Assert.Contains(tasks, task => task.TaskName == "SDAT_Volatile");
        Assert.Contains(tasks, task => task.TaskName == "SDAT_Volatile_Reminder_0010");
        Assert.Contains(tasks, task => task.TaskName == "SDAT_Volatile_Reminder_0002");
        Assert.DoesNotContain(tasks, task => task.TaskName.EndsWith("0030", StringComparison.Ordinal));
    }

    [Fact]
    public void Daily_reminder_wraps_to_the_previous_day()
    {
        var schedule = new ScheduleSnapshot(
            Guid.NewGuid(),
            4,
            ScheduleKind.Daily,
            PowerActionType.Shutdown,
            null,
            new TimeOnly(0, 1),
            "UTC",
            false,
            ScheduleStatus.Active,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var tasks = new ScheduleTaskPlanner().Plan(schedule, [2], DateTimeOffset.UtcNow);

        var reminder = Assert.Single(tasks, task => task.Role == SchedulerTaskRole.Reminder);
        Assert.Equal(new TimeOnly(23, 59), reminder.DailyAt);
        Assert.Equal(SchedulerTriggerKind.Daily, reminder.TriggerKind);
    }

    [Fact]
    public async Task Reconcile_repairs_drift_and_removes_obsolete_tasks()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = CreateOneTime(now.AddMinutes(20));
        var repository = new FakeRepository(schedule);
        var projection = new FakeProjection(
            new SchedulerTaskSnapshot("SDAT_Volatile", "wrong"),
            new SchedulerTaskSnapshot("SDAT_Volatile_Reminder_0999", "obsolete"));
        var reconciler = new SchedulerReconciler(repository, projection, new ScheduleTaskPlanner());

        var report = await reconciler.ReconcileAsync([2], now);

        Assert.True(report.IsHealthy);
        Assert.Equal(2, report.CreatedOrUpdatedCount);
        Assert.Equal(1, report.RemovedCount);
        Assert.Contains("SDAT_Volatile", projection.Upserted);
        Assert.Contains("SDAT_Volatile_Reminder_0002", projection.Upserted);
        Assert.Contains("SDAT_Volatile_Reminder_0999", projection.Removed);
    }

    [Fact]
    public async Task Reconcile_keeps_obsolete_tasks_when_repair_fails()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = CreateOneTime(now.AddMinutes(20));
        var repository = new FakeRepository(schedule);
        var projection = new FakeProjection(new SchedulerTaskSnapshot("SDAT_Obsolete", "old"))
        {
            FailUpsert = true,
        };
        var reconciler = new SchedulerReconciler(repository, projection, new ScheduleTaskPlanner());

        var report = await reconciler.ReconcileAsync([2], now);

        Assert.False(report.IsHealthy);
        Assert.Empty(projection.Removed);
    }

    private static ScheduleSnapshot CreateOneTime(DateTimeOffset target) => new(
        Guid.NewGuid(),
        3,
        ScheduleKind.OneTime,
        PowerActionType.Shutdown,
        target,
        null,
        "UTC",
        false,
        ScheduleStatus.Active,
        target.AddHours(-1),
        target.AddHours(-1));

    private sealed class FakeProjection(params SchedulerTaskSnapshot[] tasks) : ITaskSchedulerProjection
    {
        private readonly List<SchedulerTaskSnapshot> _tasks = [.. tasks];

        public bool FailUpsert { get; init; }

        public List<string> Upserted { get; } = [];

        public List<string> Removed { get; } = [];

        public Task<IReadOnlyList<SchedulerTaskSnapshot>> ListOwnedAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchedulerTaskSnapshot>>(_tasks);

        public Task UpsertAsync(
            SchedulerTaskDefinition definition,
            CancellationToken cancellationToken = default)
        {
            if (FailUpsert)
            {
                throw new InvalidOperationException("simulated repair failure");
            }

            Upserted.Add(definition.TaskName);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string taskName, CancellationToken cancellationToken = default)
        {
            Removed.Add(taskName);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRepository(params ScheduleSnapshot[] schedules) : IScheduleRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ScheduleSnapshot> CreateAsync(
            ScheduleDraft draft,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ScheduleSnapshot> UpdateAsync(
            Guid id,
            long expectedRevision,
            ScheduleDraft draft,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ScheduleSnapshot> CancelAsync(
            Guid id,
            long expectedRevision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ScheduleSnapshot?> GetAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(schedules.SingleOrDefault(schedule => schedule.Id == id));

        public Task<IReadOnlyList<ScheduleSnapshot>> ListAsync(
            bool includeInactive = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduleSnapshot>>(schedules);

        public Task<StoreHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoreHealth(StoreHealthStatus.Healthy, "ok"));
    }
}
