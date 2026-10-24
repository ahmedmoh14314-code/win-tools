using WinTools.Core.Jobs;
using WinTools.Host.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>
/// The image module's check of a worker's answer, on results built by hand (evaluator, PR #16): a hostile or buggy
/// worker can send any shape, and only a well-formed one is believed. Facts reach the user, so they are bounded too.
/// </summary>
public sealed class ImageModuleBelievabilityTests
{
    private static readonly JobRequest Info = new("i1", "metadata.info", [@"C:\p\a.png"]);
    private static readonly JobRequest Convert = new("c1", "convert.to-jpeg", [@"C:\p\a.png"]);
    private static bool Believed(JobRequest request, JobResult result) => ImageJobModule.Instance.IsBelievable(request, result);
    private static JobResult Facts(params JobFact[] facts) => new("i1", true, ErrorCode.None, null, [], facts);

    [Fact]
    public void Plain_info_facts_are_believed() =>
        Assert.True(Believed(Info, Facts(new("Dimensions", "64 × 48 px"), new("Format", "PNG"))));

    [Fact]
    public void Too_many_facts_are_not_believed() =>
        Assert.False(Believed(Info, Facts([.. Enumerable.Range(0, JobFacts.MaxCount + 1).Select(i => new JobFact($"L{i}", "v"))])));

    [Theory]
    [InlineData("", "v")]                     // no label
    [InlineData("Label", "a\u0007b")]         // control characters
    [InlineData("La\u202Ebel", "v")]          // a bidirectional override that reorders what is shown
    public void Odd_facts_are_not_believed(string label, string value) =>
        Assert.False(Believed(Info, Facts(new JobFact(label, value))));

    [Fact]
    public void Overlong_facts_are_not_believed()
    {
        Assert.False(Believed(Info, Facts(new JobFact(new string('L', JobFacts.MaxLabelLength + 1), "v"))));
        Assert.False(Believed(Info, Facts(new JobFact("L", new string('v', JobFacts.MaxValueLength + 1)))));
    }

    [Fact]
    public void A_null_fact_or_null_lists_are_not_believed()
    {
        Assert.False(Believed(Info, Facts(null!)));
        Assert.False(Believed(Info, new JobResult("i1", true, ErrorCode.None, null, [], null!)));
        Assert.False(Believed(Info, new JobResult("i1", true, ErrorCode.None, null, null!, [new("A", "b")])));
    }

    [Fact]
    public void A_failure_that_carries_facts_or_outputs_is_not_believed()
    {
        Assert.False(Believed(Info, new JobResult("i1", false, ErrorCode.DecodeFailed, "x", [], [new("Safe", "Verified by WinTools")])));
        Assert.False(Believed(Convert, new JobResult("c1", false, ErrorCode.DecodeFailed, "x", [new(@"C:\p\a.jpg", new string('A', 64))], [])));
        Assert.True(Believed(Convert, JobResult.Fail("c1", ErrorCode.DecodeFailed, "x")));
    }

    [Fact]
    public void A_file_operation_with_two_outputs_or_with_facts_is_not_believed()
    {
        var sha = new string('A', 64);
        Assert.False(Believed(Convert, new JobResult("c1", true, ErrorCode.None, null, [new(@"C:\p\a.jpg", sha), new(@"C:\p\a (2).jpg", sha)], [])));
        Assert.False(Believed(Convert, new JobResult("c1", true, ErrorCode.None, null, [new(@"C:\p\a.jpg", sha)], [new("A", "b")])));
    }
}
