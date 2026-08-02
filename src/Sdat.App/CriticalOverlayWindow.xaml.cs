using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sdat.Core.Scheduling;
using Sdat.Core.Settings;
using Sdat.Windows.Hosting;
using Windows.Graphics;

namespace Sdat.App;

public sealed partial class CriticalOverlayWindow : Window
{
    private readonly SdatRuntime _runtime;
    private readonly ScheduleSnapshot _schedule;
    private readonly double _countdownWindowSeconds;
    private readonly OverlayPlacement _placement;
    private readonly bool _isTest;
    private readonly bool _isFinalWindowsCountdown;
    private readonly DateTimeOffset? _countdownEndsAt;
    private readonly DateTimeOffset _openedAtUtc = DateTimeOffset.UtcNow;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _externalRefreshInProgress;

    public CriticalOverlayWindow(
        SdatRuntime runtime,
        ScheduleSnapshot schedule,
        TimeSpan countdownWindow,
        OverlayPlacement placement = OverlayPlacement.TopCenter,
        bool isTest = false,
        bool isFinalWindowsCountdown = false)
    {
        _runtime = runtime;
        _schedule = schedule;
        _placement = placement;
        _isTest = isTest;
        _isFinalWindowsCountdown = isFinalWindowsCountdown;
        _countdownWindowSeconds = Math.Max(1, countdownWindow.TotalSeconds);
        _countdownEndsAt = isFinalWindowsCountdown
            ? DateTimeOffset.Now.Add(countdownWindow)
            : schedule.TargetAt?.ToLocalTime();
        InitializeComponent();
        Title = AppText.Get("ReminderTitle", "ShutdownAT reminder");
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(OverlayRoot);
        SnoozeButton.Visibility = schedule.Kind == ScheduleKind.OneTime && !isFinalWindowsCountdown
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (isTest)
        {
            SnoozeButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
        }

        TitleText.Text = isTest
            ? AppText.Get("TestOverlayTitle", "Test countdown")
            : schedule.Action switch
            {
                PowerActionType.Restart =>
                    AppText.Get("RestartScheduledTitle", "Restarting soon"),
                PowerActionType.Suspend =>
                    AppText.Get("SuspendScheduledTitle", "Suspending soon"),
                _ =>
                    AppText.Get("ShutdownScheduledTitle", "Shutting down soon"),
            };
        CancelButton.Content = schedule.Action switch
        {
            PowerActionType.Restart =>
                AppText.Get("OverlayCancelRestart", "Cancel restart"),
            PowerActionType.Suspend =>
                AppText.Get("OverlayCancelSuspend", "Cancel suspend"),
            _ =>
                AppText.Get("OverlayCancelShutdown", "Cancel shutdown"),
        };
        ConfigureWindow();
        UpdateCountdown();
        _timer.Tick += OnTimerTick;
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void ConfigureWindow()
    {
        var vertical = _placement is OverlayPlacement.LeftCenter or OverlayPlacement.RightCenter;
        var visibleActionCount = 1 +
                                 (SnoozeButton.Visibility == Visibility.Visible ? 1 : 0) +
                                 (CancelButton.Visibility == Visibility.Visible ? 1 : 0);
        var width = vertical ? 280 : 440;
        var height = vertical ? 123 + (visibleActionCount * 39) : 154;
        ConfigureActionLayout(vertical);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        const int edgeGap = 20;
        var centeredX = workArea.X + (workArea.Width - width) / 2;
        var centeredY = workArea.Y + (workArea.Height - height) / 2;
        var leftX = workArea.X + edgeGap;
        var rightX = workArea.X + workArea.Width - width - edgeGap;
        var topY = workArea.Y + edgeGap;
        var bottomY = workArea.Y + workArea.Height - height - edgeGap;
        var (x, y) = _placement switch
        {
            OverlayPlacement.TopCenter => (centeredX, topY),
            OverlayPlacement.BottomCenter => (centeredX, bottomY),
            OverlayPlacement.LeftCenter => (leftX, centeredY),
            OverlayPlacement.RightCenter => (rightX, centeredY),
            OverlayPlacement.TopLeft => (leftX, topY),
            OverlayPlacement.TopRight => (rightX, topY),
            OverlayPlacement.BottomLeft => (leftX, bottomY),
            OverlayPlacement.BottomRight => (rightX, bottomY),
            _ => (centeredX, topY),
        };
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void ConfigureActionLayout(bool vertical)
    {
        ActionPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ActionPanel.HorizontalAlignment = vertical
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        var buttonAlignment = vertical
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        DismissButton.HorizontalAlignment = buttonAlignment;
        SnoozeButton.HorizontalAlignment = buttonAlignment;
        CancelButton.HorizontalAlignment = buttonAlignment;
    }

    private async void OnTimerTick(object? sender, object e)
    {
        if (_externalRefreshInProgress)
        {
            return;
        }

        _externalRefreshInProgress = true;
        try
        {
            if (!_isTest && await WasCancelledExternallyAsync())
            {
                Close();
                return;
            }

            UpdateCountdown();
        }
        catch
        {
            // The visible countdown remains useful if a transient state read fails.
            UpdateCountdown();
        }
        finally
        {
            _externalRefreshInProgress = false;
        }
    }

    private async Task<bool> WasCancelledExternallyAsync()
    {
        var signal = await _runtime.CancellationSignals.ReadLatestAsync();
        if (signal?.Matches(_schedule.Id, _schedule.Revision, _openedAtUtc) == true)
        {
            return true;
        }

        if (_isFinalWindowsCountdown)
        {
            return false;
        }

        var latest = await _runtime.Schedules.GetAsync(_schedule.Id);
        return latest is null ||
               latest.Status != ScheduleStatus.Active ||
               latest.Revision != _schedule.Revision;
    }

    private void UpdateCountdown()
    {
        var target = _countdownEndsAt;
        if (target is null)
        {
            CountdownText.Text = AppText.Format(
                "DailyActionAt",
                "Daily action at {0:HH:mm}.",
                _schedule.DailyAt);
            CountdownProgress.Value = 100;
            return;
        }

        var remaining = target.Value - DateTimeOffset.Now;
        if (_isTest)
        {
            CountdownText.Text = AppText.Format(
                "TestOverlayCountdown",
                "Closing in {0}s. No action will run.",
                Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds)));
            CountdownProgress.Value = Math.Clamp(
                remaining.TotalSeconds / _countdownWindowSeconds * 100d,
                0d,
                100d);
            if (remaining <= TimeSpan.Zero)
            {
                Close();
            }

            return;
        }

        var secondsRemaining = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        CountdownText.Text = remaining > TimeSpan.Zero
            ? _isFinalWindowsCountdown
                ? AppText.Format(
                    _schedule.Action == PowerActionType.Restart
                        ? "WindowsRestartCountdownSeconds"
                        : "WindowsShutdownCountdownSeconds",
                    _schedule.Action == PowerActionType.Restart
                        ? "Windows will restart in {0}s. Save your work."
                        : "Windows will shut down in {0}s. Save your work.",
                    secondsRemaining)
                : AppText.Format(
                    "SecondsRemaining",
                    "{0}s remaining. Save your work.",
                    secondsRemaining)
            : AppText.Get(
                _isFinalWindowsCountdown ? "WindowsCountdownEnding" : "CountdownStarting",
                _isFinalWindowsCountdown
                    ? "The Windows countdown has ended."
                    : "The Windows countdown is about to begin.");
        CountdownProgress.Value = Math.Clamp(
            remaining.TotalSeconds / _countdownWindowSeconds * 100d,
            0d,
            100d);
        if (remaining <= TimeSpan.Zero)
        {
            Close();
        }
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        try
        {
            CancelButton.IsEnabled = false;
            var result = await AppScheduleCancellation.CancelAsync(
                _runtime,
                _schedule,
                cancelScheduleState: !_isFinalWindowsCountdown);
            if (!result.IsSafe)
            {
                CountdownText.Text = AppText.Format(
                    "WindowsCountdownCancelFailed",
                    "Windows could not stop the countdown. Try sdat -a. Details: {0}",
                    result.ErrorDetail ?? "Unknown error");
                CountdownText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                CancelButton.IsEnabled = true;
                return;
            }

            Close();
        }
        catch (Exception exception)
        {
            CountdownText.Text = AppText.Format(
                "ScheduleCancelFailed",
                "Could not cancel the schedule. Details: {0}",
                exception.Message);
            CountdownText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            CancelButton.IsEnabled = true;
        }
    }

    private async void OnSnooze(object sender, RoutedEventArgs e)
    {
        if (_schedule.Kind != ScheduleKind.OneTime)
        {
            return;
        }

        try
        {
            var settings = await _runtime.Settings.LoadAsync();
            var target = _schedule.TargetAt!.Value.AddMinutes(10);
            var minimum = DateTimeOffset.UtcNow.AddMinutes(10);
            if (target < minimum)
            {
                target = minimum;
            }

            await _runtime.Coordinator.UpdateExactAsync(
                _schedule.Id,
                _schedule.Revision,
                ScheduleDraft.OneTime(
                    _schedule.Action,
                    target,
                    _schedule.TimeZoneId,
                    _schedule.KeepDaily),
                settings.ReminderOffsetsMinutes);
        }
        finally
        {
            Close();
        }
    }
}
