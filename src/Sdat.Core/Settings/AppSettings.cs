namespace Sdat.Core.Settings;

public sealed record AppSettings
{
    public IReadOnlyList<int> ReminderOffsetsMinutes { get; init; } = [2];

    public bool CriticalOverlayEnabled { get; init; } = true;

    public bool StartCompanionAtLogin { get; init; }

    public int DailyOverlapWindowMinutes { get; init; } = 120;

    public AppSettings Validate()
    {
        var offsets = ReminderOffsetsMinutes.Distinct().OrderDescending().ToArray();
        if (offsets.Length > 5 || offsets.Any(offset => offset is < 1 or > 1440))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReminderOffsetsMinutes),
                "Use at most five unique reminder offsets between 1 and 1440 minutes.");
        }

        if (DailyOverlapWindowMinutes is < 0 or > 1440)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DailyOverlapWindowMinutes),
                "The daily overlap window must be between 0 and 1440 minutes.");
        }

        return this with { ReminderOffsetsMinutes = offsets };
    }
}

public interface IAppSettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task<AppSettings> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
