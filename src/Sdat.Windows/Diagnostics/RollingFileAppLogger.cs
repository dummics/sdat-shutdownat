using System.Globalization;
using System.Text;
using Sdat.Core.Diagnostics;
using Sdat.Core.Settings;
using Sdat.Windows.Persistence;

namespace Sdat.Windows.Diagnostics;

public sealed class RollingFileAppLogger(
    SqliteStoreOptions options,
    IAppSettingsRepository settingsRepository) : IAppLogger
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public async Task EnsureFileExistsAsync(CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            if (!File.Exists(options.LogPath))
            {
                await File.WriteAllTextAsync(options.LogPath, string.Empty, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task WriteAsync(
        AppLogLevel level,
        string source,
        string message,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (level > settings.LogLevel)
        {
            return;
        }

        await WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            RotateIfNeeded();
            var safeSource = Normalize(source);
            var safeMessage = Normalize(message);
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O} [{level}] {safeSource}: {safeMessage}{Environment.NewLine}");
            await File.AppendAllTextAsync(
                    options.LogPath,
                    line,
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(options.LogPath);
        if (!file.Exists || file.Length < MaximumLogBytes)
        {
            return;
        }

        var previous = options.LogPath + ".1";
        File.Move(options.LogPath, previous, overwrite: true);
    }

    private static string Normalize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
