using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Sdat.Core.Scheduling;
using Sdat.Windows.Hosting;
using Windows.Graphics;

namespace Sdat.App;

public sealed partial class CriticalOverlayWindow : Window
{
    private readonly SdatRuntime _runtime;
    private readonly ScheduleSnapshot _schedule;
    private readonly double _countdownWindowSeconds;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public CriticalOverlayWindow(
        SdatRuntime runtime,
        ScheduleSnapshot schedule,
        int reminderOffsetMinutes)
    {
        _runtime = runtime;
        _schedule = schedule;
        _countdownWindowSeconds = Math.Max(1, reminderOffsetMinutes) * 60d;
        InitializeComponent();
        Title = AppText.Get("ReminderTitle", "ShutdownAT reminder");
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(OverlayRoot);
        SnoozeButton.Visibility = schedule.Kind == ScheduleKind.OneTime
            ? Visibility.Visible
            : Visibility.Collapsed;
        TitleText.Text = schedule.Action == PowerActionType.Restart
            ? AppText.Get("RestartScheduledTitle", "This PC is scheduled to restart")
            : AppText.Get("ShutdownScheduledTitle", "This PC is scheduled to shut down");
        ConfigureWindow();
        UpdateCountdown();
        _timer.Tick += OnTimerTick;
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void ConfigureWindow()
    {
        const int width = 500;
        const int height = 190;
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
        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + workArea.Height - height - 24,
            width,
            height));
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
        CountdownText.Text = remaining > TimeSpan.Zero
            ? AppText.Format(
                "SecondsRemaining",
                "{0} seconds remaining. Save your work now.",
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
