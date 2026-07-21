using Sdat.Core.Commands;
using Sdat.Core.Scheduling;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class CliInvocationParserTests
{
    [Fact]
    public void Parses_skip_command()
    {
        Assert.Equal(CliCommandType.Skip, CliInvocationParser.Parse(["skip"]).Command);
    }

    [Fact]
    public void Parses_logs_command_with_machine_output()
    {
        var invocation = CliInvocationParser.Parse(["logs", "--json"]);

        Assert.Equal(CliCommandType.Logs, invocation.Command);
        Assert.True(invocation.Json);
    }

    [Fact]
    public void Parses_explicit_preview_contract()
    {
        var invocation = CliInvocationParser.Parse(
            ["preview", "--action", "restart", "--time", "36m", "--json"]);

        Assert.Equal(CliCommandType.Preview, invocation.Command);
        Assert.Equal(PowerActionType.Restart, invocation.Action);
        Assert.Equal("36m", invocation.TimeExpression);
        Assert.True(invocation.Json);
    }

    [Fact]
    public void Dry_run_converts_legacy_schedule_into_preview()
    {
        var invocation = CliInvocationParser.Parse(["23:30", "-DryRun"]);

        Assert.Equal(CliCommandType.Preview, invocation.Command);
        Assert.Equal("23:30", invocation.TimeExpression);
    }

    [Fact]
    public void Legacy_s_alias_skips_daily_once()
    {
        Assert.Equal(CliCommandType.Skip, CliInvocationParser.Parse(["-s"]).Command);
    }

    [Fact]
    public void Explicit_action_rejects_numeric_enum_values()
    {
        Assert.Throws<CliUsageException>(() =>
            CliInvocationParser.Parse(["preview", "--action", "99", "--time", "36m"]));
    }

    [Fact]
    public void Parses_uninstall_keep_data_contract()
    {
        var invocation = CliInvocationParser.Parse(["uninstall", "--keep-data", "--json"]);

        Assert.Equal(CliCommandType.Uninstall, invocation.Command);
        Assert.True(invocation.KeepData);
        Assert.True(invocation.Json);
    }

    [Theory]
    [InlineData("3h")]
    [InlineData("23:30")]
    public void Bare_time_schedules_one_time_shutdown(string value)
    {
        var invocation = CliInvocationParser.Parse([value]);

        Assert.Equal(CliCommandType.Schedule, invocation.Command);
        Assert.Equal(ScheduleKind.OneTime, invocation.ScheduleKind);
        Assert.Equal(PowerActionType.Shutdown, invocation.Action);
        Assert.Equal(value, invocation.TimeExpression);
    }

    [Fact]
    public void Daily_and_restart_legacy_switches_are_preserved()
    {
        var invocation = CliInvocationParser.Parse(["02:30", "-p", "-Restart"]);

        Assert.Equal(ScheduleKind.Daily, invocation.ScheduleKind);
        Assert.Equal(PowerActionType.Restart, invocation.Action);
    }

    [Fact]
    public void Ssat_alias_defaults_to_suspend()
    {
        var invocation = CliInvocationParser.Parse(["45m"], suspendAlias: true);

        Assert.Equal(PowerActionType.Suspend, invocation.Action);
    }

    [Theory]
    [InlineData("-a", false)]
    [InlineData("-aa", true)]
    public void Cancel_aliases_are_preserved(string alias, bool all)
    {
        var invocation = CliInvocationParser.Parse([alias]);

        Assert.Equal(CliCommandType.Cancel, invocation.Command);
        Assert.Equal(all, invocation.CancelAll);
    }

    [Fact]
    public void Internal_task_invocation_requires_identity_and_revision()
    {
        Assert.Throws<CliUsageException>(() => CliInvocationParser.Parse(["--task-run", "--role", "execute"]));
    }

    [Theory]
    [InlineData("tui")]
    [InlineData("t")]
    [InlineData("-tui")]
    public void Tui_aliases_are_preserved(string alias)
    {
        Assert.Equal(CliCommandType.Tui, CliInvocationParser.Parse([alias]).Command);
    }
}
