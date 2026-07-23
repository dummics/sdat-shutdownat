using Microsoft.Data.Sqlite;
using Sdat.Core.Operations;
using Sdat.Core.Scheduling;
using Sdat.Windows.Concurrency;
using Sdat.Windows.Persistence;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class SqliteStoreInitializerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-initializer-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Missing_primary_database_is_restored_when_a_verified_backup_exists()
    {
        var options = CreateOptions();
        await CreateBackedUpScheduleAsync(options);
        SqliteConnection.ClearAllPools();
        DeletePrimaryFiles(options);

        var repository = new SqliteScheduleRepository(options, new FixedTimeProvider(Now));
        var result = await CreateInitializer(options, repository).InitializeAsync();

        Assert.True(result.WasRecovered);
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task Corrupt_primary_database_is_restored_from_a_verified_backup()
    {
        var options = CreateOptions();
        await CreateBackedUpScheduleAsync(options);
        SqliteConnection.ClearAllPools();
        DeleteSidecars(options);
        await File.WriteAllTextAsync(options.DatabasePath, "not a sqlite database");

        var repository = new SqliteScheduleRepository(options, new FixedTimeProvider(Now));
        var result = await CreateInitializer(options, repository).InitializeAsync();

        Assert.True(result.WasRecovered);
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task Existing_uninitialized_database_is_restored_instead_of_migrated_over_a_backup()
    {
        var options = CreateOptions();
        await CreateBackedUpScheduleAsync(options);
        SqliteConnection.ClearAllPools();
        DeletePrimaryFiles(options);
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        await File.WriteAllBytesAsync(options.DatabasePath, []);

        var repository = new SqliteScheduleRepository(options, new FixedTimeProvider(Now));
        var result = await CreateInitializer(options, repository).InitializeAsync();

        Assert.True(result.WasRecovered);
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task Healthy_forward_schema_is_never_overwritten_by_an_older_backup()
    {
        var options = CreateOptions();
        await CreateBackedUpScheduleAsync(options);
        await using (var connection = await SqliteSchema.OpenAsync(options, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteScheduleRepository(options, new FixedTimeProvider(Now));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateInitializer(options, repository).InitializeAsync());

        await using var verification = await SqliteSchema.OpenAsync(options, CancellationToken.None);
        Assert.Equal(99, await SqliteSchema.GetUserVersionAsync(verification, CancellationToken.None));
    }

    [Fact]
    public async Task Supported_older_schema_is_backed_up_before_migration()
    {
        var options = CreateOptions();
        var repository = new SqliteScheduleRepository(options, new FixedTimeProvider(Now));
        await repository.InitializeAsync();
        await repository.CreateAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddHours(1), "UTC"));
        await using (var connection = await SqliteSchema.OpenAsync(options, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2;";
            await command.ExecuteNonQueryAsync();
        }

        var result = await CreateInitializer(options, repository).InitializeAsync();

        Assert.False(result.WasRecovered);
        var backupPath = Assert.Single(Directory.EnumerateFiles(options.BackupDirectory, "sdat-*.db"));
        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await backup.OpenAsync();
        Assert.Equal(2, await SqliteSchema.GetUserVersionAsync(backup, CancellationToken.None));
        await using var primary = await SqliteSchema.OpenAsync(options, CancellationToken.None);
        Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.GetUserVersionAsync(primary, CancellationToken.None));
    }

    [Fact]
    public async Task Failed_pre_migration_backup_leaves_the_older_schema_untouched()
    {
        var options = CreateOptions();
        var repository = new SqliteScheduleRepository(options, new FixedTimeProvider(Now));
        await repository.InitializeAsync();
        await using (var connection = await SqliteSchema.OpenAsync(options, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<IOException>(() =>
            CreateInitializer(options, repository, new FailingBackup()).InitializeAsync());

        await using var verification = await SqliteSchema.OpenAsync(options, CancellationToken.None);
        Assert.Equal(2, await SqliteSchema.GetUserVersionAsync(verification, CancellationToken.None));
    }

    private async Task CreateBackedUpScheduleAsync(SqliteStoreOptions options)
    {
        var repository = new SqliteScheduleRepository(options, new FixedTimeProvider(Now));
        await repository.InitializeAsync();
        await repository.CreateAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, Now.AddHours(1), "UTC"));
        await new SqliteBackupService(options, new FixedTimeProvider(Now)).CreateVerifiedBackupAsync();
    }

    private static SqliteStoreInitializer CreateInitializer(
        SqliteStoreOptions options,
        SqliteScheduleRepository repository,
        IStateBackup? backup = null) => new(
        options,
        repository,
        new SqliteRecoveryService(options, new FixedTimeProvider(Now)),
        backup ?? new SqliteBackupService(options, new FixedTimeProvider(Now)),
        new FileOperationLock(options.OperationLockPath));

    private static void DeletePrimaryFiles(SqliteStoreOptions options)
    {
        File.Delete(options.DatabasePath);
        DeleteSidecars(options);
    }

    private static void DeleteSidecars(SqliteStoreOptions options)
    {
        File.Delete($"{options.DatabasePath}-wal");
        File.Delete($"{options.DatabasePath}-shm");
    }

    private SqliteStoreOptions CreateOptions() => new()
    {
        DatabasePath = Path.Combine(_root, "data", "sdat.db"),
        BackupDirectory = Path.Combine(_root, "backups"),
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingBackup : IStateBackup
    {
        public Task<string> CreateVerifiedBackupAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("simulated backup failure");
    }
}
