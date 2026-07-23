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
    private readonly SdatRuntime _runtime;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private bool _allowClose;
    private bool _isClosing;

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
        const int width = 480;
        const int height = 76;
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
            workArea.Y + workArea.Height - height - 28,
            width,
            height));
    }

    private void OnPaletteLoaded(object sender, RoutedEventArgs e)
    {
        PaletteRoot.Opacity = 1;
        if (_animationsEnabled)
        {
            FadeInStoryboard.Begin();
        }
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
                Close();
            }
            else
            {
                TimeInput.Header = AppText.Get(
                    "PaletteRecoveryWarning",
                    "Schedule saved, but the Windows integration needs attention. Open ShutdownAT for details.");
            }
        }
        catch (Exception exception)
        {
            TimeInput.Header = exception.Message;
            TimeInput.SelectAll();
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
