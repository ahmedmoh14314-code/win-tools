using System.Globalization;
using System.Security.Cryptography;
using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Text;

/// <summary>
/// The General Files module's jobs (V2 PR 1.4): File info and SHA-256 of one file or folder. Files are opened for
/// reading only, shared with writers, and never changed; nothing is written anywhere. Every fact stays within
/// <see cref="JobFacts"/>' bounds, which the host checks again.
/// </summary>
internal static class FileJobs
{
    /// <summary>The most folder entries counted, and the longest the count may take, before the answer says "more than".</summary>
    internal const int MaxEntries = 10_000;
    internal static readonly TimeSpan CountDeadline = TimeSpan.FromSeconds(2);

    public static JobResult Run(JobRequest request)
    {
        if (request.Parameters is { Count: > 0 }) return JobResult.Fail(request.Id, ErrorCode.InvalidRequest, "This command takes no parameters.");
        if (request.Inputs is not [var path]) return JobResult.Fail(request.Id, ErrorCode.InvalidRequest, "One file per request.");
        if (!PathPolicy.IsSupported(path)) return JobResult.Fail(request.Id, ErrorCode.InvalidRequest, "The path is not supported.");
        try
        {
            return request.CapabilityId switch
            {
                FilesCapabilities.InfoId => Info(request.Id, path),
                FilesCapabilities.Sha256Id => Hash(request.Id, path),
                _ => JobResult.Fail(request.Id, ErrorCode.UnsupportedOperation, "This worker does not run that command."),
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(request.Id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return JobResult.Fail(request.Id, ErrorCode.SourceNotReadable, $"The file could not be read ({ex.GetType().Name})."); }
    }

    private static JobResult Info(string id, string path)
    {
        var folder = Path.EndsInDirectorySeparator(path) ? path[..^1] : path;
        if (Directory.Exists(folder)) return JobResult.WithFacts(id, FolderFacts(new DirectoryInfo(folder)));
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException();
        var head = new byte[ContentType.HeaderLength];
        int read;
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            read = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var type = ContentType.Detect(head.AsSpan(0, read), file.Length);
        var mismatch = ContentType.Mismatch(type, file.Extension);
        var facts = new List<JobFact>
        {
            new("Type", mismatch ? $"{type.Name}, but named {file.Extension.ToLowerInvariant()}" : type.Name, Notice: mismatch),
            new("Size", Size(file.Length)),
            new("Modified", When(file.LastWriteTime)),
            new("Created", When(file.CreationTime)),
        };
        if (Attributes(file.Attributes) is { } attributes) facts.Add(new("Attributes", attributes));
        return JobResult.WithFacts(id, facts);
    }

    private static List<JobFact> FolderFacts(DirectoryInfo folder)
    {
        var link = folder.LinkTarget is not null;
        var facts = new List<JobFact> { new("Type", link ? "Folder (a link to another place)" : "Folder") };
        facts.Add(new("Contains", Contents(folder)));
        facts.Add(new("Modified", When(folder.LastWriteTime)));
        facts.Add(new("Created", When(folder.CreationTime)));
        if (Attributes(folder.Attributes & ~FileAttributes.Directory) is { } attributes) facts.Add(new("Attributes", attributes));
        return facts;
    }

    /// <summary>The folder's own entries (not their contents), bounded in number and time.</summary>
    internal static string Contents(DirectoryInfo folder)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        var deadline = DateTime.UtcNow + CountDeadline;
        int files = 0, folders = 0;
        foreach (var entry in folder.EnumerateFileSystemInfos("*", options))
        {
            if (entry.Attributes.HasFlag(FileAttributes.Directory)) folders++; else files++;
            if (files + folders >= MaxEntries || DateTime.UtcNow > deadline)
                return string.Create(CultureInfo.InvariantCulture, $"More than {files + folders:N0} items");
        }
        if (files + folders == 0) return "Nothing (empty)";
        return string.Join(", ", new[] { Count(files, "file", "files"), Count(folders, "folder", "folders") }.Where(s => s is not null));
    }

    private static string? Count(int n, string one, string many) => n switch { 0 => null, 1 => $"1 {one}", _ => string.Create(CultureInfo.InvariantCulture, $"{n:N0} {many}") };

    private static JobResult Hash(string id, string path)
    {
        if (Path.EndsInDirectorySeparator(path) || Directory.Exists(path)) return JobResult.Fail(id, ErrorCode.IsAFolder, "It is a folder.");
        var before = new FileInfo(path);
        if (!before.Exists) throw new FileNotFoundException();
        var (length, written) = (before.Length, before.LastWriteTimeUtc);
        byte[] hash;
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
            hash = SHA256.HashData(s);
        var after = new FileInfo(path);
        var facts = new List<JobFact> { new("SHA-256", Convert.ToHexStringLower(hash)), new("Size", Size(length)) };
        // A file written while it was read has no single hash: say so instead of passing one off as the file's.
        if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != written)
            facts.Add(new("Changed", "The file changed while it was read; hash it again", Notice: true));
        return JobResult.WithFacts(id, facts);
    }

    private static string Size(long bytes) =>
        bytes < 1024 ? FeedbackText.Size(bytes) : string.Create(CultureInfo.InvariantCulture, $"{FeedbackText.Size(bytes)} ({bytes:N0} bytes)");

    private static string When(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string? Attributes(FileAttributes a)
    {
        var names = new List<string>();
        if (a.HasFlag(FileAttributes.ReadOnly)) names.Add("Read-only");
        if (a.HasFlag(FileAttributes.Hidden)) names.Add("Hidden");
        if (a.HasFlag(FileAttributes.System)) names.Add("System");
        if (a.HasFlag(FileAttributes.ReparsePoint)) names.Add("Link");
        if (a.HasFlag(FileAttributes.Encrypted)) names.Add("Encrypted");
        if (a.HasFlag(FileAttributes.Compressed)) names.Add("Compressed");
        if (a.HasFlag(FileAttributes.Offline) || a.HasFlag((FileAttributes)0x00400000)) names.Add("Online only"); // RECALL_ON_DATA_ACCESS
        return names.Count == 0 ? null : string.Join(", ", names);
    }
}
