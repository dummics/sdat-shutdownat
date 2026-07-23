using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sdat.Core.Diagnostics;
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
        Title = "ShutdownAT";
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
            DatabasePathText.Text = AppText.Format(
                "DatabasePath",
                "Saved on this PC: {0}",
                _runtime.StoreOptions.DatabasePath);
            await RefreshStatusAsync();
            if (_runtime.StartupRecovery is not null)
            {
                ShowStatus(
                    AppText.Get(
                        "DatabaseRecovered",
                        "Your saved schedules were restored from the latest healthy backup."),
                    InfoBarSeverity.Warning);
            }
            if (_runtime.LegacyMigration.Status == LegacyMigrationStatus.Failed)
            {
                ShowStatus(
                    string.Join(" ", _runtime.LegacyMigration.Warnings),
                    InfoBarSeverity.Warning);
            }
            if (!_runtime.StartupReconciliation.IsHealthy)
            {
                ShowStatus(
                    AppText.Get(
                        "SchedulerRepairWarning",
                        "Your schedules are safe, but the Windows integration needs attention. Open Help & recovery to repair it."),
                    InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected
            ? "settings"
            : (args.SelectedItemContainer?.Tag as string) ?? "overview";
        OverviewView.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ScheduleView.Visibility = tag == "schedule" ? Visibility.Visible : Visibility.Collapsed;
        NotificationsView.Visibility = tag == "notifications" ? Visibility.Visible : Visibility.Collapsed;
        HotkeyTrayView.Visibility = tag == "hotkey" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedView.Visibility = tag == "advanced" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
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
                result.IsFullyApplied
                    ? AppText.Get("ScheduleSaved", "Schedule saved.")
                    : AppText.Get("ScheduleSavedWarnings", "Schedule saved, but the Windows integration needs attention."),
                result.IsFullyApplied ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnCancelOneTime(object sender, RoutedEventArgs e)
    {
        if (await ConfirmCancellationAsync(ScheduleKind.OneTime))
        {
            await CancelAsync(ScheduleKind.OneTime);
        }
    }

    private async void OnCancelDaily(object sender, RoutedEventArgs e)
    {
        if (await ConfirmCancellationAsync(ScheduleKind.Daily))
        {
            await CancelAsync(ScheduleKind.Daily);
        }
    }

    private async Task<bool> ConfirmCancellationAsync(ScheduleKind kind)
    {
        var dialog = new ContentDialog
        {
            Title = AppText.Get("CancelConfirmationTitle", "Cancel this schedule?"),
            Content = kind == ScheduleKind.Daily
                ? AppText.Get(
                    "CancelDailyConfirmationBody",
                    "The daily schedule will stop until you create it again.")
                : AppText.Get(
                    "CancelOneTimeConfirmationBody",
                    "The next one-time action will be removed."),
            PrimaryButtonText = AppText.Get("ConfirmCancelButton", "Cancel schedule"),
            CloseButtonText = AppText.Get("KeepScheduleButton", "Keep it"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

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
            ShowStatus(AppText.Get("ScheduleCancelled", "Schedule cancelled."), InfoBarSeverity.Success);
        }
        catch (KeyNotFoundException)
        {
            ShowStatus(
                AppText.Get("NoScheduleInSlot", "There is no active schedule in that slot."),
                InfoBarSeverity.Informational);
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
                PreferredLanguage = previous.PreferredLanguage,
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
            ShowStatus(AppText.Get("SettingsSaved", "Settings saved."), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnReconcile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_runtime is null)
            {
                return;
            }

            var settings = await _runtime.Settings.LoadAsync();
            var report = await _runtime.Coordinator.ReconcileAsync(settings.ReminderOffsetsMinutes);
            await RefreshDiagnosticsAsync();
            ShowStatus(
                report.IsHealthy
                    ? AppText.Format(
                        "ProjectionHealthy",
                        "Windows integration is ready. Fixed: {0}; removed old entries: {1}.",
                        report.CreatedOrUpdatedCount,
                        report.RemovedCount)
                    : AppText.Get("ReconciliationWarnings", "The repair finished, but some items still need attention."),
                report.IsHealthy ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnApplyLanguage(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            var requestedLanguage = UiLanguagePreference.Normalize(GetSelectedTag(LanguagePicker));
            var previous = await _runtime.Settings.LoadAsync();
            var saved = await _runtime.Settings.SaveAsync(previous with
            {
                PreferredLanguage = requestedLanguage,
            });
            ApplySettings(saved);

            var restartRequired = saved.PreferredLanguage != AppLanguageService.AppliedPreference;
            RestartLanguageButton.Visibility = restartRequired ? Visibility.Visible : Visibility.Collapsed;
            LanguageRestartHelp.Visibility = restartRequired ? Visibility.Visible : Visibility.Collapsed;
            ShowStatus(
                AppText.Get(
                    restartRequired ? "LanguageSavedRestartRequired" : "LanguageAlreadyActive",
                    restartRequired
                        ? "Language saved. Restart ShutdownAT to update every screen."
                        : "This language is already active."),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void OnRestartForLanguage(object sender, RoutedEventArgs e) =>
        (Application.Current as App)?.RestartForLanguageChange();

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshStatusAsync();
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        var schedules = await _runtime.Schedules.ListAsync();
        var oneTime = schedules.SingleOrDefault(schedule => schedule.Kind == ScheduleKind.OneTime);
        var daily = schedules.SingleOrDefault(schedule => schedule.Kind == ScheduleKind.Daily);
        OneTimeCancelButton.Visibility = oneTime is null ? Visibility.Collapsed : Visibility.Visible;
        DailyCancelButton.Visibility = daily is null ? Visibility.Collapsed : Visibility.Visible;
        OneTimeStatusText.Text = oneTime is null
            ? AppText.Get("NoOneTimeSchedule", "No one-time action scheduled.")
            : AppText.Format(
                "OneTimeScheduleStatus",
                "{0} at {1:ddd HH:mm}",
                AppText.PowerAction(oneTime.Action),
                oneTime.TargetAt!.Value.ToLocalTime());
        DailyStatusText.Text = daily is null
            ? AppText.Get("NoDailySchedule", "No daily action scheduled.")
            : AppText.Format(
                "DailyScheduleStatus",
                "{0} every day at {1:HH:mm}",
                AppText.PowerAction(daily.Action),
                daily.DailyAt);
        await RefreshDiagnosticsAsync();
    }

    private async Task RefreshDiagnosticsAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        var health = await _runtime.Schedules.CheckHealthAsync();
        DatabaseHealthText.Text = health.CanExecutePowerActions
            ? AppText.Get("DatabaseHealthy", "Everything is ready. Your schedules can run normally.")
            : AppText.Format(
                "DatabaseUnhealthy",
                "Schedules cannot run safely right now. Details: {0}",
                health.Detail);

        var events = await _runtime.Diagnostics.ReadRecentAsync(20);
        DiagnosticsList.ItemsSource = events
            .Select(entry => new DiagnosticViewItem(
                entry.OccurredAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                AppText.Get($"Severity{entry.Severity}", entry.Severity.ToString()),
                GetDiagnosticTitle(entry),
                GetDiagnosticMessage(entry)))
            .ToArray();
        DiagnosticsEmptyText.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsList.Visibility = events.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal async Task RefreshAfterExternalChangeAsync()
    {
        await RefreshStatusAsync();
        ShowStatus(
            AppText.Get("NotificationCancelled", "Schedule cancelled from the notification."),
            InfoBarSeverity.Success);
    }

    internal void ShowNotificationInitializationWarning(string detail) =>
        ShowStatus(
            AppText.Format(
                "NotificationUnavailable",
                "Windows notifications are unavailable. The on-screen countdown will still appear. Details: {0}",
                detail),
            InfoBarSeverity.Warning);

    internal void ShowHotkeyInitializationWarning(string detail) =>
        ShowStatus(
            AppText.Format(
                "HotkeyUnavailable",
                "ShutdownAT is running in the notification area, but the keyboard shortcut could not be enabled. Details: {0}",
                detail),
            InfoBarSeverity.Warning);

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
        SelectTag(LanguagePicker, settings.PreferredLanguage);
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

    private static void SelectTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDiagnosticTitle(DiagnosticEvent entry) => entry.Source switch
    {
        "CreateSchedule" => AppText.Get("DiagnosticScheduleCreated", "Schedule created"),
        "UpdateSchedule" => AppText.Get("DiagnosticScheduleUpdated", "Schedule updated"),
        "CancelSchedule" => AppText.Get("DiagnosticScheduleCancelled", "Schedule cancelled"),
        "RequestDailySkip" => AppText.Get("DiagnosticDailySkipped", "Daily action skipped"),
        "ClaimOccurrence" or "Occurrence" =>
            AppText.Get("DiagnosticScheduledAction", "Scheduled action"),
        _ => AppText.Get("DiagnosticGenericTitle", "ShutdownAT activity"),
    };

    private static string GetDiagnosticMessage(DiagnosticEvent entry)
    {
        var separator = entry.Message.LastIndexOf(": ", StringComparison.Ordinal);
        var outcome = separator >= 0 ? entry.Message[(separator + 2)..] : entry.Message;
        return outcome switch
        {
            "Success" or "Completed" or "Executed" or "Claimed" =>
                AppText.Get("DiagnosticCompleted", "Completed successfully."),
            "ReminderShown" => AppText.Get("DiagnosticReminderShown", "The reminder was shown."),
            "Skipped" or "SkippedByRequest" =>
                AppText.Get("DiagnosticSkippedAsRequested", "Skipped as requested."),
            "Stale" or "AlreadyHandled" or "IgnoredStale" or "IgnoredDuplicate" or "IgnoredEarly" =>
                AppText.Get("DiagnosticNoActionNeeded", "No action was needed."),
            "ReminderDegraded" =>
                AppText.Get("DiagnosticFallbackUsed", "The on-screen reminder was used instead."),
            _ when entry.Severity == DiagnosticSeverity.Error =>
                AppText.Format("DiagnosticFailed", "Something went wrong. Details: {0}", entry.Message),
            _ => entry.Message,
        };
    }

    private sealed record DiagnosticViewItem(
        string OccurredAt,
        string Severity,
        string Source,
        string Message);
}
