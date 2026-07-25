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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public CriticalOverlayWindow(
        SdatRuntime runtime,
        ScheduleSnapshot schedule,
        int reminderOffsetMinutes,
        OverlayPlacement placement = OverlayPlacement.TopCenter,
        bool isTest = false)
    {
        _runtime = runtime;
        _schedule = schedule;
        _placement = placement;
        _isTest = isTest;
        _countdownWindowSeconds = Math.Max(1, reminderOffsetMinutes) * 60d;
        InitializeComponent();
        Title = AppText.Get("ReminderTitle", "ShutdownAT reminder");
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(OverlayRoot);
        SnoozeButton.Visibility = schedule.Kind == ScheduleKind.OneTime
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (isTest)
        {
            SnoozeButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
        }

        TitleText.Text = isTest
            ? AppText.Get("TestOverlayTitle", "Test countdown")
            : schedule.Action == PowerActionType.Restart
            ? AppText.Get("RestartScheduledTitle", "Restarting soon")
            : AppText.Get("ShutdownScheduledTitle", "Shutting down soon");
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

    private void OnTimerTick(object? sender, object e) => UpdateCountdown();

    private void UpdateCountdown()
    {
        var target = _schedule.TargetAt?.ToLocalTime();
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

        CountdownText.Text = remaining > TimeSpan.Zero
            ? AppText.Format(
                "SecondsRemaining",
                "{0}s remaining. Save your work.",
                Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)))
            : AppText.Get("CountdownStarting", "The Windows countdown is about to begin.");
        CountdownProgress.Value = Math.Clamp(
            remaining.TotalSeconds / _countdownWindowSeconds * 100d,
            0d,
            100d);
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = await _runtime.Settings.LoadAsync();
            await _runtime.Coordinator.CancelExactAsync(
                _schedule.Id,
                _schedule.Revision,
                settings.ReminderOffsetsMinutes);
        }
        finally
        {
            Close();
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
