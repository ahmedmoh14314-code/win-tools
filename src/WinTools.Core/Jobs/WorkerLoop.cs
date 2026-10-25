namespace WinTools.Core.Jobs;

/// <summary>
/// The worker side of the line protocol (ADR-0018), the same for every module's worker: one message per line on
/// stdin, one result line per request on stdout, until stdin closes. A message of a type the worker does not handle is
/// skipped; a line that is not a well-formed request is answered with a protocol error.
/// </summary>
public static class WorkerLoop
{
    public static void Run(TextReader input, TextWriter output, Func<JobRequest, JobResult> handle)
    {
        while (input.ReadLine() is { } line)
        {
            var message = WorkerProtocol.Deserialize<WorkerMessage>(line);
            if (message is { Type: not WorkerMessage.RequestType }) continue; // a type this worker does not handle
            var result = message?.Request is { Id: not null, CapabilityId: not null, Inputs: not null } request
                ? handle(request)
                : JobResult.Fail("invalid", ErrorCode.WorkerProtocolError, "Malformed request line.");
            output.WriteLine(WorkerProtocol.Serialize(WorkerMessage.ForResult(result)));
        }
    }
}
