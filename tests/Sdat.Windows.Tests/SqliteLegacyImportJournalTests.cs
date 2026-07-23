using Sdat.Windows.Migration;
using Sdat.Windows.Persistence;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class SqliteLegacyImportJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-v1-journal-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Completion_marker_is_durable_and_idempotent()
    {
        var options = new SqliteStoreOptions
        {
            DatabasePath = Path.Combine(_root, "data", "sdat.db"),
            BackupDirectory = Path.Combine(_root, "backups"),
        };
        await new SqliteScheduleRepository(options).InitializeAsync();
        var journal = new SqliteLegacyImportJournal(options);

        Assert.False(await journal.IsCompletedAsync("v1"));
        await journal.MarkCompletedAsync("v1", "legacy/state.json", "{}");
        await journal.MarkCompletedAsync("v1", "legacy/state.json", "{}");

        Assert.True(await journal.IsCompletedAsync("v1"));
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
