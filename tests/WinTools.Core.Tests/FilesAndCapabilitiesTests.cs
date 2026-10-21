using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Core.Tests;

public sealed class CapabilityResolverTests
{
    private static readonly CapabilityResolver R = CapabilityResolver.Default;
    private static string[] Ids(IEnumerable<Operation> ops) => ops.Where(o => o.Kind == OperationKind.Convert).Select(o => o.Id).ToArray();

    [Theory]
    [InlineData(@"C:\p\photo.png", "convert.to-jpeg convert.to-webp convert.to-bmp convert.to-tiff")]
    [InlineData(@"C:\p\photo.JPG", "convert.to-png convert.to-webp convert.to-bmp convert.to-tiff")]
    [InlineData(@"C:\p\photo.jfif", "convert.to-png convert.to-webp convert.to-bmp convert.to-tiff")]
    [InlineData(@"C:\p\photo.webp", "convert.to-jpeg convert.to-png convert.to-bmp convert.to-tiff")]
    [InlineData(@"C:\p\photo.bmp", "convert.to-jpeg convert.to-png convert.to-webp convert.to-tiff")]
    [InlineData(@"C:\p\photo.tiff", "convert.to-jpeg convert.to-png convert.to-webp convert.to-bmp")]
    public void Each_format_gets_every_other_format_as_a_target_and_never_itself(string path, string expected)
    {
        Assert.Equal(expected.Split(' '), Ids(R.Resolve([path])));
    }

    [Theory]
    [InlineData(@"C:\p\notes.txt")]
    [InlineData(@"C:\p\image.heic")]
    [InlineData(@"C:\p\drawing.svg")]
    [InlineData(@"C:\p\no-extension")]
    [InlineData(@"C:\p\archive.png.zip")]
    public void Unsupported_files_get_no_commands(string path)
    {
        Assert.Empty(R.Resolve([path]));
    }

    [Fact]
    public void Mixed_selections_get_the_intersection()
    {
        // PNG and JPEG: "To PNG" is invalid for the PNG and "To JPEG" for the JPEG, so only the shared targets remain.
        Assert.Equal(["convert.to-webp", "convert.to-bmp", "convert.to-tiff"], Ids(R.Resolve([@"C:\a.png", @"C:\b.jpg"])));
        Assert.Equal(["convert.to-bmp", "convert.to-tiff"], Ids(R.Resolve([@"C:\a.png", @"C:\b.jpg", @"C:\c.webp"])));
    }

    [Fact]
    public void One_unsupported_file_removes_every_command()
    {
        Assert.Empty(R.Resolve([@"C:\a.png", @"C:\b.txt"]));
    }

    [Fact]
    public void Empty_and_oversized_selections_get_no_commands()
    {
        Assert.Empty(R.Resolve([]));
        Assert.Empty(R.Resolve(Enumerable.Range(0, CapabilityResolver.MaxFiles + 1).Select(i => $@"C:\{i}.png").ToArray()));
    }

    [Fact]
    public void The_catalog_drives_the_answer()
    {
        var onlyPng = new CapabilityResolver([OperationCatalog.Convert(ImageFormat.Png)]);
        Assert.Empty(onlyPng.Resolve([@"C:\a.png"]));
        Assert.Equal([OperationCatalog.Convert(ImageFormat.Png)], onlyPng.Resolve([@"C:\a.jpg"]));
    }
}

public sealed class CapabilityMatrixTests
{
    [Fact]
    public void Every_decodable_source_converts_to_every_other_encodable_target()
    {
        foreach (var source in ImageFormats.Decodable)
            foreach (var target in ImageFormats.Encodable)
                Assert.Equal(source != target, OperationCatalog.Convert(target).AppliesTo(source));
    }

    [Fact]
    public void Nothing_applies_to_unknown_content()
    {
        Assert.All(OperationCatalog.All, op => Assert.False(op.AppliesTo(ImageFormat.Unknown)));
    }

