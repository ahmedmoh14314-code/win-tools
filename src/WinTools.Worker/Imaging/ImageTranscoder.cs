using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>A failure with the code to report across the process boundary.</summary>
internal sealed class ImagingException(ErrorCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public ErrorCode Code { get; } = code;
}

/// <summary>
/// PNG ↔ JPEG through the built-in Windows Imaging Component codecs, entirely in memory (file I/O stays in .NET, which
/// handles long and Unicode paths). Policies for this slice:
/// <list type="bullet">
/// <item>PNG → JPEG: transparency is composited onto opaque white (JPEG has no alpha). Encoder default quality.</item>
/// <item>JPEG → PNG: the EXIF orientation tag is applied to the pixels, since PNG viewers do not read it.</item>
/// <item>Only the first frame is converted. Metadata (EXIF, ICC profiles) is not copied.</item>
/// </list>
/// </summary>
internal static class ImageTranscoder
{
    public const long MaxPixels = 100_000_000;
    public const uint MaxDimension = 65_535;
    private const int BandRows = 64;

    public readonly record struct Encoded(byte[] Bytes, uint Width, uint Height);

    public static Encoded Transcode(byte[] input, ImageFormat source, ImageFormat target)
    {
        using var com = new ComScope();
        var frame = DecodeFirstFrame(com, input, source, out var width, out var height); // header and size limits
        if (!ContainerCheck.IsComplete(input, source, out var damage))
            throw new ImagingException(ErrorCode.DecodeFailed, $"The file is truncated or damaged ({damage}).");
        frame.GetResolution(out var dpiX, out var dpiY);
        var transform = source == ImageFormat.Jpeg ? OrientationTransform(com, frame) : WicTransformOptions.Rotate0;

        // Decode every input pixel exactly once, sequentially, into memory. A truncated or corrupt file fails here and is
        // never half-encoded; the in-memory bitmap also gives the flip-rotator random access (on a decoder it re-reads
        // scanlines per pixel, which takes minutes for a rotated phone photo).
        IWICBitmapSource pixels;
        try
        {
            var converter = com.Add(Factory(com).CreateFormatConverter());
            converter.Initialize((IWICBitmapSource)frame, target == ImageFormat.Jpeg ? WicIds.Format32bppBGRA : WicIds.Format24bppBGR, WicDitherType.None, 0, 0, WicPaletteType.Custom);
            pixels = com.Add(Factory(com).CreateBitmapFromSource((IWICBitmapSource)converter, WicCacheOption.CacheOnLoad));
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x8007000E)) { throw new ImagingException(ErrorCode.ImageTooLarge, "Not enough memory to decode this image.", ex); }
        catch (OutOfMemoryException ex) { throw new ImagingException(ErrorCode.ImageTooLarge, "Not enough memory to decode this image.", ex); }
        catch (COMException ex) { throw new ImagingException(ErrorCode.DecodeFailed, $"The image data is corrupt or truncated (0x{ex.HResult:X8}).", ex); }
        DecoderDiagnostics.ThrowIfWarned();

        if (transform != WicTransformOptions.Rotate0)
        {
            try
            {
                var rotator = com.Add(Factory(com).CreateBitmapFlipRotator());
                rotator.Initialize(pixels, transform);
                pixels = (IWICBitmapSource)rotator;
                pixels.GetSize(out width, out height);
                if ((transform & WicTransformOptions.Rotate90) != 0) (dpiX, dpiY) = (dpiY, dpiX);
            }
            catch (COMException ex) { throw new ImagingException(ErrorCode.DecodeFailed, $"Orientation could not be applied (0x{ex.HResult:X8}).", ex); }
        }

        var stream = com.Add(WicNative.CreateStreamOnHGlobal(0, deleteOnRelease: true));
        var encoder = com.Add(WicNative.Create<IWICBitmapEncoder>(target == ImageFormat.Png ? WicIds.PngEncoder : WicIds.JpegEncoder));
        try
        {
            encoder.Initialize(stream, WicEncoderCacheOption.NoCache);
            encoder.CreateNewFrame(out var frameEncode, out var options);
            com.Add(frameEncode);
            try { frameEncode.Initialize(options); }
            finally { if (options != 0) Marshal.Release(options); }
            frameEncode.SetSize(width, height);
            frameEncode.SetResolution(SaneDpi(dpiX), SaneDpi(dpiY));
            var format = WicIds.Format24bppBGR;
            frameEncode.SetPixelFormat(ref format);
            if (format != WicIds.Format24bppBGR) throw new ImagingException(ErrorCode.EncodeFailed, "Encoder refused 24-bit BGR.");

            if (target == ImageFormat.Jpeg) WriteCompositedOnWhite(frameEncode, pixels, width, height);
            else frameEncode.WriteSource(pixels, 0);
            frameEncode.Commit();
            encoder.Commit();
        }
        catch (COMException ex) { throw new ImagingException(ErrorCode.EncodeFailed, $"Encoding failed (0x{ex.HResult:X8}).", ex); }

