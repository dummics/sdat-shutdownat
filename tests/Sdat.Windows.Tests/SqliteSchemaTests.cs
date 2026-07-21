using Microsoft.Data.Sqlite;
using Sdat.Windows.Persistence;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class SqliteSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-schema-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Existing_version_one_store_is_upgraded_to_version_two()
    {
        var options = CreateOptions();
        await using (var connection = await SqliteSchema.OpenAsync(options, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 1;";
            await command.ExecuteNonQueryAsync();
        }

        await new SqliteScheduleRepository(options).InitializeAsync();

        await using var verification = await SqliteSchema.OpenAsync(options, CancellationToken.None);
        Assert.Equal(2, await SqliteSchema.GetUserVersionAsync(verification, CancellationToken.None));
    }

    [Fact]
    public async Task Newer_store_is_rejected_without_downgrading_it()
    {
        var options = CreateOptions();
        await using (var connection = await SqliteSchema.OpenAsync(options, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteScheduleRepository(options).InitializeAsync());

        await using var verification = await SqliteSchema.OpenAsync(options, CancellationToken.None);
        Assert.Equal(99, await SqliteSchema.GetUserVersionAsync(verification, CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
