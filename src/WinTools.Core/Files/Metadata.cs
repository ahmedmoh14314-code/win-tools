using System.Text;

namespace WinTools.Core.Files;

/// <summary>Which metadata a file carries.</summary>
/// <param name="Other">Anything else the allow-list does not keep: vendor or private segments and chunks, embedded thumbnails,
/// content credentials (C2PA), unknown markers, and data after the end of the image.</param>
public readonly record struct MetadataFound(bool Exif, bool Gps, bool Xmp, bool Icc, bool Text, bool Other = false)
{
    /// <summary>Anything that "Remove metadata" takes out (ICC stays: it is colour, not information about the user).</summary>
    public bool AnyRemovable => Exif || Gps || Xmp || Text || Other;
}

/// <summary>
/// Byte-level metadata inspection and removal for JPEG, PNG and WebP. Removal never re-encodes pixels and works from
/// an allow-list: it keeps only what decoding and colour need (frame, tables and scans; JFIF without a thumbnail;
/// the Adobe colour-transform marker; ICC; gAMA/cHRM/sRGB/cICP/sBIT/pHYs; the VP8X header) and drops every other
/// segment, chunk and trailing byte. The EXIF orientation is kept as a minimal EXIF block holding only that tag, so the
/// picture does not suddenly display rotated. <see cref="Residue"/> checks a result independently of the removal: every
/// byte of the file must belong to an allowed element. This removes identifying metadata; it is not a forensic
/// anonymiser (pixels, file name and file-system times are untouched).
/// </summary>
public static class Metadata
{
    private static ReadOnlySpan<byte> ExifId => "Exif\0\0"u8;
    private static ReadOnlySpan<byte> XmpId => "http://ns.adobe.com/xap/1.0/\0"u8;
    private static ReadOnlySpan<byte> XmpExtensionId => "http://ns.adobe.com/xmp/extension/\0"u8;
    private static ReadOnlySpan<byte> IccId => "ICC_PROFILE\0"u8;
    private static ReadOnlySpan<byte> JfifId => "JFIF\0"u8;
    private static ReadOnlySpan<byte> AdobeId => "Adobe"u8;

    /// <summary>JFIF APP0 payload without a thumbnail: identifier, version, units, densities, thumbnail 0×0.</summary>
    private const int JfifPayload = 14;
    /// <summary>Adobe APP14 payload: identifier, version, two flag words, colour transform.</summary>
    private const int AdobePayload = 12;

    public static MetadataFound Inspect(ReadOnlySpan<byte> d, ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => InspectJpeg(d),
        ImageFormat.Png => InspectPng(d),
        ImageFormat.WebP => InspectWebP(d),
        ImageFormat.Tiff => InspectTiff(d),
        _ => default,
    };

    /// <summary>
    /// The file with only allowed elements, or null if this format is not stripped at the byte level (TIFF, BMP) or the
    /// structure cannot be followed to its end (damaged input is not "cleaned" into something else).
    /// </summary>
    public static byte[]? Strip(ReadOnlySpan<byte> d, ImageFormat format, int orientation) => format switch
    {
        ImageFormat.Jpeg => StripJpeg(d, orientation),
        ImageFormat.Png => StripPng(d),
        ImageFormat.WebP => StripWebP(d, orientation),
        _ => null,
    };

    /// <summary>
    /// What in a stripped file is not on the allow-list (empty when clean). It walks the whole file: an element that is
    /// not allowed, an unparseable structure, or bytes after the end of the image are all residue.
    /// </summary>
    public static IReadOnlyList<string> Residue(ReadOnlySpan<byte> d, ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => JpegResidue(d),
        ImageFormat.Png => PngResidue(d),
        ImageFormat.WebP => WebPResidue(d),
        _ => [],
    };

