using SkiaSharp;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// WebP through SkiaSharp's bundled libwebp (Windows has no WebP encoder, and its decoder is an optional Store
/// extension). Loaded only in the worker. Animated WebP is refused. WebP has no checksum over its payload, so damage
/// inside a structurally complete file can decode to wrong pixels undetected (documented in ADR-0006).
/// </summary>
internal static class SkiaCodec
{
    /// <summary>Default lossy quality for a plain conversion to WebP (0–100).</summary>
    public const int DefaultWebPQuality = 90;

    public static unsafe (RasterImage Image, int Orientation) DecodeWebP(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new ImagingException(ErrorCode.DecodeFailed, "The file could not be decoded.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Webp) throw new ImagingException(ErrorCode.DecodeFailed, "The decoder found a different format.");
        if (codec.FrameCount > 1) throw new ImagingException(ErrorCode.UnsupportedContent, "Animated WebP is not supported yet.");
        var size = codec.Info;
        var pixels = RasterImage.AllocateFor(size.Width, size.Height);
        if (!ContainerCheck.IsComplete(bytes, ImageFormat.WebP, out var damage))
            throw new ImagingException(ErrorCode.DecodeFailed, $"The file is truncated or damaged ({damage}).");
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        SKCodecResult result;
        fixed (byte* p = pixels) result = codec.GetPixels(info, (nint)p);
        if (result != SKCodecResult.Success)
            throw new ImagingException(ErrorCode.DecodeFailed, result == SKCodecResult.IncompleteInput ? "The file is truncated." : $"The file could not be decoded ({result}).");
        var origin = (int)codec.EncodedOrigin; // SKEncodedOrigin uses the EXIF values 1–8
        return (new RasterImage(size.Width, size.Height, pixels, iccProfile: WebPChunk(bytes, "ICCP"u8)), origin is >= 1 and <= 8 ? origin : 1);
    }

    /// <summary>True if the WebP's image data is lossless (a VP8L bitstream, directly or after a VP8X header).</summary>
    public static bool IsLosslessWebP(ReadOnlySpan<byte> bytes)
    {
        for (var i = 12; i + 8 <= bytes.Length;)
        {
            var id = bytes.Slice(i, 4);
            if (id.SequenceEqual("VP8L"u8)) return true;
            if (id.SequenceEqual("VP8 "u8)) return false;
            var size = (long)(uint)(bytes[i + 4] | bytes[i + 5] << 8 | bytes[i + 6] << 16 | bytes[i + 7] << 24);
            if (size > bytes.Length) return false;
            i += 8 + (int)size + (int)(size & 1);
        }
        return false;
    }

    /// <summary>The payload of the first RIFF chunk with this id, or null.</summary>
    internal static byte[]? WebPChunk(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> id)
    {
        for (var i = 12; i + 8 <= bytes.Length;)
        {
            var size = (long)(uint)(bytes[i + 4] | bytes[i + 5] << 8 | bytes[i + 6] << 16 | bytes[i + 7] << 24);
            if (i + 8 + size > bytes.Length) return null;
            if (bytes.Slice(i, 4).SequenceEqual(id)) return bytes.Slice(i + 8, (int)size).ToArray();
            i += 8 + (int)size + (int)(size & 1);
        }
        return null;
    }

    /// <summary>
    /// PNG with the strongest lossless settings (zlib level 9, every filter tried), for "compress lossless". The
    /// source's ancillary chunks (colour: gAMA, cHRM, sRGB, iCCP; pHYs; text; eXIf; tIME) are copied verbatim, so only
    /// the compression of the image data changes.
    /// </summary>
    public static byte[] EncodePngSmallest(RasterImage image, ReadOnlySpan<byte> sourcePng)
    {
        var encoded = EncodePngPixels(image);
        return PngChunks.CopyAncillary(sourcePng, encoded);
    }

    private static unsafe byte[] EncodePngPixels(RasterImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, image.HasTransparency() ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
        fixed (byte* p = image.Bgra)
        {
            using var pixmap = new SKPixmap(info, (nint)p, image.Width * 4);
            using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9))
                ?? throw new ImagingException(ErrorCode.EncodeFailed, "The PNG encoder produced no data.");
            return data.ToArray();
        }
    }

    /// <summary>Encodes BGRA as WebP: lossy at <paramref name="quality"/> (0–100, alpha kept), or lossless.</summary>
    public static unsafe byte[] EncodeWebP(RasterImage image, int quality, bool lossless)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        fixed (byte* p = image.Bgra)
        {
            using var pixmap = new SKPixmap(info, (nint)p, image.Width * 4);
            var options = new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, Math.Clamp(quality, 0, 100));
            using var data = pixmap.Encode(options) ?? throw new ImagingException(ErrorCode.EncodeFailed, "The WebP encoder produced no data.");
            var webp = data.ToArray();
            // The pixels stay in the source's colour space: embed its ICC profile byte for byte (Skia would re-serialise it).
            return image.IccProfile is { Length: > 0 } icc ? WithIccProfile(webp, icc, image.Width, image.Height, image.HasTransparency()) : webp;
        }
    }

    /// <summary>
    /// Adds an ICCP chunk to a WebP produced without one. Simple files get a VP8X header first. The ICC flag is set,
    /// ICCP goes right after VP8X (the order the format requires), and the RIFF size is updated.
    /// </summary>
    internal static byte[] WithIccProfile(byte[] webp, byte[] icc, int width, int height, bool hasAlpha)
    {
        var body = new MemoryStream();
        var hasVp8x = webp.AsSpan(12, 4).SequenceEqual("VP8X"u8);
        byte[] vp8x;
        int rest;
        if (hasVp8x)
        {
            vp8x = webp.AsSpan(12, 18).ToArray();
            rest = 30;
        }
        else
        {
            vp8x = new byte[18];
            "VP8X"u8.CopyTo(vp8x);
            vp8x[4] = 10;
            vp8x[8] = (byte)(hasAlpha ? 0x10 : 0);
            int w = width - 1, h = height - 1;
            (vp8x[12], vp8x[13], vp8x[14]) = ((byte)w, (byte)(w >> 8), (byte)(w >> 16));
            (vp8x[15], vp8x[16], vp8x[17]) = ((byte)h, (byte)(h >> 8), (byte)(h >> 16));
            rest = 12;
        }
        vp8x[8] |= 0x20; // ICC profile present
        body.Write("WEBP"u8);
        body.Write(vp8x);
        body.Write("ICCP"u8);
        body.Write([(byte)icc.Length, (byte)(icc.Length >> 8), (byte)(icc.Length >> 16), (byte)(icc.Length >> 24)]);
        body.Write(icc);
        if ((icc.Length & 1) != 0) body.WriteByte(0);
        body.Write(webp, rest, webp.Length - rest);
        var size = (int)body.Length;
        var output = new byte[8 + size];
        "RIFF"u8.CopyTo(output);
        (output[4], output[5], output[6], output[7]) = ((byte)size, (byte)(size >> 8), (byte)(size >> 16), (byte)(size >> 24));
        body.ToArray().CopyTo(output, 8);
        return output;
    }
}
