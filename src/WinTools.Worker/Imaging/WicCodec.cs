using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// PNG, JPEG, BMP and TIFF through the built-in Windows Imaging Component codecs, created by CLSID (Microsoft's codec,
/// never a third-party one registered for the same container; ADR-0006). Entirely in memory: file I/O stays in .NET.
/// </summary>
internal static class WicCodec
{
    public static bool Handles(ImageFormat format) => format is ImageFormat.Png or ImageFormat.Jpeg or ImageFormat.Bmp or ImageFormat.Tiff;

    private static (Guid Decoder, Guid Encoder, Guid Container) Ids(ImageFormat format) => format switch
    {
        ImageFormat.Png => (WicIds.PngDecoder, WicIds.PngEncoder, WicIds.ContainerPng),
        ImageFormat.Jpeg => (WicIds.JpegDecoder, WicIds.JpegEncoder, WicIds.ContainerJpeg),
        ImageFormat.Bmp => (WicIds.BmpDecoder, WicIds.BmpEncoder, WicIds.ContainerBmp),
        ImageFormat.Tiff => (WicIds.TiffDecoder, WicIds.TiffEncoder, WicIds.ContainerTiff),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Decodes the first frame to BGRA. Returns the EXIF orientation (1 if none) for the caller to apply.</summary>
    public static unsafe (RasterImage Image, int Orientation) Decode(byte[] bytes, ImageFormat format)
    {
        using var com = new ComScope();
        try
        {
            var stream = com.Add(WicNative.CreateStreamOnHGlobal(0, deleteOnRelease: true));
            stream.Write(bytes, bytes.Length, 0);
            stream.Seek(0, 0 /* STREAM_SEEK_SET */, 0);
            var ids = Ids(format);
            var decoder = com.Add(WicNative.Create<IWICBitmapDecoder>(ids.Decoder));
            decoder.Initialize(stream, WicDecodeOptions.MetadataCacheOnDemand);
            if (decoder.GetContainerFormat() != ids.Container) throw new ImagingException(ErrorCode.DecodeFailed, "Decoder reported an unexpected container format.");
            var frames = decoder.GetFrameCount();
            if (frames == 0) throw new ImagingException(ErrorCode.DecodeFailed, "Image has no frames.");
            // Converting only the first page or frame would silently lose the rest (owner decision 2026-10-21: refuse).
            if (frames > 1) throw new ImagingException(ErrorCode.UnsupportedContent, $"This file has {frames} pages or frames; multi-page images are not supported yet.");
            if (format == ImageFormat.Png && ContainerCheck.IsAnimatedPng(bytes)) throw new ImagingException(ErrorCode.UnsupportedContent, "Animated PNG is not supported yet.");
            var frame = com.Add(decoder.GetFrame(0));
            frame.GetSize(out var width, out var height);
            var pixels = RasterImage.AllocateFor(width, height); // limits before any pixel work

            if (!ContainerCheck.IsComplete(bytes, format, out var damage))
                throw new ImagingException(ErrorCode.DecodeFailed, $"The file is truncated or damaged ({damage}).");

            frame.GetResolution(out var dpiX, out var dpiY);
            var orientation = format is ImageFormat.Jpeg or ImageFormat.Tiff ? ReadOrientation(com, frame, format) : 1;
            var bgra = com.Add(Factory(com).CreateFormatConverter());
            bgra.Initialize((IWICBitmapSource)frame, WicIds.Format32bppBGRA, WicDitherType.None, 0, 0, WicPaletteType.Custom);
            fixed (byte* p = pixels) bgra.CopyPixels(null, width * 4, (uint)pixels.Length, p); // one sequential decode
            DecoderDiagnostics.ThrowIfWarned();
            return (new RasterImage((int)width, (int)height, pixels, dpiX, dpiY, ReadIccProfile(com, frame)), orientation);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x8007000E)) { throw new ImagingException(ErrorCode.ImageTooLarge, "Not enough memory to decode this image.", ex); }
        catch (COMException ex) { throw new ImagingException(ErrorCode.DecodeFailed, $"The file could not be decoded (0x{ex.HResult:X8}).", ex); }
    }

