using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
using Sdat.Windows.Hosting;
using Sdat.Windows.Migration;
using Sdat.Windows.Startup;
using Windows.Graphics;

namespace Sdat.App;

public sealed partial class MainWindow : Window
{
    private SdatRuntime? _runtime;
    private bool _companionMode;

    internal event Action<AppSettings>? CompanionSettingsApplying;

    public MainWindow(SdatRuntime? runtime = null)
    {
        _runtime = runtime;
        InitializeComponent();
        Title = "SDAT";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1040, 720));
        ExtendsContentIntoTitleBar = true;
        RootGrid.Loaded += OnLoaded;
        ShellNav.SelectedItem = ShellNav.MenuItems[0];
        AppWindow.Closing += OnWindowClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnLoaded;
        try
        {
            _runtime ??= await SdatRuntime.CreateAsync(Environment.ProcessPath!);
            ApplySettings(_runtime.CurrentSettings);
            DatabasePathText.Text = $"Database: {_runtime.StoreOptions.DatabasePath}";
            await RefreshStatusAsync();
            if (_runtime.LegacyMigration.Status == LegacyMigrationStatus.Failed)
            {
                ShowStatus(
                    string.Join(" ", _runtime.LegacyMigration.Warnings),
                    InfoBarSeverity.Warning);
            }
            if (!_runtime.StartupReconciliation.IsHealthy)
            {
                ShowStatus("The database is healthy, but some Windows tasks could not be repaired.", InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer?.Tag as string) ?? "overview";
        OverviewView.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ScheduleView.Visibility = tag == "schedule" ? Visibility.Visible : Visibility.Collapsed;
        NotificationsView.Visibility = tag == "notifications" ? Visibility.Visible : Visibility.Collapsed;
        HotkeyTrayView.Visibility = tag == "hotkey" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedView.Visibility = tag == "advanced" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenSchedule(object sender, RoutedEventArgs e) => ShellNav.SelectedItem = ShellNav.MenuItems[1];

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeepDailyToggle is not null)
        {
            KeepDailyToggle.Visibility = GetSelectedTag(KindPicker) == "OneTime"
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private async void OnSchedule(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            var kind = Enum.Parse<ScheduleKind>(GetSelectedTag(KindPicker));
            var action = Enum.Parse<PowerActionType>(GetSelectedTag(ActionPicker));
            var prepared = new ScheduleInputService().Prepare(
                TimeInput.Text,
                kind,
                action,
                KeepDailyToggle.IsOn,
                DateTimeOffset.UtcNow,
                TimeZoneInfo.Local);
            var result = await _runtime.ScheduleCommands.SetAsync(prepared.Draft);
            await RefreshStatusAsync();
            ShellNav.SelectedItem = ShellNav.MenuItems[0];
            ShowStatus(
                result.IsFullyApplied ? "Schedule saved." : "Schedule saved with recovery warnings.",
                result.IsFullyApplied ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnCancelOneTime(object sender, RoutedEventArgs e) => await CancelAsync(ScheduleKind.OneTime);

    private async void OnCancelDaily(object sender, RoutedEventArgs e) => await CancelAsync(ScheduleKind.Daily);

    private async Task CancelAsync(ScheduleKind kind)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            var settings = await _runtime.Settings.LoadAsync();
            await _runtime.Coordinator.CancelAsync(kind, settings.ReminderOffsetsMinutes);
            await RefreshStatusAsync();
            ShowStatus("Schedule cancelled.", InfoBarSeverity.Success);
        }
        catch (KeyNotFoundException)
        {
            ShowStatus("There is no active schedule in that slot.", InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            var previous = await _runtime.Settings.LoadAsync();
            var offsets = ReminderOffsetsInput.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            var candidate = new AppSettings
            {
                ReminderOffsetsMinutes = offsets,
                CriticalOverlayEnabled = CriticalOverlayToggle.IsOn,
                StartCompanionAtLogin = StartupToggle.IsOn,
                DailyOverlapWindowMinutes = checked((int)DailyOverlapInput.Value),
                PaletteHotkey = PaletteHotkeyInput.Text,
            }.Validate();
            try
            {
                CompanionSettingsApplying?.Invoke(candidate);
                new StartupRegistrationService(Environment.ProcessPath!).SetEnabled(candidate.StartCompanionAtLogin);
                var settings = await _runtime.Settings.SaveAsync(candidate);
                await _runtime.Coordinator.ReconcileAsync(settings.ReminderOffsetsMinutes);
                ApplySettings(settings);
            }
            catch (Exception applyException)
            {
                try
                {
                    CompanionSettingsApplying?.Invoke(previous);
                    new StartupRegistrationService(Environment.ProcessPath!).SetEnabled(previous.StartCompanionAtLogin);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Settings could not be applied and the previous companion configuration could not be fully restored.",
                        applyException,
                        rollbackException);
                }

                throw;
            }
            ShowStatus("Settings saved.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnReconcile(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        var settings = await _runtime.Settings.LoadAsync();
        var report = await _runtime.Coordinator.ReconcileAsync(settings.ReminderOffsetsMinutes);
        ShowStatus(
            report.IsHealthy
                ? $"Projection healthy. {report.CreatedOrUpdatedCount} repaired, {report.RemovedCount} removed."
                : "Reconciliation completed with warnings.",
            report.IsHealthy ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        var schedules = await _runtime.Schedules.ListAsync();
        var oneTime = schedules.SingleOrDefault(schedule => schedule.Kind == ScheduleKind.OneTime);
        var daily = schedules.SingleOrDefault(schedule => schedule.Kind == ScheduleKind.Daily);
        OneTimeStatusText.Text = oneTime is null
            ? "No one-time action scheduled."
            : $"{oneTime.Action} at {oneTime.TargetAt!.Value.ToLocalTime():ddd HH:mm}";
        DailyStatusText.Text = daily is null
            ? "No daily action scheduled."
            : $"{daily.Action} every day at {daily.DailyAt:HH:mm}";
    }

    internal async Task RefreshAfterExternalChangeAsync()
    {
        await RefreshStatusAsync();
        ShowStatus("Schedule cancelled from the notification.", InfoBarSeverity.Success);
    }

    internal void ShowNotificationInitializationWarning(string detail) =>
        ShowStatus(
            $"Windows notifications are unavailable. Critical overlays remain enabled. {detail}",
            InfoBarSeverity.Warning);

    internal void ShowHotkeyInitializationWarning(string detail) =>
        ShowStatus($"The tray companion is running, but the palette hotkey is unavailable. {detail}", InfoBarSeverity.Warning);

    internal void EnableCompanionMode() => _companionMode = true;

    internal void DisableCompanionMode() => _companionMode = false;

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_companionMode)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        ReminderOffsetsInput.Text = string.Join(", ", settings.ReminderOffsetsMinutes);
        CriticalOverlayToggle.IsOn = settings.CriticalOverlayEnabled;
        StartupToggle.IsOn = settings.StartCompanionAtLogin;
        DailyOverlapInput.Value = settings.DailyOverlapWindowMinutes;
        PaletteHotkeyInput.Text = settings.PaletteHotkey;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private static string GetSelectedTag(ComboBox comboBox) =>
        ((ComboBoxItem)comboBox.SelectedItem).Tag?.ToString()
        ?? throw new InvalidOperationException("Select a value.");
}
