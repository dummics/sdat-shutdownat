using Sdat.Core.Execution;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
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

    [Fact]
    public async Task Notification_failure_is_recorded_but_allows_overlay_fallback()
    {
        var schedule = CreateSchedule(Now.AddMinutes(2));
        var fixture = new Fixture(schedule);
        fixture.Notifier.Result = ReminderDeliveryResult.Failed("RegistrationFailed", "Toast unavailable.");

        var result = await fixture.Coordinator.RunAsync(
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Reminder, 2));

        Assert.Equal(TaskInvocationOutcome.ReminderDegraded, result.Outcome);
        Assert.Equal("RegistrationFailed", fixture.Ledger.Failure?.ErrorCode);
        Assert.Equal(0, fixture.Executor.CallCount);
    }

    [Fact]
    public async Task Requested_daily_skip_suppresses_the_power_action()
    {
        var schedule = new ScheduleSnapshot(
            Guid.NewGuid(),
            2,
            ScheduleKind.Daily,
            PowerActionType.Shutdown,
            null,
            new TimeOnly(20, 0),
            "UTC",
            false,
            ScheduleStatus.Active,
            Now.AddDays(-1),
            Now.AddDays(-1));
        var fixture = new Fixture(schedule);
        fixture.Ledger.NextClaimResult = OccurrenceClaimResult.SkippedByRequest;

        var result = await fixture.Coordinator.RunAsync(
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null));

        Assert.Equal(TaskInvocationOutcome.SkippedByRequest, result.Outcome);
        Assert.Equal(0, fixture.Executor.CallCount);
        Assert.Equal(0, fixture.Notifier.CallCount);
    }

    [Fact]
    public async Task Simulated_power_action_is_completed_without_reporting_real_execution()
    {
        var schedule = CreateSchedule(Now);
        var fixture = new Fixture(schedule);
        fixture.Executor.Simulate = true;

        var result = await fixture.Coordinator.RunAsync(
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null));

        Assert.Equal(TaskInvocationOutcome.Simulated, result.Outcome);
        Assert.Contains("suppressed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Ledger.Completed);
        Assert.Null(fixture.Ledger.Failure);
    }

    [Fact]
    public async Task Test_mode_short_circuits_before_claim_finalization_notification_and_execution()
    {
        var schedule = CreateSchedule(Now);
        var fixture = new Fixture(schedule, safetyPolicy: new FixedSafetyPolicy(true));

        var result = await fixture.Coordinator.RunAsync(
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null));

        Assert.Equal(TaskInvocationOutcome.Simulated, result.Outcome);
        Assert.Empty(fixture.Ledger.Claims);
        Assert.Empty(fixture.Ledger.Completed);
        Assert.Equal(0, fixture.Executor.CallCount);
        Assert.Equal(0, fixture.Notifier.CallCount);
        Assert.Equal(0, fixture.Finalizer.CallCount);
    }

    [Fact]
    public async Task Daily_reminder_claim_keeps_exact_execution_due_across_dst_change()
    {
        var reminderDue = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        var schedule = new ScheduleSnapshot(
            Guid.NewGuid(),
            1,
            ScheduleKind.Daily,
            PowerActionType.Shutdown,
            null,
            new TimeOnly(3, 30),
            "W. Europe Standard Time",
            false,
            ScheduleStatus.Active,
            reminderDue.AddDays(-1),
            reminderDue.AddDays(-1));
        var fixture = new Fixture(schedule, reminderDue);

        await fixture.Coordinator.RunAsync(
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Reminder, 120));

        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero),
            Assert.Single(fixture.Ledger.Claims).ExecuteDueAt);
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
        public Fixture(
            ScheduleSnapshot schedule,
            DateTimeOffset? now = null,
            IRuntimeSafetyPolicy? safetyPolicy = null)
        {
            Repository = new FakeRepository(schedule);
            Coordinator = new TaskInvocationCoordinator(
                Repository,
                Ledger,
                Executor,
                Notifier,
                Finalizer,
                new NoOpLock(),
                new FixedTimeProvider(now ?? Now),
                safetyPolicy: safetyPolicy);
        }

        public FakeRepository Repository { get; }

        public FakeLedger Ledger { get; } = new();

        public FakeExecutor Executor { get; } = new();

        public FakeNotifier Notifier { get; } = new();

        public TrackingFinalizer Finalizer { get; } = new();

        public TaskInvocationCoordinator Coordinator { get; }
    }

    private sealed class FakeLedger : ITaskExecutionLedger
    {
        private readonly HashSet<Guid> _claimed = [];

        public List<OccurrenceClaim> Claims { get; } = [];

        public (Guid OccurrenceId, string ErrorCode, string ErrorDetail)? Failure { get; private set; }

        public List<Guid> Completed { get; } = [];

        public OccurrenceClaimResult NextClaimResult { get; set; } = OccurrenceClaimResult.Claimed;

        public Task<OccurrenceClaimResult> TryClaimAsync(
            OccurrenceClaim claim,
            CancellationToken cancellationToken = default)
        {
            if (!_claimed.Add(claim.OccurrenceId))
            {
                return Task.FromResult(OccurrenceClaimResult.AlreadyHandled);
            }

            Claims.Add(claim);
            return Task.FromResult(NextClaimResult);
        }

        public Task CompleteAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
        {
            Completed.Add(occurrenceId);
            return Task.CompletedTask;
        }

        public Task FailAsync(
            Guid occurrenceId,
            string errorCode,
            string errorDetail,
            CancellationToken cancellationToken = default)
        {
            Failure = (occurrenceId, errorCode, errorDetail);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExecutor : IPowerActionExecutor
    {
        public int CallCount { get; private set; }

        public bool Simulate { get; set; }

        public Task ExecuteAsync(PowerActionType action, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Simulate)
            {
                throw new PowerActionSimulatedException(action);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeNotifier : ITaskReminderNotifier
    {
        public int CallCount { get; private set; }

        public ReminderDeliveryResult Result { get; set; } = ReminderDeliveryResult.Success;

        public Task<ReminderDeliveryResult> ShowAsync(
            ScheduleSnapshot schedule,
            int offsetMinutes,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
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

    public sealed class TrackingFinalizer : IOneTimeExecutionFinalizer
    {
        public int CallCount { get; private set; }

        public Task<string?> FinalizeAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FixedSafetyPolicy(bool isTestMode) : IRuntimeSafetyPolicy
    {
        public Task<bool> IsTestModeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(isTestMode);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
