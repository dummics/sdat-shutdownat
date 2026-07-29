using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using System.Diagnostics;
using Sdat.Core.Commands;
using Sdat.Core.Execution;
using Sdat.Core.Scheduling;
using Sdat.Windows.Execution;
using Sdat.Windows.Hosting;
using Sdat.Windows.Notifications;
using Sdat.Windows.Startup;

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
                var palette = new QuickPaletteWindow(runtime);
                _window = palette;
                _window.Closed += (_, _) => Exit();
                palette.ShowAndFocus();
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
            var action = ReminderNotificationActionParser.Parse(notificationArgs.Argument);
            if (action.Kind == ReminderNotificationActionKind.Cancel)
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
            var applicationPath = Environment.ProcessPath!;
            var runtime = await SdatRuntime.CreateAsync(applicationPath);
            string? startupRegistrationError = null;
            if (IsInstalledPackage(applicationPath))
            {
                try
                {
                    new StartupRegistrationService(applicationPath)
                        .SetEnabled(runtime.CurrentSettings.StartCompanionAtLogin);
                }
                catch (Exception exception)
                {
                    startupRegistrationError = exception.Message;
                }
            }

            var mainWindow = new MainWindow(runtime);
            _window = mainWindow;
            if (_notificationInitializationError is not null)
            {
                mainWindow.ShowNotificationInitializationWarning(_notificationInitializationError);
            }
            if (startupRegistrationError is not null)
            {
                mainWindow.ShowStartupInitializationWarning(startupRegistrationError);
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
            mainWindow.QuickPaletteRequested += _companion.ShowPalette;
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
        var action = ReminderNotificationActionParser.Parse(args.Argument);
        if (action.Kind == ReminderNotificationActionKind.Cancel)
        {
            await CancelFromNotificationAsync(action);
            if (_window is MainWindow mainWindow)
            {
                mainWindow.DispatcherQueue.TryEnqueue(async () => await mainWindow.RefreshAfterExternalChangeAsync());
            }

            return;
        }

        if (action.Kind == ReminderNotificationActionKind.Open)
        {
            OpenMainWindowFromNotification();
        }
    }

    private void OpenMainWindowFromNotification()
    {
        if (_window is MainWindow mainWindow)
        {
            mainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (_companion is not null)
                {
                    _companion.ShowMainWindow();
                    return;
                }

                mainWindow.AppWindow.Show();
                mainWindow.Activate();
            });
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // The critical overlay remains available if Windows cannot open the main app.
        }
    }

    private static async Task CancelFromNotificationAsync(ReminderNotificationAction action)
    {
        if (action.ScheduleId is not { } scheduleId || action.Revision is not { } revision)
        {
            return;
        }

        var initialAbort = await WindowsShutdownCountdownAborter.TryAbortAsync();
        SdatRuntime? runtime = null;
        var cancellationGuardCompleted = false;
        string? failureDetail = null;
        try
        {
            runtime = await SdatRuntime.CreateAsync(Environment.ProcessPath!);
            var schedule = await runtime.Schedules.GetAsync(scheduleId);
            if (schedule is null)
            {
                return;
            }

            var matchesActiveSchedule =
                schedule.Status == ScheduleStatus.Active && schedule.Revision == revision;
            var matchesJustCompletedOneTime =
                schedule.Kind == ScheduleKind.OneTime &&
                schedule.Status == ScheduleStatus.Completed &&
                schedule.Revision == revision + 1;
            if (!matchesActiveSchedule && !matchesJustCompletedOneTime)
            {
                return;
            }

            var result = await AppScheduleCancellation.CancelAsync(
                runtime,
                schedule,
                expectedRevision: revision,
                initialAbort: initialAbort);
            cancellationGuardCompleted = true;
            if (!result.IsSafe)
            {
                failureDetail = AppText.Format(
                    "NotificationCancelFailedBody",
                    "Windows could not confirm cancellation. Open ShutdownAT or retry sdat -a. Details: {0}",
                    result.ErrorDetail ?? "Unknown error");
            }
        }
        catch (Exception exception)
        {
            failureDetail = AppText.Format(
                "NotificationCancelFailedBody",
                "Windows could not confirm cancellation. Open ShutdownAT or retry sdat -a. Details: {0}",
                exception.Message);
        }
        finally
        {
            if (!cancellationGuardCompleted)
            {
                var finalAbort = await WindowsShutdownCountdownAborter.TryAbortAsync();
                if (finalAbort.Status == ShutdownCountdownAbortStatus.Failed)
                {
                    var abortDetail =
                        finalAbort.Detail ?? $"shutdown.exe exited with code {finalAbort.ExitCode}.";
                    failureDetail ??= AppText.Format(
                        "NotificationCancelFailedBody",
                        "Windows could not confirm cancellation. Open ShutdownAT or retry sdat -a. Details: {0}",
                        abortDetail);
                    if (!failureDetail.Contains(abortDetail, StringComparison.Ordinal))
                    {
                        failureDetail += " " + abortDetail;
                    }
                }
            }

            if (failureDetail is not null)
            {
                if (runtime is not null)
                {
                    try
                    {
                        await runtime.Logger.WriteAsync(
                            Sdat.Core.Settings.AppLogLevel.Error,
                            nameof(App),
                            failureDetail);
                    }
                    catch
                    {
                        // The foreground warning remains the primary failure signal.
                    }
                }

                await (runtime?.ReminderNotifications ?? new WindowsReminderNotifier())
                    .ShowTransientAsync(
                        AppText.Get("NotificationCancelFailedTitle", "Cancellation needs attention"),
                        failureDetail);
            }
        }
    }

    private static bool IsInstalledPackage(string applicationPath)
    {
        var directory = Path.GetDirectoryName(applicationPath);
        return !string.IsNullOrWhiteSpace(directory) &&
               File.Exists(Path.Combine(directory, ".sdat-package-manifest.json"));
    }

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
            if (!settings.CriticalOverlayEnabled ||
                schedule is null ||
                schedule.Action is not (PowerActionType.Shutdown or PowerActionType.Restart))
            {
                return null;
            }

            return result.Outcome switch
            {
                TaskInvocationOutcome.ReminderShown or TaskInvocationOutcome.ReminderDegraded =>
                    new CriticalOverlayWindow(
                        runtime,
                        schedule,
                        TimeSpan.FromMinutes(invocation.ReminderOffsetMinutes ?? 2),
                        settings.CriticalOverlayPlacement),
                TaskInvocationOutcome.Executed =>
                    new CriticalOverlayWindow(
                        runtime,
                        schedule,
                        TimeSpan.FromSeconds(30),
                        settings.CriticalOverlayPlacement,
                        isFinalWindowsCountdown: true),
                _ => null,
            };
        }
        catch
        {
            // Task Scheduler receives a fail-safe no-op; diagnostics are persisted where possible.
            return null;
        }
    }
}
