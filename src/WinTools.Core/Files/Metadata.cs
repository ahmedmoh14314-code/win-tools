using System.Text;

namespace WinTools.Core.Files;

/// <summary>Which removable metadata a file carries.</summary>
public readonly record struct MetadataFound(bool Exif, bool Gps, bool Xmp, bool Icc, bool Text)
{
    /// <summary>Anything that "Remove metadata" takes out (ICC stays: it is colour, not information about the user).</summary>
    public bool AnyRemovable => Exif || Gps || Xmp || Text;
}

/// <summary>
/// Byte-level metadata inspection and removal. Removal never re-encodes pixels: it drops segments or chunks and keeps
/// what affects how the image looks (ICC, gAMA/cHRM/sRGB, pHYs, JFIF/Adobe markers). The EXIF orientation is kept as a
/// minimal EXIF block holding only that tag, so the picture does not suddenly display rotated. This removes the usual
/// identifying metadata (camera, dates, GPS, comments, XMP); it is not a forensic anonymiser.
/// </summary>
public static class Metadata
{
    private static ReadOnlySpan<byte> ExifId => "Exif\0\0"u8;
    private static ReadOnlySpan<byte> XmpId => "http://ns.adobe.com/xap/1.0/\0"u8;
    private static ReadOnlySpan<byte> IccId => "ICC_PROFILE\0"u8;

    public static MetadataFound Inspect(ReadOnlySpan<byte> d, ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => InspectJpeg(d),
        ImageFormat.Png => InspectPng(d),
        ImageFormat.WebP => InspectWebP(d),
        ImageFormat.Tiff => InspectTiffIfd(d, isStandaloneTiff: true),
        _ => default,
    };

    /// <summary>The file without removable metadata, or null if this format is not stripped at the byte level (TIFF, BMP).</summary>
    public static byte[]? Strip(ReadOnlySpan<byte> d, ImageFormat format, int orientation) => format switch
    {
        ImageFormat.Jpeg => StripJpeg(d, orientation),
        ImageFormat.Png => StripPng(d),
        ImageFormat.WebP => StripWebP(d, orientation),
        _ => null,
    };