    [Fact]
    public void Labels_and_ids_come_from_the_format_table()
    {
        Assert.Equal(["To JPEG", "To PNG", "To WebP", "To BMP", "To TIFF"],
            OperationCatalog.All.Where(o => o.Kind == OperationKind.Convert).Select(o => o.Label));
        Assert.Equal(OperationCatalog.All.Count, OperationCatalog.All.Select(o => o.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(ImageFormat.Png, ".png", true)]
    [InlineData(ImageFormat.Jpeg, ".jpg", false)]
    [InlineData(ImageFormat.WebP, ".webp", true)]
    [InlineData(ImageFormat.Bmp, ".bmp", false)]
    [InlineData(ImageFormat.Tiff, ".tif", true)]
    public void Output_extension_and_alpha_policy(ImageFormat format, string extension, bool keepsAlpha)
    {
        Assert.Equal(extension, ImageFormats.Extension(format));
        Assert.Equal(keepsAlpha, ImageFormats.KeepsAlpha(format));
    }
}

public sealed class FileSignatureTests
{
    [Fact]
    public void Png_and_jpeg_are_recognised_by_their_magic_bytes()
    {
        Assert.Equal(ImageFormat.Png, FileSignature.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]));
        Assert.Equal(ImageFormat.Jpeg, FileSignature.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
    }

    [Fact]
    public void WebP_bmp_and_tiff_are_recognised()
    {
        Assert.Equal(ImageFormat.WebP, FileSignature.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal(ImageFormat.Tiff, FileSignature.Detect([0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0]));
        Assert.Equal(ImageFormat.Tiff, FileSignature.Detect([0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8]));
        Assert.Equal(ImageFormat.Bmp, FileSignature.Detect([0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0, 40, 0, 0, 0]));
        Assert.Equal(ImageFormat.Unknown, FileSignature.Detect("RIFF\0\0\0\0WAVEfmt "u8));                       // RIFF, not WebP
        Assert.Equal(ImageFormat.Unknown, FileSignature.Detect([0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0, 99, 0, 0, 0])); // "BM" text
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46 })] // RIFF without a WebP form type
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 })] // %PDF
    public void Anything_else_is_unknown(byte[] header)
    {
        Assert.Equal(ImageFormat.Unknown, FileSignature.Detect(header));
    }

    [Fact]
    public void Extension_hint_is_independent_of_content()
    {
        Assert.Equal(ImageFormat.Png, FormatHint.FromPath(@"C:\x\really-a-jpeg.png"));
        Assert.Equal(ImageFormat.Unknown, FormatHint.FromPath(@"C:\x\photo.jpg.exe"));
    }
}

