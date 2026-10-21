using System.Runtime.InteropServices;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>
/// Damage the decoders would hide, content the product refuses rather than half-converts, and independent oracles
/// for the WebP engine (evaluator findings on PR #2).
/// </summary>
public sealed class StructureAndContentTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;
    private static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);

    public void Dispose() => _dir.Dispose();

    private static ConversionResult Run(ImageFormat target, string source) =>
        ConversionJob.Run(new ConversionRequest($"s{Interlocked.Increment(ref _seq)}", OperationCatalog.Convert(target).Id, source));

    [Fact]
    public void WebP_decoder_matches_an_independently_encoded_lossless_sample_pixel_for_pixel()
    {
        // quadrants-64x48.webp was written by Pillow (its own libwebp) from the GDI+ PNG; the PNG is decoded by WIC.
        var webp = Pixels.Load(SamplePath("quadrants-64x48.webp"));
        var png = Pixels.Load(SamplePath("quadrants-64x48.png"));
        Assert.Equal(png.Bgra, webp.Bgra);

        var alphaWebp = Pixels.Load(SamplePath("alpha-32x32.webp"));
        var alphaPng = Pixels.Load(SamplePath("alpha-32x32.png"));
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                if (alphaPng.At(x, y).A != 0) Assert.Equal(alphaPng.At(x, y), alphaWebp.At(x, y));
    }

    [Fact]
    public void WebP_encoder_lossless_output_round_trips_exactly()
    {
        var png = Pixels.Load(SamplePath("quadrants-64x48.png"));
        var encoded = SkiaCodec.EncodeWebP(new RasterImage(png.Width, png.Height, png.Bgra), 100, lossless: true);
        var back = ImagePipeline.Decode(encoded, ImageFormat.WebP).Image; // decoder checked against Pillow above
        Assert.Equal(png.Bgra, back.Bgra);
    }

    [Theory]
    [InlineData(1)]      // uncompressed
    [InlineData(32773)]  // PackBits
    [InlineData(8)]      // Deflate
    public void Tiff_whose_last_strip_is_short_fails_instead_of_filling_black(int compression)
    {
        var tiff = TiffWriter.Rgb(40, 30, rowsPerStrip: 8, compression, (x, y) => ((byte)(x * 6), (byte)(y * 8), 90));
        var ok = Run(ImageFormat.Png, _dir.Write("ok.tif", tiff));
        Assert.True(ok.Success, ok.Message);

        var src = _dir.Write("short.tif", TiffWriter.HalveLastStripByteCount(tiff));
        var r = Run(ImageFormat.Png, src);

        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Contains("strip", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tiff_cut_inside_its_last_strip_fails()
    {
        // Header first, then the strips: the typical shape of an interrupted download.
        var tiff = TiffWriter.Rgb(40, 30, rowsPerStrip: 8, compression: 1, (x, y) => (200, (byte)x, (byte)y));
        var src = _dir.Write("cut.tif", tiff[..(tiff.Length - 500)]);
        Assert.Equal(ErrorCode.DecodeFailed, Run(ImageFormat.Png, src).Error);
    }

    [Fact]
    public void Lzw_tiff_from_gdi_plus_with_a_short_strip_fails()
    {
        var sample = File.ReadAllBytes(SamplePath("quadrants-64x48.tif"));
        Assert.Equal(5, TiffWriter.CompressionOf(sample)); // the GDI+ sample is LZW
        var src = _dir.Write("short.tif", TiffWriter.HalveLastStripByteCount(sample));
        Assert.Equal(ErrorCode.DecodeFailed, Run(ImageFormat.Png, src).Error);
    }

    [Fact]
    public void Deflate_tiff_written_by_the_product_with_a_short_strip_fails()
    {
        var made = Run(ImageFormat.Tiff, _dir.CopySample("quadrants-64x48.png", "p.png"));
        Assert.True(made.Success, made.Message);
        Assert.Equal(8, TiffWriter.CompressionOf(File.ReadAllBytes(made.OutputPath!))); // Deflate, as configured
        var src = _dir.Write("short.tif", TiffWriter.HalveLastStripByteCount(File.ReadAllBytes(made.OutputPath!)));
        Assert.Equal(ErrorCode.DecodeFailed, Run(ImageFormat.Png, src).Error);
    }

    [Fact]
    public void Rle_bmp_is_refused()
    {
        // 4x2, 8-bit palette, RLE8: one run of 4 pixels per row, end of line, end of bitmap.
        byte[] rle = [4, 1, 0, 0, 4, 2, 0, 0, 0, 1];
        var src = _dir.Write("rle.bmp", BmpWriter.Rle8(4, 2, rle));
        var r = Run(ImageFormat.Png, src);
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Contains("RLE", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_page_tiff_is_refused_not_cut_to_its_first_page()
    {
        var src = _dir.Write("pages.tif", WicTestEncoder.MultiPageTiff(3));
        var r = Run(ImageFormat.Png, src);
        Assert.Equal(ErrorCode.UnsupportedContent, r.Error);
        Assert.Contains("3 pages", r.Message, StringComparison.Ordinal);
        Assert.Equal(["pages.tif"], _dir.Names());
    }

    [Fact]
    public void Animated_png_is_refused()
    {
        var png = PngWriter.Rgba(8, 8, (x, y) => (255, 0, 0, 255));
        byte[] actl = [0, 0, 0, 2, 0, 0, 0, 0]; // num_frames = 2, num_plays = 0
        var src = _dir.Write("anim.png", PngWriter.InsertChunkAfterIhdr(png, "acTL", actl));
        Assert.Equal(ErrorCode.UnsupportedContent, Run(ImageFormat.Jpeg, src).Error);
    }

    [Fact]
    public void Jpeg_quality_option_reaches_the_encoder()
    {
        var px = PngWriter.Rgba(256, 256, (x, y) => ((byte)(x ^ y), (byte)(x * 3), (byte)(y * 5), 255));
        var image = ImagePipeline.Decode(px, ImageFormat.Png).Image;
        var low = WicCodec.Encode(image, ImageFormat.Jpeg, 0.3f);
        var high = WicCodec.Encode(image, ImageFormat.Jpeg, 0.95f);
        Assert.True(low.Length * 2 < high.Length, $"q0.3 {low.Length} bytes vs q0.95 {high.Length} bytes");
    }

    [Fact]
    public void Photographic_9_megapixel_image_converts_to_tiff_quickly()
    {
        // Evaluator finding: an HGLOBAL output stream made TIFF encoding quadratic (9 MP of noise: 25 s).
        var rnd = new Random(3);
        var noise = new byte[3000 * 3000 * 4];
        rnd.NextBytes(noise);
        for (var i = 3; i < noise.Length; i += 4) noise[i] = 255;
        var image = new RasterImage(3000, 3000, noise);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var tiff = WicCodec.Encode(image, ImageFormat.Tiff);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
        ImagePipeline.Validate(tiff, ImageFormat.Tiff, 3000, 3000);
    }

    [Fact]
    public void The_host_never_references_the_imaging_libraries()
    {
        var host = typeof(WinTools.Host.Jobs.WorkerClient).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("SkiaSharp", host);
        Assert.DoesNotContain("WinTools.Worker", host);
    }
}

/// <summary>Minimal TIFF writer (little-endian, IFD before the data) and patcher for damage tests.</summary>
public static class TiffWriter
{
    public static byte[] Rgb(int width, int height, int rowsPerStrip, int compression, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var strips = new List<byte[]>();
        for (var y0 = 0; y0 < height; y0 += rowsPerStrip)
        {
            var raw = new MemoryStream();
            for (var y = y0; y < Math.Min(height, y0 + rowsPerStrip); y++)
                for (var x = 0; x < width; x++) { var (r, g, b) = pixel(x, y); raw.Write([r, g, b]); }
            strips.Add(compression switch
            {
                1 => raw.ToArray(),
                32773 => PackBitsLiteral(raw.ToArray()),
                8 => Zlib(raw.ToArray()),
                _ => throw new ArgumentOutOfRangeException(nameof(compression)),
            });
        }
        const int entries = 10;
        var ifdSize = 2 + entries * 12 + 4;
        var extra = 8 + ifdSize;                      // BitsPerSample values (3 SHORTs, padded to 8)
        var offsetsAt = extra + 8;
        var countsAt = offsetsAt + strips.Count * 4;
        var dataAt = countsAt + strips.Count * 4;
        var o = new MemoryStream();
        void U16(int v) => o.Write([(byte)v, (byte)(v >> 8)]);
        void U32(long v) => o.Write([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
        void Entry(int tag, int type, int count, long value) { U16(tag); U16(type); U32(count); U32(value); }
        o.Write("II"u8); U16(42); U32(8);
        U16(entries);
        Entry(256, 4, 1, width);
        Entry(257, 4, 1, height);
        Entry(258, 3, 3, extra);
        Entry(259, 3, 1, compression);
        Entry(262, 3, 1, 2);
        Entry(273, 4, strips.Count, strips.Count == 1 ? dataAt : offsetsAt);
        Entry(277, 3, 1, 3);
        Entry(278, 4, 1, rowsPerStrip);
        Entry(279, 4, strips.Count, strips.Count == 1 ? strips[0].Length : countsAt);
        Entry(284, 3, 1, 1);
        U32(0);
        U16(8); U16(8); U16(8); U16(0);
        long at = dataAt;
        foreach (var s in strips) { U32(at); at += s.Length; }
        foreach (var s in strips) U32(s.Length);
        foreach (var s in strips) o.Write(s);
        return o.ToArray();
    }

    public static int CompressionOf(byte[] tiff) => (int)Tag(tiff, 259)!.Value.Values[0];

    /// <summary>
    /// Appends one SHORT tag to IFD0 by rewriting the IFD at the end of the file (entries stay sorted by tag).
    /// With count &gt; 2 the tag points at an offset that does not exist, like a crafted file.
    /// </summary>
    public static byte[] WithExtraShortTag(byte[] tiff, int tag, int value, int count = 1)
    {
        int U16(int o) => tiff[o] | tiff[o + 1] << 8;
        var ifd = tiff[4] | tiff[5] << 8 | tiff[6] << 16 | tiff[7] << 24;
        var n = U16(ifd);
        var entries = Enumerable.Range(0, n).Select(i => tiff.AsSpan(ifd + 2 + i * 12, 12).ToArray()).ToList();
        var e = new byte[12];
        e[0] = (byte)tag; e[1] = (byte)(tag >> 8); e[2] = 3;
        e[4] = (byte)count; e[5] = (byte)(count >> 8); e[6] = (byte)(count >> 16); e[7] = (byte)(count >> 24);
        if (count <= 2) { e[8] = (byte)value; e[9] = (byte)(value >> 8); }
        else { e[8] = 0xF0; e[9] = 0xFF; e[10] = 0xFF; e[11] = 0x7F; }
        entries.Add(e);
        entries.Sort((a, b) => (a[0] | a[1] << 8).CompareTo(b[0] | b[1] << 8));
        var newIfd = tiff.Length + (tiff.Length & 1);
        var o = new MemoryStream();
        o.Write(tiff);
        if ((tiff.Length & 1) != 0) o.WriteByte(0);
        o.Write([(byte)entries.Count, (byte)(entries.Count >> 8)]);
        foreach (var x in entries) o.Write(x);
        o.Write([0, 0, 0, 0]);
        var result = o.ToArray();
        result[4] = (byte)newIfd; result[5] = (byte)(newIfd >> 8); result[6] = (byte)(newIfd >> 16); result[7] = (byte)(newIfd >> 24);
        return result;
    }

    /// <summary>Halves the byte count of the last strip (or tile) in IFD0, leaving everything else intact.</summary>
    public static byte[] HalveLastStripByteCount(byte[] tiff)
    {
        var copy = (byte[])tiff.Clone();
        var (values, at, size) = Tag(copy, 279) ?? Tag(copy, 325) ?? throw new InvalidOperationException("no byte counts");
        var last = values[^1] / 2;
        var p = at + (values.Length - 1) * size;
        var le = copy[0] == 0x49;
        for (var k = 0; k < size; k++) copy[p + k] = (byte)(le ? last >> (8 * k) : last >> (8 * (size - 1 - k)));
        return copy;
    }

    private static (long[] Values, int At, int Size)? Tag(byte[] d, int wanted)
    {
        var le = d[0] == 0x49;
        int U16(int o) => le ? d[o] | d[o + 1] << 8 : d[o] << 8 | d[o + 1];
        long U32(int o) => le ? (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24) : (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
        var ifd = (int)U32(4);
        for (var i = 0; i < U16(ifd); i++)
        {
            var e = ifd + 2 + i * 12;
            if (U16(e) != wanted) continue;
            var size = U16(e + 2) == 3 ? 2 : 4;
            var count = (int)U32(e + 4);
            var at = count * size <= 4 ? e + 8 : (int)U32(e + 8);
            var values = Enumerable.Range(0, count).Select(k => size == 2 ? U16(at + k * 2) : U32(at + k * 4)).ToArray();
            return (values, at, size);
        }
        return null;
    }

    private static byte[] PackBitsLiteral(byte[] raw)
    {
        var o = new MemoryStream();
        for (var i = 0; i < raw.Length; i += 128)
        {
            var n = Math.Min(128, raw.Length - i);
            o.WriteByte((byte)(n - 1));
            o.Write(raw, i, n);
        }
        return o.ToArray();
    }

    private static byte[] Zlib(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }
}

public static class BmpWriter
{
    /// <summary>An 8-bit palettised BMP whose pixel data is the given RLE8 stream.</summary>
    public static byte[] Rle8(int width, int height, byte[] rle)
    {
        const int palette = 256 * 4;
        var offset = 14 + 40 + palette;
        var o = new MemoryStream();
        void U16(int v) => o.Write([(byte)v, (byte)(v >> 8)]);
        void U32(int v) => o.Write([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
        o.Write("BM"u8); U32(offset + rle.Length); U32(0); U32(offset);
        U32(40); U32(width); U32(height); U16(1); U16(8); U32(1 /* BI_RLE8 */); U32(rle.Length); U32(2835); U32(2835); U32(256); U32(0);
        for (var i = 0; i < 256; i++) o.Write([(byte)i, (byte)i, (byte)i, 0]);
        o.Write(rle);
        return o.ToArray();
    }
}

/// <summary>Writes test inputs that need a real WIC encoder (multi-page TIFF).</summary>
public static class WicTestEncoder
{
    public static unsafe byte[] MultiPageTiff(int pages)
    {
        var stream = new GrowableStream();
        var encoder = WicNative.Create<IWICBitmapEncoder>(WicIds.TiffEncoder);
        encoder.Initialize(stream, WicEncoderCacheOption.NoCache);
        var pixels = new byte[8 * 8 * 3];
        for (var p = 0; p < pages; p++)
        {
            encoder.CreateNewFrame(out var frame, out var bag);
            frame.Initialize(bag);
            if (bag != 0) Marshal.Release(bag);
            frame.SetSize(8, 8);
            var format = WicIds.Format24bppBGR;
            frame.SetPixelFormat(ref format);
            fixed (byte* px = pixels) frame.WritePixels(8, 24, (uint)pixels.Length, px);
            frame.Commit();
            Marshal.FinalReleaseComObject(frame);
        }
        encoder.Commit();
        Marshal.FinalReleaseComObject(encoder);
        return stream.ToArray();
    }
}

public sealed class TiffCheckEdgeTests
{
    [Fact]
    public void Fill_order_2_tiff_is_not_falsely_rejected()
    {
        // Evaluator: a FillOrder=2 LZW file was refused as damaged. With FillOrder=2 only the bounds are checked.
        var tiff = TiffWriter.Rgb(16, 16, rowsPerStrip: 16, compression: 8, (x, y) => ((byte)x, (byte)y, 1));
        Assert.True(ContainerCheck.IsComplete(tiff, ImageFormat.Tiff, out var r), r);
        var reversedDeflate = TiffWriter.WithExtraShortTag(tiff, 266, 2);
        Assert.True(ContainerCheck.IsComplete(reversedDeflate, ImageFormat.Tiff, out r), r);
    }

    [Fact]
    public void Huge_unneeded_tags_are_not_read()
    {
        var tiff = TiffWriter.Rgb(16, 16, rowsPerStrip: 16, compression: 1, (x, y) => (1, 2, 3));
        var started = System.Diagnostics.Stopwatch.StartNew();
        // A private tag claiming 5 M values is skipped without being read (the check uses only its own tags).
        var bloated = TiffWriter.WithExtraShortTag(tiff, 40000, 7, count: 5_000_000);
        Assert.True(ContainerCheck.IsComplete(bloated, ImageFormat.Tiff, out var ok), ok);
        // A needed tag with an absurd count is refused early instead of being materialised.
        var hugeStrips = TiffWriter.WithExtraShortTag(tiff, 324 /* TileOffsets */, 7, count: 5_000_000);
        Assert.False(ContainerCheck.IsComplete(hugeStrips, ImageFormat.Tiff, out var reason));
        Assert.Contains("too many", reason, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
    }
}