    /// <summary>A TIFF block ("II*\0", IFD0 with one entry: Orientation) — the minimal EXIF that keeps the orientation.</summary>
    public static byte[] OrientationOnlyExif(int orientation) =>
    [
        0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
        0x01, 0x00,
        0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    // ---------------------------------------------------------------- JPEG

    private static IEnumerable<(byte Marker, int Start, int End)> JpegSegments(byte[] d)
    {
        // Yields every marker segment before the first SOS (Start = index of 0xFF, End exclusive), then one entry for
        // "everything from SOS to EOI" with Marker 0xDA.
        var i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) yield break;
            var m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m == 0xDA)
            {
                var end = FindEoi(d, i);
                yield return (0xDA, i, end);
                yield break;
            }
            var len = d[i + 2] << 8 | d[i + 3];
            if (len < 2 || i + 2 + len > d.Length) yield break;
            yield return (m, i, i + 2 + len);
            i += 2 + len;
        }
    }

    /// <summary>Index just past EOI (scan data and any further SOS/DHT segments are kept as one block).</summary>
    private static int FindEoi(byte[] d, int from)
    {
        for (var i = from + 2; i + 1 < d.Length; i++)
            if (d[i] == 0xFF && d[i + 1] == 0xD9) return i + 2;
        return d.Length;
    }

    private static bool PayloadStarts(byte[] d, int segStart, ReadOnlySpan<byte> id) =>
        segStart + 4 + id.Length <= d.Length && d.AsSpan(segStart + 4, id.Length).SequenceEqual(id);

    private static MetadataFound InspectJpeg(ReadOnlySpan<byte> span)
    {
        if (FileSignature.Detect(span) != ImageFormat.Jpeg) return default;
        var d = span.ToArray();
        bool exif = false, gps = false, xmp = false, icc = false, text = false;
        foreach (var (m, s, e) in JpegSegments(d))
        {
            if (m == 0xE1 && PayloadStarts(d, s, ExifId))
            {
                var tiff = d.AsSpan(s + 4 + 6, e - s - 4 - 6);
                exif |= !IsOrientationOnly(tiff); // the orientation-only block this class writes is not removable metadata
                gps |= TiffHasTag(tiff, 34853);
            }
            else if (m == 0xE1 && PayloadStarts(d, s, XmpId)) xmp = true;
            else if (m == 0xE2 && PayloadStarts(d, s, IccId)) icc = true;
            else if (m == 0xFE || m == 0xED) text = true;          // COM, APP13 (Photoshop/IPTC)
            else if (m is >= 0xE3 and <= 0xEF && m != 0xEE) text = true; // other APPn carry vendor metadata
        }
        return new MetadataFound(exif, gps, xmp, icc, text);
    }

    private static byte[] StripJpeg(ReadOnlySpan<byte> span, int orientation)
    {
        var d = span.ToArray();
        var o = new MemoryStream(d.Length);
        o.Write([0xFF, 0xD8]);
        var wroteOrientation = orientation is < 2 or > 8;
        foreach (var (m, s, e) in JpegSegments(d))
        {
            var keep = m switch
            {
                0xE0 => PayloadStarts(d, s, "JFIF\0"u8),   // JFIF header yes; JFXX thumbnails no
                0xE2 => PayloadStarts(d, s, IccId),        // colour profile
                0xEE => true,                               // Adobe: colour transform of the scan
                >= 0xE1 and <= 0xEF => false,               // EXIF, XMP, other APPn
                0xFE => false,                              // comments
                _ => true,                                  // tables, frame, scan
            };
            if (!wroteOrientation && m != 0xE0)
            {
                var exif = OrientationOnlyExif(orientation);
                var len = 2 + 6 + exif.Length;
                o.Write([0xFF, 0xE1, (byte)(len >> 8), (byte)len]);
                o.Write(ExifId);
                o.Write(exif);
                wroteOrientation = true;
            }
            if (keep) o.Write(d, s, e - s);
        }
        return o.ToArray();
    }

    private static bool IsOrientationOnly(ReadOnlySpan<byte> tiff) =>
        tiff.Length >= 14 && TiffEntryCount(tiff) == 1 && TiffHasTag(tiff, 0x0112);

    // ---------------------------------------------------------------- PNG

    private static readonly string[] PngTextChunks = ["tEXt", "zTXt", "iTXt", "eXIf", "tIME"];

    private static MetadataFound InspectPng(ReadOnlySpan<byte> d)
    {
        bool exif = false, gps = false, xmp = false, icc = false, text = false;
        foreach (var (type, off, len) in PngChunks(d))
        {
            var data = d.Slice(off + 8, len - 12);
            switch (type)
            {
                case "eXIf":
                    exif = true;
                    gps |= TiffHasTag(data, 34853);
                    break;
                case "iTXt" when data.StartsWith("XML:com.adobe.xmp\0"u8): xmp = true; break;
                case "tEXt" or "zTXt" or "iTXt" or "tIME": text = true; break;
                case "iCCP": icc = true; break;
            }
        }
        return new MetadataFound(exif, gps, xmp, icc, text);
    }

    private static byte[] StripPng(ReadOnlySpan<byte> d)
    {
        var o = new MemoryStream(d.Length);
        o.Write(d[..8]);
        foreach (var (type, off, len) in PngChunks(d))
            if (!PngTextChunks.Contains(type)) o.Write(d.Slice(off, len));
        return o.ToArray();
    }

    private static List<(string Type, int Offset, int Length)> PngChunks(ReadOnlySpan<byte> d)
    {
        var list = new List<(string, int, int)>();
        if (FileSignature.Detect(d) != ImageFormat.Png) return list;
        for (var i = 8; d.Length - i >= 12;)
        {
            var length = (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
            if (length > (uint)(d.Length - i - 12)) break;
            var type = Encoding.ASCII.GetString(d.Slice(i + 4, 4));
            list.Add((type, i, 12 + (int)length));
            i += 12 + (int)length;
            if (type == "IEND") break;
        }
        return list;
    }

    // ---------------------------------------------------------------- WebP

    private static List<(string Id, int Offset, int Length)> WebPChunks(ReadOnlySpan<byte> d)
    {
        var list = new List<(string, int, int)>();
        if (FileSignature.Detect(d) != ImageFormat.WebP) return list;
        for (var i = 12; i + 8 <= d.Length;)
        {
            var size = (long)(uint)(d[i + 4] | d[i + 5] << 8 | d[i + 6] << 16 | d[i + 7] << 24);
            var total = 8 + size + (size & 1);
            if (i + total > d.Length) break;
            list.Add((Encoding.ASCII.GetString(d.Slice(i, 4)), i, (int)total));
            i += (int)total;
        }
        return list;
    }

    private static MetadataFound InspectWebP(ReadOnlySpan<byte> d)
    {
        bool exif = false, gps = false, xmp = false, icc = false;
        foreach (var (id, off, len) in WebPChunks(d))
        {
            var payload = d.Slice(off + 8, len - 8);
            if (payload.StartsWith(ExifId)) payload = payload[6..];
            switch (id)
            {
                case "EXIF":
                    exif |= !IsOrientationOnly(payload);
                    gps |= TiffHasTag(payload, 34853);
                    break;
                case "XMP ": xmp = true; break;
                case "ICCP": icc = true; break;
            }
        }
        return new MetadataFound(exif, gps, xmp, icc, false);
    }

    private static byte[] StripWebP(ReadOnlySpan<byte> d, int orientation)
    {
        var body = new MemoryStream(d.Length);
        body.Write("WEBP"u8);
        var keepOrientation = orientation is >= 2 and <= 8;
        foreach (var (id, off, len) in WebPChunks(d))
        {
            if (id is "EXIF" or "XMP ") continue;
            var chunk = d.Slice(off, len).ToArray();
            if (id == "VP8X") chunk[8] = (byte)(chunk[8] & ~0x0C | (keepOrientation ? 0x08 : 0)); // clear XMP, set EXIF only if kept
            body.Write(chunk);
        }
        if (keepOrientation)
        {
            // An orientation other than 1 can only come from an EXIF chunk, which requires the extended (VP8X) layout, so
            // the VP8X header exists and its EXIF flag was set above. The EXIF chunk goes after the image data.
            var exif = OrientationOnlyExif(orientation);
            body.Write("EXIF"u8);
            body.Write(BitConverter.GetBytes(exif.Length));
            body.Write(exif);
        }
        var size = (int)body.Length;
        var output = new byte[8 + size];
        "RIFF"u8.CopyTo(output);
        BitConverter.GetBytes(size).CopyTo(output, 4);
        body.ToArray().CopyTo(output, 8);
        return output;
    }

    // ---------------------------------------------------------------- TIFF structures (standalone TIFF or EXIF payload)

    private static readonly ushort[] TiffTextTags = [270, 271, 272, 305, 306, 315, 316, 33432, 37510];

    /// <summary>A standalone TIFF: the EXIF and GPS IFD pointers, XMP, ICC and descriptive text tags in IFD0.</summary>
    private static MetadataFound InspectTiffIfd(ReadOnlySpan<byte> t, bool isStandaloneTiff)
    {
        _ = isStandaloneTiff;
        if (t.Length < 8 || !(t.StartsWith("II*\0"u8) || t.StartsWith("MM\0*"u8))) return default;
        var text = false;
        foreach (var tag in TiffTextTags) text |= TiffHasTag(t, tag);
        return new MetadataFound(TiffHasTag(t, 34665), TiffHasTag(t, 34853), TiffHasTag(t, 700), TiffHasTag(t, 34675), text);
    }

    private static int TiffEntryCount(ReadOnlySpan<byte> t)
    {
        var le = t[0] == 0x49;
        var ifd = Read32(t, 4, le);
        return ifd + 2 <= t.Length ? Read16(t, ifd, le) : 0;
    }

    private static bool TiffHasTag(ReadOnlySpan<byte> t, ushort tag)
    {
        if (t.Length < 8) return false;
        var le = t[0] == 0x49;
        var ifd = Read32(t, 4, le);
        if (ifd < 8 || ifd + 2 > t.Length) return false;
        var n = Read16(t, ifd, le);
        for (var i = 0; i < n && ifd + 2 + (i + 1) * 12 <= t.Length; i++)
            if (Read16(t, ifd + 2 + i * 12, le) == tag) return true;
        return false;
    }

    private static int Read16(ReadOnlySpan<byte> b, int o, bool le) => le ? b[o] | b[o + 1] << 8 : b[o] << 8 | b[o + 1];

    private static int Read32(ReadOnlySpan<byte> b, int o, bool le) =>
        (int)Math.Min(int.MaxValue, le ? (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24) : (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]));
}
