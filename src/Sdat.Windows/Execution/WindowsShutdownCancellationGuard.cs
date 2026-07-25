namespace Sdat.Windows.Execution;

public sealed record WindowsShutdownCancellationGuardResult<T>(
    T? StateResult,
    Exception? StateError,
    ShutdownCountdownAbortResult InitialAbort,
    ShutdownCountdownAbortResult FinalAbort)
{
    public bool WindowsStateConfirmed =>
        FinalAbort.Status != ShutdownCountdownAbortStatus.Failed;

    public bool WasCountdownAborted =>
        InitialAbort.WasAborted || FinalAbort.WasAborted;

    public ShutdownCountdownAbortResult EffectiveAbort =>
        !WindowsStateConfirmed
            ? FinalAbort
            : WasCountdownAborted
                ? InitialAbort.WasAborted
                    ? InitialAbort
                    : FinalAbort
                : FinalAbort;
}

public static class WindowsShutdownCancellationGuard
{
    public static async Task<WindowsShutdownCancellationGuardResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> mutateState,
        ShutdownCountdownAbortResult? initialAbort = null,
        Func<CancellationToken, Task<ShutdownCountdownAbortResult>>? abortCountdown = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutateState);
        abortCountdown ??= WindowsShutdownCountdownAborter.TryAbortAsync;

        var before = initialAbort ??
                     await abortCountdown(cancellationToken).ConfigureAwait(false);
        T? stateResult = default;
        Exception? stateError = null;
        try
        {
            stateResult = await mutateState(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stateError = exception;
        }

        // This second probe closes the race where Task Scheduler starts the
        // native countdown after the first abort but before state cancellation.
        var after = await abortCountdown(CancellationToken.None).ConfigureAwait(false);
        return new WindowsShutdownCancellationGuardResult<T>(
            stateResult,
            stateError,
            before,
            after);
    }
}
