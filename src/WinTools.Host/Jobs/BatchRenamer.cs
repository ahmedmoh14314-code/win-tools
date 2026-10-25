using System.Globalization;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Host.Jobs;

/// <summary>
/// Applies Batch rename in the host (V2 PR 7.2): it renames, it parses no content, so it needs no worker. The plan is
/// made again from the files on disk with the rule from the dialog, exactly as the dialog previewed it.
/// <list type="bullet">
/// <item>Files only, all in one folder, each still there; a plan with an invalid or clashing name is refused.</item>
/// <item>A new name held by a file that is not in the batch refuses the whole rename: nothing is ever overwritten.</item>
/// <item>Two steps: every file to a temporary name in its folder, then each to its new name. So a file can take a name
/// another file of the batch frees, and a change of case alone works.</item>
/// <item>Any failure puts every name back, as far as the file system allows, and says so.</item>
/// </list>
/// </summary>
internal static class BatchRenamer
{
    public static JobResult Apply(JobRequest request)
    {
        var id = request.Id;
        if (request.Parameters is not { Count: 1 } p || !p.TryGetValue(DialogParameters.Name, out var text) || RenameRule.Parse(text) is not { } rule)
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "Batch rename needs its rule and nothing else.");
        var paths = request.Inputs;
        if (paths.Count == 0 || paths.Count > RenameRequest.MaxNames || paths.Any(x => !PathPolicy.IsSupported(x) || Path.EndsInDirectorySeparator(x)))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "Batch rename takes files only.");
        var folder = Path.GetDirectoryName(paths[0]);
        if (folder is null || paths.Any(x => !string.Equals(Path.GetDirectoryName(x), folder, StringComparison.OrdinalIgnoreCase)))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "Batch rename works on files of one folder.");
        if (paths.Any(x => !File.Exists(x))) return JobResult.Fail(id, ErrorCode.SourceNotFound, "A file is no longer there.");

        var plan = BatchRename.Plan([.. paths.Select(x => Path.GetFileName(x))], rule);
        if (plan.All(i => i.Status == RenameStatus.Unchanged)) return JobResult.Fail(id, ErrorCode.NothingToDo, "No name would change.");
        if (!BatchRename.CanApply(plan)) return JobResult.Fail(id, ErrorCode.OutputNameInvalid, plan.First(i => i.Problem is not null).Problem + ".");
        var moves = plan.Select((item, i) => (Item: item, From: paths[i])).Where(m => m.Item.Status == RenameStatus.Renamed)
            .Select(m => (m.From, To: Path.Combine(folder, m.Item.To))).ToArray();
        var freed = moves.Select(m => m.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, to) in moves)
            if ((File.Exists(to) || Directory.Exists(to)) && !freed.Contains(to))
                return JobResult.Fail(id, ErrorCode.NoFreeOutputName, $"A file named \"{Path.GetFileName(to)}\" is already in the folder; nothing was renamed.");

        // Step one: every file to its own temporary name; step two: each to its new name, never over an existing one.
        var temps = moves.Select((_, i) => Path.Combine(folder, $"~wintools-{id}-{i}.tmp")).ToArray();
        var atTemp = new bool[moves.Length];
        var atTarget = new bool[moves.Length];
        try
        {
            for (var i = 0; i < moves.Length; i++)
            {
                File.Move(moves[i].From, temps[i], overwrite: false);
                atTemp[i] = true;
            }
            for (var i = 0; i < moves.Length; i++)
            {
                File.Move(temps[i], moves[i].To, overwrite: false);
                (atTemp[i], atTarget[i]) = (false, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var restored = PutBack(moves, temps, atTemp, atTarget);
            var code = ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070020) ? ErrorCode.SourceNotReadable : ErrorCode.NoFreeOutputName;
            return JobResult.Fail(id, code, restored
                ? "A file could not be renamed (it may be open in another program); every name was put back."
                : "A file could not be renamed, and some names could not be put back: look for files named ~wintools-….tmp.");
        }
        var facts = new List<JobFact> { new("Renamed", moves.Length == 1 ? "1 file" : $"{moves.Length.ToString("N0", CultureInfo.InvariantCulture)} files", Notice: true) };
        return new JobResult(id, true, ErrorCode.None, null, [.. moves.Select(m => new JobOutput(m.To, ""))], facts);
    }

    /// <summary>Every moved file back to its old name: those at their new name first to their temporary one, then all home.</summary>
    private static bool PutBack((string From, string To)[] moves, string[] temps, bool[] atTemp, bool[] atTarget)
    {
        var ok = true;
        for (var i = 0; i < moves.Length; i++)
        {
            if (!atTarget[i]) continue;
            try { File.Move(moves[i].To, temps[i], overwrite: false); (atTarget[i], atTemp[i]) = (false, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
        }
        for (var i = 0; i < moves.Length; i++)
        {
            if (!atTemp[i]) continue;
            try { File.Move(temps[i], moves[i].From, overwrite: false); atTemp[i] = false; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
        }
        return ok;
    }

    /// <summary>A rename the host itself reports is believed only from the disk: every new name is there, and every old
    /// one is gone (or is the same file under another case).</summary>
    public static bool HoldsUp(JobRequest request, JobResult result)
    {
        if (result.Outputs is not { Count: > 0 } outputs || outputs.Count > request.Inputs.Count) return false;
        if (!outputs.All(o => File.Exists(o.Path))) return false;
        var now = outputs.Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return request.Inputs.Count(i => !File.Exists(i) || now.Contains(i)) >= outputs.Count;
    }
}
