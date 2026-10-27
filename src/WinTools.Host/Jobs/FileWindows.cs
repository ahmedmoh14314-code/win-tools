using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Host.Jobs;

/// <summary>What opens another program's window: the real shell and process start, or a recorder in tests.</summary>
internal interface IWindowOpener
{
    /// <summary>An Explorer window on <paramref name="folder"/> with <paramref name="items"/> selected; false when it failed.</summary>
    bool ShowInFolder(string folder, IReadOnlyList<string> items);

    /// <summary>Starts a program (never through a shell); false when it could not start.</summary>
    bool Start(ProcessStartInfo start);
}

/// <summary>The real opener: <see cref="WinTools.Windows.Shell.ShowInFolder"/> and <see cref="Process.Start(ProcessStartInfo)"/>.</summary>
internal sealed class WindowOpener : IWindowOpener
{
    public static WindowOpener Instance { get; } = new();

    /// <summary>How long the host waits for Explorer to take the request.</summary>
    private static readonly TimeSpan ShellPatience = TimeSpan.FromSeconds(10);

    public bool ShowInFolder(string folder, IReadOnlyList<string> items) => WinTools.Windows.Shell.ShowInFolder.Show(folder, items, ShellPatience);

    public bool Start(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }
}

/// <summary>
/// Show in folder and Open in Terminal (ADR-0032): run in the host at once, only as the user's own command, and leave
/// nothing behind. At most <see cref="FilesCapabilities.MaxWindows"/> windows, one per folder.
/// <list type="bullet">
/// <item>Show in folder: Explorer on each item's folder with the items selected (<c>SHOpenFolderAndSelectItems</c>, no
/// command line).</item>
/// <item>Open in Terminal: Windows Terminal (<c>wt.exe -d .</c>) when it is installed, otherwise Windows PowerShell, started
/// in the folder (a dropped folder, or a file's folder) as its working directory. The path is never part of an argument:
/// Windows Terminal reads ";" in its arguments as a command separator, and cmd.exe is never used.</item>
/// </list>
/// </summary>
internal static class FileWindows
{
    public static JobResult Run(JobRequest request, IWindowOpener opener)
    {
        var id = request.Id;
        if (request.Inputs.Count == 0 || request.Inputs.Any(p => !PathPolicy.IsSupported(p)) || request.Parameters is { Count: > 0 })
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "The items' paths are not supported.");
        if (request.Inputs.Any(p => TransferPlan.IsFolder(p) ? !Directory.Exists(TransferPlan.Plain(p)) : !File.Exists(p)))
            return JobResult.Fail(id, ErrorCode.SourceNotFound, "An item is no longer there.");
        return request.CapabilityId switch
        {
            FilesCapabilities.ShowId => Show(id, request.Inputs, opener),
            FilesCapabilities.TerminalId => Terminal(id, request.Inputs, opener),
            _ => JobResult.Fail(id, ErrorCode.UnsupportedOperation, "Not a window command."),
        };
    }

    private static JobResult Show(string id, IReadOnlyList<string> inputs, IWindowOpener opener)
    {
        var groups = inputs.Select(TransferPlan.Plain).GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase).ToArray();
        if (groups.Length > FilesCapabilities.MaxWindows) return JobResult.Fail(id, ErrorCode.LimitExceeded, "The items are in too many folders.");
        foreach (var group in groups)
            if (!opener.ShowInFolder(group.Key, [.. group])) return JobResult.Fail(id, ErrorCode.Internal, "Explorer did not show the items.");
        return new JobResult(id, true, ErrorCode.None, null, [], []);
    }

    private static JobResult Terminal(string id, IReadOnlyList<string> inputs, IWindowOpener opener)
    {
        var folders = inputs.Select(p => TransferPlan.IsFolder(p) ? TransferPlan.Plain(p) : Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (folders.Length > FilesCapabilities.MaxWindows) return JobResult.Fail(id, ErrorCode.LimitExceeded, "The items are in too many folders.");
        foreach (var folder in folders)
            if (!opener.Start(TerminalStart(folder, WindowsTerminal(), PowerShell()))) return JobResult.Fail(id, ErrorCode.Internal, "The terminal did not start.");
        return new JobResult(id, true, ErrorCode.None, null, [], []);
    }

    /// <summary>
    /// How a terminal starts in <paramref name="folder"/>: Windows Terminal's app alias with "-d ." (the documented way to
    /// open in the current folder), or Windows PowerShell; either with the folder as its working directory, its own
    /// console, and no shell between.
    /// </summary>
    public static ProcessStartInfo TerminalStart(string folder, string? windowsTerminal, string powerShell)
    {
        var start = new ProcessStartInfo(windowsTerminal ?? powerShell) { UseShellExecute = false, WorkingDirectory = folder };
        if (windowsTerminal is not null) { start.ArgumentList.Add("-d"); start.ArgumentList.Add("."); }
        else start.ArgumentList.Add("-NoLogo");
        return start;
    }

    /// <summary>Windows Terminal's app execution alias for this user, or null when it is not installed.</summary>
    public static string? WindowsTerminal()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(alias) ? alias : null;
    }

    /// <summary>Windows PowerShell, part of every supported Windows.</summary>
    public static string PowerShell() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
}
