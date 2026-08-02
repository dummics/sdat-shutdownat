using Sdat.Windows.Execution;
using Spectre.Console;
using Spectre.Console.Rendering;

internal static class CliTerminalPresentation
{
    private const int DefaultTransientHoldSeconds = 6;

    internal sealed record DetailRow(string Label, string Value, string Color = "white");

    public static bool TryWriteResult(
        string title,
        string summary,
        IReadOnlyList<DetailRow> rows,
        string accent = "deepskyblue1")
    {
        if (!CanRenderRichOutput())
        {
            return false;
        }

        try
        {
            PrepareTransientConsole();
            var grid = new Grid();
            grid.AddColumn(new GridColumn().NoWrap().PadRight(2));
            grid.AddColumn(new GridColumn());
            foreach (var row in rows)
            {
                grid.AddRow(
                    new Markup($"[grey58]{Markup.Escape(row.Label)}[/]"),
                    new Markup($"[{row.Color}]{Markup.Escape(row.Value)}[/]"));
            }

            var content = new List<IRenderable>
            {
                new Markup($"[bold white]{Markup.Escape(summary)}[/]"),
            };
            if (rows.Count > 0)
            {
                content.Add(Text.Empty);
                content.Add(grid);
            }

            var panel = new Panel(new Rows(content.ToArray()))
            {
                Header = new PanelHeader($"[{accent}]{Markup.Escape(title)}[/]"),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Grey35),
                Expand = false,
                Padding = new Padding(2, 1, 2, 1),
                Width = GetPanelWidth(),
            };
            AnsiConsole.WriteLine();
            AnsiConsole.Write(Align.Center(panel));
            AnsiConsole.WriteLine();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void HoldTransientResult()
    {
        if (!ShouldHoldTransientResult())
        {
            return;
        }

        Thread.Sleep(TimeSpan.FromSeconds(GetTransientHoldSeconds()));
    }

    private static bool CanRenderRichOutput() =>
        Environment.UserInteractive &&
        !Console.IsOutputRedirected &&
        AnsiConsole.Profile.Capabilities.Interactive;

    private static bool ShouldHoldTransientResult() =>
        Environment.UserInteractive &&
        !Console.IsOutputRedirected &&
        TransientConsoleLaunchDetector.IsWindowsRunLaunch();

    private static void PrepareTransientConsole()
    {
        if (!TransientConsoleLaunchDetector.IsWindowsRunLaunch())
        {
            return;
        }

        try
        {
            Console.Title = "ShutdownAT";
            AnsiConsole.Clear();
        }
        catch
        {
            // A panel can still render if the host does not allow title or clear operations.
        }
    }

    private static int GetPanelWidth()
    {
        try
        {
            return Math.Clamp(Console.WindowWidth - 6, 44, 68);
        }
        catch
        {
            return 60;
        }
    }

    private static int GetTransientHoldSeconds()
    {
        var configured = Environment.GetEnvironmentVariable("SDAT_RESULT_HOLD_SECONDS");
        return int.TryParse(configured, out var seconds)
            ? Math.Clamp(seconds, 0, 15)
            : DefaultTransientHoldSeconds;
    }
}
