using System.Diagnostics;
using System.Text;

namespace WinTools.Worker.Pdf;

/// <summary>What one qpdf run returned: its exit code and the start of what it printed.</summary>
internal sealed record QpdfRun(int ExitCode, string Output, bool TimedOut, string StdErr = "")
{
    /// <summary>qpdf could not open a file at all ("qpdf: open name: Permission denied"): a lock, not a damaged PDF.</summary>
    public bool CouldNotOpen => ExitCode == Errors && StdErr.Contains(": open ", StringComparison.Ordinal);


    /// <summary>qpdf's exit codes (manual, "Exit Status"): 0 success, 2 errors, 3 warnings but a usable result.</summary>
    public const int Success = 0, Errors = 2, Warnings = 3;

    public bool Usable => !TimedOut && ExitCode is Success or Warnings;

    /// <summary>Any other code: qpdf itself crashed or could not load (a missing DLL), which says nothing about the PDF.</summary>
    public bool EngineFailed => !TimedOut && ExitCode is not (Success or Errors or Warnings);
}

/// <summary>
/// Runs the bundled qpdf (ADR-0022) as a child of the PDF worker. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>
/// only, never through a shell or a command line we assemble. The PDF jobs pass only fixed ASCII names relative to their
/// private work folder, which is the child's working directory, so no user text reaches qpdf at all. The child gets a
/// private environment (only what Windows needs), no window, a standard input closed at once (or once it has carried the
/// arguments, with <see cref="RunWithArgumentsOnStdin"/>: the only way a password reaches it), a time limit, and is killed with its
/// tree when the limit passes. Its output is read up to <see cref="MaxOutputChars"/> and the rest discarded.
/// </summary>
internal static class Qpdf
{
    public const int MaxOutputChars = 64 * 1024;

    /// <summary>Where the build puts qpdf: a folder of its own next to the worker (its DLLs are found there first).</summary>
    public static string ExecutablePath { get; } = Path.Combine(AppContext.BaseDirectory, "qpdf", "qpdf.exe");

    /// <summary>The files qpdf needs: itself, its library and the Visual C++ runtime it imports (Get-Engines.ps1).</summary>
    private static readonly string[] Files = ["qpdf.exe", "qpdf30.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"];

    public static bool IsPresent => Files.All(f => File.Exists(Path.Combine(Path.GetDirectoryName(ExecutablePath)!, f)));

    /// <summary>The only environment variables qpdf gets.</summary>
    private static readonly string[] KeptVariables = ["SystemRoot", "windir", "TEMP", "TMP"];

    public static QpdfRun Run(IReadOnlyList<string> arguments, TimeSpan timeout, string? workingDirectory = null)
    {
        foreach (var a in arguments)
            if (a.Length == 0 || a[0] == '@' || a.Contains('\0', StringComparison.Ordinal))
                throw new ArgumentException("An argument qpdf could misread.", nameof(arguments));
        return Start(arguments, null, timeout, workingDirectory);
    }

    /// <summary>
    /// The only command line <see cref="RunWithArgumentsOnStdin"/> gives qpdf: "@-", so every argument, a password
    /// included, comes on its stdin and none is visible to another process (V2 PR 2.2, ADR-0026; ADR-0022 amended).
    /// </summary>
    internal static IReadOnlyList<string> CommandLineFor(IReadOnlyList<string> arguments) => ["@-"];

    /// <summary>
    /// Runs qpdf with its arguments on stdin, one per line (qpdf: "@- may be used to read arguments from stdin"; leading
    /// and trailing spaces are kept). An argument with a line break or a NUL, or one starting with "@", is refused: it
    /// would become another argument or another argument file.
    /// </summary>
    public static QpdfRun RunWithArgumentsOnStdin(IReadOnlyList<string> arguments, TimeSpan timeout, string? workingDirectory = null)
    {
        foreach (var a in arguments)
            if (a.Length == 0 || a[0] == '@' || a.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                throw new ArgumentException("An argument qpdf could misread.", nameof(arguments));
        return Start(CommandLineFor(arguments), arguments, timeout, workingDirectory);
    }

    private static QpdfRun Start(IReadOnlyList<string> commandLine, IReadOnlyList<string>? stdinArguments, TimeSpan timeout, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(ExecutablePath)!,
        };
        foreach (var a in commandLine) psi.ArgumentList.Add(a);
        // Only what a Windows program needs to start; nothing of the user's environment reaches the engine.
        var keep = KeptVariables.Select(k => (k, Environment.GetEnvironmentVariable(k))).ToArray();
        psi.Environment.Clear();
        foreach (var (k, v) in keep) if (v is not null) psi.Environment[k] = v;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("qpdf did not start.");
        // The arguments, when they come this way, then nothing: a closed stdin never leaves a password prompt waiting.
        if (stdinArguments is not null)
        {
            try
            {
                foreach (var a in stdinArguments) process.StandardInput.Write(a + "\n");
                process.StandardInput.Flush();
            }
            catch (IOException) { } // qpdf exited early; its exit code says why
        }
        try { process.StandardInput.Close(); } catch (IOException) { }
        var output = new StringBuilder();
        var stdout = Drain(process.StandardOutput, output);
        var errors = new StringBuilder();
        var stderr = Drain(process.StandardError, errors);
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit(5000);
            return new QpdfRun(-1, "", TimedOut: true);
        }
        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5));
        string err;
        lock (errors) err = errors.ToString();
        lock (output) return new QpdfRun(process.ExitCode, output.ToString(), TimedOut: false, err);
    }

    private static Task Drain(StreamReader reader, StringBuilder? into) => Task.Run(async () =>
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (into is not null)
                lock (into) if (into.Length < MaxOutputChars) into.Append(buffer, 0, Math.Min(n, MaxOutputChars - into.Length));
    });
}
