using WinTools.Core.Files;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>
/// The product's fidelity measure (ADR-0017) on controlled damage: each axis must see the loss it exists for, and none
/// may flag an identical image. The release gate itself is checked separately with an independent SSIM
/// (<see cref="CompressionQualityTests"/>).
/// </summary>
public sealed class FidelityMetricTests
{
    private static readonly RasterImage Photo = CompressionCorpus.Photo(seed: 21, width: 480, height: 360);

    [Fact]
    public void An_identical_image_is_perfect_on_every_axis()
    {
        var f = Fidelity.Compare(Photo, Photo);
        Assert.Equal((1.0, 1.0, 1.0, 1.0), (Math.Round(f.LumaSsim, 9), Math.Round(f.WorstLumaSsim, 3), Math.Round(f.ChromaSsim, 9), Math.Round(f.EdgeSsim, 9)));
        Assert.Equal(0, f.MeanDeltaE);
        Assert.True(f.DeltaE99 <= 0.05);
    }

    [Fact]
    public void Halving_colour_resolution_is_seen_by_chroma_not_by_brightness()
    {
        var full = Decode(WicCodec.Encode(Photo, ImageFormat.Jpeg, 0.97f, fullChroma: true));
        var halved = Decode(WicCodec.Encode(Photo, ImageFormat.Jpeg, 0.97f, fullChroma: false));
        var f444 = Fidelity.Compare(Photo, full);
        var f420 = Fidelity.Compare(Photo, halved);
        Assert.True(Math.Abs(f444.LumaSsim - f420.LumaSsim) < 0.002, $"luma {f444.LumaSsim:0.0000} vs {f420.LumaSsim:0.0000}");
        Assert.True(f420.ChromaSsim < f444.ChromaSsim - 0.005, $"chroma {f444.ChromaSsim:0.0000} vs {f420.ChromaSsim:0.0000}");
        Assert.True(f420.MeanDeltaE > f444.MeanDeltaE);
    }

    [Fact]
    public void Damage_confined_to_one_small_region_is_seen_by_the_worst_region_not_the_mean()
    {
        var damaged = (byte[])Photo.Bgra.Clone();
        for (var y = 100; y < 160; y++)
            for (var x = 200; x < 260; x++)
            {
                var i = (y * Photo.Width + x) * 4;
                var v = (byte)(((x / 4) + (y / 4)) % 2 == 0 ? 30 : 225); // a coarse checker where there was detail
                (damaged[i], damaged[i + 1], damaged[i + 2]) = (v, v, v);
            }
        var f = Fidelity.Compare(Photo, Photo.WithPixels(Photo.Width, Photo.Height, damaged));
        Assert.True(f.LumaSsim > 0.97, $"mean {f.LumaSsim:0.0000}: the region is about 2 % of the image");
        Assert.True(f.WorstLumaSsim < 0.5, $"worst {f.WorstLumaSsim:0.0000}");
    }

    // Evaluator, PR #12: percentiles over the whole image cannot see damage in less than 1 % of it, so the worst axes
    // are also pooled per 64×64 tile and the worst tile is reported.
    private static readonly RasterImage Large = CompressionCorpus.Photo(seed: 23);

    private static RasterImage Damaged(RasterImage image, int x0, int y0, int size, Action<byte[], int> damage)
    {
        var bgra = (byte[])image.Bgra.Clone();
        for (var y = y0; y < y0 + size; y++)
            for (var x = x0; x < x0 + size; x++) damage(bgra, (y * image.Width + x) * 4);
        return image.WithPixels(image.Width, image.Height, bgra);
    }

    [Fact]
    public void Damage_in_far_less_than_one_percent_of_a_large_image_is_seen_by_the_worst_tile()
    {
        // 40×40 px of 1600×1200: 0.08 % of the image
        var damaged = Damaged(Large, 803, 611, 40, (px, i) => { var v = (byte)((i / 4 % 2) == 0 ? 20 : 235); (px[i], px[i + 1], px[i + 2]) = (v, v, v); });
        var f = Fidelity.Compare(Large, damaged);
        Assert.True(f.LumaSsim > 0.99, $"mean {f.LumaSsim:0.0000}");
        Assert.True(f.WorstTileLumaSsim < 0.8, $"worst tile {f.WorstTileLumaSsim:0.0000} (1st percentile {f.WorstLumaSsim:0.0000})");
    }

    [Fact]
    public void A_colour_shift_in_a_small_region_is_seen_by_the_worst_tile_delta_e()
    {
        var shifted = Damaged(Large, 803, 611, 40, (px, i) => { px[i + 2] = (byte)Math.Min(255, px[i + 2] + 60); px[i] = (byte)Math.Max(0, px[i] - 40); });
        var f = Fidelity.Compare(Large, shifted);
        Assert.True(f.DeltaE99 < 2, $"ΔE99 {f.DeltaE99:0.00}: the region is below the 99th percentile");
        Assert.True(f.WorstTileDeltaE > 4, $"worst tile ΔE {f.WorstTileDeltaE:0.00}");
    }

