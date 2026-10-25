using System.Runtime.InteropServices;
using System.Text;
using WinTools.Core.Jobs;
using WinTools.Worker.Text;

// Text worker (V2 PR 1.4): started by the host for one batch, answers each request line with one result line, exits when
// stdin closes. Nothing is accepted on the command line.

internal static class Program
{
    [MTAThread]
    private static int Main()
    {
        _ = SetErrorMode(0x0001 | 0x0002 | 0x8000); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 4096);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        // One worker for the text-only modules: General Files and Subtitles (V2 plan §6).
        WorkerLoop.Run(input, output, r => r.CapabilityId.StartsWith("subtitles.", StringComparison.Ordinal) ? SubtitleJobs.Run(r) : FileJobs.Run(r));
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
