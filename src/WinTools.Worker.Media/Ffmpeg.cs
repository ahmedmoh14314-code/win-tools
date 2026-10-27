using System.Diagnostics;
using System.Text;

namespace WinTools.Worker.Media;

/// <summary>What one FFmpeg or ffprobe run returned: its exit code, what it printed (bounded), and whether it timed out.</summary>
internal sealed record EngineRun(int ExitCode, string Output, string Errors, bool TimedOut)
{
    public bool Ok => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs the bundled FFmpeg and ffprobe (ADR-0028, LGPL build) as children of the media worker, as qpdf runs for the PDF
/// worker (ADR-0022): arguments through <see cref="ProcessStartInfo.ArgumentList"/> only, never a shell or a command line
/// we assemble; a private environment (only what Windows needs); no window; a standard input closed at once; a time
/// limit, after which the child and its tree are killed; output read up to <see cref="MaxOutputChars"/>.
/// <para>
/// Every input and output path is given as <c>file:</c> plus the full path, and only the <c>file</c> protocol is allowed
/// (<c>-protocol_whitelist file</c>): a path is never read as a URL or another protocol, and nothing a media file names
/// (a playlist, a reference) can reach the network. Paths are validated by the worker before they get here.
/// </para>
/// </summary>
internal static class Ffmpeg
{
    public const int MaxOutputChars = 256 * 1024;

    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    public static string FfmpegPath { get; } = Path.Combine(Folder, "ffmpeg.exe");
    public static string FfprobePath { get; } = Path.Combine(Folder, "ffprobe.exe");

    /// <summary>The files the engine needs (scripts\Get-Engines.ps1).</summary>
    private static readonly string[] Files =
        ["ffmpeg.exe", "ffprobe.exe", "avcodec-62.dll", "avdevice-62.dll", "avfilter-11.dll", "avformat-62.dll", "avutil-60.dll", "swresample-6.dll", "swscale-9.dll"];

    public static bool IsPresent => Files.All(f => File.Exists(Path.Combine(Folder, f)));

    /// <summary>The only environment variables the engine gets.</summary>
    private static readonly string[] KeptVariables = ["SystemRoot", "windir", "TEMP", "TMP"];

    /// <summary>A path as the engine must read it: the file protocol, never anything else.</summary>
    public static string FileUrl(string fullPath) => "file:" + fullPath;

    /// <summary>
    /// A numbered output ("frame %04d.png") in a folder: the engine reads every "%" of the name as part of the pattern, so
    /// one in the folder's own path is doubled to stay literal.
    /// </summary>
    public static string PatternUrl(string folder, string pattern) => FileUrl(Path.Combine(folder.Replace("%", "%%", StringComparison.Ordinal), pattern));

    public static EngineRun Probe(IReadOnlyList<string> arguments, TimeSpan timeout) => Run(FfprobePath, arguments, timeout);

    public static EngineRun Convert(IReadOnlyList<string> arguments, TimeSpan timeout) => Run(FfmpegPath, arguments, timeout);

    private static EngineRun Run(string exe, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        foreach (var a in arguments)
            if (a.Length == 0 || a.Contains('\0', StringComparison.Ordinal))
                throw new ArgumentException("An argument the engine could misread.", nameof(arguments));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Folder,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        var keep = KeptVariables.Select(k => (k, Environment.GetEnvironmentVariable(k))).ToArray();
        psi.Environment.Clear();
        foreach (var (k, v) in keep) if (v is not null) psi.Environment[k] = v;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("The media engine did not start.");
        try { process.StandardInput.Close(); } catch (IOException) { }
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var stdout = Drain(process.StandardOutput, output);
        var stderr = Drain(process.StandardError, errors);
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit(5000);
            return new EngineRun(-1, "", "", TimedOut: true);
        }
        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5));
        string o, e;
        lock (output) o = output.ToString();
        lock (errors) e = errors.ToString();
        return new EngineRun(process.ExitCode, o, e, TimedOut: false);
    }

    private static Task Drain(StreamReader reader, StringBuilder into) => Task.Run(async () =>
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            lock (into) if (into.Length < MaxOutputChars) into.Append(buffer, 0, Math.Min(n, MaxOutputChars - into.Length));
    });
}