    [Fact]
    public void An_identical_image_has_perfect_tiles()
    {
        var f = Fidelity.Compare(Large, Large);
        Assert.Equal(1.0, Math.Round(f.WorstTileLumaSsim, 9));
        Assert.Equal(0, f.WorstTileDeltaE);
    }

    [Theory]
    [InlineData(1600, 1200)]
    [InlineData(1603, 1203)]
    public void A_small_colour_swap_reads_the_same_at_the_edges_as_inside(int width, int height)
    {
        // Evaluator, PR #12: windows were given to the cell of their top-left corner, so the last cells along the right
        // and bottom edges held too few to count and a swap in the bottom-right corner read 0.996 instead of about 0.61.
        var photo = CompressionCorpus.Photo(seed: 25, width: width, height: height);
        double Swap(int x0, int y0)
        {
            var bgra = (byte[])photo.Bgra.Clone();
            for (var y = y0; y < y0 + 16; y++)
                for (var x = x0; x < x0 + 16; x++)
                {
                    var i = (y * width + x) * 4;
                    (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);   // red and blue swapped
                }
            return Fidelity.Compare(photo, photo.WithPixels(width, height, bgra)).WorstCellChromaSsim;
        }
        var inside = Swap(800, 600);
        foreach (var (x, y) in new[] { (width - 16, 600), (800, height - 16), (width - 16, height - 16), (0, 0) })
            Assert.True(Swap(x, y) < inside + 0.15, $"swap at {x},{y}: {Swap(x, y):0.000} against {inside:0.000} inside");
    }

    [Fact]
    public void A_long_thin_image_is_compared_in_windows_along_its_length()
    {
        // 6 px high: no 8×8 window fits; a single window over 4000 px would average a damaged stretch away.
        var thin = CompressionCorpus.Photo(seed: 24, width: 4000, height: 6);
        var damaged = (byte[])thin.Bgra.Clone();
        for (var y = 0; y < 6; y++)
            for (var x = 2000; x < 2040; x++)
            {
                var i = (y * 4000 + x) * 4;
                var v = (byte)(x % 2 == 0 ? 20 : 235);
                (damaged[i], damaged[i + 1], damaged[i + 2]) = (v, v, v);
            }
        var f = Fidelity.Compare(thin, thin.WithPixels(4000, 6, damaged));
        Assert.True(f.LumaSsim > 0.95, $"mean {f.LumaSsim:0.0000}");
        Assert.True(f.WorstLumaSsim < 0.8 && f.WorstTileLumaSsim < 0.8, $"worst {f.WorstLumaSsim:0.0000}, tile {f.WorstTileLumaSsim:0.0000}");
    }

    [Fact]
    public void A_colour_shift_is_measured_in_delta_e()
    {
        var shifted = (byte[])Photo.Bgra.Clone();
        for (var i = 0; i < shifted.Length; i += 4) shifted[i + 2] = (byte)Math.Min(255, shifted[i + 2] + 12); // red +12
        var f = Fidelity.Compare(Photo, Photo.WithPixels(Photo.Width, Photo.Height, shifted));
        Assert.InRange(f.MeanDeltaE, 2.0, 12.0);
        Assert.True(f.LumaSsim > 0.99, "a uniform shift keeps structure");
    }

    [Fact]
    public void Blurred_edges_are_seen_by_the_edge_axis()
    {
        var sharp = CompressionCorpus.Screenshot(seed: 22, w: 480, h: 360);
        var blurred = Resampler.Resize(Resampler.Resize(sharp, 240, 180), 480, 360);
        var f = Fidelity.Compare(sharp, blurred);
        Assert.True(f.EdgeSsim < 0.9, $"edge {f.EdgeSsim:0.0000}");
    }

    [Fact]
    public void Images_smaller_than_one_window_are_compared_without_failing()
    {
        var tiny = new RasterImage(5, 3, Enumerable.Repeat((byte)200, 5 * 3 * 4).ToArray());
        Assert.Equal(1.0, Math.Round(Fidelity.Compare(tiny, tiny).LumaSsim, 9));
    }

    [Fact]
    public void The_frame_header_says_whether_a_jpeg_keeps_full_colour_resolution()
    {
        Assert.True(Metadata.IsFullChromaJpeg(WicCodec.Encode(Photo, ImageFormat.Jpeg, 0.9f, fullChroma: true)));
        Assert.False(Metadata.IsFullChromaJpeg(WicCodec.Encode(Photo, ImageFormat.Jpeg, 0.9f, fullChroma: false)));
        Assert.False(Metadata.IsFullChromaJpeg([0xFF, 0xD8, 0xFF, 0xD9]));
        Assert.False(Metadata.IsFullChromaJpeg([0x89, 0x50, 0x4E, 0x47]));
    }

    private static RasterImage Decode(byte[] jpeg) => ImagePipeline.Decode(jpeg, ImageFormat.Jpeg).Image;
}
