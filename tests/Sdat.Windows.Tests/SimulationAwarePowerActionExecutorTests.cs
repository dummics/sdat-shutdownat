using Sdat.Core.Diagnostics;
using Sdat.Core.Execution;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
using Sdat.Windows.Execution;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class SimulationAwarePowerActionExecutorTests
{
    [Fact]
    public async Task Safe_test_mode_suppresses_the_inner_power_executor()
    {
        var inner = new FakeExecutor();
        var logger = new FakeLogger();
        var executor = new SimulationAwarePowerActionExecutor(
            new FakeSettingsRepository(new AppSettings
            {
                DeveloperModeEnabled = true,
                SimulationModeEnabled = true,
            }),
            inner,
            logger);

        await Assert.ThrowsAsync<PowerActionSimulatedException>(() =>
            executor.ExecuteAsync(PowerActionType.Shutdown));

        Assert.Equal(0, inner.CallCount);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task Normal_mode_delegates_to_the_real_executor()
    {
        var inner = new FakeExecutor();
        var executor = new SimulationAwarePowerActionExecutor(
            new FakeSettingsRepository(new AppSettings()),
            inner,
            new FakeLogger());

        await executor.ExecuteAsync(PowerActionType.Restart);

        Assert.Equal(1, inner.CallCount);
    }

    private sealed class FakeExecutor : IPowerActionExecutor
    {
        public int CallCount { get; private set; }

        public Task ExecuteAsync(
            PowerActionType action,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLogger : IAppLogger
    {
        public List<(AppLogLevel Level, string Source, string Message)> Entries { get; } = [];

        public Task WriteAsync(
            AppLogLevel level,
            string source,
            string message,
            CancellationToken cancellationToken = default)
        {
            Entries.Add((level, source, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettingsRepository(AppSettings settings) : IAppSettingsRepository
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings.Validate());

        public Task<AppSettings> SaveAsync(
            AppSettings value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(value.Validate());
    }
}
