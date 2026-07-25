using System.Diagnostics;

namespace Sdat.Windows.Execution;

public enum ShutdownCountdownAbortStatus
{
    Aborted,
    NoCountdown,
    Failed,
}

public sealed record ShutdownCountdownAbortResult(
    ShutdownCountdownAbortStatus Status,
    int? ExitCode = null,
    string? Detail = null)
{
    public bool WasAborted => Status == ShutdownCountdownAbortStatus.Aborted;
}

public static class WindowsShutdownCountdownAborter
{
    private const int NoShutdownInProgressExitCode = 1116;

    public static async Task<ShutdownCountdownAbortResult> TryAbortAfterLauncherPreflightAsync(
        CancellationToken cancellationToken = default)
    {
        var launcherResult = InterpretLauncherPreflight(
            Environment.GetEnvironmentVariable("SDAT_FAST_ABORT_ATTEMPTED"),
            Environment.GetEnvironmentVariable("SDAT_FAST_ABORT_SUCCEEDED"),
            Environment.GetEnvironmentVariable("SDAT_FAST_ABORT_EXIT_CODE"));
        return launcherResult ??
               await TryAbortAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ShutdownCountdownAbortResult> TryAbortAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo());
            if (process is null)
            {
                return new ShutdownCountdownAbortResult(
                    ShutdownCountdownAbortStatus.Failed,
                    Detail: "Windows did not start the shutdown cancellation command.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return InterpretExitCode(process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ShutdownCountdownAbortResult(
                ShutdownCountdownAbortStatus.Failed,
                Detail: "Windows did not confirm the cancellation in time.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ShutdownCountdownAbortResult(
                ShutdownCountdownAbortStatus.Failed,
                Detail: exception.Message);
        }
    }

    internal static ShutdownCountdownAbortResult InterpretExitCode(int exitCode) =>
        exitCode switch
        {
            0 => new ShutdownCountdownAbortResult(
                ShutdownCountdownAbortStatus.Aborted,
                exitCode),
            NoShutdownInProgressExitCode => new ShutdownCountdownAbortResult(
                ShutdownCountdownAbortStatus.NoCountdown,
                exitCode),
            _ => new ShutdownCountdownAbortResult(
                ShutdownCountdownAbortStatus.Failed,
                exitCode,
                $"shutdown.exe exited with code {exitCode}."),
        };

    internal static ShutdownCountdownAbortResult? InterpretLauncherPreflight(
        string? attempted,
        string? succeeded,
        string? exitCode)
    {
        if (!string.Equals(attempted, "1", StringComparison.Ordinal))
        {
            return null;
        }

        if (int.TryParse(exitCode, out var parsedExitCode))
        {
            return InterpretExitCode(parsedExitCode);
        }

        return string.Equals(succeeded, "1", StringComparison.Ordinal)
            ? new ShutdownCountdownAbortResult(ShutdownCountdownAbortStatus.Aborted)
            : null;
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("/a");
        return startInfo;
    }
}