    /// <summary>
    /// True for a JPEG that carries a further image: an MPF index (APP2 "MPF\0") or a second JPEG after EOI, as Ultra
    /// HDR gain maps, Motion Photos and multi-picture files do. Removal would silently drop that image, so it is refused.
    /// </summary>
    public static bool HasSecondaryImage(ReadOnlySpan<byte> d, ImageFormat format)
    {
        if (format != ImageFormat.Jpeg) return false;
        foreach (var (kind, m, s, e) in JpegElements(d))
        {
            if (kind == JpegKind.Marker && m == 0xE2 && Payload(d, s, e).StartsWith("MPF\0"u8)) return true;
            if (kind == JpegKind.Trailing && d[s..e].IndexOf([(byte)0xFF, (byte)0xD8, (byte)0xFF]) >= 0) return true;
        }
        return false;
    }

    /// <summary>A TIFF block ("II*\0", IFD0 with one entry: Orientation) — the minimal EXIF that keeps the orientation.</summary>
    public static byte[] OrientationOnlyExif(int orientation) =>
    [
        0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
        0x01, 0x00,
        0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>True only for exactly the block <see cref="OrientationOnlyExif"/> writes: nothing hides beside the tag.</summary>
    private static bool IsOrientationOnly(ReadOnlySpan<byte> tiff) =>
        tiff.Length == 26 && tiff[18] is >= 1 and <= 8 && tiff.SequenceEqual(OrientationOnlyExif(tiff[18]));

    // ---------------------------------------------------------------- JPEG

    private enum JpegKind { Marker, Standalone, EntropyData, Eoi, Trailing, Broken }

    /// <summary>
    /// Every element of a JPEG from SOI on: marker segments (with their full extent), standalone markers, entropy-coded
    /// data between markers, EOI, then anything after EOI as Trailing. Stops with Broken where the structure cannot be
    /// followed.
    /// </summary>
    private static List<(JpegKind Kind, byte Marker, int Start, int End)> JpegElements(ReadOnlySpan<byte> d)
    {
        var list = new List<(JpegKind, byte, int, int)>();
        if (FileSignature.Detect(d) != ImageFormat.Jpeg) { list.Add((JpegKind.Broken, 0, 0, d.Length)); return list; }
        var i = 2;
        var inScan = false;
        while (i < d.Length)
        {
            if (inScan)
            {
                // Entropy-coded data runs to the next marker that is neither a stuffed 0x00 nor a restart marker.
                var start = i;
                while (i + 1 < d.Length && !(d[i] == 0xFF && d[i + 1] != 0x00 && d[i + 1] is not (>= 0xD0 and <= 0xD7) && d[i + 1] != 0xFF)) i++;
                if (i + 1 >= d.Length) { list.Add((JpegKind.Broken, 0, start, d.Length)); return list; }
                if (i > start) list.Add((JpegKind.EntropyData, 0, start, i));
                inScan = false;
                continue;
            }
            if (d[i] != 0xFF || i + 1 >= d.Length) { list.Add((JpegKind.Broken, 0, i, d.Length)); return list; }
            var m = d[i + 1];
            if (m == 0xFF) { i++; continue; } // fill byte
            if (m == 0xD9)
            {
                list.Add((JpegKind.Eoi, m, i, i + 2));
                if (i + 2 < d.Length) list.Add((JpegKind.Trailing, 0, i + 2, d.Length));
                return list;
            }
            if (m is 0x01 or (>= 0xD0 and <= 0xD7)) { list.Add((JpegKind.Standalone, m, i, i + 2)); i += 2; continue; }
            if (i + 4 > d.Length) { list.Add((JpegKind.Broken, m, i, d.Length)); return list; }
            var len = d[i + 2] << 8 | d[i + 3];
            if (len < 2 || i + 2 + len > d.Length) { list.Add((JpegKind.Broken, m, i, d.Length)); return list; }
            list.Add((JpegKind.Marker, m, i, i + 2 + len));
            i += 2 + len;
            inScan = m == 0xDA;
        }
        list.Add((JpegKind.Broken, 0, d.Length, d.Length)); // ran out before EOI
        return list;
    }

    /// <summary>Frame, tables, scans and restart markers: what decoding needs.</summary>
    private static bool IsJpegImageMarker(byte m) =>
        m is (>= 0xC0 and <= 0xC7) or (>= 0xC9 and <= 0xCF) or 0xDA or 0xDB or 0xDC or 0xDD or 0xDE or 0xDF;

    private static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> d, int start, int end) => d[(start + 4)..end];

