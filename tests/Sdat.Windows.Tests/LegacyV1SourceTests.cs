using Sdat.Core.Scheduling;
using Sdat.Windows.Migration;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class LegacyV1SourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-v1-source-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Builds_native_drafts_from_verified_v1_tasks_and_state()
    {
        WriteState("restart", "suspend");
        var reader = new FakeTaskReader(
            new LegacyTaskSnapshot(
                "SDAT_Volatile",
                ScheduleKind.OneTime,
                Now.AddHours(2),
                null,
                PowerActionType.Shutdown,
                true),
            new LegacyTaskSnapshot(
                "SDAT_Permanent",
                ScheduleKind.Daily,
                null,
                new TimeOnly(2, 30),
                PowerActionType.Shutdown,
                true));

        var plan = await new LegacyV1Source(_root, reader, new FixedTimeProvider(Now)).ReadAsync();

        Assert.True(plan.SourceFound);
        Assert.True(plan.IsValid);
        Assert.Equal(2, plan.Schedules.Count);
        Assert.Contains(plan.Schedules, draft =>
            draft.Kind == ScheduleKind.OneTime && draft.Action == PowerActionType.Restart);
        Assert.Contains(plan.Schedules, draft =>
            draft.Kind == ScheduleKind.Daily && draft.Action == PowerActionType.Suspend &&
            draft.DailyAt == new TimeOnly(2, 30));
        Assert.Empty(plan.ObsoleteTaskNames);
        Assert.True(plan.SkipNextDaily);
    }

    [Fact]
    public async Task Stale_verified_one_time_task_is_marked_for_cleanup()
    {
        WriteState("shutdown", "shutdown");
        var reader = new FakeTaskReader(new LegacyTaskSnapshot(
            "SDAT_Volatile",
            ScheduleKind.OneTime,
            Now.AddMinutes(-1),
            null,
            PowerActionType.Shutdown,
            true));

        var plan = await new LegacyV1Source(_root, reader, new FixedTimeProvider(Now)).ReadAsync();

        Assert.Empty(plan.Schedules);
        Assert.Equal("SDAT_Volatile", Assert.Single(plan.ObsoleteTaskNames));
    }

    [Fact]
    public async Task Unrecognized_task_is_never_imported_or_removed()
    {
        WriteState("shutdown", "shutdown");
        var reader = new FakeTaskReader(new LegacyTaskSnapshot(
            "SDAT_Volatile",
            ScheduleKind.OneTime,
            Now.AddHours(1),
            null,
            PowerActionType.Shutdown,
            false));

        var source = new LegacyV1Source(_root, reader, new FixedTimeProvider(Now));
        var plan = await source.ReadAsync();
        await source.RemoveObsoleteTasksAsync(plan);

        Assert.Empty(plan.Schedules);
        Assert.Empty(plan.ObsoleteTaskNames);
        Assert.Empty(reader.Removed);
        Assert.False(plan.IsValid);
        Assert.Contains(plan.Warnings, warning => warning.Contains("left untouched", StringComparison.Ordinal));
    }

    private void WriteState(string volatileAction, string permanentAction)
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(
            Path.Combine(data, "state.json"),
            $$"""
              {
                "Version": 1,
                "Volatile": { "ActionType": "{{volatileAction}}" },
                "Permanent": { "ActionType": "{{permanentAction}}" },
                "SuspendPermanentUntil": "2026-07-21T14:05:00Z"
              }
              """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeTaskReader(params LegacyTaskSnapshot[] tasks) : ILegacyTaskReader
    {
        public List<string> Removed { get; } = [];

        public Task<LegacyTaskSnapshot?> ReadAsync(
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LegacyTaskSnapshot?>(
                tasks.SingleOrDefault(task => task.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase)));

        public Task RemoveAsync(string taskName, CancellationToken cancellationToken = default)
        {
            Removed.Add(taskName);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
