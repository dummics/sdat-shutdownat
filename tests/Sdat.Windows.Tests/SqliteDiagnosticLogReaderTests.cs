using Sdat.Core.Diagnostics;
using Sdat.Core.Execution;
using Sdat.Core.Scheduling;
using Sdat.Windows.Persistence;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class SqliteDiagnosticLogReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-diagnostics-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Returns_structured_failure_without_exposing_operation_json()
    {
        var options = new SqliteStoreOptions
        {
            DatabasePath = Path.Combine(_root, "data", "sdat.db"),
            BackupDirectory = Path.Combine(_root, "backups"),
        };
        var repository = new SqliteScheduleRepository(options);
        await repository.InitializeAsync();
        var schedule = await repository.CreateAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, DateTimeOffset.UtcNow, "UTC"));
        var occurrenceId = Guid.NewGuid();
        var ledger = new SqliteTaskExecutionLedger(options);
        await ledger.TryClaimAsync(new OccurrenceClaim(
            occurrenceId,
            new TaskInvocation(schedule.Id, schedule.Revision, SchedulerTaskRole.Reminder, 2),
            schedule.TargetAt!.Value.AddMinutes(-2),
            OccurrenceOutcome.Pending,
            schedule.TargetAt.Value));
        await ledger.FailAsync(occurrenceId, "SyntheticFailure", "test detail");

        var events = await new SqliteDiagnosticLogReader(options).ReadRecentAsync();

        var failure = Assert.Single(events, entry => entry.Severity == DiagnosticSeverity.Error);
        Assert.Equal("SyntheticFailure: test detail", failure.Message);
        Assert.DoesNotContain("{", failure.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
