using System.IO.Compression;

namespace WinTools.Core.Files;

/// <summary>
/// Checks for embedded ICC profiles. Every output is RGB or RGBA, so a profile is carried only if it is a well-formed
/// RGB profile. A CMYK or gray profile (whose pixels were converted to RGB without it), or bytes that are not a profile
/// at all, would make the output describe its pixels wrongly, so those are dropped.
/// </summary>
public static class IccProfile
{
    public const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>
    /// A profile is kept if it is at least 132 bytes (the 128-byte header plus the tag count), its declared size matches
    /// its length, it carries the 'acsp' signature, and its data colour space is 'RGB '.
    /// </summary>
    public static bool IsUsableRgb(ReadOnlySpan<byte> p) =>
        p.Length is >= 132 and <= MaxBytes
        && (uint)(p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3]) == (uint)p.Length
        && p.Slice(36, 4).SequenceEqual("acsp"u8)
        && p.Slice(16, 4).SequenceEqual("RGB "u8);

    /// <summary>The profile inside a PNG iCCP chunk's data (name, 0, method 0, zlib stream), or null if it cannot be read.</summary>
    public static byte[]? FromPngIccp(ReadOnlySpan<byte> chunkData)
    {
        var nul = chunkData.IndexOf((byte)0);
        if (nul is < 1 or > 79 || chunkData.Length < nul + 3 || chunkData[nul + 1] != 0) return null;
        try
        {
            using var z = new ZLibStream(new MemoryStream(chunkData[(nul + 2)..].ToArray()), CompressionMode.Decompress);
            var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = z.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, n);
                if (output.Length > MaxBytes) return null;
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }
}
