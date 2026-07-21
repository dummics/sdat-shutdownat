using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
using Sdat.Windows.Hosting;
using Windows.Graphics;

namespace Sdat.App;

public sealed partial class MainWindow : Window
{
    private SdatRuntime? _runtime;

    public MainWindow()
    {
        InitializeComponent();
        Title = "SDAT";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1040, 720));
        ExtendsContentIntoTitleBar = true;
        RootGrid.Loaded += OnLoaded;
        ShellNav.SelectedItem = ShellNav.MenuItems[0];
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnLoaded;
        try
        {
            _runtime = await SdatRuntime.CreateAsync(Environment.ProcessPath!);
            ApplySettings(_runtime.CurrentSettings);
            DatabasePathText.Text = $"Database: {_runtime.StoreOptions.DatabasePath}";
            await RefreshStatusAsync();
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
            var settings = await _runtime.Settings.LoadAsync();
            var result = await _runtime.Coordinator.SetAsync(prepared.Draft, settings.ReminderOffsetsMinutes);
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
            var offsets = ReminderOffsetsInput.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            var settings = await _runtime.Settings.SaveAsync(new AppSettings
            {
                ReminderOffsetsMinutes = offsets,
                CriticalOverlayEnabled = CriticalOverlayToggle.IsOn,
                StartCompanionAtLogin = StartupToggle.IsOn,
            });
            await _runtime.Coordinator.ReconcileAsync(settings.ReminderOffsetsMinutes);
            ApplySettings(settings);
            ShowStatus("Notification settings saved.", InfoBarSeverity.Success);
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

    private void ApplySettings(AppSettings settings)
    {
        ReminderOffsetsInput.Text = string.Join(", ", settings.ReminderOffsetsMinutes);
        CriticalOverlayToggle.IsOn = settings.CriticalOverlayEnabled;
        StartupToggle.IsOn = settings.StartCompanionAtLogin;
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
