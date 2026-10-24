using WinTools.Core.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 1.2 (ADR-0018): an output made from a file Windows marked as downloaded must stay marked, or converting a file
/// from the internet would launder it past SmartScreen and Office's Protected View. Only the zone is carried: the
/// source's URLs are its own data, not the output's.
/// </summary>
public sealed class MarkOfTheWebTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static string Stream(string path) => path + ":Zone.Identifier";

    private string Marked(string name, int zone, string extra = "")
    {
        var path = _dir.CopySample("quadrants-64x48.png", name);
        File.WriteAllText(Stream(path), $"[ZoneTransfer]\r\nZoneId={zone}\r\n{extra}");
        return path;
    }

    private static ConversionResult Convert(string source) => ConversionJob.Run(new ConversionRequest(Guid.NewGuid().ToString("N"), "convert.to-jpeg", source));

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void An_output_of_a_downloaded_file_carries_the_zone_and_nothing_else(int zone)
    {
        var source = Marked($"downloaded{zone}.png", zone, "ReferrerUrl=https://example.com/page\r\nHostUrl=https://example.com/a.png\r\n");
        var r = Convert(source);
        Assert.True(r.Success, r.Message);
        Assert.Equal($"[ZoneTransfer]\r\nZoneId={zone}\r\n", File.ReadAllText(Stream(r.OutputPath!)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Local_intranet_and_trusted_sources_give_unmarked_outputs(int zone)
    {
        var r = Convert(Marked($"local{zone}.png", zone));
        Assert.True(r.Success, r.Message);
        Assert.False(File.Exists(Stream(r.OutputPath!)));
    }

    [Fact]
    public void An_unmarked_source_gives_an_unmarked_output()
    {
        var r = Convert(_dir.CopySample("quadrants-64x48.png", "plain.png"));
        Assert.True(r.Success, r.Message);
        Assert.False(File.Exists(Stream(r.OutputPath!)));
    }

    [Theory]
    [InlineData("ZoneId=abc\r\n")]
    [InlineData("ZoneId=\r\n")]
    [InlineData("garbage without a section")]
    public void An_unreadable_mark_is_treated_as_internet(string body)
    {
        // A mark that is present but unreadable came from somewhere; the safe reading is the internet zone.
        var source = _dir.CopySample("quadrants-64x48.png", $"odd{Math.Abs(body.GetHashCode())}.png");
        File.WriteAllText(Stream(source), body.StartsWith("Zone", StringComparison.Ordinal) ? "[ZoneTransfer]\r\n" + body : body);
        var r = Convert(source);
        Assert.True(r.Success, r.Message);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(Stream(r.OutputPath!)));
    }

    [Fact]
    public void The_source_and_its_mark_are_left_as_they_were()
    {
        var source = Marked("keep.png", 3, "HostUrl=https://example.com/x\r\n");
        var before = (File.ReadAllBytes(source), File.ReadAllText(Stream(source)));
        Convert(source);
        Assert.Equal(before.Item1, File.ReadAllBytes(source));
        Assert.Equal(before.Item2, File.ReadAllText(Stream(source)));
    }
}
