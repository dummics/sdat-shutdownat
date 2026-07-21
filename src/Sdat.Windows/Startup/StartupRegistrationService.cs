using Microsoft.Win32;

namespace Sdat.Windows.Startup;

public sealed class StartupRegistrationService(string applicationPath)
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SDAT";

    public void SetEnabled(bool enabled)
    {
        if (!Path.IsPathFullyQualified(applicationPath))
        {
            throw new ArgumentException("The companion path must be absolute.", nameof(applicationPath));
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The current-user startup registry key is unavailable.");
        var expectedValue = $"\"{Path.GetFullPath(applicationPath)}\" --background";
        if (enabled)
        {
            key.SetValue(ValueName, expectedValue, RegistryValueKind.String);
        }
        else if (string.Equals(
                     key.GetValue(ValueName) as string,
                     expectedValue,
                     StringComparison.OrdinalIgnoreCase))
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
