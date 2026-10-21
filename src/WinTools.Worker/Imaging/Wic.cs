using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WinTools.Worker.Imaging;

// Minimal Windows Imaging Component interop (wincodec.h). Only the methods this worker calls are declared; COM vtables
// are positional, so each interface lists its methods in header order up to the last one used.
// Codecs are created by CLSID, which selects the built-in Microsoft codec and never a third-party one registered for
// the same container (ADR-0006).

internal static class WicIds
{
    public static readonly Guid ImagingFactory = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2"); // CLSID_WICImagingFactory2
    public static readonly Guid PngDecoder = new("e018945b-aa86-4008-9bd4-6777a1e40c11");     // CLSID_WICPngDecoder2
    public static readonly Guid JpegDecoder = new("9456a480-e88b-43ea-9e73-0b2d9b71b1ca");    // CLSID_WICJpegDecoder
    public static readonly Guid PngEncoder = new("27949969-876a-41d7-9447-568f6a35a4dc");     // CLSID_WICPngEncoder
    public static readonly Guid JpegEncoder = new("1a34f5c1-4a5a-46dc-b644-1f4567e7a676");    // CLSID_WICJpegEncoder

    public static readonly Guid BmpDecoder = new("6b462062-7cbf-400d-9fdb-813dd10f2778");     // CLSID_WICBmpDecoder
    public static readonly Guid BmpEncoder = new("69be8bb4-d66d-47c8-865a-ed1589433782");     // CLSID_WICBmpEncoder
    public static readonly Guid TiffDecoder = new("b54e85d9-fe23-499f-8b88-6acea713752b");    // CLSID_WICTiffDecoder
    public static readonly Guid TiffEncoder = new("0131be10-2001-4c5f-a9b0-cc88fab64ce8");    // CLSID_WICTiffEncoder
    public static readonly Guid ContainerBmp = new("0af1d87e-fcfe-4188-bdeb-a7906471cbe3");
    public static readonly Guid ContainerTiff = new("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3");

    public static readonly Guid VendorMicrosoft = new("f0e749ca-edef-4589-a73a-ee0e626a2a2b");

    public static readonly Guid ContainerPng = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    public static readonly Guid ContainerJpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");

    public static readonly Guid Format24bppBGR = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    public static readonly Guid Format32bppBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    public static readonly Guid Format32bppPBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
}

internal enum WicDecodeOptions { MetadataCacheOnDemand = 0, MetadataCacheOnLoad = 1 }
internal enum WicEncoderCacheOption { InMemory = 0, TempFile = 1, NoCache = 2 }
internal enum WicDitherType { None = 0 }
internal enum WicInterpolationMode { NearestNeighbor = 0, Linear = 1, Cubic = 2, Fant = 3, HighQualityCubic = 4 }
internal enum WicCacheOption { NoCache = 0, CacheOnDemand = 1, CacheOnLoad = 2 }
internal enum WicPaletteType { Custom = 0 }

[Flags]
internal enum WicTransformOptions { Rotate0 = 0, Rotate90 = 1, Rotate180 = 2, Rotate270 = 3, FlipHorizontal = 8, FlipVertical = 16 }

[StructLayout(LayoutKind.Sequential)]
internal struct WicRect { public int X, Y, Width, Height; }

[ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICImagingFactory
{
    void CreateDecoderFromFilename_Unused();
    void CreateDecoderFromStream_Unused();
    void CreateDecoderFromFileHandle_Unused();
    IWICComponentInfo CreateComponentInfo(in Guid clsid);
    void CreateDecoder_Unused();
    void CreateEncoder_Unused();
    void CreatePalette_Unused();
    IWICFormatConverter CreateFormatConverter();
    IWICBitmapScaler CreateBitmapScaler();
    void CreateBitmapClipper_Unused();
    IWICBitmapFlipRotator CreateBitmapFlipRotator();
    void CreateStream_Unused();
    IWICColorContext CreateColorContext();
    void CreateColorTransformer_Unused();
    void CreateBitmap_Unused();
    /// <summary>Returns an IWICBitmap (declared by its IWICBitmapSource base, the only part used).</summary>
    IWICBitmapSource CreateBitmapFromSource(IWICBitmapSource source, WicCacheOption option);
    void CreateBitmapFromSourceRect_Unused();
    /// <summary>Returns an IWICBitmap over a copy of the buffer (declared by its IWICBitmapSource base).</summary>
    unsafe IWICBitmapSource CreateBitmapFromMemory(uint width, uint height, in Guid pixelFormat, uint stride, uint bufferSize, byte* buffer);
}

[ComImport, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapScaler // : IWICBitmapSource
{
    void GetSize(out uint width, out uint height);
    Guid GetPixelFormat();
    void GetResolution(out double dpiX, out double dpiY);
    void CopyPalette(nint palette);
    unsafe void CopyPixels(WicRect* rect, uint stride, uint bufferSize, byte* buffer);
    void Initialize(IWICBitmapSource source, uint width, uint height, WicInterpolationMode mode);
}

[ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapSource
{
    void GetSize(out uint width, out uint height);
    Guid GetPixelFormat();
    void GetResolution(out double dpiX, out double dpiY);
    void CopyPalette(nint palette);
    unsafe void CopyPixels(WicRect* rect, uint stride, uint bufferSize, byte* buffer);
}

[ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFrameDecode // : IWICBitmapSource
{
    void GetSize(out uint width, out uint height);
    Guid GetPixelFormat();
    void GetResolution(out double dpiX, out double dpiY);
    void CopyPalette(nint palette);
    unsafe void CopyPixels(WicRect* rect, uint stride, uint bufferSize, byte* buffer);
    [PreserveSig] int GetMetadataQueryReader(out IWICMetadataQueryReader? reader);
    [PreserveSig] unsafe int GetColorContexts(uint count, nint* contexts, out uint actual);
}

[ComImport, Guid("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICColorContext
{
    void InitializeFromFilename_Unused();
    unsafe void InitializeFromMemory(byte* buffer, uint size);
    void InitializeFromExifColorSpace_Unused();
    uint GetType_();
    [PreserveSig] unsafe int GetProfileBytes(uint size, byte* buffer, out uint actual);
}

[ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICFormatConverter // : IWICBitmapSource
{
    void GetSize(out uint width, out uint height);
    Guid GetPixelFormat();
    void GetResolution(out double dpiX, out double dpiY);
    void CopyPalette(nint palette);
    unsafe void CopyPixels(WicRect* rect, uint stride, uint bufferSize, byte* buffer);
    void Initialize(IWICBitmapSource source, in Guid destinationFormat, WicDitherType dither, nint palette, double alphaThresholdPercent, WicPaletteType paletteType);
}

[ComImport, Guid("5009834f-2d6a-41ce-9e1b-17c5aff7a782"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFlipRotator // : IWICBitmapSource
{
    void GetSize(out uint width, out uint height);
    Guid GetPixelFormat();
    void GetResolution(out double dpiX, out double dpiY);
    void CopyPalette(nint palette);
    unsafe void CopyPixels(WicRect* rect, uint stride, uint bufferSize, byte* buffer);
    void Initialize(IWICBitmapSource source, WicTransformOptions options);
}

[ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapDecoder
{
    uint QueryCapability(IStream stream);
    void Initialize(IStream stream, WicDecodeOptions options);
    Guid GetContainerFormat();
    void GetDecoderInfo_Unused();
    void CopyPalette_Unused();
    void GetMetadataQueryReader_Unused();
    void GetPreview_Unused();
    void GetColorContexts_Unused();
    void GetThumbnail_Unused();
    uint GetFrameCount();
    IWICBitmapFrameDecode GetFrame(uint index);
}

[ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapEncoder
{
    void Initialize(IStream stream, WicEncoderCacheOption cacheOption);
    Guid GetContainerFormat();
    void GetEncoderInfo_Unused();
    void SetColorContexts_Unused();
    void SetPalette_Unused();
    void SetThumbnail_Unused();
    void SetPreview_Unused();
    void CreateNewFrame(out IWICBitmapFrameEncode frame, out nint propertyBag);
    void Commit();
}

[ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFrameEncode
{
    void Initialize(nint propertyBag);
    void SetSize(uint width, uint height);
    void SetResolution(double dpiX, double dpiY);
    void SetPixelFormat(ref Guid pixelFormat);
    unsafe void SetColorContexts(uint count, nint* contexts);
    void SetPalette_Unused();
    void SetThumbnail_Unused();
    unsafe void WritePixels(uint lineCount, uint stride, uint bufferSize, byte* pixels);
    void WriteSource(IWICBitmapSource source, nint rect);
    void Commit();
}

/// <summary>IWICComponentInfo (the base of the decoder/encoder info interfaces); used to check a codec's vendor.</summary>
[ComImport, Guid("23bc3f0a-698b-4357-886b-f24d50671334"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICComponentInfo
{
    uint GetComponentType();
    Guid GetCLSID();
    uint GetSigningStatus();
    void GetAuthor_Unused();
    Guid GetVendorGUID();
}

[ComImport, Guid("30989668-e1c9-4597-b395-458eedb808df"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICMetadataQueryReader
{
    void GetContainerFormat_Unused();
    void GetLocation_Unused();
    [PreserveSig] unsafe int GetMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string name, PropVariant* value);
}

/// <summary>PROPVARIANT, enough to read a VT_UI2 and to be cleared safely.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VtUi2 = 18;
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public ushort UInt16;
}

internal static partial class WicNative
{
    private const uint ClsctxInprocServer = 1;

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern IStream CreateStreamOnHGlobal(nint hGlobal, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease);

    [LibraryImport("ole32.dll")]
    public static unsafe partial int PropVariantClear(PropVariant* value);

    public static T Create<T>(Guid clsid) where T : class
    {
        CoCreateInstance(clsid, 0, ClsctxInprocServer, typeof(T).GUID, out var instance);
        return (T)instance;
    }
}
