using System.Runtime.InteropServices;

namespace WinTools.Worker;

/// <summary>
/// Creates a folder that must not exist yet. <see cref="Directory.CreateDirectory(string)"/> succeeds silently on an
/// existing folder or junction, which would let a folder planted under the staging name receive a job's files
/// (evaluator, PR 2.1). CreateDirectoryW fails when the name is taken, so the folder is ours; it is then checked to be a
/// plain, empty folder.
/// </summary>
public static partial class ExclusiveDirectory
{
    public static bool TryCreate(string path)
    {
        var native = path.Length >= 248 && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path
            : path;
        if (!CreateDirectoryW(native, 0)) return false;
        var created = new DirectoryInfo(path);
        return created.LinkTarget is null && !created.Attributes.HasFlag(FileAttributes.ReparsePoint) && !created.EnumerateFileSystemInfos().Any();
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryW(string path, nint securityAttributes);
}