    private static MetadataFound InspectJpeg(ReadOnlySpan<byte> d)
    {
        bool exif = false, gps = false, xmp = false, icc = false, text = false, other = false;
        foreach (var (kind, m, s, e) in JpegElements(d))
        {
            if (kind is JpegKind.Trailing) { other = true; continue; }
            if (kind is not JpegKind.Marker || IsJpegImageMarker(m)) continue;
            var p = Payload(d, s, e);
            switch (m)
            {
                case 0xE1 when p.StartsWith(ExifId):
                    exif |= !IsOrientationOnly(p[6..]);
                    gps |= TiffHasTag(p[6..], 34853);
                    break;
                case 0xE1 when p.StartsWith(XmpId) || p.StartsWith(XmpExtensionId): xmp = true; break;
                case 0xE2 when p.StartsWith(IccId): icc = true; break;
                case 0xE0 when p.StartsWith(JfifId) && p.Length == JfifPayload && p[12] == 0 && p[13] == 0: break; // plain JFIF
                case 0xEE when p.StartsWith(AdobeId) && p.Length == AdobePayload: break;                           // colour transform
                case 0xFE or 0xED: text = true; break;   // COM, APP13 (Photoshop/IPTC)
                default: other = true; break;            // other APPn, JFIF thumbnails, JFXX, MPF, unknown markers
            }
        }
        return new MetadataFound(exif, gps, xmp, icc, text, other);
    }

    private static byte[]? StripJpeg(ReadOnlySpan<byte> d, int orientation)
    {
        var elements = JpegElements(d);
        if (elements.Any(x => x.Kind == JpegKind.Broken)) return null;
        var o = new MemoryStream(d.Length);
        o.Write([0xFF, 0xD8]);
        var orientationDue = orientation is >= 2 and <= 8;
        void WriteOrientation()
        {
            if (!orientationDue) return;
            var exif = OrientationOnlyExif(orientation);
            var len = 2 + 6 + exif.Length;
            o.Write([0xFF, 0xE1, (byte)(len >> 8), (byte)len]);
            o.Write(ExifId);
            o.Write(exif);
            orientationDue = false;
        }
        foreach (var (kind, m, s, e) in elements)
        {
            switch (kind)
            {
                case JpegKind.Trailing: continue;
                case JpegKind.EntropyData or JpegKind.Standalone or JpegKind.Eoi: o.Write(d[s..e]); continue;
            }
            var p = Payload(d, s, e);
            if (m == 0xE0 && p.StartsWith(JfifId) && p.Length >= JfifPayload)
            {
                // JFIF first (as the standard wants), rewritten without its thumbnail.
                o.Write([0xFF, 0xE0, 0, JfifPayload + 2]);
                o.Write(p[..12]);
                o.Write([0, 0]);
                continue;
            }
            WriteOrientation();
            if (m == 0xE2 && p.StartsWith(IccId)) o.Write(d[s..e]);
            else if (m == 0xEE && p.StartsWith(AdobeId) && p.Length >= AdobePayload)
            {
                o.Write([0xFF, 0xEE, 0, AdobePayload + 2]); // only the transform the scan depends on
                o.Write(p[..AdobePayload]);
            }
            else if (IsJpegImageMarker(m)) o.Write(d[s..e]);
        }
        return o.ToArray();
    }

