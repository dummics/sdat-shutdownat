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
using Sdat.Core.TimeExpressions;
using Sdat.Windows.Hosting;
using WinRT.Interop;
using Windows.Graphics;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using Sdat.Core.Settings;

namespace Sdat.App;

public sealed partial class QuickPaletteWindow : Window
{
    private const int HorizontalCompactWidth = 620;
    private const int HorizontalCompactHeight = 116;
    private const int VerticalCompactWidth = 300;
    private const int VerticalHeight = 218;
    private const int ScreenEdgeGap = 28;
    private const int FeedbackGap = 8;
    private const int MorphDurationMilliseconds = 100;
    private const int MorphFrameCount = 5;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowBorderColor = 34;
    private const int DwmWindowCornerRound = 2;
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    private const uint AnimateWindowHide = 0x00010000;
    private const uint AnimateWindowActivate = 0x00020000;
    private const uint AnimateWindowBlend = 0x00080000;
    private const uint FadeInMilliseconds = 140;
    private const uint FadeOutMilliseconds = 100;
    private const int OffscreenCoordinate = -32000;
    private static readonly TimeSpan TransientFeedbackDuration = TimeSpan.FromSeconds(2.2);
    private readonly SdatRuntime _runtime;
    private readonly OverlayPlacement _placement;
    private readonly ScheduleInputService _scheduleInputService = new();
    private readonly DispatcherTimer _previewTimer =
        new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private readonly nint _windowHandle;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly TaskCompletionSource _paletteReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _allowClose;
    private bool _isClosing;
    private bool _showInProgress;
    private bool _hasBeenActivated;
    private bool _feedbackExpanded;
    private int _feedbackExtent;
    private string? _validationInputText;
    private ScheduleSnapshot? _activeSchedule;
    private CancellationTokenSource? _feedbackResetCancellation;
    private CancellationTokenSource? _morphCancellation;
    private PaletteFeedbackKind _feedbackKind;
    private bool _scheduleBusy;

    public QuickPaletteWindow(
        SdatRuntime runtime,
        OverlayPlacement? placement = null)
    {
        _runtime = runtime;
        _placement = placement ?? runtime.CurrentSettings.PalettePlacement;
        InitializeComponent();
        _previewTimer.Tick += OnPreviewTimerTick;
        ConfigurePaletteLayout();
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
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }

