using System.Diagnostics;
using System.Text;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>V2 PR 1.2 (ADR-0018): the line protocol at the real worker process, and the host's reading of it.</summary>
public sealed class WorkerProtocolV2Tests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    /// <summary>Writes the lines to a fresh worker, closes its input and returns every line it answered.</summary>
    private static async Task<string[]> Exchange(params string[] lines)
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        using var worker = Process.Start(psi)!;
        _ = worker.StandardError.ReadToEndAsync();
        foreach (var line in lines) await worker.StandardInput.WriteLineAsync(line);
        worker.StandardInput.Close();
        var output = await worker.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string Request(JobRequest r) => WorkerProtocol.Serialize(WorkerMessage.ForRequest(r));

    private static JobResult Result(string line) => WorkerProtocol.Deserialize<WorkerMessage>(line)!.Result!;

    [Fact]
    public async Task Messages_of_other_types_are_ignored_and_requests_still_answered_in_order()
    {
        var png = _dir.CopySample("quadrants-64x48.png", "a.png");
        var answers = await Exchange(
            "{\"type\":\"progress\",\"jobId\":\"x\",\"fraction\":0.5}",
            WorkerProtocol.Serialize(WorkerMessage.Cancel("nothing-running")),
            Request(new JobRequest("j1", "convert.to-jpeg", [png])),
            Request(new JobRequest("j2", "metadata.info", [png])));

        Assert.Equal(2, answers.Length);
        var (convert, info) = (Result(answers[0]), Result(answers[1]));
        Assert.True(convert.Success, convert.Message);
        Assert.Equal("j1", convert.Id);
        Assert.EndsWith("a.jpg", Assert.Single(convert.Outputs).Path, StringComparison.Ordinal);
        Assert.Equal(64, Assert.Single(convert.Outputs).Sha256.Length);
        Assert.True(info.Success && info.Outputs.Count == 0);
        Assert.Contains(info.Facts, f => f.Label == "Dimensions" && f.Value.StartsWith("64 × 48 px", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"request\"}")]
    [InlineData("{\"type\":\"request\",\"request\":{\"id\":\"r\",\"capabilityId\":\"convert.to-jpeg\"}}")]
    public async Task A_malformed_request_line_gets_a_protocol_error_not_a_crash(string line)
    {
        var answer = Result(Assert.Single(await Exchange(line)));
        Assert.Equal(ErrorCode.WorkerProtocolError, answer.Error);
    }

    [Fact]
    public async Task An_image_job_refuses_several_files_unknown_parameters_and_bad_qualities()
    {
        var png = _dir.CopySample("quadrants-64x48.png", "b.png");
        var answers = await Exchange(
            Request(new JobRequest("two", "convert.to-jpeg", [png, png])),
            Request(new JobRequest("none", "convert.to-jpeg", [])),
            Request(new JobRequest("odd", "convert.to-jpeg", [png], null, new Dictionary<string, string> { ["colour"] = "red" })),
            Request(new JobRequest("bad", "convert.to-jpeg", [png], null, new Dictionary<string, string> { ["jpegQuality"] = "101" })),
            Request(new JobRequest("ok", "convert.to-jpeg", [png], null, new Dictionary<string, string> { ["jpegQuality"] = "40" })));
        var results = answers.Select(Result).ToArray();
        Assert.Equal(["two", "none", "odd", "bad", "ok"], results.Select(r => r.Id));
        Assert.All(results[..4], r => Assert.Equal(ErrorCode.InvalidRequest, r.Error));
        Assert.True(results[4].Success, results[4].Message);
        Assert.Equal(["b.jpg", "b.png"], _dir.Names()); // the refused ones wrote nothing
    }

    [Fact]
    public async Task The_host_skips_lines_that_are_not_results()
    {
        var text = "{\"type\":\"progress\",\"jobId\":\"r\"}\n{\"type\":\"something-new\"}\n" + WorkerProtocol.Serialize(WorkerMessage.ForResult(JobResult.Fail("r", ErrorCode.NothingToDo, "x"))) + "\n";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var line = await WorkerClient.ReadResultLineAsync(reader, CancellationToken.None);
        Assert.Equal("r", WorkerProtocol.Deserialize<WorkerMessage>(line)!.Result!.Id);
    }
}
