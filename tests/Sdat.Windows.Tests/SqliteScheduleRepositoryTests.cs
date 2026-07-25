using Sdat.Core.Scheduling;
using Sdat.Core.Storage;
using Sdat.Windows.Persistence;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class SqliteScheduleRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Initialize_creates_a_healthy_store()
    {
        var repository = CreateRepository();

        await repository.InitializeAsync();

        var health = await repository.CheckHealthAsync();
        Assert.Equal(StoreHealthStatus.Healthy, health.Status);
    }

    [Fact]
    public async Task One_time_schedule_round_trips_in_utc()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var target = new DateTimeOffset(2026, 7, 21, 23, 41, 0, TimeSpan.FromHours(2));

        var created = await repository.CreateAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, target, "W. Europe Standard Time"));
        var loaded = await repository.GetAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Revision);
        Assert.Equal(target.ToUniversalTime(), loaded.TargetAt);
        Assert.Equal(ScheduleStatus.Active, loaded.Status);
    }

    [Fact]
    public async Task Only_one_active_schedule_per_kind_is_allowed()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.CreateAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, DateTimeOffset.UtcNow.AddHours(1), "UTC"));

        await Assert.ThrowsAsync<ScheduleConflictException>(() =>
            repository.CreateAsync(
                ScheduleDraft.OneTime(PowerActionType.Restart, DateTimeOffset.UtcNow.AddHours(2), "UTC")));
    }

    [Fact]
    public async Task Stale_revision_cannot_update_a_schedule()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var created = await repository.CreateAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, DateTimeOffset.UtcNow.AddHours(1), "UTC"));
        var updated = await repository.UpdateAsync(
            created.Id,
            created.Revision,
            ScheduleDraft.OneTime(PowerActionType.Shutdown, DateTimeOffset.UtcNow.AddHours(2), "UTC"));

        Assert.Equal(2, updated.Revision);
        await Assert.ThrowsAsync<ScheduleConflictException>(() =>
            repository.UpdateAsync(
                created.Id,
                created.Revision,
                ScheduleDraft.OneTime(PowerActionType.Restart, DateTimeOffset.UtcNow.AddHours(3), "UTC")));
    }

    [Fact]
    public async Task Backup_is_created_and_verified()
    {
        var options = CreateOptions();
        var repository = new SqliteScheduleRepository(options);
        await repository.InitializeAsync();
        await repository.CreateAsync(
            ScheduleDraft.Daily(PowerActionType.Suspend, new TimeOnly(2, 30), "W. Europe Standard Time"));

        var backupPath = await new SqliteBackupService(options).CreateVerifiedBackupAsync();

        Assert.True(File.Exists(backupPath));
        Assert.True(new FileInfo(backupPath).Length > 0);
    }

    [Fact]
    public async Task Corrupt_database_can_be_restored_from_verified_backup()
    {
        var options = CreateOptions();
        var repository = new SqliteScheduleRepository(options);
        await repository.InitializeAsync();
        var created = await repository.CreateAsync(
            ScheduleDraft.OneTime(PowerActionType.Shutdown, DateTimeOffset.UtcNow.AddHours(1), "UTC"));
        var backupPath = await new SqliteBackupService(options).CreateVerifiedBackupAsync();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(options.DatabasePath, "not a sqlite database");

        var recovery = new SqliteRecoveryService(options);
        var unhealthy = await recovery.CheckCurrentAsync(full: true);
        var result = await recovery.RestoreLatestVerifiedBackupAsync();
        var restored = await new SqliteScheduleRepository(options).GetAsync(created.Id);

        Assert.NotEqual(StoreHealthStatus.Healthy, unhealthy.Status);
        Assert.Equal(backupPath, result.RestoredBackupPath);
        Assert.Equal(StoreHealthStatus.Healthy, result.Health.Status);
        Assert.NotNull(result.EvidenceDirectory);
        Assert.NotNull(restored);
        Assert.Equal(created.Id, restored.Id);
    }

    [Fact]
    public async Task Healthy_database_is_not_overwritten_by_default()
    {
        var options = CreateOptions();
        var repository = new SqliteScheduleRepository(options);
        await repository.InitializeAsync();
        await new SqliteBackupService(options).CreateVerifiedBackupAsync();

        var recovery = new SqliteRecoveryService(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.RestoreLatestVerifiedBackupAsync());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SqliteScheduleRepository CreateRepository() => new(CreateOptions());

    private SqliteStoreOptions CreateOptions() => new()
    {
        DatabasePath = Path.Combine(_root, "data", "sdat.db"),
        BackupDirectory = Path.Combine(_root, "backups"),
        BackupRetentionCount = 3,
    };
}
