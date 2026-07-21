using Sdat.Core.Execution;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Core.Storage;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class TaskInvocationCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Current_due_execution_is_claimed_once_and_executed()
    {
        var schedule = CreateSchedule(Now);
        var fixture = new Fixture(schedule);
        var invocation = new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null);

        var first = await fixture.Coordinator.RunAsync(invocation);
        var second = await fixture.Coordinator.RunAsync(invocation);

        Assert.Equal(TaskInvocationOutcome.Executed, first.Outcome);
        Assert.Equal(TaskInvocationOutcome.IgnoredDuplicate, second.Outcome);
        Assert.Equal(1, fixture.Executor.CallCount);
    }

    [Fact]
    public async Task Superseded_revision_is_a_safe_no_op()
    {
        var schedule = CreateSchedule(Now);
        var fixture = new Fixture(schedule);

        var result = await fixture.Coordinator.RunAsync(
            new TaskInvocation(schedule.Id, schedule.Revision - 1, SchedulerTaskRole.Execute, null));

        Assert.Equal(TaskInvocationOutcome.IgnoredStale, result.Outcome);
        Assert.Equal(0, fixture.Executor.CallCount);
        Assert.Empty(fixture.Ledger.Claims);
    }

    [Fact]
    public async Task Invocation_outside_late_window_is_claimed_as_skipped()
    {
        var schedule = CreateSchedule(Now.AddMinutes(-16));
        var fixture = new Fixture(schedule);

        var result = await fixture.Coordinator.RunAsync(
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null));

        Assert.Equal(TaskInvocationOutcome.SkippedLate, result.Outcome);
        Assert.Equal(OccurrenceOutcome.Skipped, Assert.Single(fixture.Ledger.Claims).InitialOutcome);
        Assert.Equal(0, fixture.Executor.CallCount);
    }

    [Fact]
    public async Task Reminder_uses_notifier_instead_of_power_executor()
    {
        var schedule = CreateSchedule(Now.AddMinutes(2));
        var fixture = new Fixture(schedule);

        var result = await fixture.Coordinator.RunAsync(
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Reminder, 2));

        Assert.Equal(TaskInvocationOutcome.ReminderShown, result.Outcome);
        Assert.Equal(1, fixture.Notifier.CallCount);
        Assert.Equal(0, fixture.Executor.CallCount);
    }

    private static ScheduleSnapshot CreateSchedule(DateTimeOffset target) => new(
        Guid.NewGuid(),
        3,
        ScheduleKind.OneTime,
        PowerActionType.Shutdown,
        target,
        null,
        "UTC",
        false,
        ScheduleStatus.Active,
        Now.AddHours(-1),
        Now.AddHours(-1));

    private sealed class Fixture
    {
        public Fixture(ScheduleSnapshot schedule)
        {
            Repository = new FakeRepository(schedule);
            Coordinator = new TaskInvocationCoordinator(
                Repository,
                Ledger,
                Executor,
                Notifier,
                new NoOpFinalizer(),
                new NoOpLock(),
                new FixedTimeProvider(Now));
        }

        public FakeRepository Repository { get; }

        public FakeLedger Ledger { get; } = new();

        public FakeExecutor Executor { get; } = new();

        public FakeNotifier Notifier { get; } = new();

        public TaskInvocationCoordinator Coordinator { get; }
    }

    private sealed class FakeLedger : ITaskExecutionLedger
    {
        private readonly HashSet<Guid> _claimed = [];

        public List<OccurrenceClaim> Claims { get; } = [];

        public Task<OccurrenceClaimResult> TryClaimAsync(
            OccurrenceClaim claim,
            CancellationToken cancellationToken = default)
        {
            if (!_claimed.Add(claim.OccurrenceId))
            {
                return Task.FromResult(OccurrenceClaimResult.AlreadyHandled);
            }

            Claims.Add(claim);
            return Task.FromResult(OccurrenceClaimResult.Claimed);
        }

        public Task CompleteAsync(Guid occurrenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task FailAsync(
            Guid occurrenceId,
            string errorCode,
            string errorDetail,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeExecutor : IPowerActionExecutor
    {
        public int CallCount { get; private set; }

        public Task ExecuteAsync(PowerActionType action, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNotifier : ITaskReminderNotifier
    {
        public int CallCount { get; private set; }

        public Task ShowAsync(
            ScheduleSnapshot schedule,
            int offsetMinutes,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRepository(ScheduleSnapshot schedule) : IScheduleRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ScheduleSnapshot> CreateAsync(ScheduleDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ScheduleSnapshot> UpdateAsync(
            Guid id,
            long expectedRevision,
            ScheduleDraft draft,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ScheduleSnapshot> CancelAsync(
            Guid id,
            long expectedRevision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ScheduleSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScheduleSnapshot?>(id == schedule.Id ? schedule : null);

        public Task<IReadOnlyList<ScheduleSnapshot>> ListAsync(
            bool includeInactive = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduleSnapshot>>([schedule]);

        public Task<StoreHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoreHealth(StoreHealthStatus.Healthy, "ok"));
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

    private sealed class NoOpFinalizer : IOneTimeExecutionFinalizer
    {
        public Task<string?> FinalizeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
