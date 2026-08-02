using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sdat.Core.Diagnostics;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
using Sdat.Core.TimeExpressions;
using Sdat.Windows.Hosting;
using Sdat.Windows.Migration;
using Sdat.Windows.Startup;
using Windows.Graphics;

namespace Sdat.App;

public sealed partial class MainWindow : Window
{
    private SdatRuntime? _runtime;
    private bool _companionMode;
    private bool _applyingSettings;
    private CriticalOverlayWindow? _testOverlay;
    private readonly DispatcherTimer _statusDismissTimer = new();
    private readonly DispatcherTimer _schedulePreviewTimer =
        new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly ScheduleInputService _scheduleInputService = new();
    private bool _scheduleBusy;
    private ScheduleSnapshot? _oneTimeSchedule;
    private ScheduleSnapshot? _dailySchedule;
    private bool _backgroundHintShown;

    internal event Action<AppSettings>? CompanionSettingsApplying;
    internal event Action? QuickPaletteRequested;
    internal event Action<string>? BackgroundHintRequested;

    public MainWindow(SdatRuntime? runtime = null)
    {
        _runtime = runtime;
        InitializeComponent();
        _statusDismissTimer.Tick += OnStatusDismissTimerTick;
        _schedulePreviewTimer.Tick += OnSchedulePreviewTimerTick;
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
                        "Your schedules are safe, but the Windows integration needs attention. Open Diagnostics in Settings to repair it."),
                    InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            ShowStatus(
                exception is TestModeScheduleBlockedException
                    ? AppText.Get(
                        "TestModeScheduleBlocked",
                        "Safe test mode is active. Turn it off before creating a real schedule.")
                    : exception.Message,
                InfoBarSeverity.Error);
        }
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected
            ? "settings"
            : (args.SelectedItemContainer?.Tag as string) ?? "overview";
        OverviewView.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ScheduleView.Visibility = tag == "schedule" ? Visibility.Visible : Visibility.Collapsed;
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

        QueueSchedulePreview();
    }

    private async void OnSchedule(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        SetScheduleBusy(true);
        try
        {
            var kind = Enum.Parse<ScheduleKind>(GetSelectedTag(KindPicker));
            var action = Enum.Parse<PowerActionType>(GetSelectedTag(ActionPicker));
            var now = DateTimeOffset.UtcNow;
            var preview = _scheduleInputService.Preview(
                TimeInput.Text,
                kind,
                action,
                KeepDailyToggle.IsOn,
                now,
                TimeZoneInfo.Local);
            if (!preview.IsValid)
            {
                ShowScheduleInputError(preview.ErrorCode);
                return;
            }

            var prepared = _scheduleInputService.Prepare(
                TimeInput.Text,
                kind,
                action,
                KeepDailyToggle.IsOn,
                now,
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
        catch (TimeExpressionParseException exception)
        {
            ShowScheduleInputError(exception.ErrorCode);
        }
        catch (Exception exception)
        {
            ShowUnexpectedError(
                exception,
                "UnableToSchedule",
                "ShutdownAT could not create this schedule. Try again or open Diagnostics.");
        }
        finally
        {
            SetScheduleBusy(false);
        }
    }

    private void OnScheduleInputChanged(object sender, RoutedEventArgs e) => QueueSchedulePreview();

    private void QueueSchedulePreview()
    {
        if (TimeInput is null || ActionPicker is null || KindPicker is null || MainScheduleButton is null)
        {
            return;
        }

        _schedulePreviewTimer.Stop();
        _schedulePreviewTimer.Start();
    }

    private void OnSchedulePreviewTimerTick(object? sender, object e)
    {
        _schedulePreviewTimer.Stop();
        UpdateSchedulePreview();
    }

    private void UpdateSchedulePreview()
    {
        if (_scheduleBusy || ActionPicker.SelectedItem is null || KindPicker.SelectedItem is null)
        {
            return;
        }

        ScheduleInputErrorText.Visibility = Visibility.Collapsed;
        ScheduleInputErrorText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(TimeInput.Text))
        {
            SchedulePreviewPanel.Visibility = Visibility.Collapsed;
            MainScheduleButton.Content = AppText.Get("ScheduleButtonDefault", "Schedule");
            MainScheduleButton.IsEnabled = false;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var preview = _scheduleInputService.Preview(
            TimeInput.Text,
            Enum.Parse<ScheduleKind>(GetSelectedTag(KindPicker)),
            Enum.Parse<PowerActionType>(GetSelectedTag(ActionPicker)),
            KeepDailyToggle.IsOn,
            now,
            TimeZoneInfo.Local);
        if (!preview.IsValid)
        {
            ShowScheduleInputError(preview.ErrorCode);
            return;
        }

        SchedulePreviewText.Text = SchedulePreviewFormatter.Format(
            preview,
            now,
            TimeZoneInfo.Local);
        SchedulePreviewPanel.Visibility = Visibility.Visible;
        MainScheduleButton.Content = SchedulePreviewFormatter.FormatButton(preview);
        MainScheduleButton.IsEnabled = true;
    }

    private void ShowScheduleInputError(ScheduleInputErrorCode? errorCode)
    {
        SchedulePreviewPanel.Visibility = Visibility.Collapsed;
        ScheduleInputErrorText.Text = SchedulePreviewFormatter.FormatError(errorCode);
        ScheduleInputErrorText.Visibility = Visibility.Visible;
        MainScheduleButton.Content = AppText.Get("ScheduleButtonDefault", "Schedule");
        MainScheduleButton.IsEnabled = false;
    }

    private void SetScheduleBusy(bool busy)
    {
        _scheduleBusy = busy;
        MainScheduleButton.IsEnabled = !busy && HasValidScheduleInput();
        ScheduleProgress.IsActive = busy;
        ScheduleProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ActionPicker.IsEnabled = !busy;
        KindPicker.IsEnabled = !busy;
        TimeInput.IsEnabled = !busy;
        KeepDailyToggle.IsEnabled = !busy;
    }

    private bool HasValidScheduleInput()
    {
        if (ActionPicker.SelectedItem is null ||
            KindPicker.SelectedItem is null ||
            string.IsNullOrWhiteSpace(TimeInput.Text))
        {
            return false;
        }

        return _scheduleInputService.Preview(
            TimeInput.Text,
            Enum.Parse<ScheduleKind>(GetSelectedTag(KindPicker)),
            Enum.Parse<PowerActionType>(GetSelectedTag(ActionPicker)),
            KeepDailyToggle.IsOn,
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Local).IsValid;
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

    private async void OnExtendOneTime(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _oneTimeSchedule?.TargetAt is not { } targetAt)
        {
            return;
        }

        SetOverviewBusy(true);
        try
        {
            var schedule = _oneTimeSchedule;
            var settings = await _runtime.Settings.LoadAsync();
            await _runtime.Coordinator.UpdateExactAsync(
                schedule.Id,
                schedule.Revision,
                ScheduleDraft.OneTime(
                    schedule.Action,
                    targetAt.AddMinutes(10),
                    schedule.TimeZoneId,
                    schedule.KeepDaily),
                settings.ReminderOffsetsMinutes);
            await RefreshStatusAsync();
            ShowStatus(AppText.Get("OneTimeExtended", "Moved 10 minutes later."), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowUnexpectedError(
                exception,
                "UnableToChangeSchedule",
                "ShutdownAT could not update this schedule. Refresh the page and try again.");
            await RefreshStatusAsync();
        }
        finally
        {
            SetOverviewBusy(false);
        }
    }

    private async void OnSkipDaily(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _dailySchedule is null)
        {
            return;
        }

        SetOverviewBusy(true);
        try
        {
            var settings = await _runtime.Settings.LoadAsync();
            if (settings.IsTestMode)
            {
                ShowStatus(
                    AppText.Get("TestModeScheduleBlocked", "Test mode blocks schedule changes."),
                    InfoBarSeverity.Warning);
                return;
            }

            var result = await _runtime.DailySkips.RequestNextAsync();
            await RefreshStatusAsync();
            ShowStatus(
                AppText.Format(
                    "DailySkipped",
                    "The next daily action at {0:g} will be skipped.",
                    result.Request.ExecuteDueAt.ToLocalTime()),
                result.IsFullyPersisted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowUnexpectedError(
                exception,
                "UnableToChangeSchedule",
                "ShutdownAT could not update this schedule. Refresh the page and try again.");
            await RefreshStatusAsync();
        }
        finally
        {
            SetOverviewBusy(false);
        }
    }

    private void OnModifyOneTime(object sender, RoutedEventArgs e) => OpenScheduleEditor(_oneTimeSchedule);

    private void OnModifyDaily(object sender, RoutedEventArgs e) => OpenScheduleEditor(_dailySchedule);

    private void OpenScheduleEditor(ScheduleSnapshot? schedule)
    {
        if (schedule is null)
        {
            return;
        }

        SelectTag(ActionPicker, schedule.Action.ToString());
        SelectTag(KindPicker, schedule.Kind.ToString());
        TimeInput.Text = schedule.Kind == ScheduleKind.Daily
            ? schedule.DailyAt?.ToString("HH:mm")
            : schedule.TargetAt?.ToLocalTime().ToString("HH:mm");
        KeepDailyToggle.IsOn = schedule.KeepDaily;
        ShellNav.SelectedItem = ShellNav.MenuItems[1];
        TimeInput.Focus(FocusState.Programmatic);
        TimeInput.SelectAll();
    }

    private void SetOverviewBusy(bool busy)
    {
        foreach (var button in new[]
                 {
                     OneTimeCancelButton,
                     DailyCancelButton,
                 })
        {
            button.IsEnabled = !busy;
        }

        if (OneTimeActions is not null)
        {
            foreach (var button in OneTimeActions.Children.OfType<Button>())
            {
                button.IsEnabled = !busy;
            }
        }

        if (DailyActions is not null)
        {
            foreach (var button in DailyActions.Children.OfType<Button>())
            {
                button.IsEnabled = !busy;
            }
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
            var schedule = (await _runtime.Schedules.ListAsync())
                .SingleOrDefault(item => item.Kind == kind)
                ?? throw new KeyNotFoundException();
            var result = await AppScheduleCancellation.CancelAsync(_runtime, schedule);
            await RefreshStatusAsync();
            if (result.IsSafe)
            {
                ShowStatus(AppText.Get("ScheduleCancelled", "Schedule cancelled."), InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus(
                    AppText.Format(
                        "WindowsCountdownCancelFailed",
                        "Windows could not stop the countdown. Try sdat -a. Details: {0}",
                        result.ErrorDetail ?? "Unknown error"),
                    InfoBarSeverity.Error);
            }
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
                CriticalOverlayPlacement =
                    Enum.Parse<OverlayPlacement>(GetSelectedTag(OverlayPlacementPicker)),
                StartCompanionAtLogin = StartupToggle.IsOn,
                DailyOverlapWindowMinutes = checked((int)DailyOverlapInput.Value),
                PaletteHotkey = PaletteHotkeyInput.Text,
                PalettePlacement =
                    Enum.Parse<OverlayPlacement>(GetSelectedTag(PalettePlacementPicker)),
                LogLevel = Enum.Parse<AppLogLevel>(GetSelectedTag(LogLevelPicker)),
                DeveloperModeEnabled = DeveloperModeToggle.IsOn,
                SimulationModeEnabled = DeveloperModeToggle.IsOn && SimulationModeToggle.IsOn,
            }.Validate();
            try
            {
                CompanionSettingsApplying?.Invoke(candidate);
                new StartupRegistrationService(Environment.ProcessPath!).SetEnabled(candidate.StartCompanionAtLogin);
                var settings = await _runtime.Settings.SaveAsync(candidate);
                var projectionSettingsChanged =
                    !previous.ReminderOffsetsMinutes.SequenceEqual(settings.ReminderOffsetsMinutes) ||
                    previous.IsTestMode;
                if (!settings.IsTestMode && projectionSettingsChanged)
                {
                    await _runtime.Coordinator.ReconcileAsync(settings.ReminderOffsetsMinutes);
                }
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
            if (report.SuppressedByTestMode)
            {
                ShowStatus(
                    AppText.Get(
                        "TestModeRepairSuppressed",
                        "Safe test mode is active. Windows integration was not changed."),
                    InfoBarSeverity.Informational);
                return;
            }

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

    private void OnDeveloperModeToggled(object sender, RoutedEventArgs e)
    {
        DeveloperToolsBody.Visibility = DeveloperModeToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_applyingSettings && DeveloperModeToggle.IsOn)
        {
            SimulationModeToggle.IsOn = true;
        }
    }

    private async void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.Logger.EnsureFileExistsAsync();
            await _runtime.Logger.WriteAsync(
                AppLogLevel.Information,
                nameof(MainWindow),
                "Log opened from Settings.");
            OpenPath(_runtime.StoreOptions.LogPath);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_runtime.StoreOptions.DataDirectory);
            OpenPath(_runtime.StoreOptions.DataDirectory);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnCreateDiagnosticReport(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            var reportPath = await _runtime.DiagnosticReports.WriteAsync(
                typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown");
            OpenPath(reportPath);
            ShowStatus(
                AppText.Get("DiagnosticReportCreated", "Diagnostic report created."),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnTestNotification(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || !DeveloperModeToggle.IsOn)
        {
            return;
        }

        try
        {
            var result = await _runtime.ReminderNotifications.ShowTestAsync(
                AppText.Get("TestNotificationTitle", "[TEST] ShutdownAT notification"),
                AppText.Get(
                    "TestNotificationBody",
                    "This is a safe preview. No schedule or power action was created."));
            await _runtime.Logger.WriteAsync(
                result.Delivered ? AppLogLevel.Information : AppLogLevel.Error,
                nameof(MainWindow),
                result.Delivered
                    ? "Displayed a synthetic test notification."
                    : $"Test notification failed: {result.ErrorCode}: {result.ErrorDetail}");
            ShowStatus(
                result.Delivered
                    ? AppText.Get("TestNotificationShown", "Test notification sent. No schedule was created.")
                    : AppText.Format(
                        "TestNotificationFailed",
                        "The test notification could not be shown. Details: {0}",
                        result.ErrorDetail ?? result.ErrorCode ?? "Unknown error"),
                result.Delivered ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void OnTestOverlay(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || !DeveloperModeToggle.IsOn)
        {
            return;
        }

        if (_testOverlay is not null)
        {
            _testOverlay.Activate();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var schedule = new ScheduleSnapshot(
            Guid.NewGuid(),
            1,
            ScheduleKind.OneTime,
            PowerActionType.Shutdown,
            now.AddSeconds(15),
            null,
            TimeZoneInfo.Local.Id,
            false,
            ScheduleStatus.Active,
            now,
            now);
        _testOverlay = new CriticalOverlayWindow(
            _runtime,
            schedule,
            TimeSpan.FromSeconds(15),
            Enum.Parse<OverlayPlacement>(GetSelectedTag(OverlayPlacementPicker)),
            isTest: true);
        _testOverlay.Closed += (_, _) => _testOverlay = null;
        _testOverlay.Activate();
    }

    private void OnTestPalette(object sender, RoutedEventArgs e)
    {
        if (!DeveloperModeToggle.IsOn)
        {
            return;
        }

        QuickPaletteRequested?.Invoke();
    }

    private async Task RefreshStatusAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        var schedules = await _runtime.Schedules.ListAsync();
        _oneTimeSchedule = schedules.SingleOrDefault(schedule => schedule.Kind == ScheduleKind.OneTime);
        _dailySchedule = schedules.SingleOrDefault(schedule => schedule.Kind == ScheduleKind.Daily);
        OneTimeActions.Visibility = _oneTimeSchedule is null ? Visibility.Collapsed : Visibility.Visible;
        DailyActions.Visibility = _dailySchedule is null ? Visibility.Collapsed : Visibility.Visible;
        OneTimeStatusText.Text = _oneTimeSchedule is null
            ? AppText.Get("NoOneTimeSchedule", "No one-time action scheduled.")
            : AppText.Format(
                "OneTimeOverviewStatus",
                "{0} · {1:ddd d MMM, HH:mm}\n{2}",
                AppText.PowerAction(_oneTimeSchedule.Action),
                _oneTimeSchedule.TargetAt!.Value.ToLocalTime(),
                SchedulePreviewFormatter.FormatRemaining(
                    _oneTimeSchedule.TargetAt.Value - DateTimeOffset.Now));
        var dailyDueAt = _dailySchedule is null
            ? (DateTimeOffset?)null
            : DailyScheduleOccurrenceResolver.GetNextExecution(_dailySchedule, DateTimeOffset.UtcNow);
        DailyStatusText.Text = _dailySchedule is null
            ? AppText.Get("NoDailySchedule", "No daily action scheduled.")
            : AppText.Format(
                "DailyOverviewStatus",
                "{0} · every day at {1:HH:mm}\nNext: {2:ddd d MMM, HH:mm} · {3}",
                AppText.PowerAction(_dailySchedule.Action),
                _dailySchedule.DailyAt,
                dailyDueAt!.Value.ToLocalTime(),
                SchedulePreviewFormatter.FormatRemaining(dailyDueAt.Value - DateTimeOffset.Now));
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

    internal void ShowStartupInitializationWarning(string detail) =>
        ShowStatus(
            AppText.Format(
                "StartupRegistrationUnavailable",
                "ShutdownAT could not update its Windows startup entry. Save Settings to try again. Details: {0}",
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
            if (!_backgroundHintShown && _runtime is not null)
            {
                _backgroundHintShown = true;
                BackgroundHintRequested?.Invoke(PaletteHotkeyInput.Text.Trim());
                _ = PersistBackgroundHintAsync();
            }
        }
    }

    private async Task PersistBackgroundHintAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            var settings = await _runtime.Settings.LoadAsync();
            if (!settings.BackgroundHintShown)
            {
                await _runtime.Settings.SaveAsync(settings with { BackgroundHintShown = true });
            }
        }
        catch
        {
            // The hint is non-critical and must never prevent the window from closing.
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _applyingSettings = true;
        try
        {
            SelectTag(LanguagePicker, settings.PreferredLanguage);
            ReminderOffsetsInput.Text = string.Join(", ", settings.ReminderOffsetsMinutes);
            CriticalOverlayToggle.IsOn = settings.CriticalOverlayEnabled;
            SelectTag(OverlayPlacementPicker, settings.CriticalOverlayPlacement.ToString());
            StartupToggle.IsOn = settings.StartCompanionAtLogin;
            DailyOverlapInput.Value = settings.DailyOverlapWindowMinutes;
            PaletteHotkeyInput.Text = settings.PaletteHotkey;
            SelectTag(PalettePlacementPicker, settings.PalettePlacement.ToString());
            SelectTag(LogLevelPicker, settings.LogLevel.ToString());
            DeveloperModeToggle.IsOn = settings.DeveloperModeEnabled;
            SimulationModeToggle.IsOn = settings.IsTestMode;
            _backgroundHintShown = settings.BackgroundHintShown;
            DeveloperToolsBody.Visibility = settings.DeveloperModeEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;
            OverviewTestModeBanner.Visibility = settings.IsTestMode
                ? Visibility.Visible
                : Visibility.Collapsed;
            ScheduleTestModeBanner.Visibility = settings.IsTestMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        _statusDismissTimer.Stop();
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
        _statusDismissTimer.Interval = severity is InfoBarSeverity.Error or InfoBarSeverity.Warning
            ? TimeSpan.FromSeconds(10)
            : TimeSpan.FromSeconds(5);
        _statusDismissTimer.Start();
        _ = LogStatusAsync(message, severity);
    }

    private void OnStatusDismissTimerTick(object? sender, object e)
    {
        _statusDismissTimer.Stop();
        StatusBar.IsOpen = false;
    }

    private async Task LogStatusAsync(string message, InfoBarSeverity severity)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.Logger.WriteAsync(
                severity == InfoBarSeverity.Error ? AppLogLevel.Error : AppLogLevel.Information,
                nameof(MainWindow),
                message);
        }
        catch
        {
            // A diagnostic write must never interrupt the UI flow it describes.
        }
    }

    private void ShowUnexpectedError(Exception exception, string resourceKey, string fallback)
    {
        ShowStatus(AppText.Get(resourceKey, fallback), InfoBarSeverity.Error);
        _ = LogStatusAsync(exception.ToString(), InfoBarSeverity.Error);
    }

    private static void OpenPath(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

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
