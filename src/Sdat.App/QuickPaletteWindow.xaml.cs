using Microsoft.UI.Windowing;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private const int CompactWidth = 560;
    private const int ValidationHeight = 116;
    private const int PaletteCornerRadiusDip = 14;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowBorderColor = 34;
    private const int DwmWindowCornerRound = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const int WindowStyleIndex = -16;
    private const long WindowFrameStyleMask = 0x00CC0000; // WS_BORDER | WS_DLGFRAME | WS_SYSMENU | WS_THICKFRAME
    private const uint FrameChangedFlags = 0x0037; // SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER
    private const uint AnimateWindowHide = 0x00010000;
    private const uint AnimateWindowActivate = 0x00020000;
    private const uint AnimateWindowBlend = 0x00080000;
    private const uint FadeInMilliseconds = 220;
    private const uint FadeOutMilliseconds = 160;
    private const int OffscreenCoordinate = -32000;
    private static readonly TimeSpan TransientFeedbackDuration = TimeSpan.FromSeconds(2.2);
    private readonly SdatRuntime _runtime;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private readonly nint _windowHandle;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly TaskCompletionSource _paletteReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _allowClose;
    private bool _isClosing;
    private bool _showInProgress;
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

    public async void ShowAndFocus()
    {
        if (_showInProgress || _isClosing)
        {
            return;
        }

        _showInProgress = true;
        AppWindow.MoveAndResize(new RectInt32(
            OffscreenCoordinate,
            OffscreenCoordinate,
            CompactWidth,
            ValidationHeight));
        ApplyRoundedWindowRegion(CompactWidth, ValidationHeight);
        AppWindow.Show(activateWindow: false);

        await _paletteReady.Task;
        if (_isClosing)
        {
            return;
        }

        // Pre-render XAML and acrylic away from the desktop so the complete
        // palette appears together instead of exposing an empty black HWND.
        await Task.Delay(32);
        if (_isClosing)
        {
            return;
        }

        AppWindow.Hide();
        ResizePalette(ValidationHeight);
        ApplyNativeWindowStyle();
        ShowNativeWindow();
        _ = BringWindowToTop(_windowHandle);
        _ = SetForegroundWindow(_windowHandle);
        QueueInputFocus();
        _showInProgress = false;
    }

    private void ApplyNativeWindowStyle()
    {
        var currentStyle = GetWindowLongPtr(_windowHandle, WindowStyleIndex).ToInt64();
        var borderlessStyle = currentStyle & ~WindowFrameStyleMask;
        if (borderlessStyle != currentStyle)
        {
            _ = SetWindowLongPtr(
                _windowHandle,
                WindowStyleIndex,
                new nint(borderlessStyle));
            _ = SetWindowPos(
                _windowHandle,
                nint.Zero,
                0,
                0,
                0,
                0,
                FrameChangedFlags);
        }

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

    private void ApplyRoundedWindowRegion(int width, int height)
    {
        var dpi = GetDpiForWindow(_windowHandle);
        var diameter = (int)Math.Round(
            PaletteCornerRadiusDip * 2 * Math.Max(dpi, 96u) / 96d);
        var region = CreateRoundRectRgn(
            0,
            0,
            width + 1,
            height + 1,
            diameter,
            diameter);
        if (region == nint.Zero)
        {
            return;
        }

        if (SetWindowRgn(_windowHandle, region, redraw: true) == 0)
        {
            _ = DeleteObject(region);
        }
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
        ApplyRoundedWindowRegion(CompactWidth, height);
    }

    private async void OnPaletteLoaded(object sender, RoutedEventArgs e)
    {
        PaletteRoot.ActualThemeChanged += OnPaletteThemeChanged;
        UpdateBackdropTheme();
        await RefreshActiveScheduleAsync();
        _paletteReady.TrySetResult();
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
        if (!AnimateWindow(
                _windowHandle,
                FadeOutMilliseconds,
                AnimateWindowHide | AnimateWindowBlend))
        {
            AppWindow.Hide();
        }
        _allowClose = true;
        Close();
    }

    private void ShowNativeWindow()
    {
        if (!_animationsEnabled ||
            !AnimateWindow(
                _windowHandle,
                FadeInMilliseconds,
                AnimateWindowActivate | AnimateWindowBlend))
        {
            AppWindow.Show(activateWindow: true);
            Activate();
        }
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
        if (FeedbackPanel.Visibility == Visibility.Visible &&
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
        FeedbackPanel.Visibility = Visibility.Visible;
    }

    private void ClearValidation()
    {
        _feedbackResetCancellation?.Cancel();
        _validationInputText = null;
        FeedbackPanel.Visibility = Visibility.Collapsed;
        FeedbackText.Text = string.Empty;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private void ShowStatus(string message)
    {
        _feedbackResetCancellation?.Cancel();
        _validationInputText = null;
        FeedbackText.Text = message;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        FeedbackPanel.Visibility = Visibility.Visible;
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
                FeedbackPanel.Visibility = Visibility.Collapsed;
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AnimateWindow(nint window, uint time, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int widthEllipse,
        int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(
        nint window,
        nint region,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
}
