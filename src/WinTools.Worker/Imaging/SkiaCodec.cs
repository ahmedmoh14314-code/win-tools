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
        return (new RasterImage(size.Width, size.Height, pixels), origin is >= 1 and <= 8 ? origin : 1);
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

    /// <summary>PNG with the strongest lossless settings (zlib level 9, every filter tried), for "compress lossless".</summary>
    public static unsafe byte[] EncodePngSmallest(RasterImage image)
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
            return data.ToArray();
        }
    }
}
