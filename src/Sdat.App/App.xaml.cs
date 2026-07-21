using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Sdat.Core.Commands;
using Sdat.Core.Execution;
using Sdat.Core.Scheduling;
using Sdat.Windows.Hosting;

namespace Sdat.App;

public partial class App : Application
{
    private Window? _window;
    private AppNotificationManager? _notificationManager;
    private CompanionController? _companion;

    public App()
    {
        InitializeComponent();
        if (!Environment.GetCommandLineArgs().Contains("--task-run", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _notificationManager = AppNotificationManager.Default;
                _notificationManager.NotificationInvoked += OnNotificationInvoked;
                _notificationManager.Register();
            }
            catch
            {
                _notificationManager = null;
            }
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (commandLine.Contains("--task-run", StringComparer.OrdinalIgnoreCase))
        {
            _window = await RunScheduledInvocationAsync(commandLine);
            if (_window is null)
            {
                Exit();
                return;
            }

            _window.Closed += (_, _) => Exit();
            _window.Activate();
            return;
        }

        if (commandLine.Contains("--palette", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var runtime = await SdatRuntime.CreateAsync(Environment.ProcessPath!);
                _window = new QuickPaletteWindow(runtime);
                _window.Closed += (_, _) => Exit();
                _window.Activate();
            }
            catch
            {
                Exit();
            }

            return;
        }

        var activation = TryGetActivation();
        if (activation?.Kind == ExtendedActivationKind.AppNotification &&
            activation.Data is AppNotificationActivatedEventArgs notificationArgs)
        {
            var action = ParseArguments(notificationArgs.Argument);
            if (action.GetValueOrDefault("action") == "cancel")
            {
                await CancelFromNotificationAsync(action);
                Exit();
                return;
            }
        }

        try
        {
            var runtime = await SdatRuntime.CreateAsync(Environment.ProcessPath!);
            var mainWindow = new MainWindow(runtime);
            _window = mainWindow;
            var background = commandLine.Contains("--background", StringComparer.OrdinalIgnoreCase);
            if (background || runtime.CurrentSettings.StartCompanionAtLogin)
            {
                mainWindow.EnableCompanionMode();
                _companion = new CompanionController(runtime, mainWindow, ExitCompanion);
            }

            if (!background)
            {
                mainWindow.Activate();
            }
        }
        catch
        {
            Exit();
        }
    }

    private static AppActivationArguments? TryGetActivation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch
        {
            return null;
        }
    }

    private async void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        var action = ParseArguments(args.Argument);
        if (action.GetValueOrDefault("action") == "cancel")
        {
            await CancelFromNotificationAsync(action);
            if (_window is MainWindow mainWindow)
            {
                mainWindow.DispatcherQueue.TryEnqueue(async () => await mainWindow.RefreshAfterExternalChangeAsync());
            }

            return;
        }

        _window?.DispatcherQueue.TryEnqueue(() => _window.Activate());
    }

    private static async Task CancelFromNotificationAsync(IReadOnlyDictionary<string, string> arguments)
    {
        if (!Guid.TryParse(arguments.GetValueOrDefault("scheduleId"), out var scheduleId) ||
            !long.TryParse(
                arguments.GetValueOrDefault("revision"),
                System.Globalization.CultureInfo.InvariantCulture,
                out var revision))
        {
            return;
        }

        try
        {
            var runtime = await SdatRuntime.CreateAsync(Environment.ProcessPath!);
            var settings = await runtime.Settings.LoadAsync();
            await runtime.Coordinator.CancelExactAsync(
                scheduleId,
                revision,
                settings.ReminderOffsetsMinutes);
        }
        catch
        {
            // Stale notification actions and unhealthy state are deliberate no-ops.
        }
    }

    private static IReadOnlyDictionary<string, string> ParseArguments(string value) =>
        value.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part[1]),
                StringComparer.OrdinalIgnoreCase);

    private void ExitCompanion()
    {
        _companion?.Dispose();
        _companion = null;
        if (_window is MainWindow mainWindow)
        {
            mainWindow.DisableCompanionMode();
            mainWindow.Close();
        }

        Exit();
    }

    private static async Task<Window?> RunScheduledInvocationAsync(string[] commandLine)
    {
        try
        {
            var invocation = CliInvocationParser.Parse(commandLine);
            var runtime = await SdatRuntime.CreateAsync(Environment.ProcessPath!);
            var schedule = await runtime.Schedules.GetAsync(invocation.ScheduleId!.Value);
            var result = await runtime.TaskInvocations.RunAsync(new TaskInvocation(
                invocation.ScheduleId!.Value,
                invocation.Revision!.Value,
                invocation.TaskRole!.Value,
                invocation.ReminderOffsetMinutes));
            var settings = await runtime.Settings.LoadAsync();
            return result.Outcome == TaskInvocationOutcome.ReminderShown &&
                   settings.CriticalOverlayEnabled &&
                   schedule is not null &&
                   schedule.Action is PowerActionType.Shutdown or PowerActionType.Restart
                ? new CriticalOverlayWindow(
                    runtime,
                    schedule,
                    invocation.ReminderOffsetMinutes ?? 2)
                : null;
        }
        catch
        {
            // Task Scheduler receives a fail-safe no-op; diagnostics are persisted where possible.
            return null;
        }
    }
}
