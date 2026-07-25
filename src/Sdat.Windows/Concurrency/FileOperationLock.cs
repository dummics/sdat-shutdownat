using Sdat.Core.Operations;

namespace Sdat.Windows.Concurrency;

public sealed class FileOperationLock(
    string lockPath,
    TimeSpan? timeout = null,
    TimeSpan? retryDelay = null) : IOperationLock
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(lockPath))!;
        Directory.CreateDirectory(directory);
        var deadline = DateTimeOffset.UtcNow + _timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
                return new FileLease(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new TimeoutException("Another SDAT process is still updating the schedule.", exception);
            }
        }
    }

    private sealed class FileLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
