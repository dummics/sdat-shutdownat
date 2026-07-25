using Sdat.Windows.Concurrency;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class FileOperationLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdat-lock-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Second_caller_times_out_while_lock_is_held()
    {
        var fileLock = new FileOperationLock(
            Path.Combine(_root, "operation.lock"),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(20));
        await using var first = await fileLock.AcquireAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => fileLock.AcquireAsync());
    }

    [Fact]
    public async Task Lock_can_be_reacquired_after_release()
    {
        var fileLock = new FileOperationLock(Path.Combine(_root, "operation.lock"));
        await using (await fileLock.AcquireAsync())
        {
        }

        await using var second = await fileLock.AcquireAsync();
        Assert.NotNull(second);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
