using Microsoft.UI.Xaml;
using Sdat.Core.Commands;
using Sdat.Core.Execution;
using Sdat.Windows.Hosting;

namespace Sdat.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (commandLine.Contains("--task-run", StringComparer.OrdinalIgnoreCase))
        {
            await RunScheduledInvocationAsync(commandLine);
            Exit();
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }

    private static async Task RunScheduledInvocationAsync(string[] commandLine)
    {
        try
        {
            var invocation = CliInvocationParser.Parse(commandLine);
            var runtime = await SdatRuntime.CreateAsync(Environment.ProcessPath!);
            await runtime.TaskInvocations.RunAsync(new TaskInvocation(
                invocation.ScheduleId!.Value,
                invocation.Revision!.Value,
                invocation.TaskRole!.Value,
                invocation.ReminderOffsetMinutes));
        }
        catch
        {
            // Task Scheduler receives a fail-safe no-op; diagnostics are persisted where possible.
        }
    }
}
