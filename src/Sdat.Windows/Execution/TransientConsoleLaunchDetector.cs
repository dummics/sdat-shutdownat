using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sdat.Windows.Execution;

public static class TransientConsoleLaunchDetector
{
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static bool IsWindowsRunLaunch()
    {
        if (Environment.GetEnvironmentVariable("SDAT_FROM_WINR") == "1")
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("SDAT_WRAPPER_PROCESS") != "1")
        {
            return false;
        }

        try
        {
            var currentParent = GetParentProcessId(Environment.ProcessId);
            if (currentParent is null)
            {
                return false;
            }

            var caller = GetParentProcessId(currentParent.Value);
            return caller is not null && IsWindowsRunProcessChain(
                wrapperMarkerPresent: true,
                GetProcessName(currentParent.Value),
                GetProcessName(caller.Value));
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsWindowsRunProcessChain(
        bool wrapperMarkerPresent,
        string? wrapperProcessName,
        string? callerProcessName) =>
        wrapperMarkerPresent &&
        string.Equals(
            Path.GetFileNameWithoutExtension(wrapperProcessName),
            "cmd",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Path.GetFileNameWithoutExtension(callerProcessName),
            "explorer",
            StringComparison.OrdinalIgnoreCase);

    private static string? GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static int? GetParentProcessId(int processId)
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
            };
            if (!Process32First(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                if (entry.ProcessId == processId)
                {
                    return unchecked((int)entry.ParentProcessId);
                }
            }
            while (Process32Next(snapshot, ref entry));

            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public int ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