    /// <summary>
    /// Encodes BGRA. Formats that keep alpha get 32-bit BGRA only when some pixel is transparent; everything else is
    /// 24-bit BGR, composited onto white. <paramref name="jpegQuality"/> (0–1) sets the JPEG encoder's ImageQuality.
    /// </summary>
    public static unsafe byte[] Encode(RasterImage image, ImageFormat format, float? jpegQuality = null, bool fullChroma = false)
    {
        using var com = new ComScope();
        try
        {
            var stream = new GrowableStream();
            var encoder = com.Add(WicNative.Create<IWICBitmapEncoder>(Ids(format).Encoder));
            encoder.Initialize(stream, WicEncoderCacheOption.NoCache);
            encoder.CreateNewFrame(out var frameEncode, out var options);
            com.Add(frameEncode);
            try
            {
                if (jpegQuality is { } q && format == ImageFormat.Jpeg && options != 0) PropertyBag.WriteFloat(options, "ImageQuality", Math.Clamp(q, 0f, 1f));
                // 4:4:4 keeps full colour resolution when a JPEG is only rotated or resized (the default is 4:2:0).
                if (fullChroma && format == ImageFormat.Jpeg && options != 0) PropertyBag.WriteByte(options, "JpegYCrCbSubsampling", 3 /* WICJpegYCrCbSubsampling444 */);
                // The TIFF encoder's default ("don't care") compression takes minutes on photographic content; Deflate is
                // lossless, widely read, and fast.
                if (format == ImageFormat.Tiff && options != 0) PropertyBag.WriteByte(options, "TiffCompressionMethod", 6 /* WICTiffCompressionZIP */);
                frameEncode.Initialize(options);
            }
            finally { if (options != 0) Marshal.Release(options); }
            frameEncode.SetSize((uint)image.Width, (uint)image.Height);
            frameEncode.SetResolution(image.DpiX, image.DpiY);
            var alpha = ImageFormats.KeepsAlpha(format) && image.HasTransparency();
            var wanted = alpha ? WicIds.Format32bppBGRA : WicIds.Format24bppBGR;
            var pf = wanted;
            frameEncode.SetPixelFormat(ref pf);
            if (pf != wanted) throw new ImagingException(ErrorCode.EncodeFailed, "The encoder refused the pixel format.");
            if (image.IccProfile is { Length: > 0 } icc && format != ImageFormat.Bmp) WriteIccProfile(com, frameEncode, icc);

            const int band = 64;
            var w = image.Width;
            if (alpha)
            {
                fixed (byte* p = image.Bgra)
                    for (var y = 0; y < image.Height; y += band)
                    {
                        var rows = Math.Min(band, image.Height - y);
                        frameEncode.WritePixels((uint)rows, (uint)(w * 4), (uint)(w * 4 * rows), p + (long)y * w * 4);
                    }
            }
            else
            {
                var bgr = new byte[w * 3 * band];
                fixed (byte* p = bgr)
                    for (var y = 0; y < image.Height; y += band)
                    {
                        var rows = Math.Min(band, image.Height - y);
                        RasterImage.CompositeOnWhite(image.Bgra.AsSpan(y * w * 4, rows * w * 4), bgr.AsSpan(0, rows * w * 3));
                        frameEncode.WritePixels((uint)rows, (uint)(w * 3), (uint)(w * 3 * rows), p);
                    }
            }
            frameEncode.Commit();
            encoder.Commit();
            var bytes = stream.ToArray();
            if (bytes.Length == 0) throw new ImagingException(ErrorCode.EncodeFailed, "Encoder produced no data.");
            return bytes;
        }
        catch (COMException ex) { throw new ImagingException(ErrorCode.EncodeFailed, $"Encoding failed (0x{ex.HResult:X8}).", ex); }
    }

