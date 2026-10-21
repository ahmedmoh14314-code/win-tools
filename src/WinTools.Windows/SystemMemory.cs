using System.Runtime.InteropServices;

namespace WinTools.Windows;

/// <summary>Physical memory the system can hand out right now (GlobalMemoryStatusEx).</summary>
public static partial class SystemMemory
{
    /// <summary>Available physical memory in bytes, or null if the system does not say.</summary>
    public static ulong? AvailablePhysicalBytes()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullAvailPhys : null;
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