        return new Encoded(ReadAll(stream), width, height);
    }

    /// <summary>Decodes every pixel of an encoded image and checks its format and size. Throws on any mismatch.</summary>
    public static void Validate(byte[] encoded, ImageFormat format, uint expectedWidth, uint expectedHeight)
    {
        if (FileSignature.Detect(encoded) != format) throw new ImagingException(ErrorCode.OutputValidationFailed, "Output signature does not match the target format.");
        if (!ContainerCheck.IsComplete(encoded, format, out var damage)) throw new ImagingException(ErrorCode.OutputValidationFailed, $"Output is structurally incomplete ({damage}).");
        using var com = new ComScope();
        IWICBitmapFrameDecode frame;
        uint w, h;
        try { frame = DecodeFirstFrame(com, encoded, format, out w, out h); }
        catch (ImagingException ex) { throw new ImagingException(ErrorCode.OutputValidationFailed, "Output does not decode: " + ex.Message, ex); }
        if (w != expectedWidth || h != expectedHeight) throw new ImagingException(ErrorCode.OutputValidationFailed, $"Output is {w}x{h}, expected {expectedWidth}x{expectedHeight}.");
        try { DecodeAllPixels(com, (IWICBitmapSource)frame, w, h); }
        catch (COMException ex) { throw new ImagingException(ErrorCode.OutputValidationFailed, $"Output pixels do not decode (0x{ex.HResult:X8}).", ex); }
        try { DecoderDiagnostics.ThrowIfWarned(); }
        catch (ImagingException ex) { throw new ImagingException(ErrorCode.OutputValidationFailed, "Output: " + ex.Message, ex); }
    }

    private static IWICBitmapFrameDecode DecodeFirstFrame(ComScope com, byte[] bytes, ImageFormat format, out uint width, out uint height)
    {
        try
        {
            var stream = com.Add(WicNative.CreateStreamOnHGlobal(0, deleteOnRelease: true));
            stream.Write(bytes, bytes.Length, 0);
            stream.Seek(0, 0 /* STREAM_SEEK_SET */, 0);
            var decoder = com.Add(WicNative.Create<IWICBitmapDecoder>(format == ImageFormat.Png ? WicIds.PngDecoder : WicIds.JpegDecoder));
            decoder.Initialize(stream, WicDecodeOptions.MetadataCacheOnDemand);
            var container = decoder.GetContainerFormat();
            if (container != (format == ImageFormat.Png ? WicIds.ContainerPng : WicIds.ContainerJpeg))
                throw new ImagingException(ErrorCode.DecodeFailed, "Decoder reported an unexpected container format.");
            if (decoder.GetFrameCount() == 0) throw new ImagingException(ErrorCode.DecodeFailed, "Image has no frames.");
            var frame = com.Add(decoder.GetFrame(0));
            frame.GetSize(out width, out height);
            if (width == 0 || height == 0) throw new ImagingException(ErrorCode.DecodeFailed, "Image has zero size.");
            if (width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
                throw new ImagingException(ErrorCode.ImageTooLarge, $"Image is {width}x{height}; the limit is {MaxPixels:N0} pixels and {MaxDimension} per side.");
            return frame;
        }
        catch (COMException ex) { throw new ImagingException(ErrorCode.DecodeFailed, $"The file could not be decoded (0x{ex.HResult:X8}).", ex); }
    }

    /// <summary>EXIF orientation (tag 274) mapped to a WIC transform. Missing or invalid tags mean "as stored".</summary>
    private static unsafe WicTransformOptions OrientationTransform(ComScope com, IWICBitmapFrameDecode frame)
    {
        if (frame.GetMetadataQueryReader(out var reader) < 0 || reader is null) return WicTransformOptions.Rotate0;
        com.Add(reader);
        PropVariant value = default;
        try
        {
            if (reader.GetMetadataByName("/app1/ifd/{ushort=274}", &value) < 0 || value.Vt != PropVariant.VtUi2) return WicTransformOptions.Rotate0;
            return OrientationToTransform(value.UInt16);
        }
        finally { _ = WicNative.PropVariantClear(&value); }
    }

    internal static WicTransformOptions OrientationToTransform(ushort orientation) => orientation switch
    {
        2 => WicTransformOptions.FlipHorizontal,
        3 => WicTransformOptions.Rotate180,
        4 => WicTransformOptions.FlipVertical,
        5 => WicTransformOptions.Rotate270 | WicTransformOptions.FlipHorizontal, // transpose; pixel-tested in ConversionJobTests
        6 => WicTransformOptions.Rotate90,
        7 => WicTransformOptions.Rotate90 | WicTransformOptions.FlipHorizontal,  // transverse
        8 => WicTransformOptions.Rotate270,
        _ => WicTransformOptions.Rotate0,
    };

    /// <summary>Writes the BGRA bitmap in bands, blended over white, as BGR.</summary>
    private static unsafe void WriteCompositedOnWhite(IWICBitmapFrameEncode frameEncode, IWICBitmapSource bgra, uint width, uint height)
    {
        var inStride = checked(width * 4);
        var outStride = checked(width * 3);
        var inBuf = new byte[checked(inStride * BandRows)];
        var outBuf = new byte[checked(outStride * BandRows)];
        for (uint y = 0; y < height; y += BandRows)
        {
            var rows = Math.Min(BandRows, height - y);
            var rect = new WicRect { X = 0, Y = (int)y, Width = (int)width, Height = (int)rows };
            fixed (byte* pin = inBuf)
            fixed (byte* pout = outBuf)
            {
                bgra.CopyPixels(&rect, inStride, inStride * rows, pin);
                CompositeOnWhite(new ReadOnlySpan<byte>(pin, (int)(inStride * rows)), new Span<byte>(pout, (int)(outStride * rows)));
                frameEncode.WritePixels(rows, outStride, outStride * rows, pout);
            }
        }
    }

    /// <summary>Straight-alpha BGRA over opaque white, rounded: c' = (c·a + 255·(255 − a)) / 255.</summary>
    internal static void CompositeOnWhite(ReadOnlySpan<byte> bgra, Span<byte> bgr)
    {
        for (int i = 0, o = 0; i + 3 < bgra.Length; i += 4, o += 3)
        {
            int a = bgra[i + 3], inv = 255 - a;
            bgr[o] = (byte)((bgra[i] * a + 255 * inv + 127) / 255);
            bgr[o + 1] = (byte)((bgra[i + 1] * a + 255 * inv + 127) / 255);
            bgr[o + 2] = (byte)((bgra[i + 2] * a + 255 * inv + 127) / 255);
        }
    }

    private static unsafe void DecodeAllPixels(ComScope com, IWICBitmapSource source, uint width, uint height)
    {
        var bgra = com.Add(Factory(com).CreateFormatConverter());
        bgra.Initialize(source, WicIds.Format32bppBGRA, WicDitherType.None, 0, 0, WicPaletteType.Custom);
        var stride = checked(width * 4);
        var buf = new byte[checked(stride * BandRows)];
        fixed (byte* p = buf)
            for (uint y = 0; y < height; y += BandRows)
            {
                var rows = Math.Min(BandRows, height - y);
                var rect = new WicRect { X = 0, Y = (int)y, Width = (int)width, Height = (int)rows };
                bgra.CopyPixels(&rect, stride, stride * rows, p);
            }
    }

    private static unsafe byte[] ReadAll(IStream stream)
    {
        stream.Stat(out var stat, 1 /* STATFLAG_NONAME */);
        if (stat.cbSize <= 0 || stat.cbSize > int.MaxValue) throw new ImagingException(ErrorCode.EncodeFailed, "Encoder produced no data.");
        var bytes = new byte[stat.cbSize];
        stream.Seek(0, 0, 0);
        int read;
        stream.Read(bytes, bytes.Length, (nint)(&read));
        if (read != bytes.Length) throw new ImagingException(ErrorCode.EncodeFailed, "Short read from the encoded stream.");
        return bytes;
    }

    private static double SaneDpi(double dpi) => double.IsFinite(dpi) && dpi is >= 1 and <= 10_000 ? dpi : 96;

    private static IWICImagingFactory Factory(ComScope com) => com.Factory ??= com.Add(WicNative.Create<IWICImagingFactory>(WicIds.ImagingFactory));

    /// <summary>Releases every COM object of one operation deterministically (large decoders hold the whole input).</summary>
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
