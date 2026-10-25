using System.Diagnostics;
using System.Text;

namespace WinTools.Worker.Pdf;

/// <summary>What one qpdf run returned: its exit code and the start of what it printed.</summary>
internal sealed record QpdfRun(int ExitCode, string Output, bool TimedOut)
{
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
/// private environment (only what Windows needs), no window, a closed standard input, a time limit, and is killed with its
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

        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(ExecutablePath)!,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        // Only what a Windows program needs to start; nothing of the user's environment reaches the engine.
        var keep = KeptVariables.Select(k => (k, Environment.GetEnvironmentVariable(k))).ToArray();
        psi.Environment.Clear();
        foreach (var (k, v) in keep) if (v is not null) psi.Environment[k] = v;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("qpdf did not start.");
        process.StandardInput.Close(); // nothing is ever read from the terminal (a password prompt would wait forever)
        var output = new StringBuilder();
        var stdout = Drain(process.StandardOutput, output);
        var stderr = Drain(process.StandardError, null);
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit(5000);
            return new QpdfRun(-1, "", TimedOut: true);
        }
        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5));
        lock (output) return new QpdfRun(process.ExitCode, output.ToString(), TimedOut: false);
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
