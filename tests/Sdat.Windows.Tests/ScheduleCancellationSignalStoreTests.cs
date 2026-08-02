using Sdat.Windows.Execution;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class ScheduleCancellationSignalStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sdat-cancellation-signal-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Exact_signal_matches_only_the_cancelled_schedule_revision()
    {
        var now = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var scheduleId = Guid.NewGuid();
        var store = CreateStore(now);

        await store.PublishAsync(scheduleId, 7, windowsCountdownAborted: true);

        var signal = await store.ReadLatestAsync();
        Assert.NotNull(signal);
        Assert.True(signal.Matches(scheduleId, 7, now.AddSeconds(-1)));
        Assert.False(signal.Matches(scheduleId, 8, now.AddSeconds(-1)));
        Assert.False(signal.Matches(Guid.NewGuid(), 7, now.AddSeconds(-1)));
    }

    [Fact]
    public async Task Generic_windows_abort_closes_any_countdown_opened_before_it()
    {
        var now = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var store = CreateStore(now);

        await store.PublishAsync(
            scheduleId: null,
            revision: null,
            windowsCountdownAborted: true);

        var signal = await store.ReadLatestAsync();
        Assert.NotNull(signal);
        Assert.True(signal.Matches(Guid.NewGuid(), 3, now.AddMilliseconds(-1)));
        Assert.False(signal.Matches(Guid.NewGuid(), 3, now.AddMilliseconds(1)));
    }

    [Fact]
    public async Task Latest_signal_replaces_the_previous_cross_process_message()
    {
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(_root, "cancellation-signal.json");
        var store = new ScheduleCancellationSignalStore(path, time);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await store.PublishAsync(first, 1, windowsCountdownAborted: false);
        time.UtcNow = time.UtcNow.AddSeconds(1);
        await store.PublishAsync(second, 4, windowsCountdownAborted: false);

        var signal = await store.ReadLatestAsync();
        Assert.NotNull(signal);
        Assert.Equal(second, signal.ScheduleId);
        Assert.Equal(4, signal.Revision);
        Assert.Equal(time.UtcNow, signal.OccurredAtUtc);
    }

    [Fact]
    public async Task Countdown_abort_is_published_generically_after_schedule_results()
    {
        var now = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var store = CreateStore(now);
        var schedule = new Sdat.Core.Scheduling.ScheduleSnapshot(
            Guid.NewGuid(),
            8,
            Sdat.Core.Scheduling.ScheduleKind.OneTime,
            Sdat.Core.Scheduling.PowerActionType.Shutdown,
            now.AddMinutes(2),
            null,
            TimeZoneInfo.Utc.Id,
            false,
            Sdat.Core.Scheduling.ScheduleStatus.Cancelled,
            now,
            now);
        var mutation = new Sdat.Core.Operations.ScheduleMutationResult(
            schedule,
            "backup",
            null,
            new Sdat.Core.Scheduling.ReconciliationReport(0, 0, 0, []));
        IReadOnlyList<Sdat.Core.Operations.ScheduleMutationResult> results = [mutation];
        var guard = new WindowsShutdownCancellationGuardResult<IReadOnlyList<Sdat.Core.Operations.ScheduleMutationResult>>(
            results,
            null,
            new ShutdownCountdownAbortResult(ShutdownCountdownAbortStatus.Aborted),
            new ShutdownCountdownAbortResult(ShutdownCountdownAbortStatus.NoCountdown));

        await ScheduleCancellationSignalPublisher.PublishAvailableAsync(store, results, guard);

        var signal = await store.ReadLatestAsync();
        Assert.NotNull(signal);
        Assert.Null(signal.ScheduleId);
        Assert.True(signal.WindowsCountdownAborted);
    }

    [Fact]
    public async Task Exact_cancellation_with_windows_abort_ends_with_a_generic_countdown_signal()
    {
        var now = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var store = CreateStore(now);
        var guard = new WindowsShutdownCancellationGuardResult<bool>(
            true,
            null,
            new ShutdownCountdownAbortResult(ShutdownCountdownAbortStatus.Aborted),
            new ShutdownCountdownAbortResult(ShutdownCountdownAbortStatus.NoCountdown));

        await ScheduleCancellationSignalPublisher.PublishExactAsync(
            store,
            Guid.NewGuid(),
            5,
            scheduleSettled: true,
            guard);

        var signal = await store.ReadLatestAsync();
        Assert.NotNull(signal);
        Assert.Null(signal.ScheduleId);
        Assert.True(signal.WindowsCountdownAborted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ScheduleCancellationSignalStore CreateStore(DateTimeOffset now) =>
        new(
            Path.Combine(_root, "cancellation-signal.json"),
            new MutableTimeProvider(now));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
