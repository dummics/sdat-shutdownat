using Sdat.Core.Storage;

namespace Sdat.Core.Scheduling;

public interface IScheduleRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<ScheduleSnapshot> CreateAsync(ScheduleDraft draft, CancellationToken cancellationToken = default);

    Task<ScheduleSnapshot> UpdateAsync(
        Guid id,
        long expectedRevision,
        ScheduleDraft draft,
        CancellationToken cancellationToken = default);

    Task<ScheduleSnapshot> CancelAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<ScheduleSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleSnapshot>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    Task<StoreHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public sealed class ScheduleConflictException(string message) : InvalidOperationException(message);
