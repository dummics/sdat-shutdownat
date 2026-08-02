using System.Text.Json;
using Sdat.Core.Operations;

namespace Sdat.Windows.Execution;

public sealed record ScheduleCancellationSignal(
    Guid? ScheduleId,
    long? Revision,
    bool WindowsCountdownAborted,
    DateTimeOffset OccurredAtUtc)
{
    public bool Matches(
        Guid scheduleId,
        long revision,
        DateTimeOffset surfaceOpenedAtUtc)
    {
        if (OccurredAtUtc <= surfaceOpenedAtUtc.ToUniversalTime())
        {
            return false;
        }

        if (ScheduleId is null)
        {
            return WindowsCountdownAborted;
        }

        return ScheduleId == scheduleId &&
               (Revision is null || Revision == revision);
    }
}

public sealed class ScheduleCancellationSignalStore(
    string path,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task PublishAsync(
        Guid? scheduleId,
        long? revision,
        bool windowsCountdownAborted,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The cancellation signal path has no parent directory.");
        Directory.CreateDirectory(directory);
        var signal = new ScheduleCancellationSignal(
            scheduleId,
            revision,
            windowsCountdownAborted,
            _timeProvider.GetUtcNow());
        var json = JsonSerializer.Serialize(signal, JsonOptions);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A stale temporary signal is harmless and can be replaced later.
            }
        }
    }

    public async Task<ScheduleCancellationSignal?> ReadLatestAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<ScheduleCancellationSignal>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or
            DirectoryNotFoundException or
            IOException or
            JsonException)
        {
            return null;
        }
    }
}

public static class ScheduleCancellationSignalPublisher
{
    public static async Task PublishAvailableAsync(
        ScheduleCancellationSignalStore store,
        IReadOnlyList<ScheduleMutationResult> results,
        WindowsShutdownCancellationGuardResult<IReadOnlyList<ScheduleMutationResult>> guard,
        CancellationToken cancellationToken = default)
    {
        if (!guard.WindowsStateConfirmed)
        {
            return;
        }

        if (results.Count == 0)
        {
            if (guard.WasCountdownAborted)
            {
                await store.PublishAsync(
                        scheduleId: null,
                        revision: null,
                        windowsCountdownAborted: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        foreach (var result in results)
        {
            await store.PublishAsync(
                    result.Schedule.Id,
                    Math.Max(1, result.Schedule.Revision - 1),
                    guard.WasCountdownAborted,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (guard.WasCountdownAborted)
        {
            await store.PublishAsync(
                    scheduleId: null,
                    revision: null,
                    windowsCountdownAborted: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public static async Task PublishExactAsync(
        ScheduleCancellationSignalStore store,
        Guid scheduleId,
        long revision,
        bool scheduleSettled,
        WindowsShutdownCancellationGuardResult<bool> guard,
        CancellationToken cancellationToken = default)
    {
        if (!scheduleSettled || !guard.WindowsStateConfirmed)
        {
            return;
        }

        await store.PublishAsync(
                scheduleId,
                revision,
                guard.WasCountdownAborted,
                cancellationToken)
            .ConfigureAwait(false);
        if (guard.WasCountdownAborted)
        {
            await store.PublishAsync(
                    scheduleId: null,
                    revision: null,
                    windowsCountdownAborted: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
