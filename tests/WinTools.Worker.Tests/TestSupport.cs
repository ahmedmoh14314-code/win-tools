using System.IO.Compression;
using System.Security.Cryptography;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>A fresh folder per test, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wintools-tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string CopySample(string sample, string? asName = null)
    {
        var dest = System.IO.Path.Combine(Path, asName ?? sample);
        File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "samples", sample), dest);
        return dest;
    }

    public string Write(string name, byte[] bytes)
    {
        var dest = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(dest, bytes);
        return dest;
    }

    public string[] Names() => Directory.GetFiles(Path).Select(f => System.IO.Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class Hash
{
    public static string Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

/// <summary>Decodes a file to straight BGRA pixels with the system codecs, for asserting colours in outputs.</summary>
public sealed record Pixels(int Width, int Height, byte[] Bgra)
{
    public (int R, int G, int B, int A) At(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i], Bgra[i + 3]);
    }

    public static Pixels Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var image = ImagePipeline.Decode(bytes, WinTools.Core.Files.FileSignature.Detect(bytes)).Image; // stored orientation, no EXIF applied
        return new Pixels(image.Width, image.Height, image.Bgra);
    }
}

/// <summary>Minimal PNG writer (RGBA 8-bit, filter 0) independent of WIC, for crafted inputs.</summary>
public static class PngWriter
{
    public static byte[] Rgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++) { var (r, g, b, a) = pixel(x, y); raw.Write([r, g, b, a]); }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zs);
        return Build(width, height, z.ToArray());
    }

    /// <summary>Re-packs a PNG so its IDAT holds only the first <paramref name="fraction"/> of the zlib stream, with valid lengths and CRCs.</summary>
    public static byte[] WithTruncatedImageData(byte[] png, double fraction)
    {
        var width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
        var height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
        var idat = new MemoryStream();
        for (var i = 8; i + 8 <= png.Length;)
        {
            var len = png[i] << 24 | png[i + 1] << 16 | png[i + 2] << 8 | png[i + 3];
            if (System.Text.Encoding.ASCII.GetString(png, i + 4, 4) == "IDAT") idat.Write(png, i + 8, len);
            i += 12 + len;
        }
        var z = idat.ToArray();
        return Build(width, height, z[..(int)(z.Length * fraction)]);
    }

    /// <summary>A PNG whose header claims the given size but carries only a token of image data.</summary>
    public static byte[] HeaderOnly(int width, int height) => Build(width, height, [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);

    private static byte[] Build(int width, int height, byte[] idat)
    {
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)width); BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BigEndian(len, 0, (uint)data.Length); s.Write(len);
        var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typeAndData);
        var crc = new byte[4]; BigEndian(crc, 0, Crc32(typeAndData)); s.Write(crc);
    }

    private static void BigEndian(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var d in data)
        {
            c ^= d;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>Inserts a minimal EXIF APP1 segment carrying only the orientation tag right after the JPEG SOI marker.</summary>
public static class ExifWriter
{
    public static byte[] WithOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // "II", 42, IFD0 at offset 8
            0x01, 0x00,                                     // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, // tag 0x0112, SHORT, count 1
            (byte)orientation, (byte)(orientation >> 8), 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                         // no next IFD
        ];
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var segLen = payload.Length + 2;
        byte[] app1 = [0xFF, 0xE1, (byte)(segLen >> 8), (byte)segLen, .. payload];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }
}
