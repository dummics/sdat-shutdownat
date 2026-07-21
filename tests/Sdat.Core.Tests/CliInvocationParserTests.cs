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
