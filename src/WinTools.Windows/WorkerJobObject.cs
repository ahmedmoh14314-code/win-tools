using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinTools.Windows;

/// <summary>
/// One Windows job object per host, with two limits. JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: when the host process ends for
/// any reason (including a crash), Windows terminates any worker still running. JOB_OBJECT_LIMIT_PROCESS_MEMORY: no
/// worker can commit more than <see cref="WorkerMemoryLimitBytes"/>, so a decoder that runs away on a hostile file fails
/// its own allocation (reported as ImageTooLarge, or as a crash) instead of exhausting the machine (ADR-0015).
/// </summary>
public static partial class WorkerJobObject
{
    /// <summary>
    /// Per-worker commit limit: the measured worst case at the pixel limit (conversion to BMP of 50 MP, 1.06 GB) plus
    /// 40 %. The worker's runtimeconfig sets the same GC heap limit, so the GC does not derive a smaller one from this job.
    /// </summary>
    public const long WorkerMemoryLimitBytes = 1536L * 1024 * 1024;

    private static readonly Lazy<nint> Job = new(Create);

    /// <summary>Assigns the process to the host's job; false if that is not possible (the worker then still exits when its stdin closes).</summary>
    public static bool TryAssign(Process process)
    {
        var job = Job.Value;
        try { return job != 0 && AssignProcessToJobObject(job, process.Handle); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static unsafe nint Create()
    {
        var job = CreateJobObjectW(0, 0);
        if (job == 0) return 0;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000 | 0x100; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_PROCESS_MEMORY
        info.ProcessMemoryLimit = (nuint)WorkerMemoryLimitBytes;
        if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            CloseHandle(job);
            return 0;
        }
        return job; // intentionally never closed: closing it is what kills the workers, at process exit
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint CreateJobObjectW(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint handle);
}
