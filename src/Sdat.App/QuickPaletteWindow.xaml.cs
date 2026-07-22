using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Sdat.Core.Scheduling;
using Sdat.Windows.Hosting;
using Windows.Graphics;
using Windows.System;

namespace Sdat.App;

public sealed partial class QuickPaletteWindow : Window
{
    private readonly SdatRuntime _runtime;

    public QuickPaletteWindow(SdatRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        Title = AppText.Get("QuickPaletteTitle", "Quick schedule — ShutdownAT");
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(PaletteRoot);
        ConfigureWindow();
        Activated += (_, _) => TimeInput.Focus(FocusState.Programmatic);
    }

    private void ConfigureWindow()
    {
        const int width = 560;
        const int height = 100;
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
                    "Saved with a recovery warning. Open ShutdownAT for details.");
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