    private static List<string> JpegResidue(ReadOnlySpan<byte> d)
    {
        var residue = new List<string>();
        var elements = JpegElements(d);
        if (elements.Count == 0 || elements[^1].Kind != JpegKind.Eoi) residue.Add("structure does not end in EOI");
        var exifSeen = false;
        foreach (var (kind, m, s, e) in elements)
        {
            if (kind is JpegKind.Broken or JpegKind.Trailing) { residue.Add($"{kind} at {s}"); continue; }
            if (kind != JpegKind.Marker || IsJpegImageMarker(m)) continue;
            var p = Payload(d, s, e);
            var allowed = m switch
            {
                0xE0 => p.StartsWith(JfifId) && p.Length == JfifPayload && p[12] == 0 && p[13] == 0,
                0xE1 => !exifSeen && p.StartsWith(ExifId) && IsOrientationOnly(p[6..]),
                0xE2 => p.StartsWith(IccId),
                0xEE => p.StartsWith(AdobeId) && p.Length == AdobePayload,
                _ => false,
            };
            exifSeen |= m == 0xE1;
            if (!allowed) residue.Add($"marker 0x{m:X2} at {s}");
        }
        return residue;
    }

    // ---------------------------------------------------------------- PNG

    /// <summary>Chunks that decoding or colour depend on. Everything else is dropped.</summary>
    private static readonly HashSet<string> PngKeep = ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "pHYs", "cICP", "mDCV", "cLLI"];

    /// <summary>Every chunk up to and including IEND; Complete is false if the walk could not reach IEND, Trailing if bytes follow it.</summary>
    private static (List<(string Type, int Offset, int Length)> Chunks, bool Complete, bool Trailing) PngChunks(ReadOnlySpan<byte> d)
    {
        var list = new List<(string, int, int)>();
        if (FileSignature.Detect(d) != ImageFormat.Png) return (list, false, false);
        for (var i = 8; d.Length - i >= 12;)
        {
            var length = (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
            if (length > (uint)(d.Length - i - 12)) break;
            var type = Encoding.ASCII.GetString(d.Slice(i + 4, 4));
            list.Add((type, i, 12 + (int)length));
            i += 12 + (int)length;
            if (type == "IEND") return (list, true, i < d.Length);
        }
        return (list, false, false);
    }

    private static MetadataFound InspectPng(ReadOnlySpan<byte> d)
    {
        bool exif = false, gps = false, xmp = false, icc = false, text = false, other = false;
        var (chunks, complete, trailing) = PngChunks(d);
        other |= trailing || (!complete && chunks.Count > 0);
        foreach (var (type, off, len) in chunks)
        {
            var data = d.Slice(off + 8, len - 12);
            switch (type)
            {
                case "eXIf":
                    exif |= !IsOrientationOnly(data);
                    gps |= TiffHasTag(data, 34853);
                    break;
                case "iTXt" when data.StartsWith("XML:com.adobe.xmp\0"u8): xmp = true; break;
                case "tEXt" or "zTXt" or "iTXt" or "tIME": text = true; break;
                case "iCCP": icc = true; break;
                default: other |= !PngKeep.Contains(type); break; // caBX (C2PA), zxIf, vendor and private chunks
            }
        }
        return new MetadataFound(exif, gps, xmp, icc, text, other);
    }

    private static byte[]? StripPng(ReadOnlySpan<byte> d)
    {
        var (chunks, complete, _) = PngChunks(d);
        if (!complete) return null;
        // An eXIf orientation is kept as an orientation-only eXIf before the first IDAT, as the PNG specification requires.
        var orientation = 1;
        foreach (var (type, off, len) in chunks)
            if (type == "eXIf") { orientation = TiffOrientation(d.Slice(off + 8, len - 12)); break; }
        var o = new MemoryStream(d.Length);
        o.Write(d[..8]);
        foreach (var (type, off, len) in chunks)
        {
            if (type == "IDAT" && orientation is >= 2 and <= 8)
            {
                WritePngChunk(o, "eXIf", OrientationOnlyExif(orientation));
                orientation = 1;
            }
            if (PngKeep.Contains(type)) o.Write(d.Slice(off, len));
        }
        return o.ToArray();
    }

    private static void WritePngChunk(MemoryStream o, string type, byte[] data)
    {
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];
        var crc = ContainerCheck.Crc32(typeAndData);
        o.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
        o.Write(typeAndData);
        o.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    private static List<string> PngResidue(ReadOnlySpan<byte> d)
    {
        var residue = new List<string>();
        var (chunks, complete, trailing) = PngChunks(d);
        if (!complete) residue.Add("structure does not reach IEND");
        if (trailing) residue.Add("data after IEND");
        foreach (var (type, off, len) in chunks)
            if (!PngKeep.Contains(type) && !(type == "eXIf" && IsOrientationOnly(d.Slice(off + 8, len - 12))))
                residue.Add($"chunk {type} at {off}");
        return residue;
    }

    // ---------------------------------------------------------------- WebP

    private static readonly HashSet<string> WebPKeep = ["VP8X", "VP8 ", "VP8L", "ALPH", "ICCP"];

    /// <summary>Chunks inside the declared RIFF size. Complete when they fill it exactly; Trailing if bytes follow the RIFF.</summary>
    private static (List<(string Id, int Offset, int Length)> Chunks, bool Complete, bool Trailing) WebPChunks(ReadOnlySpan<byte> d)
    {
        var list = new List<(string, int, int)>();
        if (FileSignature.Detect(d) != ImageFormat.WebP || d.Length < 12) return (list, false, false);
        var riffEnd = 8L + (uint)(d[4] | d[5] << 8 | d[6] << 16 | d[7] << 24);
        if (riffEnd > d.Length) return (list, false, false);
        var i = 12L;
        while (i + 8 <= riffEnd)
        {
            var at = (int)i;
            var size = (long)(uint)(d[at + 4] | d[at + 5] << 8 | d[at + 6] << 16 | d[at + 7] << 24);
            var total = 8 + size + (size & 1);
            if (i + total > riffEnd) return (list, false, riffEnd < d.Length);
            list.Add((Encoding.ASCII.GetString(d.Slice(at, 4)), at, (int)total));
            i += total;
        }
        return (list, i == riffEnd, riffEnd < d.Length);
    }

    private static MetadataFound InspectWebP(ReadOnlySpan<byte> d)
    {
        bool exif = false, gps = false, xmp = false, icc = false, other = false;
        var (chunks, complete, trailing) = WebPChunks(d);
        other |= trailing || (!complete && chunks.Count > 0);
        foreach (var (id, off, len) in chunks)
        {
            switch (id)
            {
                case "EXIF":
                    var tiff = ExifPayload(d, off, len);
                    exif |= !IsOrientationOnly(tiff);
                    gps |= TiffHasTag(tiff, 34853);
                    break;
                case "XMP ": xmp = true; break;
                case "ICCP": icc = true; break;
                default: other |= !WebPKeep.Contains(id); break; // C2PA, vendor and unknown chunks
            }
        }
        return new MetadataFound(exif, gps, xmp, icc, false, other);
    }

    /// <summary>The TIFF block of a WebP EXIF chunk: its declared size (no padding), without an optional "Exif\0\0" prefix.</summary>
    private static ReadOnlySpan<byte> ExifPayload(ReadOnlySpan<byte> d, int off, int len)
    {
        var size = (int)Math.Min(len - 8, (uint)(d[off + 4] | d[off + 5] << 8 | d[off + 6] << 16 | d[off + 7] << 24));
        var p = d.Slice(off + 8, size);
        return p.StartsWith(ExifId) ? p[6..] : p;
    }

    private static byte[]? StripWebP(ReadOnlySpan<byte> d, int orientation)
    {
        var (chunks, complete, _) = WebPChunks(d);
        if (!complete) return null;
        var hasVp8X = chunks.Count > 0 && chunks[0].Id == "VP8X" && chunks[0].Length >= 18;
        var keepOrientation = hasVp8X && orientation is >= 2 and <= 8; // EXIF needs the extended (VP8X) layout
        var body = new MemoryStream(d.Length);
        body.Write("WEBP"u8);
        foreach (var (id, off, len) in chunks)
        {
            if (!WebPKeep.Contains(id)) continue;
            var chunk = d.Slice(off, len).ToArray();
            if (id == "VP8X") chunk[8] = (byte)(chunk[8] & ~0x0C | (keepOrientation ? 0x08 : 0)); // XMP off; EXIF only if kept
            body.Write(chunk);
        }
        if (keepOrientation)
        {
            var exif = OrientationOnlyExif(orientation); // after the image data, where the container places EXIF
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

    private static List<string> WebPResidue(ReadOnlySpan<byte> d)
    {
        var residue = new List<string>();
        var (chunks, complete, trailing) = WebPChunks(d);
        if (!complete) residue.Add("chunks do not fill the RIFF size");
        if (trailing) residue.Add("data after the RIFF");
        foreach (var (id, off, len) in chunks)
            if (!WebPKeep.Contains(id) && !(id == "EXIF" && IsOrientationOnly(ExifPayload(d, off, len))))
                residue.Add($"chunk {id.TrimEnd()} at {off}");
        if (chunks.Count > 0 && chunks[0].Id == "VP8X" && chunks[0].Length >= 18 && (d[chunks[0].Offset + 8] & 0x04) != 0)
            residue.Add("VP8X still announces XMP");
        return residue;
    }

    // ---------------------------------------------------------------- TIFF structures (standalone TIFF or EXIF payload)

    private static readonly ushort[] TiffTextTags = [270, 271, 272, 305, 306, 315, 316, 33432, 37510];

    /// <summary>A standalone TIFF: the EXIF and GPS IFD pointers, XMP, ICC, IPTC/Photoshop and descriptive text tags in IFD0.</summary>
    private static MetadataFound InspectTiff(ReadOnlySpan<byte> t)
    {
        if (t.Length < 8 || !(t.StartsWith("II*\0"u8) || t.StartsWith("MM\0*"u8))) return default;
        var text = false;
        foreach (var tag in TiffTextTags) text |= TiffHasTag(t, tag);
        var other = TiffHasTag(t, 33723) || TiffHasTag(t, 34377); // IPTC, Photoshop resources
        return new MetadataFound(TiffHasTag(t, 34665), TiffHasTag(t, 34853), TiffHasTag(t, 700), TiffHasTag(t, 34675), text, other);
    }

    /// <summary>The Orientation tag (SHORT) of IFD0, or 1 when absent or malformed.</summary>
    private static int TiffOrientation(ReadOnlySpan<byte> t)
    {
        if (t.Length < 8 || !(t.StartsWith("II*\0"u8) || t.StartsWith("MM\0*"u8))) return 1;
        var le = t[0] == 0x49;
        var ifd = Read32(t, 4, le);
        if (ifd < 8 || (long)ifd + 2 > t.Length) return 1; // long: an offset near int.MaxValue must not wrap past the check
        var n = Read16(t, ifd, le);
        for (var i = 0; i < n && ifd + 2 + (i + 1) * 12 <= t.Length; i++)
        {
            var e = ifd + 2 + i * 12;
            if (Read16(t, e, le) == 0x0112 && Read16(t, e + 2, le) == 3)
                return Read16(t, e + 8, le) is var v and >= 1 and <= 8 ? v : 1;
        }
        return 1;
    }

    private static bool TiffHasTag(ReadOnlySpan<byte> t, ushort tag)
    {
        if (t.Length < 8 || !(t.StartsWith("II*\0"u8) || t.StartsWith("MM\0*"u8))) return false;
        var le = t[0] == 0x49;
        var ifd = Read32(t, 4, le);
        if (ifd < 8 || (long)ifd + 2 > t.Length) return false;
        var n = Read16(t, ifd, le);
        for (var i = 0; i < n && ifd + 2 + (i + 1) * 12 <= t.Length; i++)
            if (Read16(t, ifd + 2 + i * 12, le) == tag) return true;
        return false;
    }

    private static int Read16(ReadOnlySpan<byte> b, int o, bool le) => le ? b[o] | b[o + 1] << 8 : b[o] << 8 | b[o + 1];

    private static int Read32(ReadOnlySpan<byte> b, int o, bool le) =>
        (int)Math.Min(int.MaxValue, le ? (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24) : (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]));
}
