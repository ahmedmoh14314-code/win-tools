using System.Globalization;
using System.IO.Compression;
using System.Text;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Pdf;

/// <summary>
/// Writes a PDF of pictures, one page per picture, each page the picture's own size (its resolution, 96 dpi when it has
/// none, kept within PDF's 200-inch limit). A JPEG whose pixels need no change (8-bit grey or colour, upright) is
/// embedded as it is, losslessly; any other picture is written as 8-bit RGB, composited on white, Flate-compressed.
/// The file is written as it goes, so memory follows one picture. Nothing else goes in: no metadata, no names.
/// </summary>
internal sealed class ImagePdfWriter(Stream output)
{
    private const double MaxPoints = 14_400; // 200 inches, PDF's largest page
    private readonly List<long> _offsets = [0, 0, 0]; // objects 1 (catalog) and 2 (pages) are written last; [0] is unused
    private readonly List<int> _pages = [];
    private long _position;

    public void Begin() => Write("%PDF-1.7\n%âãÏÓ\n", latin1: true);

    /// <summary>Adds one page for a decoded picture; <paramref name="jpeg"/> is its original JPEG when it may be embedded as it is.</summary>
    public void AddPage(RasterImage image, byte[]? jpeg, int jpegComponents)
    {
        var (w, h) = PageSize(image);
        var imageObj = NewObject();
        if (jpeg is not null)
        {
            WriteStreamObject(imageObj, string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /{(jpegComponents == 1 ? "DeviceGray" : "DeviceRGB")} /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>"), jpeg);
        }
        else
        {
            var rgb = Flatten(image);
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(rgb);
            var data = packed.ToArray();
            WriteStreamObject(imageObj, string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {data.Length} >>"), data);
        }
        var content = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"q {w:0.###} 0 0 {h:0.###} 0 0 cm /Im0 Do Q"));
        var contentObj = NewObject();
        WriteStreamObject(contentObj, string.Create(CultureInfo.InvariantCulture, $"<< /Length {content.Length} >>"), content);
        var pageObj = NewObject();
        WriteObject(pageObj, string.Create(CultureInfo.InvariantCulture,
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w:0.###} {h:0.###}] /Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>"));
        _pages.Add(pageObj);
    }

    public void End()
    {
        WriteObject(2, $"<< /Type /Pages /Kids [{string.Join(" ", _pages.Select(p => p.ToString(CultureInfo.InvariantCulture) + " 0 R"))}] /Count {_pages.Count} >>");
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        var xref = _position;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {_offsets.Count}\n0000000000 65535 f \n");
        for (var i = 1; i < _offsets.Count; i++) sb.Append(CultureInfo.InvariantCulture, $"{_offsets[i]:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
    }

    /// <summary>The page size in points: the picture at its resolution, scaled down to fit 200 inches.</summary>
    public static (double W, double H) PageSize(RasterImage image)
    {
        var w = image.Width * 72.0 / (image.DpiX > 0 ? image.DpiX : 96);
        var h = image.Height * 72.0 / (image.DpiY > 0 ? image.DpiY : 96);
        var scale = Math.Min(1, Math.Min(MaxPoints / w, MaxPoints / h));
        return (Math.Max(1, w * scale), Math.Max(1, h * scale));
    }

    /// <summary>RGB rows of a BGRA picture composited on white.</summary>
    private static byte[] Flatten(RasterImage image)
    {
        var src = image.Bgra;
        var rgb = new byte[(long)image.Width * image.Height * 3];
        for (long i = 0, o = 0; i < src.Length; i += 4, o += 3)
        {
            var a = src[i + 3];
            rgb[o] = (byte)((src[i + 2] * a + 255 * (255 - a) + 127) / 255);
            rgb[o + 1] = (byte)((src[i + 1] * a + 255 * (255 - a) + 127) / 255);
            rgb[o + 2] = (byte)((src[i] * a + 255 * (255 - a) + 127) / 255);
        }
        return rgb;
    }

    /// <summary>
    /// The colour components of a baseline or progressive 8-bit JPEG (1 grey, 3 colour), or 0 when it must be re-encoded
    /// (CMYK, 12-bit, lossless or arithmetic-coded, or a frame header that cannot be found).
    /// </summary>
    public static int EmbeddableJpegComponents(ReadOnlySpan<byte> jpeg)
    {
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) return 0;
            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2) return 0;
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                var precision = jpeg[i + 4];
                var components = jpeg[i + 9];
                return precision == 8 && components is 1 or 3 ? components : 0;
            }
            if (marker is >= 0xC3 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return 0; // lossless, hierarchical, arithmetic
            if (marker == 0xDA) return 0; // scan data before any frame header
            i += 2 + length;
        }
        return 0;
    }

    private int NewObject()
    {
        _offsets.Add(0);
        return _offsets.Count - 1;
    }

    private void WriteObject(int number, string body)
    {
        _offsets[number] = _position;
        Write(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n{body}\nendobj\n"));
    }

    private void WriteStreamObject(int number, string dictionary, ReadOnlySpan<byte> data)
    {
        _offsets[number] = _position;
        Write(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n{dictionary}\nstream\n"));
        output.Write(data);
        _position += data.Length;
        Write("\nendstream\nendobj\n");
    }

    private void Write(string text, bool latin1 = false)
    {
        var bytes = latin1 ? Encoding.Latin1.GetBytes(text) : Encoding.ASCII.GetBytes(text);
        output.Write(bytes);
        _position += bytes.Length;
    }
}
