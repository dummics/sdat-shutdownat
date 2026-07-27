using Microsoft.UI.Windowing;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Runtime.InteropServices;
using Sdat.Core.Scheduling;
using Sdat.Windows.Hosting;
using WinRT.Interop;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Sdat.App;

public sealed partial class QuickPaletteWindow : Window
{
    private const int CompactWidth = 480;
    private const int ValidationHeight = 116;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowBorderColor = 34;
    private const int DwmWindowCornerRound = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private static readonly TimeSpan TransientFeedbackDuration = TimeSpan.FromSeconds(2.2);
    private readonly SdatRuntime _runtime;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private readonly nint _windowHandle;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private bool _allowClose;
    private bool _isClosing;
    private string? _validationInputText;
    private ScheduleSnapshot? _activeSchedule;
    private CancellationTokenSource? _feedbackResetCancellation;

    public QuickPaletteWindow(SdatRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        Title = AppText.Get("QuickPaletteTitle", "Quick schedule — ShutdownAT");
        ConfigureBackdrop();
        ExtendsContentIntoTitleBar = true;
        _windowHandle = WindowNative.GetWindowHandle(this);
        ConfigureWindow();
        AppWindow.Closing += OnWindowClosing;
        Activated += OnActivated;
        Closed += (_, _) => DisposeBackdrop();
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

        ApplyNativeWindowStyle();
        ResizePalette(ValidationHeight);
    }

    public void ShowAndFocus()
    {
        AppWindow.Show(activateWindow: true);
        Activate();
        ApplyNativeWindowStyle();
        _ = BringWindowToTop(_windowHandle);
        _ = SetForegroundWindow(_windowHandle);
        QueueInputFocus();
    }

    private void ApplyNativeWindowStyle()
    {
        var cornerPreference = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmWindowBorderColor,
            ref borderColor,
            sizeof(int));
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
        PaletteRoot.ActualThemeChanged += OnPaletteThemeChanged;
        UpdateBackdropTheme();
        if (_animationsEnabled)
        {
            FadeInStoryboard.Begin();
        }
        else
        {
            PaletteRoot.Opacity = 1;
            PaletteTransform.Y = 0;
        }

        await RefreshActiveScheduleAsync();
        QueueInputFocus();
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
        _feedbackResetCancellation?.Cancel();
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
                    "Schedule created · {0:HH:mm}",
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
        _feedbackResetCancellation?.Cancel();
        _validationInputText = TimeInput.Text;
        FeedbackText.Text = message;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        FeedbackText.Visibility = Visibility.Visible;
    }

    private void ClearValidation()
    {
        _feedbackResetCancellation?.Cancel();
        _validationInputText = null;
        FeedbackText.Visibility = Visibility.Collapsed;
        FeedbackText.Text = string.Empty;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private void ShowStatus(string message)
    {
        _feedbackResetCancellation?.Cancel();
        _validationInputText = null;
        FeedbackText.Text = message;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        FeedbackText.Visibility = Visibility.Visible;
        _feedbackResetCancellation = new CancellationTokenSource();
        _ = ClearTransientStatusAsync(_feedbackResetCancellation.Token);
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

            PaletteCancelButton.Visibility = Visibility.Visible;
        }
        catch
        {
            // Scheduling remains available if status refresh is temporarily unavailable.
        }
    }

    private void OnTimeInputGotFocus(object sender, RoutedEventArgs e) =>
        TimeInputFrame.BorderBrush =
            (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private void OnTimeInputLostFocus(object sender, RoutedEventArgs e) =>
        TimeInputFrame.BorderBrush =
            (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];

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

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            ApplyNativeWindowStyle();
            QueueInputFocus();
        }
    }

    private void ConfigureBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }

        _backdropConfiguration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
        };
        UpdateBackdropTheme();

        _acrylicController = new DesktopAcrylicController
        {
            TintOpacity = 0.62f,
            LuminosityOpacity = 0.78f,
        };
        _acrylicController.AddSystemBackdropTarget(
            WinRT.CastExtensions.As<ICompositionSupportsSystemBackdrop>(this));
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
        UpdateBackdropTheme();
    }

    private void OnPaletteThemeChanged(FrameworkElement sender, object args) =>
        UpdateBackdropTheme();

    private void UpdateBackdropTheme()
    {
        if (_backdropConfiguration is null)
        {
            return;
        }

        var theme = PaletteRoot.ActualTheme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => GetWindowsBackdropTheme(),
        };
        _backdropConfiguration.Theme = theme;
        _backdropConfiguration.IsInputActive = true;
        if (_acrylicController is not null)
        {
            _acrylicController.TintColor = theme == SystemBackdropTheme.Dark
                ? global::Windows.UI.Color.FromArgb(255, 32, 32, 32)
                : global::Windows.UI.Color.FromArgb(255, 248, 248, 248);
        }
        PaletteRoot.BorderBrush = new SolidColorBrush(
            theme == SystemBackdropTheme.Dark
                ? global::Windows.UI.Color.FromArgb(16, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(20, 0, 0, 0));
        TimeInputFrame.Background = new SolidColorBrush(
            theme == SystemBackdropTheme.Dark
                ? global::Windows.UI.Color.FromArgb(245, 38, 38, 38)
                : global::Windows.UI.Color.FromArgb(245, 250, 250, 250));
    }

    private static SystemBackdropTheme GetWindowsBackdropTheme()
    {
        var background = new UISettings().GetColorValue(UIColorType.Background);
        return background.R + background.G + background.B < 384
            ? SystemBackdropTheme.Dark
            : SystemBackdropTheme.Light;
    }

    private void DisposeBackdrop()
    {
        PaletteRoot.ActualThemeChanged -= OnPaletteThemeChanged;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfiguration = null;
    }

    private void QueueInputFocus() =>
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
            () =>
            {
                _ = TimeInput.Focus(FocusState.Programmatic);
                TimeInput.Select(TimeInput.Text.Length, 0);
            });

    private async Task ClearTransientStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TransientFeedbackDuration, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                FeedbackText.Visibility = Visibility.Collapsed;
                FeedbackText.Text = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer status or validation message owns the feedback area.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
