using System.IO.Compression;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>What operations must not lose silently: bit depth, colour profile, PNG metadata, chroma (evaluator, PR #3).</summary>
public sealed class FidelityTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;
    private static readonly string SrgbProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color\sRGB Color Space Profile.icm");

    public void Dispose() => _dir.Dispose();

    private static ConversionResult Run(string opId, string source) =>
        ConversionJob.Run(new ConversionRequest($"f{Interlocked.Increment(ref _seq)}", opId, source));

    private static byte[] Png16(int w, int h)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < w; x++) { var v = (ushort)(x * 1000 + y * 7); raw.Write([(byte)(v >> 8), (byte)v, 0x12, 0x34, (byte)(v >> 4), 0]); }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zs);
        return PngWriter.Custom(w, h, bitDepth: 16, colorType: 2, z.ToArray());
    }

    [Theory]
    [InlineData("compress.lossless")]
    [InlineData("rotate.right")]
    [InlineData("resize.percent-50")]
    public void Sixteen_bit_png_is_refused_by_operations_that_keep_the_format(string op)
    {
        var src = _dir.Write("deep.png", Png16(32, 16));
        var r = Run(op, src);
        Assert.Equal(ErrorCode.UnsupportedContent, r.Error);
        Assert.Contains("16-bit", r.Message, StringComparison.Ordinal);
        Assert.Equal(["deep.png"], _dir.Names());
    }

    [Fact]
    public void Sixteen_bit_png_can_still_be_converted()
    {
        var r = Run("convert.to-jpeg", _dir.Write("deep.png", Png16(32, 16)));
        Assert.True(r.Success, r.Message);
    }

    // A photograph-like image large enough that Compress has something to save next to the 3 KB profile (ADR-0017
    // refuses copies that save under 8 %; a 64×48 image is mostly profile and was refused, which hid nothing about ICC).
    private string JpegWithIcc(string name)
    {
        var icc = File.ReadAllBytes(SrgbProfile);
        var decoded = CompressionCorpus.Photo(seed: 11, width: 480, height: 360);
        var tagged = new RasterImage(decoded.Width, decoded.Height, decoded.Bgra, 300, 300, icc);
        return _dir.Write(name, WicCodec.Encode(tagged, ImageFormat.Jpeg, 0.95f));
    }

    [Theory]
    [InlineData("convert.to-png")]
    [InlineData("convert.to-webp")]
    [InlineData("convert.to-tiff")]
    [InlineData("rotate.180")]
    [InlineData("resize.percent-50")]
    [InlineData("compress.small")]
    public void Icc_profile_survives_every_operation(string op)
    {
        Assert.True(File.Exists(SrgbProfile), "Windows ships this profile");
        var src = JpegWithIcc("tagged.jpg");
        var sourceIcc = ImagePipeline.Decode(File.ReadAllBytes(src), ImageFormat.Jpeg).Image.IccProfile;
        Assert.NotNull(sourceIcc);

        var r = Run(op, src);

        Assert.True(r.Success, r.Message);
        var bytes = File.ReadAllBytes(r.OutputPath!);
        var outIcc = ImagePipeline.Decode(bytes, FileSignature.Detect(bytes)).Image.IccProfile;
        Assert.NotNull(outIcc);
        Assert.Equal(sourceIcc, outIcc);
    }

    private string JpegTaggedWith(string name, byte[] profile)
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.png"));
        var decoded = ImagePipeline.Decode(png, ImageFormat.Png).Image;
        // RasterImage drops non-RGB profiles, so tag the encoder output directly to build a hostile source.
        return _dir.Write(name, WicTestEncoder.JpegWithProfile(decoded, profile));
    }

    private static byte[] MinimalProfile(string colourSpace)
    {
        var p = new byte[132];
        p[3] = 132;                                              // declared size
        System.Text.Encoding.ASCII.GetBytes(colourSpace).CopyTo(p, 16);
        "acsp"u8.CopyTo(p.AsSpan(36));
        return p;
    }

    [Theory]
    [InlineData("cmyk")]
    [InlineData("gray")]
    [InlineData("junk")]
    public void Profiles_that_do_not_describe_rgb_pixels_are_dropped_not_carried(string kind)
    {
        var profile = kind switch
        {
            "cmyk" => File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color\RSWOP.icm")),
            "gray" => MinimalProfile("GRAY"),
            _ => "this is not an ICC profile, just 250 bytes of text pretending to be one....................................................................................................................................................."u8.ToArray(),
        };
        var src = JpegTaggedWith("tagged.jpg", profile);
        foreach (var op in new[] { "convert.to-png", "convert.to-webp", "rotate.right" })
        {
            var r = Run(op, src);
            Assert.True(r.Success, r.Message);
            var bytes = File.ReadAllBytes(r.OutputPath!);
            Assert.Null(ImagePipeline.Decode(bytes, FileSignature.Detect(bytes)).Image.IccProfile);
            File.Delete(r.OutputPath!);
        }
    }

    [Fact]
    public void Lossless_png_drops_a_gray_iccp_when_the_output_is_rgb()
    {
        var gray = MinimalProfile("GRAY");
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(gray);
        byte[] iccp = [.. "gray\0"u8, 0, .. z.ToArray()];
        var rows = new MemoryStream();
        for (var y = 0; y < 64; y++) { rows.WriteByte(0); for (var x = 0; x < 64; x++) rows.WriteByte((byte)(x * 4)); }
        var idat = new MemoryStream();
        using (var zs = new ZLibStream(idat, CompressionLevel.NoCompression, leaveOpen: true)) rows.WriteTo(zs);
        var src = _dir.Write("gray.png", PngWriter.InsertChunkAfterIhdr(PngWriter.Custom(64, 64, 8, 0, idat.ToArray()), "iCCP", iccp));

        var r = Run("compress.lossless", src);

        Assert.True(r.Success, r.Message);
        Assert.DoesNotContain("iCCP", PngWriter.ChunkTypes(File.ReadAllBytes(r.OutputPath!)));
    }

    [Fact]
    public void Lossless_png_keeps_colour_and_text_chunks_exactly_once()
    {
        var png = PngWriter.Rgba(200, 100, (x, y) => ((byte)(x / 20 * 20), (byte)(y / 20 * 20), 99, 255), CompressionLevel.NoCompression);
        byte[] gama = [0, 1, 0x86, 0xA0];                         // gamma 1.0 (100000)
        byte[] phys = [0, 0, 0x2E, 0x23, 0, 0, 0x2E, 0x23, 1];  // 11811 px/m = 300 dpi
        var withMeta = PngWriter.InsertChunkAfterIhdr(PngWriter.InsertChunkAfterIhdr(PngWriter.InsertChunkAfterIhdr(png,
            "tEXt", "Comment\0hello"u8.ToArray()), "pHYs", phys), "gAMA", gama);
        var src = _dir.Write("meta.png", withMeta);

        var r = Run("compress.lossless", src);

        Assert.True(r.Success, r.Message);
        var outChunks = PngWriter.ChunkTypes(File.ReadAllBytes(r.OutputPath!));
        foreach (var t in new[] { "gAMA", "pHYs", "tEXt" }) Assert.Single(outChunks, t);
        Assert.Equal(Pixels.Load(src).Bgra, Pixels.Load(r.OutputPath!).Bgra);
    }

    [Fact]
    public void Rotated_jpeg_keeps_full_chroma_resolution()
    {
        var r = Run("rotate.right", JpegWithIcc("photo.jpg"));
        Assert.True(r.Success, r.Message);
        Assert.Equal(0x11, JpegLumaSampling(File.ReadAllBytes(r.OutputPath!))); // Y sampled 1x1: 4:4:4
    }

    private static int JpegLumaSampling(byte[] jpeg)
    {
        for (var i = 2; i + 4 < jpeg.Length;)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            var marker = jpeg[i + 1];
            var length = jpeg[i + 2] << 8 | jpeg[i + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2) return jpeg[i + 4 + 6 + 1]; // SOF: P, Y, X, Nf, then C1 id, C1 H|V
            i += 2 + length;
        }
        return -1;
    }

    [Fact]
    public void Same_format_operations_cannot_claim_a_different_format()
    {
        var src = _dir.CopySample("quadrants-64x48.jpg", "x.jpg");
        var png = _dir.CopySample("quadrants-64x48.png", "x (rotated right).png");
        Assert.False(WorkerClient.IsBelievable(new ConversionRequest("r1", "rotate.right", src), ConversionResult.Ok("r1", png, Hash.Of(png))));
        var jpg = _dir.CopySample("quadrants-64x48.jpg", "x (rotated right).jpg");
        Assert.True(WorkerClient.IsBelievable(new ConversionRequest("r1", "rotate.right", src), ConversionResult.Ok("r1", jpg, Hash.Of(jpg))));
    }

    [Fact]
    public async Task Missing_operation_id_is_a_structured_error_not_a_crash()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "p.png");
        Assert.Equal(ErrorCode.UnsupportedOperation, ConversionJob.Run(new ConversionRequest("n1", null!, src)).Error);
        var client = new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe"), TimeSpan.FromSeconds(20));
        var results = await client.RunAsync([new("n2", "", src), new("n3", "convert.to-jpeg", src)], CancellationToken.None);
        Assert.Equal(ErrorCode.UnsupportedOperation, results[0].Error);
        Assert.True(results[1].Success, results[1].Message); // the worker survived
    }
}
