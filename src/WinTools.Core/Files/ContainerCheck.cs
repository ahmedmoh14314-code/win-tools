namespace WinTools.Core.Files;

/// <summary>
/// Structural completeness check run before decoding, because decoders can paper over damage: the Windows PNG decoder
/// fills the rows of a truncated IDAT without reporting an error, which would turn a damaged file into a believable
/// but wrong output. PNG: every chunk fits in the file, every CRC matches, IHDR comes first, IDAT is present, IEND
/// is reached, and the image data inflates to exactly the size IHDR implies. JPEG: the marker segments and entropy-coded scans can be walked from SOI to an EOI.
/// Bytes after the end marker are ignored (some cameras append trailers).
/// </summary>
public static class ContainerCheck
{
    public static bool IsComplete(ReadOnlySpan<byte> data, ImageFormat format, out string reason)
    {
        reason = format switch
        {
            ImageFormat.Png => CheckPng(data),
            ImageFormat.Jpeg => CheckJpeg(data),
            _ => "unsupported format",
        } ?? "";
        return reason.Length == 0;
    }

    private static string? CheckPng(ReadOnlySpan<byte> d)
    {
        if (FileSignature.Detect(d) != ImageFormat.Png) return "not a PNG signature";
        var i = 8;
        var first = true;
        ReadOnlySpan<byte> ihdr = default;
        var idat = new List<(int Offset, int Length)>();
        while (true)
        {
            if (d.Length - i < 12) return "truncated before IEND";
            var length = (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
            if (length > int.MaxValue || length > (uint)(d.Length - i - 12)) return "chunk runs past the end of the file";
            var typeAndData = d.Slice(i + 4, 4 + (int)length);
            var crcAt = i + 8 + (int)length;
            var crc = (uint)(d[crcAt] << 24 | d[crcAt + 1] << 16 | d[crcAt + 2] << 8 | d[crcAt + 3]);
            if (Crc32(typeAndData) != crc) return "chunk CRC mismatch";
            var type = typeAndData[..4];
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) return "first chunk is not a valid IHDR";
                ihdr = typeAndData[4..];
                first = false;
            }
            if (type.SequenceEqual("IDAT"u8)) idat.Add((i + 8, (int)length));
            if (type.SequenceEqual("IEND"u8)) return idat.Count == 0 ? "no image data" : CheckImageData(d, ihdr, idat);
            i = crcAt + 4;
        }
    }

    /// <summary>
    /// Inflates the concatenated IDAT data and requires exactly the filtered byte count IHDR implies. A cut zlib stream
    /// yields too few bytes and a damaged one fails its Adler-32 check; the decoder would invent pixels for either.
    /// </summary>
    private static string? CheckImageData(ReadOnlySpan<byte> d, ReadOnlySpan<byte> ihdr, List<(int Offset, int Length)> idat)
    {
        var width = (long)(uint)(ihdr[0] << 24 | ihdr[1] << 16 | ihdr[2] << 8 | ihdr[3]);
        var height = (long)(uint)(ihdr[4] << 24 | ihdr[5] << 16 | ihdr[6] << 8 | ihdr[7]);
        int bitDepth = ihdr[8], colorType = ihdr[9], interlace = ihdr[12];
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (width == 0 || height == 0 || channels == 0 || bitDepth is not (1 or 2 or 4 or 8 or 16) || interlace > 1) return "invalid IHDR";
        var bitsPerPixel = channels * bitDepth;
        long Filtered(long w, long h) => w == 0 || h == 0 ? 0 : h * (1 + (w * bitsPerPixel + 7) / 8);
        long expected;
        if (interlace == 0) expected = Filtered(width, height);
        else
        {
            ReadOnlySpan<int> x0 = [0, 4, 0, 2, 0, 1, 0], y0 = [0, 0, 4, 0, 2, 0, 1], dx = [8, 8, 4, 4, 2, 2, 1], dy = [8, 8, 8, 4, 4, 2, 2];
            expected = 0;
            for (var p = 0; p < 7; p++)
                expected += Filtered(width > x0[p] ? (width - x0[p] + dx[p] - 1) / dx[p] : 0, height > y0[p] ? (height - y0[p] + dy[p] - 1) / dy[p] : 0);
        }
        if (expected > MaxInflatedBytes) return "image data too large to verify";

        var total = 0;
        foreach (var (_, length) in idat) total += length;
        var compressed = new byte[total];
        var at = 0;
        foreach (var (offset, length) in idat) { d.Slice(offset, length).CopyTo(compressed.AsSpan(at)); at += length; }
        long produced = 0;
        try
        {
            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(compressed), System.IO.Compression.CompressionMode.Decompress);
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = z.Read(buffer)) > 0)
            {
                produced += n;
                if (produced > expected) return "image data is longer than the header implies";
            }
        }
        catch (InvalidDataException) { return "image data is damaged (zlib)"; }
        return produced == expected ? null : "image data is truncated";
    }

    /// <summary>Largest decompressed PNG stream the check will inflate (100 MP at 16-bit RGBA, plus filter bytes).</summary>
    private const long MaxInflatedBytes = 100_000_000L * 8 + 65_536;

    private static string? CheckJpeg(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return "not a JPEG signature";
        var i = 2;
        var sawScan = false;
        while (true)
        {
            if (i >= d.Length || d[i] != 0xFF) return i >= d.Length ? "truncated before EOI" : "marker expected";
            while (i < d.Length && d[i] == 0xFF) i++; // fill bytes
            if (i >= d.Length) return "truncated before EOI";
            var marker = d[i++];
            if (marker == 0xD9) return sawScan ? null : "no image scan";
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue; // standalone markers
            if (d.Length - i < 2) return "truncated segment";
            var length = d[i] << 8 | d[i + 1];
            if (length < 2 || length > d.Length - i) return "segment runs past the end of the file";
            i += length;
            if (marker != 0xDA) continue;

            // Entropy-coded data: 0xFF is followed by 0x00 (stuffed), RSTn, or the next marker.
            sawScan = true;
            while (true)
            {
                if (i >= d.Length) return "truncated scan";
                if (d[i] != 0xFF) { i++; continue; }
                if (i + 1 >= d.Length) return "truncated scan";
                var next = d[i + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                if (next == 0xFF) { i++; continue; }
                break; // a real marker starts at i
            }
        }
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
