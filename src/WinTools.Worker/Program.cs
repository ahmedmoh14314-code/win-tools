using System.Runtime.InteropServices;
using System.Text;
using WinTools.Core.Jobs;
using WinTools.Worker;

// Image worker: started by the host for one batch, reads one protocol message per line on stdin (ADR-0018) and answers
// each request with one result line on stdout, exits when stdin closes. Nothing else is accepted on the command line.
// A cancel message cannot be read while a job runs (one job at a time): the host's fallback, killing the worker, applies.

internal static class Program
{
    [MTAThread]
    private static int Main()
    {
        // A crash in a codec must not pop a Windows error dialog; the host reports it.
        _ = SetErrorMode(0x0001 | 0x0002 | 0x8000); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
        WinTools.Worker.Imaging.DecoderDiagnostics.Enable(); // before any codec loads
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 4096);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        while (input.ReadLine() is { } line)
        {
            var message = WorkerProtocol.Deserialize<WorkerMessage>(line);
            if (message is { Type: not WorkerMessage.RequestType }) continue; // a type this worker does not handle (ADR-0018)
            var result = message?.Request is { Id: not null, CapabilityId: not null, Inputs: not null } request
                ? ImageJobs.Run(request)
                : JobResult.Fail("invalid", ErrorCode.WorkerProtocolError, "Malformed request line.");
            output.WriteLine(WorkerProtocol.Serialize(WorkerMessage.ForResult(result)));
        }
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