        ApplyNativeWindowStyle();
        MovePalette(expanded: false);
    }

    public async void ShowAndFocus()
    {
        if (_showInProgress || _isClosing)
        {
            return;
        }

        _showInProgress = true;
        var compactBounds = GetPaletteBounds(expanded: false);
        AppWindow.MoveAndResize(new RectInt32(
            OffscreenCoordinate,
            OffscreenCoordinate,
            compactBounds.Width,
            compactBounds.Height));
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
        MovePalette(expanded: false);
        ApplyNativeWindowStyle();
        ShowNativeWindow();
        _ = BringWindowToTop(_windowHandle);
        _ = SetForegroundWindow(_windowHandle);
        QueueInputFocus();
        _showInProgress = false;
    }

    private void ApplyNativeWindowStyle()
    {
        var cornerPreference = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
        var borderColor = DwmColorDefault;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmWindowBorderColor,
            ref borderColor,
            sizeof(int));
    }

    private bool UsesVerticalLayout =>
        _placement is OverlayPlacement.LeftCenter or OverlayPlacement.RightCenter;

    private bool GrowsTowardTop =>
        _placement is OverlayPlacement.BottomCenter
            or OverlayPlacement.BottomLeft
            or OverlayPlacement.BottomRight;

    private void ConfigurePaletteLayout()
    {
        CommandPanel.ColumnDefinitions.Clear();
        CommandPanel.RowDefinitions.Clear();

        if (UsesVerticalLayout)
        {
            CommandPanel.Width = VerticalCompactWidth - 24;
            CommandPanel.HorizontalAlignment = _placement == OverlayPlacement.LeftCenter
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
            CommandPanel.VerticalAlignment = VerticalAlignment.Center;
            CommandPanel.ColumnSpacing = 0;
            CommandPanel.RowSpacing = 10;
            CommandPanel.ColumnDefinitions.Add(new ColumnDefinition());
            for (var row = 0; row < 4; row++)
            {
                CommandPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            PlaceCommandControl(ActionPicker, row: 0);
            PlaceCommandControl(TimeInputFrame, row: 1);
            PlaceCommandControl(ScheduleButton, row: 2);
            PlaceCommandControl(PaletteCancelButton, row: 3);
            ActionPicker.HorizontalAlignment = HorizontalAlignment.Stretch;
            ScheduleButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            PaletteCancelButton.HorizontalAlignment = HorizontalAlignment.Stretch;

            FeedbackPanel.Width = 238;
            FeedbackPanel.HorizontalAlignment = _placement == OverlayPlacement.LeftCenter
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
            FeedbackPanel.VerticalAlignment = VerticalAlignment.Center;
            return;
        }

        CommandPanel.ColumnSpacing = 10;
        CommandPanel.RowSpacing = 0;
        CommandPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        CommandPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        CommandPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        CommandPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        CommandPanel.RowDefinitions.Add(new RowDefinition());
        PlaceCommandControl(ActionPicker, column: 0);
        PlaceCommandControl(TimeInputFrame, column: 1);
        PlaceCommandControl(ScheduleButton, column: 2);
        PlaceCommandControl(PaletteCancelButton, column: 3);

        CommandPanel.VerticalAlignment = GrowsTowardTop
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Top;
        CommandPanel.Margin = GrowsTowardTop
            ? new Thickness(0, 0, 0, 29)
            : new Thickness(0, 29, 0, 0);
        FeedbackPanel.VerticalAlignment = GrowsTowardTop
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Top;
        FeedbackPanel.Margin = GrowsTowardTop
            ? new Thickness(0, 0, 0, 70)
            : new Thickness(0, 70, 0, 0);
    }

    private static void PlaceCommandControl(
        FrameworkElement element,
        int row = 0,
        int column = 0)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private void MovePalette(bool expanded) =>
        AppWindow.MoveAndResize(GetPaletteBounds(expanded));

    private RectInt32 GetPaletteBounds(bool expanded)
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var width = UsesVerticalLayout
            ? expanded ? VerticalCompactWidth + _feedbackExtent : VerticalCompactWidth
            : HorizontalCompactWidth;
        var height = UsesVerticalLayout
            ? VerticalHeight
            : expanded ? HorizontalCompactHeight + _feedbackExtent : HorizontalCompactHeight;

        var x = _placement switch
        {
            OverlayPlacement.TopLeft or OverlayPlacement.BottomLeft or OverlayPlacement.LeftCenter =>
                workArea.X + ScreenEdgeGap,
            OverlayPlacement.TopRight or OverlayPlacement.BottomRight or OverlayPlacement.RightCenter =>
                workArea.X + workArea.Width - width - ScreenEdgeGap,
            _ => workArea.X + (workArea.Width - width) / 2,
        };
        var y = _placement switch
        {
            OverlayPlacement.TopCenter or OverlayPlacement.TopLeft or OverlayPlacement.TopRight =>
                workArea.Y + ScreenEdgeGap,
            OverlayPlacement.BottomCenter or OverlayPlacement.BottomLeft or OverlayPlacement.BottomRight =>
                workArea.Y + workArea.Height - height - ScreenEdgeGap,
            _ => workArea.Y + (workArea.Height - height) / 2,
        };

        return new RectInt32(x, y, width, height);
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
        _morphCancellation?.Cancel();
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
        if (_scheduleBusy)
        {
            return;
        }

        SetScheduleBusy(true);
        try
        {
            var action = Enum.Parse<PowerActionType>(
                ((ComboBoxItem)ActionPicker.SelectedItem).Tag!.ToString()!);
            var now = DateTimeOffset.UtcNow;
            var preview = _scheduleInputService.Preview(
                TimeInput.Text,
                ScheduleKind.OneTime,
                action,
                keepDaily: false,
                now,
                TimeZoneInfo.Local);
            if (!preview.IsValid)
            {
                ShowValidation(SchedulePreviewFormatter.FormatError(preview.ErrorCode));
                TimeInput.Focus(FocusState.Programmatic);
                TimeInput.SelectAll();
                return;
            }

            var prepared = _scheduleInputService.Prepare(
                TimeInput.Text,
                ScheduleKind.OneTime,
                action,
                keepDaily: false,
                now,
                TimeZoneInfo.Local);
            var result = await _runtime.ScheduleCommands.SetAsync(prepared.Draft);
            if (result.IsFullyApplied)
            {
                _activeSchedule = result.Mutation.Schedule;
                ShowStatus(AppText.Format(
                    "QuickScheduleSaved",
                    "Schedule created · {0:HH:mm}",
                    _activeSchedule.TargetAt?.ToLocalTime()));
                UpdateCancelButton();
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
        catch (TimeExpressionParseException exception)
        {
            ShowValidation(SchedulePreviewFormatter.FormatError(exception.ErrorCode));
            TimeInput.Focus(FocusState.Programmatic);
            TimeInput.SelectAll();
        }
        catch (Exception exception)
        {
            ShowValidation(AppText.Get(
                "UnableToSchedule",
                "ShutdownAT could not create this schedule. Try again or open Diagnostics."));
            _ = LogUnexpectedErrorAsync(exception);
            TimeInput.Focus(FocusState.Programmatic);
            TimeInput.SelectAll();
        }
        finally
        {
            SetScheduleBusy(false);
        }
    }

    private async Task LogUnexpectedErrorAsync(Exception exception)
    {
        try
        {
            await _runtime.Logger.WriteAsync(
                Sdat.Core.Settings.AppLogLevel.Error,
                nameof(QuickPaletteWindow),
                exception.ToString());
        }
        catch
        {
            // Logging must never interrupt the quick scheduling flow.
        }
    }

    private void OnTimeInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_feedbackKind == PaletteFeedbackKind.Error &&
            !string.Equals(TimeInput.Text, _validationInputText, StringComparison.Ordinal))
        {
            _validationInputText = null;
            _feedbackKind = PaletteFeedbackKind.None;
        }

        QueueLivePreview();
    }

    private void OnActionChanged(object sender, SelectionChangedEventArgs e) => QueueLivePreview();

    private void QueueLivePreview()
    {
        if (TimeInput is null || ScheduleButton is null)
        {
            return;
        }

        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnPreviewTimerTick(object? sender, object e)
    {
        _previewTimer.Stop();
        UpdateLivePreview();
    }

    private void UpdateLivePreview()
    {
        if (_scheduleBusy || _feedbackKind == PaletteFeedbackKind.Status ||
            ActionPicker.SelectedItem is not ComboBoxItem selectedAction)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TimeInput.Text))
        {
            _feedbackKind = PaletteFeedbackKind.None;
            ScheduleButton.Content = AppText.Get("ScheduleButtonDefault", "Schedule");
            ScheduleButton.IsEnabled = false;
            _ = SetFeedbackVisibleAsync(visible: false, clearTextWhenHidden: true);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var preview = _scheduleInputService.Preview(
            TimeInput.Text,
            ScheduleKind.OneTime,
            Enum.Parse<PowerActionType>(selectedAction.Tag!.ToString()!),
            keepDaily: false,
            now,
            TimeZoneInfo.Local);
        if (!preview.IsValid)
        {
            ShowValidation(SchedulePreviewFormatter.FormatError(preview.ErrorCode));
            ScheduleButton.Content = AppText.Get("ScheduleButtonDefault", "Schedule");
            ScheduleButton.IsEnabled = false;
            return;
        }

        _feedbackResetCancellation?.Cancel();
        _validationInputText = null;
        _feedbackKind = PaletteFeedbackKind.Preview;
        FeedbackText.Text = SchedulePreviewFormatter.Format(preview, now, TimeZoneInfo.Local);
        FeedbackText.Foreground =
            (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        ScheduleButton.Content = SchedulePreviewFormatter.FormatButton(preview);
        ScheduleButton.IsEnabled = true;
        _ = SetFeedbackVisibleAsync(visible: true);
    }

    private void ShowValidation(string message)
    {
        _feedbackResetCancellation?.Cancel();
        _validationInputText = TimeInput.Text;
        _feedbackKind = PaletteFeedbackKind.Error;
        FeedbackText.Text = message;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        _ = SetFeedbackVisibleAsync(visible: true);
    }

    private void ShowStatus(string message)
    {
        _feedbackResetCancellation?.Cancel();
        _validationInputText = null;
        _feedbackKind = PaletteFeedbackKind.Status;
        FeedbackText.Text = message;
        FeedbackText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _ = SetFeedbackVisibleAsync(visible: true);
        _feedbackResetCancellation = new CancellationTokenSource();
        _ = ClearTransientStatusAsync(_feedbackResetCancellation.Token);
    }

    private async Task SetFeedbackVisibleAsync(
        bool visible,
        bool clearTextWhenHidden = false)
    {
        if (visible)
        {
            FeedbackPanel.Visibility = Visibility.Visible;
            FeedbackPanel.UpdateLayout();
            var measuredExtent = UsesVerticalLayout
                ? (int)Math.Ceiling(FeedbackPanel.ActualWidth) + FeedbackGap
                : (int)Math.Ceiling(FeedbackPanel.ActualHeight) + FeedbackGap;
            measuredExtent = Math.Max(measuredExtent, FeedbackGap);
            if (_feedbackExpanded && measuredExtent == _feedbackExtent)
            {
                FeedbackPanel.Opacity = 1;
                FeedbackTransform.X = 0;
                FeedbackTransform.Y = 0;
                return;
            }

            _feedbackExtent = measuredExtent;
        }

        _morphCancellation?.Cancel();
        _morphCancellation?.Dispose();
        _morphCancellation = new CancellationTokenSource();
        var cancellationToken = _morphCancellation.Token;

        var start = new RectInt32(
            AppWindow.Position.X,
            AppWindow.Position.Y,
            AppWindow.Size.Width,
            AppWindow.Size.Height);
        var end = GetPaletteBounds(visible);
        var startOpacity = FeedbackPanel.Opacity;
        var endOpacity = visible ? 1d : 0d;
        var hiddenOffset = GetFeedbackHiddenOffset();
        var startX = visible && startOpacity <= 0 ? hiddenOffset.X : FeedbackTransform.X;
        var startY = visible && startOpacity <= 0 ? hiddenOffset.Y : FeedbackTransform.Y;
        var endX = visible ? 0d : hiddenOffset.X;
        var endY = visible ? 0d : hiddenOffset.Y;
        _feedbackExpanded = visible;

        try
        {
            var frames = _animationsEnabled ? MorphFrameCount : 1;
            for (var frame = 1; frame <= frames; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var progress = (double)frame / frames;
                var eased = 1d - Math.Pow(1d - progress, 3d);
                AppWindow.MoveAndResize(Interpolate(start, end, eased));
                FeedbackPanel.Opacity = Lerp(startOpacity, endOpacity, eased);
                FeedbackTransform.X = Lerp(startX, endX, eased);
                FeedbackTransform.Y = Lerp(startY, endY, eased);
                if (frame < frames)
                {
                    await Task.Delay(MorphDurationMilliseconds / frames, cancellationToken);
                }
            }

            if (!visible)
            {
                FeedbackPanel.Visibility = Visibility.Collapsed;
                if (clearTextWhenHidden)
                {
                    FeedbackText.Text = string.Empty;
                    FeedbackText.Foreground =
                        (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer feedback state owns the morph.
        }
    }

    private Point GetFeedbackHiddenOffset()
    {
        if (_placement == OverlayPlacement.LeftCenter)
        {
            return new Point(-8, 0);
        }

        if (_placement == OverlayPlacement.RightCenter)
        {
            return new Point(8, 0);
        }

        return GrowsTowardTop
            ? new Point(0, 8)
            : new Point(0, -8);
    }

    private static RectInt32 Interpolate(RectInt32 start, RectInt32 end, double amount) =>
        new(
            (int)Math.Round(Lerp(start.X, end.X, amount)),
            (int)Math.Round(Lerp(start.Y, end.Y, amount)),
            (int)Math.Round(Lerp(start.Width, end.Width, amount)),
            (int)Math.Round(Lerp(start.Height, end.Height, amount)));

    private static double Lerp(double start, double end, double amount) =>
        start + ((end - start) * amount);

    private void UpdateCancelButton()
    {
        if (_activeSchedule is null)
        {
            PaletteCancelButton.Visibility = Visibility.Collapsed;
            PaletteCancelButton.Content = string.Empty;
            ToolTipService.SetToolTip(PaletteCancelButton, null);
            return;
        }

        var target = _activeSchedule.TargetAt?.ToLocalTime();
        var (resourceKey, fallback) = _activeSchedule.Action switch
        {
            PowerActionType.Restart =>
                ("QuickCancelRestart", "Cancel restart · {0:HH:mm}"),
            PowerActionType.Suspend =>
                ("QuickCancelSuspend", "Cancel suspend · {0:HH:mm}"),
            _ =>
                ("QuickCancelShutdown", "Cancel shutdown · {0:HH:mm}"),
        };
        var label = AppText.Format(resourceKey, fallback, target);
        PaletteCancelButton.Content = label;
        ToolTipService.SetToolTip(
            PaletteCancelButton,
            AppText.Get(
                "QuickCancelToolTip",
                "Cancel this active schedule."));
        PaletteCancelButton.Visibility = Visibility.Visible;
    }

    private async Task RefreshActiveScheduleAsync()
    {
        try
        {
            _activeSchedule = (await _runtime.Schedules.ListAsync())
                .SingleOrDefault(schedule => schedule.Kind == ScheduleKind.OneTime);
            if (_activeSchedule is null)
            {
                UpdateCancelButton();
                return;
            }

            UpdateCancelButton();
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
            UpdateCancelButton();
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
            var focused = FocusManager.GetFocusedElement(PaletteRoot.XamlRoot);
            if (ActionPicker.IsDropDownOpen ||
                focused is Button ||
                focused is ComboBoxItem)
            {
                return;
            }

            if (!ScheduleButton.IsEnabled)
            {
                return;
            }

            e.Handled = true;
            await ScheduleAsync();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_hasBeenActivated && !_showInProgress && !_isClosing)
            {
                Close();
            }
            return;
        }

        _hasBeenActivated = true;
        if (!_isClosing)
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
                _feedbackKind = PaletteFeedbackKind.None;
                UpdateLivePreview();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer status or validation message owns the feedback area.
        }
    }

    private void SetScheduleBusy(bool busy)
    {
        _scheduleBusy = busy;
        ScheduleButton.IsEnabled = !busy && HasValidScheduleInput();
        ActionPicker.IsEnabled = !busy;
        TimeInput.IsEnabled = !busy;
        PaletteCancelButton.IsEnabled = !busy;
    }

    private bool HasValidScheduleInput()
    {
        if (ActionPicker.SelectedItem is not ComboBoxItem selectedAction ||
            string.IsNullOrWhiteSpace(TimeInput.Text))
        {
            return false;
        }

        return _scheduleInputService.Preview(
            TimeInput.Text,
            ScheduleKind.OneTime,
            Enum.Parse<PowerActionType>(selectedAction.Tag!.ToString()!),
            keepDaily: false,
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Local).IsValid;
    }

    private enum PaletteFeedbackKind
    {
        None,
        Preview,
        Error,
        Status,
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AnimateWindow(nint window, uint time, uint flags);

}
