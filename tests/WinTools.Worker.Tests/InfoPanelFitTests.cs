using WinTools.Core.Feedback;
using WinTools.Windows.Feedback;
using WinTools.Windows.Rendering;

namespace WinTools.Worker.Tests;

/// <summary>
/// The info panel must show General Files' facts whole (evaluator, PR #18): a 64-digit SHA-256 and a mismatch notice
/// were cut to an ellipsis in a single-line value column. Long values wrap; nothing is lost up to the wrap limit.
/// </summary>
public sealed class InfoPanelFitTests
{
    public static TheoryData<uint> Dpis => [96, 120, 144, 192];

    [Theory]
    [MemberData(nameof(Dpis))]
    public void A_sha256_and_a_mismatch_notice_are_shown_whole(uint dpi)
    {
        var hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        foreach (var (value, notice) in new[] { (hash, false), ("Windows program, but named .pdf", true), ("The file changed while it was read; hash it again", true) })
        {
            var lines = InfoPanelRenderer.ValueLines(value, notice, dpi);
            Assert.Equal(value.Replace(" ", ""), string.Concat(lines).Replace(" ", "")); // every character is on some line
            Assert.All(lines, l => Assert.True(Fonts.Width(l, InfoPanelRenderer.TextPx(dpi), notice ? 600 : 400) <= InfoPanelRenderer.ValueWidth(notice, dpi),
                $"\"{l}\" is wider than the value column at {dpi} DPI"));
            Assert.InRange(lines.Count, 1, InfoPanelRenderer.MaxValueLines);
        }
    }

    [Fact]
    public void A_short_value_stays_on_one_line_and_the_panel_grows_with_wrapped_rows()
    {
        Assert.Single(InfoPanelRenderer.ValueLines("PNG image", false, 96));
        var one = new[] { new InfoSection("a.txt", [new InfoRow("Type", "Text")]) };
        var wrapped = new[] { new InfoSection("a.txt", [new InfoRow("SHA-256", new string('a', 64))]) };
        Assert.True(InfoPanelRenderer.WindowSize(wrapped, 0, 96).H > InfoPanelRenderer.WindowSize(one, 0, 96).H);
    }
}
