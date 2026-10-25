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

        // Straight to the new name, except a file whose name another file of the batch takes (a chain, or a change of
        // case alone): that one goes to a temporary name first. Every file passing through a temporary name made OneDrive
        // see it leave and come back (review, PR 7.2). Never over an existing file.
        var targets = moves.Select(m => m.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var viaTemp = moves.Select(m => targets.Contains(m.From)).ToArray();
        var temps = moves.Select((m, i) => TempName(folder, i, Path.GetFileName(m.From))).ToArray();
        var at = new Place[moves.Length];
        try
        {
            for (var i = 0; i < moves.Length; i++)
            {
                if (!viaTemp[i]) continue;
                Move(moves[i].From, temps[i]);
                at[i] = Place.Temp;
            }
            for (var i = 0; i < moves.Length; i++)
            {
                Move(at[i] == Place.Temp ? temps[i] : moves[i].From, moves[i].To);
                at[i] = Place.Target;
            }
        }
        catch (Exception ex)
        {
            if (!PutBack(moves, temps, at))
                return JobResult.Fail(id, ErrorCode.Incomplete, "A file could not be renamed or put back; look for names starting with \"wintools-renaming\".");
            var code = ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070020) ? ErrorCode.SourceNotReadable
                : ex is IOException && (ex.HResult == unchecked((int)0x80070050) || ex.HResult == unchecked((int)0x800700B7)) ? ErrorCode.NoFreeOutputName
                : ErrorCode.SourceNotReadable;
            return JobResult.Fail(id, code, "A file could not be renamed; every name was put back.");
        }
        var facts = new List<JobFact> { new("Renamed", moves.Length == 1 ? "1 file" : $"{moves.Length.ToString("N0", CultureInfo.InvariantCulture)} files", Notice: true) };
        return new JobResult(id, true, ErrorCode.None, null, [.. moves.Select(m => new JobOutput(m.To, ""))], facts);
    }

    private enum Place { Source, Temp, Target }

    /// <summary>A temporary name that keeps the original one, extension included, so a file left there is recognisable.</summary>
    private static string TempName(string folder, int index, string original)
    {
        var name = $"wintools-renaming-{index} {original}";
        if (name.Length > BatchRename.MaxNameLength) name = $"wintools-renaming-{index}{Path.GetExtension(original)}";
        return Path.Combine(folder, name);
    }

    /// <summary>One move, never over an existing file, tried again briefly while another program (an indexer, an antivirus,
    /// a sync client) holds the file.</summary>
    private static void Move(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: false);
                return;
            }
            catch (IOException ex) when (attempt < 4 && ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                Thread.Sleep(100); // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: held for a moment
            }
        }
    }

    /// <summary>Every moved file back to its old name: those at their new name first to their temporary one (another file
    /// of the batch may stand on their old name), then all home. False when one could not be put back.</summary>
    private static bool PutBack((string From, string To)[] moves, string[] temps, Place[] at)
    {
        var ok = true;
        for (var i = 0; i < moves.Length; i++)
        {
            if (at[i] != Place.Target) continue;
            try { Move(moves[i].To, temps[i]); at[i] = Place.Temp; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
        }
        for (var i = 0; i < moves.Length; i++)
        {
            if (at[i] != Place.Temp) continue;
            try { Move(temps[i], moves[i].From); at[i] = Place.Source; }
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
