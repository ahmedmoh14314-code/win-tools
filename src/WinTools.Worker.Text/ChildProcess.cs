using System.Diagnostics;
using System.Text;

namespace WinTools.Worker.Text;

/// <summary>What one run of an external program returned: its exit code, the start of what it printed, whether it was stopped.</summary>
internal sealed record ProgramRun(int ExitCode, string Output, bool TimedOut);

/// <summary>
/// Runs an external program as a child of the worker (ADR-0033, the pattern of ADR-0022's qpdf runner): an argument list,
/// never a command line we assemble and never a shell; a working directory we own; the environment given and nothing
/// else; no window; stdin closed at once; stdout and stderr read up to <see cref="MaxOutputChars"/>; a time limit, after
/// which the program is killed with every process it started.
/// </summary>
internal static class ChildProcess
{
    public const int MaxOutputChars = 64 * 1024;

    public static ProgramRun Run(string executable, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
    {
        foreach (var a in arguments)
            if (a.Length == 0 || a.Contains('\0', StringComparison.Ordinal) || a.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException("An argument the program could misread.", nameof(arguments));
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in environment) psi.Environment[k] = v;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("The program did not start.");
        try { process.StandardInput.Close(); } catch (IOException) { }
        var output = new StringBuilder();
        var stdout = Drain(process.StandardOutput, output);
        var stderr = Drain(process.StandardError, output);
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.WaitForExit(5000);
            return new ProgramRun(-1, "", TimedOut: true);
        }
        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5));
        lock (output) return new ProgramRun(process.ExitCode, output.ToString(), TimedOut: false);
    }

    private static Task Drain(StreamReader reader, StringBuilder into) => Task.Run(async () =>
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            lock (into) if (into.Length < MaxOutputChars) into.Append(buffer, 0, Math.Min(n, MaxOutputChars - into.Length));
    });
}
