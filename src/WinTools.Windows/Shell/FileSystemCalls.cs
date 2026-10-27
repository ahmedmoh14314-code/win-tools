using System.Runtime.InteropServices;

namespace WinTools.Windows.Shell;

/// <summary>
/// The two file-system calls Copy to and Move to need beyond .NET's (ADR-0032), as Microsoft documents them:
/// <list type="bullet">
/// <item><c>CopyFileExW</c> with <c>COPY_FILE_FAIL_IF_EXISTS</c>: never over an existing file; it copies the alternate data
/// streams (the Mark of the Web stays with a downloaded file), the attributes and the times, and stops when the cancel flag
/// is set, deleting what it wrote (<c>ERROR_REQUEST_ABORTED</c>).</item>
/// <item><c>MoveFileExW</c> with no flags: a rename on one volume, for a file or a folder, failing with
/// <c>ERROR_ALREADY_EXISTS</c> when the name is taken and with <c>ERROR_NOT_SAME_DEVICE</c> across volumes. .NET's
/// <c>File.Move</c> passes <c>MOVEFILE_COPY_ALLOWED</c>, which would copy and delete without our verification.</item>
/// </list>
/// Both return the Win32 error (0 for success) instead of throwing; paths go with the <c>\\?\</c> prefix, so long paths work
/// whatever the machine's LongPathsEnabled setting.
/// </summary>
public static unsafe partial class FileSystemCalls
{
    public const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5, ErrorNotSameDevice = 17,
        ErrorSharingViolation = 32, ErrorLockViolation = 33, ErrorHandleDiskFull = 39, ErrorFileExists = 80, ErrorDiskFull = 112,
        ErrorInvalidName = 123, ErrorAlreadyExists = 183, ErrorFilenameExcedRange = 206, ErrorRequestAborted = 1235;

    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x1;

    /// <summary>Copies one file to a new name, never over an existing one; stops when <paramref name="cancellationToken"/> is
    /// cancelled. Returns 0 or the Win32 error.</summary>
    public static int CopyFile(string from, string to, CancellationToken cancellationToken)
    {
        var cancel = (int*)NativeMemory.AllocZeroed(sizeof(int));
        try
        {
            // The flag is read by CopyFileExW between chunks; Dispose waits for a callback that is running, so the flag
            // outlives every write to it.
            using (cancellationToken.Register(static p => Volatile.Write(ref *(int*)(nint)p!, 1), (nint)cancel))
            {
                if (cancellationToken.IsCancellationRequested) return ErrorRequestAborted;
                return CopyFileExW(Extended(from), Extended(to), 0, 0, cancel, COPY_FILE_FAIL_IF_EXISTS) ? 0 : Marshal.GetLastPInvokeError();
            }
        }
        finally { NativeMemory.Free(cancel); }
    }

    /// <summary>Renames a file or folder on its own volume, never over an existing name. Returns 0 or the Win32 error
    /// (<see cref="ErrorNotSameDevice"/> across volumes).</summary>
    public static int MoveOnVolume(string from, string to) =>
        MoveFileExW(Extended(from), Extended(to), 0) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>A path with the <c>\\?\</c> prefix (<c>\\?\UNC\</c> for a share), which turns off MAX_PATH. The path must
    /// already be canonical (PathPolicy): the prefix also turns off normalisation.</summary>
    public static string Extended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CopyFileExW(string existingFileName, string newFileName, nint progressRoutine, nint data, int* cancel, uint copyFlags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existingFileName, string newFileName, uint flags);
}