public sealed class ContainerCheckTests
{
    private static byte[] Chunk(string type, byte[] data)
    {
        var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var crc = ContainerCheck.Crc32(td);
        return [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. td,
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    private static readonly byte[] Sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static byte[] Png(params byte[][] chunks) => [.. Sig, .. chunks.SelectMany(c => c)];
    // 2x1 RGBA8, non-interlaced: filtered size = 1 row x (1 filter byte + 8) = 9 bytes.
    private static readonly byte[] Ihdr = Chunk("IHDR", [0, 0, 0, 2, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
    private static byte[] Zlib(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }
    private static readonly byte[] Pixels = [0, 1, 2, 3, 255, 4, 5, 6, 255];
    private static readonly byte[] Idat = Chunk("IDAT", Zlib(Pixels)), Iend = Chunk("IEND", []);

    [Fact]
    public void Crc32_matches_the_standard_check_value()
    {
        Assert.Equal(0xCBF43926u, ContainerCheck.Crc32("123456789"u8));
    }

    [Fact]
    public void Complete_png_structure_passes_and_trailing_bytes_are_ignored()
    {
        Assert.True(ContainerCheck.IsComplete(Png(Ihdr, Idat, Iend), ImageFormat.Png, out _));
        Assert.True(ContainerCheck.IsComplete([.. Png(Ihdr, Idat, Iend), 0, 0, 0], ImageFormat.Png, out _));
    }

    [Fact]
    public void Png_failures_are_detected()
    {
        Assert.False(ContainerCheck.IsComplete(Png(Ihdr, Idat), ImageFormat.Png, out var r1)); Assert.Contains("IEND", r1);
        Assert.False(ContainerCheck.IsComplete(Png(Ihdr, Iend), ImageFormat.Png, out var r2)); Assert.Contains("no image data", r2);
        Assert.False(ContainerCheck.IsComplete(Png(Idat, Ihdr, Iend), ImageFormat.Png, out var r3)); Assert.Contains("IHDR", r3);
        var bad = Png(Ihdr, Idat, Iend); bad[Sig.Length + 8] ^= 0xFF; // flip a byte inside IHDR data
        Assert.False(ContainerCheck.IsComplete(bad, ImageFormat.Png, out var r4)); Assert.Contains("CRC", r4);
        var cut = Png(Ihdr, Idat, Iend).AsSpan(0, Sig.Length + Ihdr.Length + 6);
        Assert.False(ContainerCheck.IsComplete(cut, ImageFormat.Png, out _));
    }

    [Fact]
    public void Png_image_data_must_inflate_to_the_size_the_header_implies()
    {
        var z = Zlib(Pixels);
        Assert.False(ContainerCheck.IsComplete(Png(Ihdr, Chunk("IDAT", z[..(z.Length - 6)]), Iend), ImageFormat.Png, out var cut));
        Assert.Contains("truncated", cut); // valid chunks and CRCs, zlib stream cut short
        var damaged = (byte[])z.Clone(); damaged[^1] ^= 0xFF;   // Adler-32 broken
        Assert.False(ContainerCheck.IsComplete(Png(Ihdr, Chunk("IDAT", damaged), Iend), ImageFormat.Png, out var bad));
        Assert.Contains("damaged", bad);
        Assert.False(ContainerCheck.IsComplete(Png(Ihdr, Chunk("IDAT", Zlib([.. Pixels, 0])), Iend), ImageFormat.Png, out var longer));
        Assert.Contains("longer", longer);
        var half = z.Length / 2; // split across two IDAT chunks is fine
        Assert.True(ContainerCheck.IsComplete(Png(Ihdr, Chunk("IDAT", z[..half]), Chunk("IDAT", z[half..]), Iend), ImageFormat.Png, out var split), split);
    }

    private static readonly byte[] JpegOk =
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46,             // APP0, length 4
        0xFF, 0xDA, 0x00, 0x02,                         // SOS with an empty header
        0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56,       // scan data: stuffed FF00, RST0
        0xFF, 0xD9,
    ];

    [Fact]
    public void Complete_jpeg_structure_passes()
    {
        Assert.True(ContainerCheck.IsComplete(JpegOk, ImageFormat.Jpeg, out var reason), reason);
        Assert.True(ContainerCheck.IsComplete([.. JpegOk, 1, 2, 3], ImageFormat.Jpeg, out _));
    }

    [Fact]
    public void Jpeg_failures_are_detected()
    {
        Assert.False(ContainerCheck.IsComplete(JpegOk.AsSpan(0, JpegOk.Length - 2), ImageFormat.Jpeg, out var r1)); Assert.Contains("truncated", r1);
        Assert.False(ContainerCheck.IsComplete([0xFF, 0xD8, 0xFF, 0xD9], ImageFormat.Jpeg, out var r2)); Assert.Contains("no image scan", r2);
        Assert.False(ContainerCheck.IsComplete([0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x00], ImageFormat.Jpeg, out var r3)); Assert.Contains("past the end", r3);
        Assert.False(ContainerCheck.IsComplete([0x00, 0x01], ImageFormat.Jpeg, out _));
    }
}

public sealed class OutputNamingTests
{
    [Fact]
    public void Candidates_start_with_the_plain_name_then_number_upwards()
    {
        var c = OutputNaming.Candidates(@"C:\pics\photo.png", ImageFormat.Jpeg).Take(3).ToArray();
        Assert.Equal([@"C:\pics\photo.jpg", @"C:\pics\photo (1).jpg", @"C:\pics\photo (2).jpg"], c);
    }

    [Fact]
    public void Candidates_are_bounded()
    {
        var all = OutputNaming.Candidates(@"C:\pics\a.jpg", ImageFormat.Png).ToArray();
        Assert.Equal(OutputNaming.MaxCollisionIndex + 1, all.Length);
        Assert.Equal(@"C:\pics\a (999).png", all[^1]);
    }

    [Theory]
    [InlineData(@"C:\p\my photo (1).PNG", ImageFormat.Jpeg, @"C:\p\my photo (1).jpg")]
    [InlineData(@"C:\p\ملف 日本語 🚀.jpeg", ImageFormat.Png, @"C:\p\ملف 日本語 🚀.png")]
    [InlineData(@"C:\p\archive.tar.jpg", ImageFormat.Png, @"C:\p\archive.tar.png")]
    [InlineData(@"\\server\share\x.png", ImageFormat.Jpeg, @"\\server\share\x.jpg")]
    public void Only_the_last_extension_changes(string source, ImageFormat target, string expected)
    {
        Assert.Equal(expected, OutputNaming.Candidates(source, target).First());
    }

    [Fact]
    public void Output_never_equals_the_source_path()
    {
        foreach (var src in new[] { @"C:\p\a.png", @"C:\p\a.jpg" })
        {
            var target = src.EndsWith(".png", StringComparison.Ordinal) ? ImageFormat.Jpeg : ImageFormat.Png;
            Assert.DoesNotContain(src, OutputNaming.Candidates(src, target), StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Temp_path_is_in_the_destination_folder_and_unmistakable()
    {
        Assert.Equal(@"C:\p\~wintools-abc123.tmp", OutputNaming.TempPath(@"C:\p\photo.png", "abc123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\x")]
    [InlineData("a b")]
    public void Temp_path_rejects_unsafe_request_ids(string id)
    {
        Assert.Throws<ArgumentException>(() => OutputNaming.TempPath(@"C:\p\photo.png", id));
    }

    [Theory]
    [InlineData("photo.png")]
    [InlineData(@"..\photo.png")]
    [InlineData(@"C:\p\.png")]
    public void Relative_or_nameless_sources_are_rejected(string source)
    {
        Assert.Throws<ArgumentException>(() => OutputNaming.Candidates(source, ImageFormat.Jpeg).First());
    }
}

public sealed class WorkerProtocolTests
{
    [Fact]
    public void Requests_round_trip_and_the_wire_is_ascii_only()
    {
        var req = new ConversionRequest("abc", "convert.png-to-jpeg", @"C:\p\ملف 日本語 🚀 ""q"".png");
        var line = WorkerProtocol.Serialize(req);
        Assert.All(line, ch => Assert.True(ch < 128));
        Assert.DoesNotContain('\n', line);
        Assert.Equal(req, WorkerProtocol.Deserialize<ConversionRequest>(line));
    }

    [Fact]
    public void Results_carry_error_codes_by_name()
    {
        var line = WorkerProtocol.Serialize(ConversionResult.Fail("x", ErrorCode.ContentMismatch, "m"));
        Assert.Contains("\"ContentMismatch\"", line);
        Assert.Equal(ErrorCode.ContentMismatch, WorkerProtocol.Deserialize<ConversionResult>(line)!.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"id\":")]
    public void Malformed_lines_are_rejected(string? line)
    {
        Assert.Null(WorkerProtocol.Deserialize<ConversionResult>(line));
    }

    [Fact]
    public void Oversized_lines_are_rejected()
    {
        Assert.Null(WorkerProtocol.Deserialize<ConversionRequest>(new string(' ', WorkerProtocol.MaxLineLength + 1)));
    }
}

public sealed class PathPolicyTests
{
    [Theory]
    [InlineData(@"C:\p\photo.png")]
    [InlineData(@"C:\p\my photo (1).PNG")]
    [InlineData(@"\\server\share\x.png")]
    [InlineData("C:\\p\\\u0645\u0644\u0641 \u65E5\u672C \uD83D\uDE80.jpg")]   // Arabic, Japanese, emoji (surrogate pair)
    public void Canonical_paths_are_supported(string path)
    {
        Assert.True(PathPolicy.IsSupported(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("photo.png")]                        // relative
    [InlineData(@"C:\dir.\photo.png")]               // trailing dot: Win32 would open C:\dir\photo.png
    [InlineData(@"C:\p\photo.png.")]                 // trailing dot on the file
    [InlineData(@"C:\p\..\photo.png")]               // not normalised
    [InlineData(@"\\?\C:\p\photo.png")]              // verbatim
    [InlineData(@"\\.\C:\p\photo.png")]              // device namespace
    [InlineData(@"C:\p\.png")]                       // no stem
    [InlineData(@"C:\p\a.png:stream.png")]            // alternate data stream
    [InlineData("C:\\p\\a\u0001.png")]                // control character
    public void Other_paths_are_refused(string? path)
    {
        Assert.False(PathPolicy.IsSupported(path));
    }

    [Fact]
    public void Lone_surrogates_are_refused()
    {
        // Built at run time: attribute arguments are stored as UTF-8, which cannot carry a lone surrogate.
        Assert.False(PathPolicy.IsSupported(@"C:\p\a" + '\uD800' + ".png"));
        Assert.False(PathPolicy.IsSupported(@"C:\p\a" + '\uDC00' + "b.png"));
        Assert.False(PathPolicy.IsSupported(@"C:\p\a" + '\uD800'));
    }

    [Fact]
    public void Resolver_offers_nothing_for_a_refused_path()
    {
        Assert.Empty(CapabilityResolver.Default.Resolve([@"C:\ok\a.png", @"C:\dir.\b.png"]));
    }
}

public sealed class WebPAndBmpStructureTests
{
    private static byte[] Le32(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];

    private static byte[] WebP(params (string Id, int DeclaredSize, int ActualSize)[] chunks)
    {
        var body = new List<byte>("WEBP"u8.ToArray());
        foreach (var (id, declared, actual) in chunks)
        {
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(id));
            body.AddRange(Le32(declared));
            body.AddRange(new byte[actual + (actual & 1)]);
        }
        return [.. "RIFF"u8, .. Le32(body.Count), .. body];
    }

    [Fact]
    public void Complete_webp_container_passes()
    {
        Assert.True(ContainerCheck.IsComplete(WebP(("VP8L", 9, 9)), ImageFormat.WebP, out var r), r);
        Assert.True(ContainerCheck.IsComplete(WebP(("VP8X", 10, 10), ("VP8 ", 20, 20), ("EXIF", 3, 3)), ImageFormat.WebP, out r), r);
    }

    [Fact]
    public void Webp_failures_are_detected()
    {
        var ok = WebP(("VP8L", 9, 9));
        Assert.False(ContainerCheck.IsComplete(ok.AsSpan(0, ok.Length - 4), ImageFormat.WebP, out var r1)); Assert.Contains("RIFF size", r1);
        Assert.False(ContainerCheck.IsComplete(WebP(("VP8L", 500, 9)), ImageFormat.WebP, out var r2)); Assert.Contains("past the end", r2);
        Assert.False(ContainerCheck.IsComplete(WebP(("EXIF", 4, 4)), ImageFormat.WebP, out var r3)); Assert.Contains("no image", r3);
    }

    private static byte[] Bmp24(int width, int height, int pixelBytes, int compression = 0)
    {
        var header = new List<byte>("BM"u8.ToArray());
        header.AddRange(Le32(54 + pixelBytes)); header.AddRange(Le32(0)); header.AddRange(Le32(54));
        header.AddRange(Le32(40)); header.AddRange(Le32(width)); header.AddRange(Le32(height));
        header.AddRange([1, 0, 24, 0]); header.AddRange(Le32(compression)); header.AddRange(new byte[20]);
        return [.. header, .. new byte[pixelBytes]];
    }

    [Fact]
    public void Bmp_pixel_array_must_fit_and_rle_is_refused()
    {
        // 3x2 at 24 bpp: rows are padded to 12 bytes, so 24 bytes of pixels.
        Assert.True(ContainerCheck.IsComplete(Bmp24(3, 2, 24), ImageFormat.Bmp, out var r), r);
        Assert.True(ContainerCheck.IsComplete(Bmp24(3, -2, 24), ImageFormat.Bmp, out r), r); // top-down
        Assert.False(ContainerCheck.IsComplete(Bmp24(3, 2, 23), ImageFormat.Bmp, out var cut)); Assert.Contains("truncated", cut);
        Assert.False(ContainerCheck.IsComplete(Bmp24(3, 2, 24, compression: 1), ImageFormat.Bmp, out var rle)); Assert.Contains("RLE", rle);
    }
}
