using WinTools.Core.Jobs;

namespace WinTools.Core.Tests;

/// <summary>V2 PR 1.2: the job contract every module shares, and its line framing (ADR-0018).</summary>
public sealed class JobContractTests
{
    [Fact]
    public void A_request_crosses_the_pipe_intact_with_unicode_paths_and_parameters()
    {
        var request = new JobRequest("a1", "convert.to-png", [@"C:\صور\😀 photo.jpg", @"\\server\share\b.jpg"], @"D:\out",
            new Dictionary<string, string> { ["jpegQuality"] = "85" });
        var line = WorkerProtocol.Serialize(WorkerMessage.ForRequest(request));
        Assert.DoesNotContain('\n', line);
        Assert.True(line.All(c => c < 128), "the pipe carries ASCII only");

        var back = WorkerProtocol.Deserialize<WorkerMessage>(line)!;
        Assert.Equal(WorkerMessage.RequestType, back.Type);
        Assert.Equal(request.Inputs, back.Request!.Inputs);
        Assert.Equal((request.Id, request.CapabilityId, request.OutputDirectory), (back.Request.Id, back.Request.CapabilityId, back.Request.OutputDirectory));
        Assert.Equal("85", back.Request.Parameter("jpegQuality"));
        Assert.Null(back.Request.Parameter("webpQuality"));
    }

    [Fact]
    public void A_result_crosses_the_pipe_with_outputs_facts_and_a_stable_error_name()
    {
        var ok = JobResult.Ok("a1", new JobOutput(@"C:\p\a.png", new string('A', 64)));
        var facts = JobResult.WithFacts("a2", [new JobFact("GPS location", "Yes", Notice: true)]);
        var fail = JobResult.Fail("a3", ErrorCode.NothingToDo, "Already optimized");
        foreach (var r in new[] { ok, facts, fail })
        {
            var line = WorkerProtocol.Serialize(WorkerMessage.ForResult(r));
            var back = WorkerProtocol.Deserialize<WorkerMessage>(line)!.Result!;
            Assert.Equal((r.Id, r.Success, r.Error, r.Message), (back.Id, back.Success, back.Error, back.Message));
            Assert.Equal(r.Outputs, back.Outputs);
            Assert.Equal(r.Facts, back.Facts);
        }
        Assert.Contains("\"NothingToDo\"", WorkerProtocol.Serialize(WorkerMessage.ForResult(fail)), StringComparison.Ordinal);
        Assert.Equal(@"C:\p\a.png", ok.OutputPath);
        Assert.Null(facts.OutputPath);
    }

    [Fact]
    public void An_unknown_message_type_is_read_and_left_to_the_caller_to_ignore()
    {
        var back = WorkerProtocol.Deserialize<WorkerMessage>("{\"type\":\"progress\",\"jobId\":\"a1\",\"fraction\":0.5}");
        Assert.NotNull(back);
        Assert.Equal("progress", back.Type);
        Assert.Null(back.Request);
        Assert.Null(back.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"type\":\"request\",\"request\":{\"id\":5}}")]
    public void Malformed_lines_are_null_not_exceptions(string line)
    {
        var m = WorkerProtocol.Deserialize<WorkerMessage>(line);
        Assert.True(m is null || m.Request is null || m.Request.Inputs is null || m.Request.Id is null, line);
    }

    [Fact]
    public void An_oversized_line_is_refused()
    {
        var huge = "{\"type\":\"cancel\",\"jobId\":\"" + new string('x', WorkerProtocol.MaxLineLength) + "\"}";
        Assert.Null(WorkerProtocol.Deserialize<WorkerMessage>(huge));
    }
}
