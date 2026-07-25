using Sdat.Core.Diagnostics;
using Sdat.Core.Execution;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;

namespace Sdat.Windows.Execution;

public sealed class SimulationAwarePowerActionExecutor(
    IAppSettingsRepository settingsRepository,
    IPowerActionExecutor inner,
    IAppLogger logger) : IPowerActionExecutor
{
    public async Task ExecuteAsync(
        PowerActionType action,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsTestMode)
        {
            await inner.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
            return;
        }

        await logger.WriteAsync(
                AppLogLevel.Information,
                nameof(SimulationAwarePowerActionExecutor),
                $"Suppressed {action} because safe test mode is active.",
                cancellationToken)
            .ConfigureAwait(false);
        throw new PowerActionSimulatedException(action);
    }
}
