using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>
/// Found by the release matrix on PR #12: on a GDI+ photograph Balanced reported "Already optimized" although q82 passed
/// every gate. Bisection tested q85 first, which failed on one axis (edge 0.9465 against 0.950) while q82 passed (0.9726):
/// pass and fail are not monotonic in quality, and one failure discarded every lower quality. "Already optimized" must be
/// true of the ladder, not of the path bisection took. Ladder indexes: 0 is the highest quality.
/// </summary>
public sealed class CompressionSearchTests
{
    private static int? Choose(int count, Func<int, bool> small, Func<int, bool> passes, List<int>? asked = null) =>
        LossyCompression.Choose(count, small, i => { asked?.Add(i); return passes(i); }, () => false, out _);

    [Fact]
    public void A_failure_just_above_a_pass_does_not_hide_the_pass()
    {
        // The measured sample: Balanced's ladder 95..65 (10 qualities); 95..88 too big; q85 fails; q82 and q80 pass.
        var chosen = Choose(10, i => i >= 4, i => i is 5 or 6);
        Assert.Equal(6, chosen);
    }

    [Fact]
    public void Nothing_is_called_already_optimized_until_every_small_enough_quality_has_failed()
    {
        var asked = new List<int>();
        Assert.Null(Choose(10, i => i >= 4, _ => false, asked));
        Assert.Equal([4, 5, 6, 7, 8, 9], asked.Order());
    }

    [Fact]
    public void A_lone_pass_far_below_is_still_found()
    {
        Assert.Equal(9, Choose(10, i => i >= 2, i => i == 9));
    }

    [Fact]
    public void A_monotonic_ladder_is_still_searched_by_bisection()
    {
        var asked = new List<int>();
        Assert.Equal(6, Choose(13, _ => true, i => i <= 6, asked));
        Assert.True(asked.Count <= 6, $"asked {asked.Count} times: {string.Join(",", asked)}");
    }
}
