using Sdat.Core.Execution;
using Sdat.Core.Scheduling;
using Sdat.Windows.Persistence;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class SqliteTaskExecutionLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-ledger-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Claiming_one_time_execution_atomically_completes_schedule()
    {
        var options = CreateOptions();
        var repository = new SqliteScheduleRepository(options);
        await repository.InitializeAsync();
        var schedule = await repository.CreateAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, DateTimeOffset.UtcNow, "UTC"));
        var ledger = new SqliteTaskExecutionLedger(options);
        var occurrenceId = Guid.NewGuid();

        var result = await ledger.TryClaimAsync(new OccurrenceClaim(
            occurrenceId,
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null),
            schedule.TargetAt!.Value,
            OccurrenceOutcome.Pending));

        Assert.Equal(OccurrenceClaimResult.Claimed, result);
        var completed = await repository.GetAsync(schedule.Id);
        Assert.Equal(ScheduleStatus.Completed, completed!.Status);
        Assert.Equal(schedule.Revision + 1, completed.Revision);
        Assert.Equal(
            OccurrenceClaimResult.Stale,
            await ledger.TryClaimAsync(new OccurrenceClaim(
                occurrenceId,
                new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null),
                schedule.TargetAt.Value,
                OccurrenceOutcome.Pending)));
    }

    [Fact]
    public async Task Duplicate_daily_occurrence_is_handled_only_once()
    {
        var options = CreateOptions();
        var repository = new SqliteScheduleRepository(options);
        await repository.InitializeAsync();
        var schedule = await repository.CreateAsync(
            ScheduleDraft.Daily(PowerActionType.Suspend, new TimeOnly(2, 30), "UTC"));
        var ledger = new SqliteTaskExecutionLedger(options);
        var claim = new OccurrenceClaim(
            Guid.NewGuid(),
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null),
            DateTimeOffset.UtcNow,
            OccurrenceOutcome.Pending);

        Assert.Equal(OccurrenceClaimResult.Claimed, await ledger.TryClaimAsync(claim));
        Assert.Equal(OccurrenceClaimResult.AlreadyHandled, await ledger.TryClaimAsync(claim));
        await ledger.CompleteAsync(claim.OccurrenceId);
        Assert.Equal(ScheduleStatus.Active, (await repository.GetAsync(schedule.Id))!.Status);
    }

    [Fact]
    public async Task Durable_daily_skip_suppresses_reminder_and_execution_once()
    {
        var options = CreateOptions();
        var repository = new SqliteScheduleRepository(options);
        await repository.InitializeAsync();
        var schedule = await repository.CreateAsync(
            ScheduleDraft.Daily(PowerActionType.Shutdown, new TimeOnly(22, 0), "UTC"));
        var dueAt = new DateTimeOffset(2026, 7, 22, 22, 0, 0, TimeSpan.Zero);
        await new SqliteDailySkipStore(options).RequestAsync(schedule.Id, schedule.Revision, dueAt);
        var ledger = new SqliteTaskExecutionLedger(options);

        var reminderResult = await ledger.TryClaimAsync(new OccurrenceClaim(
            Guid.NewGuid(),
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Reminder, 2),
            dueAt.AddMinutes(-2),
            OccurrenceOutcome.Pending,
            dueAt));
        var executionResult = await ledger.TryClaimAsync(new OccurrenceClaim(
            Guid.NewGuid(),
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null),
            dueAt,
            OccurrenceOutcome.Pending));
        var tomorrowResult = await ledger.TryClaimAsync(new OccurrenceClaim(
            Guid.NewGuid(),
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Execute, null),
            dueAt.AddDays(1),
            OccurrenceOutcome.Pending));

        Assert.Equal(OccurrenceClaimResult.SkippedByRequest, reminderResult);
        Assert.Equal(OccurrenceClaimResult.SkippedByRequest, executionResult);
        Assert.Equal(OccurrenceClaimResult.Claimed, tomorrowResult);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SqliteStoreOptions CreateOptions() => new()
    {
        DatabasePath = Path.Combine(_root, "data", "sdat.db"),
        BackupDirectory = Path.Combine(_root, "backups"),
    };
}
