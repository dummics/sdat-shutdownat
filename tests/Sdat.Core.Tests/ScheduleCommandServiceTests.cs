using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
using Sdat.Core.Storage;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class ScheduleCommandServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task One_time_schedule_skips_a_daily_occurrence_inside_the_overlap_window()
    {
        var fixture = new Fixture(new AppSettings { DailyOverlapWindowMinutes = 120 });
        await fixture.Service.SetAsync(ScheduleDraft.Daily(PowerActionType.Shutdown, new TimeOnly(22, 0), "UTC"));

        var result = await fixture.Service.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddHours(1), "UTC"));

        var skip = Assert.Single(fixture.SkipStore.Requests);
        Assert.Equal(Now.AddHours(2), skip.ExecuteDueAt);
        Assert.Same(skip, result.AutomaticDailySkip!.Request);
        Assert.True(result.IsFullyApplied);
        Assert.Equal(2, fixture.OperationLock.AcquisitionCount);
    }

    [Fact]
    public async Task Keep_daily_prevents_the_automatic_skip()
    {
        var fixture = new Fixture(new AppSettings { DailyOverlapWindowMinutes = 120 });
        await fixture.Service.SetAsync(ScheduleDraft.Daily(PowerActionType.Shutdown, new TimeOnly(22, 0), "UTC"));

        var result = await fixture.Service.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddHours(1), "UTC", keepDaily: true));

        Assert.Empty(fixture.SkipStore.Requests);
        Assert.Null(result.AutomaticDailySkip);
    }

    [Fact]
    public async Task One_time_schedule_outside_the_overlap_window_does_not_skip_daily()
    {
        var fixture = new Fixture(new AppSettings { DailyOverlapWindowMinutes = 30 });
        await fixture.Service.SetAsync(ScheduleDraft.Daily(PowerActionType.Shutdown, new TimeOnly(22, 0), "UTC"));

        var result = await fixture.Service.SetAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddHours(1), "UTC"));

        Assert.Empty(fixture.SkipStore.Requests);
        Assert.Null(result.AutomaticDailySkip);
    }

    private sealed class Fixture
    {
        public Fixture(AppSettings settings)
        {
            OperationLock = new CountingLock();
            var backup = new FakeBackup();
            var coordinator = new ScheduleCoordinator(
                Repository,
                backup,
                new SchedulerReconciler(Repository, new FakeProjection(), new ScheduleTaskPlanner()),
                OperationLock,
                new FixedTimeProvider(Now));
            var dailySkips = new DailySkipCoordinator(
                Repository,
                SkipStore,
                backup,
                OperationLock,
                new FixedTimeProvider(Now));
            Service = new ScheduleCommandService(
                coordinator,
                Repository,
                dailySkips,
                new FakeSettingsRepository(settings),
                OperationLock,
                new FixedTimeProvider(Now));
        }

        public FakeRepository Repository { get; } = new();

        public FakeSkipStore SkipStore { get; } = new();

        public CountingLock OperationLock { get; }

        public ScheduleCommandService Service { get; }
    }

    private sealed class FakeRepository : IScheduleRepository
    {
        private readonly Dictionary<Guid, ScheduleSnapshot> _schedules = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ScheduleSnapshot> CreateAsync(ScheduleDraft draft, CancellationToken cancellationToken = default)
        {
            var snapshot = ToSnapshot(Guid.NewGuid(), 1, draft);
            _schedules.Add(snapshot.Id, snapshot);
            return Task.FromResult(snapshot);
        }

        public Task<ScheduleSnapshot> UpdateAsync(
            Guid id,
            long expectedRevision,
            ScheduleDraft draft,
            CancellationToken cancellationToken = default)
        {
            var current = GetActive(id, expectedRevision);
            var snapshot = ToSnapshot(current.Id, current.Revision + 1, draft);
            _schedules[id] = snapshot;
            return Task.FromResult(snapshot);
        }

        public Task<ScheduleSnapshot> CancelAsync(
            Guid id,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            var current = GetActive(id, expectedRevision);
            var snapshot = current with { Revision = current.Revision + 1, Status = ScheduleStatus.Cancelled };
            _schedules[id] = snapshot;
            return Task.FromResult(snapshot);
        }

        public Task<ScheduleSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_schedules.GetValueOrDefault(id));

        public Task<IReadOnlyList<ScheduleSnapshot>> ListAsync(
            bool includeInactive = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduleSnapshot>>(
                [.. _schedules.Values.Where(schedule => includeInactive || schedule.Status == ScheduleStatus.Active)]);

        public Task<StoreHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoreHealth(StoreHealthStatus.Healthy, "ok"));

        private ScheduleSnapshot GetActive(Guid id, long revision)
        {
            if (!_schedules.TryGetValue(id, out var schedule) || schedule.Revision != revision ||
                schedule.Status != ScheduleStatus.Active)
            {
                throw new ScheduleConflictException("Schedule changed.");
            }

            return schedule;
        }

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

    private sealed class FakeSkipStore : IDailySkipStore
    {
        public List<DailySkipRequest> Requests { get; } = [];

        public Task<DailySkipRequest> RequestAsync(
            Guid scheduleId,
            long scheduleRevision,
            DateTimeOffset executeDueAt,
            CancellationToken cancellationToken = default)
        {
            var request = new DailySkipRequest(scheduleId, scheduleRevision, executeDueAt, Now);
            Requests.Add(request);
            return Task.FromResult(request);
        }
    }

    private sealed class FakeSettingsRepository(AppSettings settings) : IAppSettingsRepository
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings.Validate());

        public Task<AppSettings> SaveAsync(AppSettings value, CancellationToken cancellationToken = default) =>
            Task.FromResult(value.Validate());
    }

    private sealed class FakeBackup : IStateBackup
    {
        public Task<string> CreateVerifiedBackupAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("backup.db");
    }

    private sealed class FakeProjection : ITaskSchedulerProjection
    {
        public Task<IReadOnlyList<SchedulerTaskSnapshot>> ListOwnedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchedulerTaskSnapshot>>([]);

        public Task UpsertAsync(SchedulerTaskDefinition definition, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(string taskName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CountingLock : IOperationLock
    {
        public int AcquisitionCount { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(CreateLease());

        private IAsyncDisposable CreateLease()
        {
            AcquisitionCount++;
            return new Lease();
        }

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
