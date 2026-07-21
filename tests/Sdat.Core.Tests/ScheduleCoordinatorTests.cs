using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Core.Storage;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class ScheduleCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Set_creates_backs_up_and_projects_the_schedule()
    {
        var fixture = new Fixture();

        var result = await fixture.Coordinator.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddMinutes(36), "UTC"),
            [2]);

        Assert.True(result.IsFullyApplied);
        Assert.Equal("backup.db", result.BackupReference);
        Assert.Equal(2, fixture.Projection.Tasks.Count);
        Assert.Single(await fixture.Repository.ListAsync());
    }

    [Fact]
    public async Task Set_replaces_the_existing_schedule_of_the_same_kind()
    {
        var fixture = new Fixture();
        var first = await fixture.Coordinator.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddMinutes(36), "UTC"),
            [2]);

        var second = await fixture.Coordinator.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Restart, Now.AddMinutes(50), "UTC"),
            [2]);

        Assert.Equal(first.Schedule.Id, second.Schedule.Id);
        Assert.Equal(2, second.Schedule.Revision);
        Assert.Equal(PowerActionType.Restart, second.Schedule.Action);
        Assert.Single(await fixture.Repository.ListAsync());
    }

    [Fact]
    public async Task Backup_failure_is_reported_without_discarding_authoritative_state()
    {
        var fixture = new Fixture { Backup = { Failure = new IOException("disk unavailable") } };

        var result = await fixture.Coordinator.SetAsync(
            ScheduleDraft.Daily(PowerActionType.Suspend, new TimeOnly(2, 30), "UTC"),
            [2]);

        Assert.False(result.IsFullyApplied);
        Assert.Equal("disk unavailable", result.BackupFailure);
        Assert.Single(await fixture.Repository.ListAsync());
        Assert.True(result.Reconciliation.IsHealthy);
    }

    [Fact]
    public async Task Unhealthy_store_blocks_mutation_and_projection()
    {
        var fixture = new Fixture();
        fixture.Repository.Health = new StoreHealth(StoreHealthStatus.Corrupt, "integrity check failed");

        var exception = await Assert.ThrowsAsync<ScheduleStoreUnavailableException>(() =>
            fixture.Coordinator.SetAsync(
                ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddMinutes(10), "UTC"),
                [2]));

        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Projection.Tasks);
    }

    [Fact]
    public async Task Exact_cancel_rejects_a_superseded_notification_revision()
    {
        var fixture = new Fixture();
        var first = await fixture.Coordinator.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddMinutes(10), "UTC"),
            [2]);
        await fixture.Coordinator.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Restart, Now.AddMinutes(20), "UTC"),
            [2]);

        await Assert.ThrowsAsync<ScheduleConflictException>(() =>
            fixture.Coordinator.CancelExactAsync(first.Schedule.Id, first.Schedule.Revision, [2]));

        Assert.Single(await fixture.Repository.ListAsync());
    }

    [Fact]
    public async Task Exact_update_rejects_a_superseded_overlay_revision()
    {
        var fixture = new Fixture();
        var first = await fixture.Coordinator.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddMinutes(10), "UTC"),
            [2]);
        await fixture.Coordinator.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddMinutes(20), "UTC"),
            [2]);

        await Assert.ThrowsAsync<ScheduleConflictException>(() =>
            fixture.Coordinator.UpdateExactAsync(
                first.Schedule.Id,
                first.Schedule.Revision,
                ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddMinutes(30), "UTC"),
                [2]));
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Coordinator = new ScheduleCoordinator(
                Repository,
                Backup,
                new SchedulerReconciler(Repository, Projection, new ScheduleTaskPlanner()),
                new NoOpLock(),
                new FixedTimeProvider(Now));
        }

        public FakeRepository Repository { get; } = new();

        public FakeBackup Backup { get; } = new();

        public FakeProjection Projection { get; } = new();

        public ScheduleCoordinator Coordinator { get; }
    }

    private sealed class FakeRepository : IScheduleRepository
    {
        private ScheduleSnapshot? _schedule;

        public StoreHealth Health { get; set; } = new(StoreHealthStatus.Healthy, "ok");

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ScheduleSnapshot> CreateAsync(
            ScheduleDraft draft,
            CancellationToken cancellationToken = default)
        {
            _schedule = ToSnapshot(Guid.NewGuid(), 1, draft);
            return Task.FromResult(_schedule);
        }

        public Task<ScheduleSnapshot> UpdateAsync(
            Guid id,
            long expectedRevision,
            ScheduleDraft draft,
            CancellationToken cancellationToken = default)
        {
            if (_schedule is null || _schedule.Id != id || _schedule.Revision != expectedRevision ||
                _schedule.Status != ScheduleStatus.Active)
            {
                throw new ScheduleConflictException("Schedule changed before it could be updated.");
            }

            _schedule = ToSnapshot(id, expectedRevision + 1, draft);
            return Task.FromResult(_schedule);
        }

        public Task<ScheduleSnapshot> CancelAsync(
            Guid id,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (_schedule is null || _schedule.Id != id || _schedule.Revision != expectedRevision ||
                _schedule.Status != ScheduleStatus.Active)
            {
                throw new ScheduleConflictException("Schedule changed before it could be cancelled.");
            }

            _schedule = _schedule! with
            {
                Revision = expectedRevision + 1,
                Status = ScheduleStatus.Cancelled,
                UpdatedAt = Now,
            };
            return Task.FromResult(_schedule);
        }

        public Task<ScheduleSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_schedule?.Id == id ? _schedule : null);

        public Task<IReadOnlyList<ScheduleSnapshot>> ListAsync(
            bool includeInactive = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduleSnapshot>>(
                _schedule is not null && (includeInactive || _schedule.Status == ScheduleStatus.Active)
                    ? [_schedule]
                    : []);

        public Task<StoreHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Health);

        private static ScheduleSnapshot ToSnapshot(Guid id, long revision, ScheduleDraft draft) => new(
            id,
            revision,
            draft.Kind,
            draft.Action,
            draft.TargetAt,
            draft.DailyAt,
            draft.TimeZoneId,
            draft.KeepDaily,
            ScheduleStatus.Active,
            Now,
            Now);
    }

    private sealed class FakeBackup : IStateBackup
    {
        public Exception? Failure { get; set; }

        public Task<string> CreateVerifiedBackupAsync(CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult("backup.db") : Task.FromException<string>(Failure);
    }

    private sealed class FakeProjection : ITaskSchedulerProjection
    {
        public Dictionary<string, SchedulerTaskSnapshot> Tasks { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<SchedulerTaskSnapshot>> ListOwnedAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchedulerTaskSnapshot>>([.. Tasks.Values]);

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

    private sealed class NoOpLock : IOperationLock
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
