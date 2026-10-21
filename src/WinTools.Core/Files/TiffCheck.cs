using System.IO.Compression;

namespace WinTools.Core.Files;

/// <summary>
/// Structural check of a TIFF's first image. The Windows TIFF decoder fills short or missing strip data with black and
/// reports success, so the check reads IFD0 and requires:
/// <list type="bullet">
/// <item>every strip or tile to lie inside the file;</item>
/// <item>uncompressed data to have at least the byte count the dimensions imply;</item>
/// <item>Deflate, PackBits and LZW data to decompress to at least that byte count.</item>
/// </list>
/// Other compressions (JPEG-in-TIFF, CCITT) only get the bounds check. That gap is recorded in ADR-0006.
/// </summary>
internal static class TiffCheck
{
    private const int TagWidth = 256, TagHeight = 257, TagBitsPerSample = 258, TagCompression = 259, TagStripOffsets = 273,
        TagSamplesPerPixel = 277, TagRowsPerStrip = 278, TagStripByteCounts = 279, TagPlanar = 284,
        TagTileWidth = 322, TagTileLength = 323, TagTileOffsets = 324, TagTileByteCounts = 325, TagFillOrder = 266;

    private static readonly HashSet<uint> Needed = [TagWidth, TagHeight, TagBitsPerSample, TagCompression, TagFillOrder, TagStripOffsets,
        TagSamplesPerPixel, TagRowsPerStrip, TagStripByteCounts, TagPlanar, TagTileWidth, TagTileLength, TagTileOffsets, TagTileByteCounts];

    /// <summary>More strips or tiles than this cannot belong to an image within the pixel limits; refusing early keeps a crafted IFD cheap.</summary>
    private const long MaxChunks = 1_000_000;

    /// <summary>The largest BitsPerSample value of IFD0 (1 if absent, 0 if unreadable).</summary>
    public static int MaxBitsPerSample(ReadOnlySpan<byte> d)
    {
        if (FileSignature.Detect(d) != ImageFormat.Tiff || d.Length < 8) return 0;
        var le = d[0] == 0x49;
        int U16(ReadOnlySpan<byte> b, long o) => le ? b[(int)o] | b[(int)o + 1] << 8 : b[(int)o] << 8 | b[(int)o + 1];
        long U32(ReadOnlySpan<byte> b, long o) => le
            ? (uint)(b[(int)o] | b[(int)o + 1] << 8 | b[(int)o + 2] << 16 | b[(int)o + 3] << 24)
            : (uint)(b[(int)o] << 24 | b[(int)o + 1] << 16 | b[(int)o + 2] << 8 | b[(int)o + 3]);
        var ifd = U32(d, 4);
        if (ifd < 8 || ifd + 2 > d.Length) return 0;
        var n = U16(d, ifd);
        for (var i = 0; i < n && ifd + 2 + (i + 1) * 12L <= d.Length; i++)
        {
            var e = ifd + 2 + i * 12L;
            if (U16(d, e) != TagBitsPerSample) continue;
            var count = U32(d, e + 4);
            if (U16(d, e + 2) != 3 || count is <= 0 or > 16) return 0;
            var at = count <= 2 ? e + 8 : U32(d, e + 8);
            if (at + count * 2 > d.Length) return 0;
            var max = 0;
            for (var k = 0; k < count; k++) max = Math.Max(max, U16(d, at + k * 2));
            return max;
        }
        return 1;
    }

