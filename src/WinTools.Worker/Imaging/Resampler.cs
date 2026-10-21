using System.Runtime.InteropServices;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// High-quality resize: WIC's HighQualityCubic scaler (a wide, area-aware kernel for downscaling; Windows 10+) on
/// premultiplied pixels, so transparent neighbours do not bleed dark or wrong colours into edges.
/// </summary>
internal static class Resampler
{
    public static unsafe RasterImage Resize(RasterImage source, int width, int height)
    {
        var target = RasterImage.AllocateFor(width, height);
        var premultiplied = (byte[])source.Bgra.Clone();
        Premultiply(premultiplied);
        object? factory = null, bitmap = null, scaler = null;
        try
        {
            var f = WicNative.Create<IWICImagingFactory>(WicIds.ImagingFactory);
            factory = f;
            IWICBitmapSource b;
            fixed (byte* p = premultiplied)
                b = f.CreateBitmapFromMemory((uint)source.Width, (uint)source.Height, WicIds.Format32bppPBGRA, (uint)source.Width * 4, (uint)premultiplied.Length, p);
            bitmap = b;
            var s = f.CreateBitmapScaler();
            scaler = s;
            s.Initialize(b, (uint)width, (uint)height, WicInterpolationMode.HighQualityCubic);
            fixed (byte* p = target) s.CopyPixels(null, (uint)width * 4, (uint)target.Length, p);
        }
        catch (COMException ex) { throw new ImagingException(ErrorCode.EncodeFailed, $"Resizing failed (0x{ex.HResult:X8}).", ex); }
        finally
        {
            foreach (var o in new[] { scaler, bitmap, factory }) if (o is not null) Marshal.FinalReleaseComObject(o);
        }
        Unpremultiply(target);
        // Resolution is kept: a resized photo prints smaller at the same DPI, which is what image editors do too.
        return new RasterImage(width, height, target, source.DpiX, source.DpiY);
    }

    internal static void Premultiply(Span<byte> bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            bgra[i] = (byte)((bgra[i] * a + 127) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 127) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 127) / 255);
        }
    }

    internal static void Unpremultiply(Span<byte> bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a is 255 or 0) continue;
            bgra[i] = (byte)Math.Min(255, (bgra[i] * 255 + a / 2) / a);
            bgra[i + 1] = (byte)Math.Min(255, (bgra[i + 1] * 255 + a / 2) / a);
            bgra[i + 2] = (byte)Math.Min(255, (bgra[i + 2] * 255 + a / 2) / a);
        }
    }
}
