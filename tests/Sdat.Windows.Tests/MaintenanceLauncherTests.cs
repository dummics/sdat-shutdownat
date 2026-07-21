using Sdat.Windows.Maintenance;
using Xunit;

namespace Sdat.Windows.Tests;

public sealed class MaintenanceLauncherTests
{
    [Fact]
    public void Update_waits_for_cli_and_escapes_literal_paths()
    {
        var command = MaintenanceLauncher.BuildDeferredUpdateCommand(
            42,
            @"C:\Temp\dom's update.ps1",
            @"C:\Program Files\SDAT");

        Assert.Contains("Wait-Process -Id 42", command, StringComparison.Ordinal);
        Assert.Contains("dom''s update.ps1", command, StringComparison.Ordinal);
        Assert.Contains("-Update", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, " -KeepData")]
    [InlineData(false, "")]
    public void Uninstall_only_adds_keep_data_when_requested(bool keepData, string expected)
    {
        var command = MaintenanceLauncher.BuildDeferredUninstallCommand(
            7,
            @"C:\Temp\uninstall.ps1",
            @"C:\SDAT",
            keepData);

        if (keepData)
        {
            Assert.Contains(expected, command, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("-KeepData", command, StringComparison.Ordinal);
        }
    }
}
