using System.Globalization;
using System.Security.Cryptography;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;
using WinTools.Core.Subtitles;

namespace WinTools.Worker.Text;

/// <summary>
/// The Subtitles module's jobs (V2 PR 7.1, ADR-0024). The source is read once, bounded (<see cref="SubtitleCapabilities.MaxInputBytes"/>),
/// decoded (<see cref="SubtitleEncoding"/>) and parsed (<see cref="SubtitleParser"/>). A file with malformed timing is not
/// converted or shifted: the job fails with the first problem's line, and Info lists the count. Outputs follow ADR-0005:
/// written to an exclusive temporary file in the destination, read back and parsed again, marked with the source's
/// zone, then moved to the first free name without overwriting.
/// </summary>
internal static class SubtitleJobs
{
    private const int ErrorFileExists = unchecked((int)0x80070050), ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;

    public static JobResult Run(JobRequest request)
    {
        var id = request.Id;
        if (id is not { Length: > 0 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return JobResult.Fail("invalid", ErrorCode.InvalidRequest, "Request id must be 1-64 ASCII letters or digits.");
        if (request.Parameters is { Count: > 0 }) return JobResult.Fail(id, ErrorCode.InvalidRequest, "This command takes no parameters.");
        if (request.Inputs is not [var path] || !PathPolicy.IsSupported(path) || Path.EndsInDirectorySeparator(path))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "One subtitle file per request.");
        if (request.OutputDirectory is { } od && !PathPolicy.IsSupported(Path.Combine(od, "x.srt")))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        try
        {
            var (decoded, size) = Read(path);
            var format = SubtitleCapabilities.FormatOf(path);
            var doc = SubtitleParser.Parse(decoded.Text, format);
            if (request.CapabilityId == SubtitleCapabilities.InfoId) return Info(id, decoded, size, doc);
            if (doc.Problems.Count > 0) return JobResult.Fail(id, ErrorCode.DecodeFailed, $"The file has errors, so it was not changed: {doc.Problems[0]}");
            if (doc.Cues.Count == 0) return JobResult.Fail(id, ErrorCode.NothingToDo, "The file has no cues.");
            var output = request.CapabilityId switch
            {
                SubtitleCapabilities.ToVttId or SubtitleCapabilities.ToSrtId or SubtitleCapabilities.CleanUpId => doc,
                _ when SubtitleCapabilities.ShiftOf(request.CapabilityId) is { } by => SubtitleTiming.Shift(doc, by),
                _ => null,
            };
            if (output is null) return JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command.");
            var target = SubtitleCapabilities.OutputFormat(request.CapabilityId, path);
            var bytes = SubtitleWriter.Bytes(output, target);
            // Clean up on a file that already is exactly what it would write: say so, write nothing.
            if (request.CapabilityId == SubtitleCapabilities.CleanUpId && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                return JobResult.Fail(id, ErrorCode.NothingToDo, "The file already is clean UTF-8.");
            return Commit(request, path, bytes, target, output.Cues.Count);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (InvalidOperationException ex) { return JobResult.Fail(id, ErrorCode.InputTooLarge, ex.Message); }
        catch (UnauthorizedAccessException) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (IOException ex) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, $"Writing failed (0x{ex.HResult:X8})."); }
    }

    private static (DecodedText Text, long Size) Read(string path)
    {
        if (Directory.Exists(path)) throw new FileNotFoundException();
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) throw new InvalidOperationException("The file is online only; reading it would download it.");
        if (info.Length > SubtitleCapabilities.MaxInputBytes) throw new InvalidOperationException($"The file is larger than {SubtitleCapabilities.MaxInputBytes >> 20} MB; a subtitle file is far smaller.");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[Math.Min(fs.Length, SubtitleCapabilities.MaxInputBytes)];
        var n = fs.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        return (SubtitleEncoding.Decode(bytes.AsSpan(0, n)), n);
    }

    private static JobResult Info(string id, DecodedText decoded, long size, SubtitleDocument doc)
    {
        var facts = new List<JobFact>
        {
            new("Type", "Subtitles"),
            new("Encoding", decoded.EncodingName),
            new("Cues", doc.Cues.Count.ToString("N0", CultureInfo.InvariantCulture)),
            new("Size", Core.Feedback.FeedbackText.Size(size)),
        };
        if (doc.Cues.Count > 0)
            facts.Add(new("Runs", $"{Time(doc.Cues[0].Start)} to {Time(doc.Cues[^1].End)}"));
        var overlaps = SubtitleTiming.Overlaps(doc);
        if (overlaps > 0) facts.Add(new("Overlaps", $"{overlaps:N0} cues start before the previous one ends", Notice: true));
        if (doc.Problems.Count > 0) facts.Add(new("Problems", $"{doc.Problems.Count:N0}{(doc.Problems.Count >= SubtitleParser.MaxProblems ? "+" : "")}; first: {Clip(doc.Problems[0])}", Notice: true));
        return JobResult.WithFacts(id, facts);
    }

    private static string Clip(string s) => s.Length <= 150 ? s : s[..149] + "…";

    private static string Time(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}");

    private static JobResult Commit(JobRequest request, string source, byte[] bytes, SubtitleFormat format, int cues)
    {
        var dir = new DirectoryInfo(request.OutputDirectory ?? Path.GetDirectoryName(source)!);
        if (!dir.Exists) return JobResult.Fail(request.Id, request.OutputDirectory is null ? ErrorCode.SourceNotFound : ErrorCode.OutputDirectoryNotWritable, "The destination folder does not exist.");
        for (var d = dir; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) return JobResult.Fail(request.Id, ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");
        var temp = OutputNaming.TempPath(source, request.Id, request.OutputDirectory);
        var committed = false;
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.SetAttributes(temp, FileAttributes.Hidden);
            // Read back: the written file must parse to the same number of cues without a problem.
            var again = SubtitleParser.Parse(SubtitleEncoding.Decode(File.ReadAllBytes(temp)).Text, format);
            if (again.Problems.Count > 0 || again.Cues.Count != cues)
                return JobResult.Fail(request.Id, ErrorCode.OutputValidationFailed, "The new file did not check out; nothing was saved.");
            if (MarkOfTheWeb.ZoneToCarry(source) is { } z) MarkOfTheWeb.Apply(temp, z);
            File.SetAttributes(temp, FileAttributes.Normal);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            foreach (var candidate in SubtitleCapabilities.Candidates(request.CapabilityId, source, request.OutputDirectory))
            {
                try
                {
                    File.Move(temp, candidate, overwrite: false);
                    committed = true;
                    return JobResult.Ok(request.Id, new JobOutput(candidate, hash));
                }
                catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists) { }
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free output name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
