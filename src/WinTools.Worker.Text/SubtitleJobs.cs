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
        // Shift… takes its form's answer, read again here; every other command takes nothing.
        if (request.CapabilityId == SubtitleCapabilities.ShiftCustomId ? SubtitleCapabilities.ShiftOf(request.CapabilityId, Answer(request)) is null : request.Parameters is { Count: > 0 })
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "This command takes no parameters, or Shift… lacks its form's answer.");
        if (request.Inputs is not [var path] || !PathPolicy.IsSupported(path) || Path.EndsInDirectorySeparator(path))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "One subtitle file per request.");
        if (request.OutputDirectory is { } od && !PathPolicy.IsSupported(Path.Combine(od, "x.srt")))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        var known = request.CapabilityId is SubtitleCapabilities.InfoId or SubtitleCapabilities.ToVttId or SubtitleCapabilities.ToSrtId or SubtitleCapabilities.CleanUpId
            || SubtitleCapabilities.ShiftOf(request.CapabilityId, Answer(request)) is not null;
        if (!known) return JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command."); // before the file is read
        try
        {
            if (Read(path, out var raw, out var refusal) is not { } failure) { }
            else return JobResult.Fail(id, failure, refusal!);
            var format = SubtitleCapabilities.FormatOf(path);
            var decoded = SubtitleEncoding.TryDecode(raw, out var why);
            if (request.CapabilityId == SubtitleCapabilities.InfoId) return Info(id, decoded, why, raw.Length, decoded is null ? null : SubtitleParser.Parse(decoded.Text, format));
            if (decoded is null) return JobResult.Fail(id, ErrorCode.UnsupportedContent, why);
            var doc = SubtitleParser.Parse(decoded.Text, format);
            if (doc.Problems.Count > 0) return JobResult.Fail(id, ErrorCode.DecodeFailed, $"The file has errors, so it was not changed: {doc.Problems[0]}");
            if (doc.Cues.Count == 0) return JobResult.Fail(id, ErrorCode.NothingToDo, "The file has no cues.");
            var shift = SubtitleCapabilities.ShiftOf(request.CapabilityId, Answer(request));
            // A shift that would push a cue wholly before 0:00 would erase its timing: refused, never clamped away (review,
            // #38). A cue that only starts before 0:00 is cut to start there, and the toast says how many were.
            if (shift is { } early && doc.Cues.Any(c => c.End + early <= TimeSpan.Zero))
                return JobResult.Fail(id, ErrorCode.LimitExceeded,
                    $"{Math.Abs(early.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture)} s earlier would move subtitles before the start; the first one starts at {Time(doc.Cues.Min(c => c.Start))}.");
            var clamped = shift is { } by ? SubtitleTiming.ClampedBy(doc, by) : 0;
            var output = shift is { } move ? SubtitleTiming.Shift(doc, move) : doc;
            if (output.Cues.Any(c => c.End > SubtitleWriter.MaxTime))
                return JobResult.Fail(id, ErrorCode.LimitExceeded, "A cue would end after 999:59:59, which subtitle files cannot hold.");
            var target = SubtitleCapabilities.OutputFormat(request.CapabilityId, path);
            if (SubtitleWriter.CannotWrite(output, target) is { } cannot) return JobResult.Fail(id, ErrorCode.UnsupportedContent, cannot);
            var bytes = SubtitleWriter.Bytes(output, target);
            // Clean up on a file that already is exactly what it would write: say so, write nothing.
            if (request.CapabilityId == SubtitleCapabilities.CleanUpId && raw.AsSpan().SequenceEqual(bytes))
                return JobResult.Fail(id, ErrorCode.NothingToDo, "The file already is clean UTF-8.");
            // A legacy code page is a judgement, not a fact the file states: say which one was used (third look, PR 7.1).
            var readAs = decoded.EncodingName.StartsWith("Windows-", StringComparison.Ordinal) ? $"Read as {decoded.EncodingName}" : null;
            return Commit(request, path, bytes, target, output, SubtitleWriter.Dropped(output, target), readAs, clamped);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (UnauthorizedAccessException) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (IOException ex) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, $"Writing failed (0x{ex.HResult:X8})."); }
    }

    /// <summary>The form's answer a request carries, when it carries exactly that.</summary>
    private static string? Answer(JobRequest request) =>
        request.Parameters is { Count: 1 } p && p.TryGetValue(DialogParameters.Name, out var a) ? a : null;

    /// <summary>Reads the source once, bounded; the refusal's code and reason when it cannot or must not be read.</summary>
    private static ErrorCode? Read(string path, out byte[] bytes, out string? why)
    {
        bytes = [];
        why = null;
        if (Directory.Exists(path)) { why = "It is a folder."; return ErrorCode.IsAFolder; }
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) { why = "The file is online only; reading it would download it."; return ErrorCode.OnlineOnly; }
        if (info.Length > SubtitleCapabilities.MaxInputBytes) { why = $"The file is larger than {SubtitleCapabilities.MaxInputBytes >> 20} MB; a subtitle file is far smaller."; return ErrorCode.LimitExceeded; }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(fs.Length, SubtitleCapabilities.MaxInputBytes)];
        var n = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        bytes = n == buffer.Length ? buffer : buffer[..n];
        return null;
    }

    private static JobResult Info(string id, DecodedText? decoded, string why, long size, SubtitleDocument? doc)
    {
        var facts = new List<JobFact> { new("Type", "Subtitles") };
        if (decoded is null || doc is null)
        {
            facts.Add(new("Encoding", "Not recognised", Notice: true));
            facts.Add(new("Size", Core.Feedback.FeedbackText.Size(size)));
            facts.Add(new("Problems", Clip(why), Notice: true));
            return JobResult.WithFacts(id, facts);
        }
        facts.Add(new("Encoding", decoded.EncodingName));
        facts.Add(new("Cues", doc.Cues.Count.ToString("N0", CultureInfo.InvariantCulture)));
        facts.Add(new("Size", Core.Feedback.FeedbackText.Size(size)));
        if (doc.Cues.Count > 0)
            facts.Add(new("Runs", $"{Time(doc.Cues[0].Start)} to {Time(doc.Cues[^1].End)}"));
        var overlaps = SubtitleTiming.Overlaps(doc);
        if (overlaps > 0) facts.Add(new("Overlaps", $"{overlaps:N0} cues start before the previous one ends", Notice: true));
        if (doc.Problems.Count > 0) facts.Add(new("Problems", $"{doc.Problems.Count:N0}{(doc.Problems.Count >= SubtitleParser.MaxProblems ? "+" : "")}; first: {Clip(doc.Problems[0])}", Notice: true));
        return JobResult.WithFacts(id, facts);
    }

    private static string Clip(string s) => s.Length <= 150 ? s : s[..149] + "…";


    private static string Time(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}");

    private static JobResult Commit(JobRequest request, string source, byte[] bytes, SubtitleFormat format, SubtitleDocument written, IReadOnlyList<string> dropped, string? readAs, int clamped = 0)
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
            // Read back: the written file must parse, without a problem, to the same cues: every time, and every line once
            // turned into the target format.
            // The expected lines come from the document in memory, not from a parse of the same bytes (second review, PR 7.1).
            var again = SubtitleParser.Parse(SubtitleEncoding.Decode(File.ReadAllBytes(temp)).Text, format);
            var lines = written.Cues.Select(c => SubtitleWriter.LinesAs(c, written.Format, format)).ToArray();
            if (again.Problems.Count > 0 || again.Cues.Count != written.Cues.Count
                || !again.Cues.Select(c => (c.Start, c.End)).SequenceEqual(written.Cues.Select(c => (c.Start, c.End)))
                || !again.Cues.Select((c, k) => c.Lines.SequenceEqual(lines[k])).All(same => same))
                return JobResult.Fail(request.Id, ErrorCode.OutputValidationFailed, "The new file did not check out; nothing was saved.");
            if (MarkOfTheWeb.ZoneToCarry(source) is { } z) MarkOfTheWeb.Apply(temp, z);
            File.SetAttributes(temp, FileAttributes.Normal);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            foreach (var candidate in SubtitleCapabilities.Candidates(request.CapabilityId, source, request.OutputDirectory, Answer(request)))
            {
                try
                {
                    File.Move(temp, candidate, overwrite: false);
                    committed = true;
                    // What the target format cannot hold is said in the toast (evaluator, PR 7.1).
                    var facts = new List<JobFact>();
                    if (readAs is not null) facts.Add(new JobFact("Read as", readAs, Notice: true));
                    if (dropped.Count > 0) facts.Add(new JobFact("Dropped", "Not kept: " + string.Join(", ", dropped), Notice: true));
                    if (clamped > 0) facts.Add(new JobFact("Clamped", clamped == 1 ? "1 subtitle now starts at 0:00" : $"{clamped:N0} subtitles now start at 0:00", Notice: true));
                    return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, hash)], facts);
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
