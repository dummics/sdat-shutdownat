using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Sdat.Core.Scheduling;
using Sdat.Windows.Hosting;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Sdat.App;

public sealed partial class QuickPaletteWindow : Window
{
    private const int CompactWidth = 480;
    private const int ValidationHeight = 116;
    private readonly SdatRuntime _runtime;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private bool _allowClose;
    private bool _isClosing;
    private string? _validationInputText;
    private ScheduleSnapshot? _activeSchedule;

    public QuickPaletteWindow(SdatRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        Title = AppText.Get("QuickPaletteTitle", "Quick schedule — ShutdownAT");
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(PaletteRoot);
        ConfigureWindow();
        AppWindow.Closing += OnWindowClosing;
        Activated += (_, _) => TimeInput.Focus(FocusState.Programmatic);
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        ResizePalette(ValidationHeight);
    }

    private void ResizePalette(int height)
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - CompactWidth) / 2,
            workArea.Y + workArea.Height - height - 28,
            CompactWidth,
            height));
    }

    private async void OnPaletteLoaded(object sender, RoutedEventArgs e)
    {
        PaletteRoot.Opacity = 1;
        if (_animationsEnabled)
        {
            FadeInStoryboard.Begin();
        }

        await RefreshActiveScheduleAsync();
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || !_animationsEnabled)
        {
            return;
        }

        args.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        PaletteRoot.IsHitTestVisible = false;
        FadeOutStoryboard.Begin();
    }

    private void OnFadeOutCompleted(object? sender, object e)
    {
        _allowClose = true;
        Close();
    }

    private async void OnSchedule(object sender, RoutedEventArgs e) => await ScheduleAsync();

    private async Task ScheduleAsync()
    {
        try
        {
            ClearValidation();
            var action = Enum.Parse<PowerActionType>(
                ((ComboBoxItem)ActionPicker.SelectedItem).Tag!.ToString()!);
            var prepared = new ScheduleInputService().Prepare(
                TimeInput.Text,
                ScheduleKind.OneTime,
                action,
                keepDaily: false,
                DateTimeOffset.UtcNow,
                TimeZoneInfo.Local);
            var result = await _runtime.ScheduleCommands.SetAsync(prepared.Draft);
            if (result.IsFullyApplied)
            {
                _activeSchedule = result.Mutation.Schedule;
                ShowStatus(AppText.Format(
                    "QuickScheduleSaved",
                    "Scheduled for {0:HH:mm}.",
                    _activeSchedule.TargetAt?.ToLocalTime()));
                PaletteCancelButton.Visibility = Visibility.Visible;
            }
            else
            {
                ShowValidation(AppText.Get(
                    "PaletteRecoveryWarning",
                    "Schedule saved, but the Windows integration needs attention. Open ShutdownAT for details."));
            }
        }
        catch (TestModeScheduleBlockedException)
        {
            ShowValidation(AppText.Get(
                "TestModeScheduleBlocked",
                "Safe test mode is active. Turn it off before creating a real schedule."));
            TimeInput.Focus(FocusState.Programmatic);
            TimeInput.SelectAll();
        }
        catch (Exception exception)
        {
            ShowValidation(exception.Message);
            TimeInput.Focus(FocusState.Programmatic);
            TimeInput.SelectAll();
        }
    }

    private void OnTimeInputChanged(object sender, TextChangedEventArgs e)
    {
        if (FeedbackText.Visibility == Visibility.Visible &&
            FeedbackText.Foreground == (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] &&
            !string.Equals(TimeInput.Text, _validationInputText, StringComparison.Ordinal))
        {
            ClearValidation();
        }
    }

    private void ShowValidation(string message)
    {
        _validationInputText = TimeInput.Text;
        FeedbackText.Text = message;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        FeedbackText.Visibility = Visibility.Visible;
    }

    private void ClearValidation()
    {
        _validationInputText = null;
        FeedbackText.Visibility = Visibility.Collapsed;
        FeedbackText.Text = string.Empty;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private void ShowStatus(string message)
    {
        _validationInputText = null;
        FeedbackText.Text = message;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        FeedbackText.Visibility = Visibility.Visible;
    }

    private async Task RefreshActiveScheduleAsync()
    {
        try
        {
            _activeSchedule = (await _runtime.Schedules.ListAsync())
                .SingleOrDefault(schedule => schedule.Kind == ScheduleKind.OneTime);
            if (_activeSchedule is null)
            {
                PaletteCancelButton.Visibility = Visibility.Collapsed;
                return;
            }

            ShowStatus(AppText.Format(
                "QuickScheduleActive",
                "Active schedule for {0:HH:mm}.",
                _activeSchedule.TargetAt?.ToLocalTime()));
            PaletteCancelButton.Visibility = Visibility.Visible;
        }
        catch
        {
            // Scheduling remains available if status refresh is temporarily unavailable.
        }
    }

    private async void OnCancelSchedule(object sender, RoutedEventArgs e)
    {
        PaletteCancelButton.IsEnabled = false;
        try
        {
            if (_activeSchedule is null)
            {
                throw new InvalidOperationException(
                    AppText.Get("NothingToCancel", "There is no active schedule to cancel."));
            }

            var result = await AppScheduleCancellation.CancelAsync(_runtime, _activeSchedule);
            if (!result.IsSafe)
            {
                throw new InvalidOperationException(result.ErrorDetail);
            }

            _activeSchedule = null;
            PaletteCancelButton.Visibility = Visibility.Collapsed;
            ShowStatus(AppText.Get(
                result.Guard.WasCountdownAborted
                    ? "WindowsCountdownCancelled"
                    : "QuickScheduleCancelled",
                result.Guard.WasCountdownAborted
                    ? "Windows countdown stopped."
                    : "Schedule cancelled."));
            TimeInput.Focus(FocusState.Programmatic);
            TimeInput.SelectAll();
        }
        catch (Exception exception)
        {
            ShowValidation(exception.Message);
        }
        finally
        {
            PaletteCancelButton.IsEnabled = true;
        }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ScheduleAsync();
        }
    }
}