    public static string? Check(ReadOnlySpan<byte> d)
    {
        if (FileSignature.Detect(d) != ImageFormat.Tiff) return "not a TIFF signature";
        var le = d[0] == 0x49;
        uint U16(ReadOnlySpan<byte> b, long o) => le ? (uint)(b[(int)o] | b[(int)o + 1] << 8) : (uint)(b[(int)o] << 8 | b[(int)o + 1]);
        uint U32(ReadOnlySpan<byte> b, long o) => le
            ? (uint)(b[(int)o] | b[(int)o + 1] << 8 | b[(int)o + 2] << 16 | b[(int)o + 3] << 24)
            : (uint)(b[(int)o] << 24 | b[(int)o + 1] << 16 | b[(int)o + 2] << 8 | b[(int)o + 3]);

        long ifd = U32(d, 4);
        if (ifd < 8 || ifd + 2 > d.Length) return "IFD outside the file";
        var entries = U16(d, ifd);
        if (ifd + 2 + entries * 12L > d.Length) return "IFD truncated";

        var tags = new Dictionary<uint, long[]>();
        for (var i = 0; i < entries; i++)
        {
            var e = ifd + 2 + i * 12L;
            var tag = U16(d, e);
            var type = U16(d, e + 2);
            var count = (long)U32(d, e + 4);
            var size = type switch { 3 => 2, 4 => 4, _ => 0 };
            if (!Needed.Contains(tag) || size == 0 || count <= 0) continue; // only the SHORT/LONG tags this check uses
            if (count > MaxChunks) return "too many strips or values";
            var at = count * size <= 4 ? e + 8 : U32(d, e + 8);
            if (at + count * size > d.Length) return "tag data outside the file";
            var values = new long[count];
            for (var k = 0; k < count; k++) values[k] = size == 2 ? U16(d, at + k * 2) : U32(d, at + k * 4);
            tags[tag] = values;
        }

        long One(uint tag, long fallback) => tags.TryGetValue(tag, out var v) && v.Length > 0 ? v[0] : fallback;
        var width = One(TagWidth, 0);
        var height = One(TagHeight, 0);
        if (width <= 0 || height <= 0) return "invalid image size";
        var compression = One(TagCompression, 1);
        var samples = Math.Max(1, One(TagSamplesPerPixel, 1));
        var planar = One(TagPlanar, 1);
        var bits = tags.TryGetValue(TagBitsPerSample, out var bps) ? bps : [1];
        var bitsPerPixel = planar == 2 ? bits[0] : (bits.Length >= samples ? bits.Take((int)samples).Sum() : bits[0] * samples);
        var planes = planar == 2 ? samples : 1;
        // FillOrder 2 stores each byte's bits reversed; the codecs below read MSB-first, so such files get the bounds check only.
        var lengthChecks = One(TagFillOrder, 1) != 2;

        var tiled = tags.ContainsKey(TagTileOffsets);
        var offsets = tags.GetValueOrDefault(tiled ? (uint)TagTileOffsets : TagStripOffsets);
        var counts = tags.GetValueOrDefault(tiled ? (uint)TagTileByteCounts : TagStripByteCounts);
        if (offsets is null) return "no image data";
        if (counts is null || counts.Length != offsets.Length) return "strip table is invalid";

        long perPlane, chunkRows, chunkWidth;
        if (tiled)
        {
            chunkWidth = One(TagTileWidth, 0);
            chunkRows = One(TagTileLength, 0);
            if (chunkWidth <= 0 || chunkRows <= 0) return "invalid tile size";
            perPlane = (width + chunkWidth - 1) / chunkWidth * ((height + chunkRows - 1) / chunkRows);
        }
        else
        {
            chunkWidth = width;
            chunkRows = Math.Clamp(One(TagRowsPerStrip, height), 1, height);
            perPlane = (height + chunkRows - 1) / chunkRows;
        }
        if (offsets.Length < perPlane * planes) return "strips are missing";
        // Exactly as many as the image needs: extra entries are never decoded, but each would be inflated here, so a few
        // kilobytes of table pointing at one compressed blob could keep the worker busy for minutes (security review M2).
        // With the exact count, the work is bounded by the image's own size, which the pixel limit has already checked.
        if (offsets.Length > perPlane * planes) return "the strip table does not match the image size";

        var rowBytes = (chunkWidth * bitsPerPixel + 7) / 8;
        for (var i = 0; i < offsets.Length; i++)
        {
            long off = offsets[i], cnt = counts[i];
            if (cnt <= 0 || off < 8 || off + cnt > d.Length) return "strip data outside the file";
            var rows = tiled ? chunkRows : Math.Min(chunkRows, height - i % perPlane * chunkRows);
            var expected = rowBytes * rows;
            var data = d.Slice((int)off, (int)cnt);
            var produced = !lengthChecks ? long.MaxValue : compression switch
            {
                1 => cnt,
                8 or 32946 => Inflate(data, expected),
                32773 => PackBitsLength(data, expected),
                5 => LzwLength(data, expected),
                _ => long.MaxValue, // JPEG, CCITT and others: bounds check only
            };
            if (produced < 0) return "strip data is damaged";
            if (produced < expected) return "strip data is truncated";
        }
        return null;
    }

    private static long Inflate(ReadOnlySpan<byte> data, long expected)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int n;
            while (total < expected && (n = z.Read(buffer)) > 0) total += n;
            return total;
        }
        catch (InvalidDataException) { return -1; }
    }

    private static long PackBitsLength(ReadOnlySpan<byte> data, long expected)
    {
        long total = 0;
        var i = 0;
        while (i < data.Length && total < expected)
        {
            var n = (sbyte)data[i++];
            if (n >= 0) { if (i + n + 1 > data.Length) return total + (data.Length - i); total += n + 1; i += n + 1; }
            else if (n != -128) { if (i >= data.Length) return total; total += 1 - n; i++; }
        }
        return total;
    }

    /// <summary>Counts the bytes a TIFF LZW strip (MSB-first codes, "early change") decodes to; -1 if it is invalid.</summary>
    private static long LzwLength(ReadOnlySpan<byte> data, long expected)
    {
        var length = new int[4096];
        for (var c = 0; c < 256; c++) length[c] = 1;
        int bits = 9, next = 258, old = -1;
        long total = 0, bitPos = 0, totalBits = data.Length * 8L;
        while (total < expected && bitPos + bits <= totalBits)
        {
            var code = 0;
            for (var b = 0; b < bits; b++, bitPos++) code = code << 1 | (data[(int)(bitPos >> 3)] >> (7 - (int)(bitPos & 7)) & 1);
            if (code == 256) { bits = 9; next = 258; old = -1; continue; }
            if (code == 257) break;
            if (old < 0)
            {
                if (code > 255) return -1;
                total += 1;
                old = code;
                continue;
            }
            if (code < next) total += length[code];
            else if (code == next) total += length[old] + 1;
            else return -1;
            if (next < 4096) length[next++] = length[old] + 1;
            old = code;
            if (next >= (1 << bits) - 1 && bits < 12) bits++;
        }
        return total;
    }
}
