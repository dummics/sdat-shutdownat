using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using System.Diagnostics;
using Sdat.Core.Commands;
using Sdat.Core.Execution;
using Sdat.Core.Scheduling;
using Sdat.Windows.Hosting;

namespace Sdat.App;

public partial class App : Application
{
    private const string CompanionInstanceKey = "ShutdownAT.UserCompanion";
    private Window? _window;
    private AppNotificationManager? _notificationManager;
    private CompanionController? _companion;
    private AppInstance? _mainInstance;
    private string? _notificationInitializationError;

    public App()
    {
        AppLanguageService.ApplyBeforeResourcesLoad();
        InitializeComponent();
        if (!Environment.GetCommandLineArgs().Contains("--task-run", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _notificationManager = AppNotificationManager.Default;
                _notificationManager.NotificationInvoked += OnNotificationInvoked;
                _notificationManager.Register();
            }
            catch (Exception exception)
            {
                _notificationManager = null;
                _notificationInitializationError = exception.Message;
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

        if (await RedirectToExistingCompanionAsync())
        {
            Exit();
            return;
        }

        try
        {
            var runtime = await SdatRuntime.CreateAsync(Environment.ProcessPath!);
            var mainWindow = new MainWindow(runtime);
            _window = mainWindow;
            if (_notificationInitializationError is not null)
            {
                mainWindow.ShowNotificationInitializationWarning(_notificationInitializationError);
            }
            var background = commandLine.Contains("--background", StringComparer.OrdinalIgnoreCase);
            var keepRunningInBackground = background || runtime.CurrentSettings.StartCompanionAtLogin;
            if (keepRunningInBackground)
            {
                mainWindow.EnableCompanionMode();
            }

            _companion = new CompanionController(
                runtime,
                mainWindow,
                ExitCompanion,
                keepRunningInBackground);
            mainWindow.CompanionSettingsApplying += settings =>
            {
                var shouldKeepRunning = background || settings.StartCompanionAtLogin;
                _companion.ApplySettings(settings, shouldKeepRunning);
                if (shouldKeepRunning)
                {
                    mainWindow.EnableCompanionMode();
                }
                else
                {
                    mainWindow.DisableCompanionMode();
                }
            };
            if (_companion.HotkeyRegistrationError is not null)
            {
                mainWindow.ShowHotkeyInitializationWarning(_companion.HotkeyRegistrationError);
            }

            if (!background)
            {
                mainWindow.Activate();
            }
            else
            {
                // An unpackaged WinUI process exits if no top-level window has
                // ever been initialized. Create its HWND once, then keep only
                // the per-user companion/tray surface alive.
                mainWindow.Activate();
                mainWindow.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => mainWindow.AppWindow.Hide());
            }
        }
        catch (Exception exception)
        {
            WriteBootstrapFailure(exception);
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

    private async Task<bool> RedirectToExistingCompanionAsync()
    {
        try
        {
            var current = AppInstance.GetCurrent();
            var registered = AppInstance.FindOrRegisterForKey(CompanionInstanceKey);
            if (!registered.IsCurrent)
            {
                await registered.RedirectActivationToAsync(current.GetActivatedEventArgs());
                return true;
            }

            _mainInstance = registered;
            _mainInstance.Activated += OnCompanionActivated;
        }
        catch
        {
            // App Lifecycle is a convenience for unpackaged single-instancing.
            // The companion remains usable if a Windows build cannot provide it.
        }

        return false;
    }

    private void OnCompanionActivated(object? sender, AppActivationArguments args)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_companion is not null)
            {
                _companion.ShowMainWindow();
                return;
            }

            if (_window is MainWindow mainWindow)
            {
                mainWindow.AppWindow.Show();
            }

            _window?.Activate();
        });
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

    private static void WriteBootstrapFailure(Exception exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SDAT");
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, "bootstrap-errors.log"),
                $"{DateTimeOffset.UtcNow:O} {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
        }
        catch
        {
            // Startup diagnostics must never replace the original failure.
        }
    }

    private void ExitCompanion()
    {
        ReleaseMainInstance();
        _companion?.Dispose();
        _companion = null;
        if (_window is MainWindow mainWindow)
        {
            mainWindow.DisableCompanionMode();
            mainWindow.Close();
        }

        Exit();
    }

    internal void RestartForLanguageChange()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        ReleaseMainInstance();
        _companion?.Dispose();
        _companion = null;
        _window?.Close();
        Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
        });
        Exit();
    }

    private void ReleaseMainInstance()
    {
        if (_mainInstance is null)
        {
            return;
        }

        _mainInstance.Activated -= OnCompanionActivated;
        try
        {
            _mainInstance.UnregisterKey();
        }
        catch
        {
            // Process shutdown still releases the registration.
        }

        _mainInstance = null;
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
            return (result.Outcome is TaskInvocationOutcome.ReminderShown or
                    TaskInvocationOutcome.ReminderDegraded) &&
                   settings.CriticalOverlayEnabled &&
                   schedule is not null &&
                   schedule.Action is PowerActionType.Shutdown or PowerActionType.Restart
                ? new CriticalOverlayWindow(
                    runtime,
                    schedule,
                    invocation.ReminderOffsetMinutes ?? 2,
                    settings.CriticalOverlayPlacement)
                : null;
        }
        catch
        {
            // Task Scheduler receives a fail-safe no-op; diagnostics are persisted where possible.
            return null;
        }
    }
}
