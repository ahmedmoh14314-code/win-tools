using System.Runtime.InteropServices;

namespace WinTools.Windows;

/// <summary>Memory the system can still hand out (GlobalMemoryStatusEx).</summary>
public static partial class SystemMemory
{
    /// <summary>
    /// Commit the system can still grant (RAM plus page file, minus what is committed), in bytes, or null if the system
    /// does not say. When it runs out, allocations fail in every process, not only ours.
    /// </summary>
    public static ulong? AvailableCommitBytes()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullAvailPageFile : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