    /// <summary>EXIF orientation tag 274 (JPEG: APP1 IFD0; TIFF: IFD0). Missing or invalid means 1.</summary>
    private static unsafe int ReadOrientation(ComScope com, IWICBitmapFrameDecode frame, ImageFormat format)
    {
        if (frame.GetMetadataQueryReader(out var reader) < 0 || reader is null) return 1;
        com.Add(reader);
        PropVariant value = default;
        try
        {
            var query = format == ImageFormat.Jpeg ? "/app1/ifd/{ushort=274}" : "/ifd/{ushort=274}";
            if (reader.GetMetadataByName(query, &value) < 0 || value.Vt != PropVariant.VtUi2) return 1;
            return value.UInt16 is >= 1 and <= 8 ? value.UInt16 : 1;
        }
        finally { _ = WicNative.PropVariantClear(&value); }
    }

    /// <summary>The frame's embedded ICC profile bytes, or null (no profile, or only an EXIF colour-space tag).</summary>
    private static unsafe byte[]? ReadIccProfile(ComScope com, IWICBitmapFrameDecode frame)
    {
        if (frame.GetColorContexts(0, null, out var count) < 0 || count == 0) return null;
        var contexts = new IWICColorContext[count];
        var raw = new nint[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                contexts[i] = com.Add(Factory(com).CreateColorContext());
                raw[i] = Marshal.GetComInterfaceForObject(contexts[i], typeof(IWICColorContext));
            }
            fixed (nint* p = raw) if (frame.GetColorContexts(count, p, out _) < 0) return null;
            foreach (var ctx in contexts)
            {
                if (ctx.GetType_() != 1 /* WICColorContextProfile */) continue;
                if (ctx.GetProfileBytes(0, null, out var size) < 0 || size == 0 || size > 16 * 1024 * 1024) continue;
                var bytes = new byte[size];
                fixed (byte* b = bytes) if (ctx.GetProfileBytes(size, b, out _) < 0) continue;
                return bytes;
            }
            return null;
        }
        finally { foreach (var r in raw) if (r != 0) Marshal.Release(r); }
    }

    private static unsafe void WriteIccProfile(ComScope com, IWICBitmapFrameEncode frameEncode, byte[] icc)
    {
        var ctx = com.Add(Factory(com).CreateColorContext());
        fixed (byte* p = icc) ctx.InitializeFromMemory(p, (uint)icc.Length);
        var raw = Marshal.GetComInterfaceForObject(ctx, typeof(IWICColorContext));
        try { frameEncode.SetColorContexts(1, &raw); }
        finally { Marshal.Release(raw); }
    }

    private static IWICImagingFactory Factory(ComScope com) => com.Factory ??= com.Add(WicNative.Create<IWICImagingFactory>(WicIds.ImagingFactory));

    /// <summary>Releases every COM object of one operation deterministically (decoders hold the whole input).</summary>
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = [];
        public IWICImagingFactory? Factory;

        public T Add<T>(T obj) where T : class { _objects.Add(obj); return obj; }

        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--) Marshal.FinalReleaseComObject(_objects[i]);
            _objects.Clear();
        }
    }
}

/// <summary>IPropertyBag2::Write for one encoder option (VT_R4 or VT_UI1), through the raw vtable (only this call is needed).</summary>
internal static unsafe class PropertyBag
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPBAG2 { public uint dwType; public ushort vt; public ushort cfType; public uint dwHint; public nint pstrName; public Guid clsid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct VARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public float fltVal; [FieldOffset(8)] public byte bVal; }

    public static void WriteByte(nint bag, string name, byte value) => Write(bag, name, new VARIANT { vt = 17 /* VT_UI1 */, bVal = value });

    public static void WriteFloat(nint bag, string name, float value) => Write(bag, name, new VARIANT { vt = 4 /* VT_R4 */, fltVal = value });

    private static void Write(nint bag, string name, VARIANT v)
    {
        var namePtr = Marshal.StringToCoTaskMemUni(name);
        try
        {
            var prop = new PROPBAG2 { vt = v.vt, pstrName = namePtr };
            var write = (delegate* unmanaged[Stdcall]<nint, uint, PROPBAG2*, VARIANT*, int>)(*(*(void***)bag + 4)); // IUnknown(3) + Read, Write
            var hr = write(bag, 1, &prop, &v);
            if (hr < 0) throw new ImagingException(ErrorCode.EncodeFailed, $"The encoder rejected option {name} (0x{hr:X8}).");
        }
        finally { Marshal.FreeCoTaskMem(namePtr); }
    }
}
