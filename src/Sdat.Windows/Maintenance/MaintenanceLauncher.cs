using System.Diagnostics;
using System.Text;

namespace Sdat.Windows.Maintenance;

public sealed record MaintenanceLaunchResult(string Operation, int ProcessId, string Detail);

public sealed class MaintenanceLauncher(string installDirectory)
{
    private readonly string _installDirectory = Path.GetFullPath(installDirectory);

    public MaintenanceLaunchResult StartUpdate()
    {
        var sourceScript = RequireScript("install.ps1");
        var temporaryScript = Path.Combine(
            Path.GetTempPath(),
            $"sdat-update-{Guid.NewGuid():N}.ps1");
        File.Copy(sourceScript, temporaryScript, overwrite: false);
        var command = BuildDeferredUpdateCommand(Environment.ProcessId, temporaryScript, _installDirectory);
        var process = StartEncodedPowerShell(command);
        return new MaintenanceLaunchResult(
            "update",
            process.Id,
            "Update helper started and will continue after this CLI process exits.");
    }

    public MaintenanceLaunchResult StartUninstall(bool keepData)
    {
        var sourceScript = RequireScript("uninstall.ps1");
        var temporaryScript = Path.Combine(
            Path.GetTempPath(),
            $"sdat-uninstall-{Guid.NewGuid():N}.ps1");
        File.Copy(sourceScript, temporaryScript, overwrite: false);
        var command = BuildDeferredUninstallCommand(
            Environment.ProcessId,
            temporaryScript,
            _installDirectory,
            keepData);
        var process = StartEncodedPowerShell(command);
        return new MaintenanceLaunchResult(
            "uninstall",
            process.Id,
            keepData
                ? "Uninstall started; runtime data will be moved to a backup."
                : "Uninstall started; runtime data will be removed.");
    }

    private Process StartEncodedPowerShell(string command)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = CreateHiddenStartInfo();
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The SDAT maintenance helper could not be started.");
    }

    private static ProcessStartInfo CreateHiddenStartInfo() => new()
    {
        FileName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"),
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    };

    private string RequireScript(string name)
    {
        var path = Path.Combine(_installDirectory, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The packaged maintenance script is missing: {name}", path);
        }

        return path;
    }

    internal static string BuildDeferredUpdateCommand(
        int processId,
        string temporaryScript,
        string installDirectory) => string.Join(
        Environment.NewLine,
        $"Wait-Process -Id {processId} -ErrorAction SilentlyContinue",
        "try {",
        BuildStopInstalledCompanionCommand(installDirectory),
        $"& '{EscapePowerShellLiteral(temporaryScript)}' -Update -InstallDir '{EscapePowerShellLiteral(installDirectory)}'",
        "exit $LASTEXITCODE",
        $"}} finally {{ Remove-Item -LiteralPath '{EscapePowerShellLiteral(temporaryScript)}' -Force -ErrorAction SilentlyContinue }}");

    internal static string BuildDeferredUninstallCommand(
        int processId,
        string temporaryScript,
        string installDirectory,
        bool keepData)
    {
        var keepDataSwitch = keepData ? " -KeepData" : string.Empty;
        return string.Join(
            Environment.NewLine,
            $"Wait-Process -Id {processId} -ErrorAction SilentlyContinue",
            "try {",
            BuildStopInstalledCompanionCommand(installDirectory),
            $"& '{EscapePowerShellLiteral(temporaryScript)}' -InstallDir '{EscapePowerShellLiteral(installDirectory)}'{keepDataSwitch}",
            "exit $LASTEXITCODE",
            $"}} finally {{ Remove-Item -LiteralPath '{EscapePowerShellLiteral(temporaryScript)}' -Force -ErrorAction SilentlyContinue }}");
    }

    private static string BuildStopInstalledCompanionCommand(string installDirectory)
    {
        var companionPath = EscapePowerShellLiteral(Path.Combine(installDirectory, "SDAT.exe"));
        return string.Join(
            Environment.NewLine,
            $"$sdatCompanionPath = [IO.Path]::GetFullPath('{companionPath}')",
            "$sdatCompanion = @(Get-Process -Name 'SDAT' -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq $sdatCompanionPath) } catch { $false } })",
            "if ($sdatCompanion.Count -gt 0) { $sdatCompanion | Stop-Process -Force -ErrorAction Stop; $sdatCompanion | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue }");
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
