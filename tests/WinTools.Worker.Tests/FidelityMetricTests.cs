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
