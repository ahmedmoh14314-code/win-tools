using System.Runtime.InteropServices;
using System.Text;
using WinTools.Core.Jobs;
using WinTools.Worker;

// Conversion worker: started by the host for one batch, reads one JSON request per line on stdin and answers one JSON
// result per line on stdout, exits when stdin closes. Nothing else is accepted on the command line.

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
            var request = WorkerProtocol.Deserialize<ConversionRequest>(line);
            var result = request is null
                ? ConversionResult.Fail("invalid", ErrorCode.WorkerProtocolError, "Malformed request line.")
                : ConversionJob.Run(request);
            output.WriteLine(WorkerProtocol.Serialize(result));
        }
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
