using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sdat.Core.Execution;
using Sdat.Core.Scheduling;

namespace Sdat.Windows.Execution;

public sealed class WindowsPowerActionExecutor : IPowerActionExecutor
{
    public async Task ExecuteAsync(PowerActionType action, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SDAT power actions require Windows.");
        }

        if (action == PowerActionType.Suspend)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the suspend request.");
            }

            return;
        }

        var startInfo = CreateShutdownStartInfo(action);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows shutdown process could not be started.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new Win32Exception(process.ExitCode, $"shutdown.exe exited with code {process.ExitCode}.");
        }
    }

    internal static ProcessStartInfo CreateShutdownStartInfo(PowerActionType action)
    {
        if (action == PowerActionType.Suspend)
        {
            throw new ArgumentOutOfRangeException(nameof(action), "Suspend does not use shutdown.exe.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(action == PowerActionType.Restart ? "/r" : "/s");
        startInfo.ArgumentList.Add("/f");
        startInfo.ArgumentList.Add("/t");
        startInfo.ArgumentList.Add("30");
        return startInfo;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);
}
