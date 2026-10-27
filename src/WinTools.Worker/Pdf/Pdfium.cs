using System.Reflection;
using System.Runtime.InteropServices;

namespace WinTools.Worker.Pdf;

/// <summary>
/// The bundled PDFium (ADR-0029), loaded by full path from the "pdfium" folder next to the worker, never from the DLL
/// search path. Built without V8 and XFA: it runs no JavaScript and no form logic. It is not thread-safe: the worker runs
/// one job at a time, and every call goes through <see cref="Gate"/>.
/// </summary>
internal static partial class Pdfium
{
    private const string Lib = "pdfium";
    public const uint ErrPassword = 4, ErrSecurity = 5, ErrFormat = 3, ErrFile = 2;
    public const int RenderAnnotations = 0x01;

    public static readonly Lock Gate = new();
    private static bool _initialized;

    public static string LibraryPath => Path.Combine(AppContext.BaseDirectory, "pdfium", "pdfium.dll");

    public static bool IsPresent => File.Exists(LibraryPath);

    static Pdfium() => NativeLibrary.SetDllImportResolver(typeof(Pdfium).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
        name == Lib ? NativeLibrary.Load(LibraryPath) : 0;

    /// <summary>Starts the library once per process.</summary>
    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;
            FPDF_InitLibrary();
            _initialized = true;
        }
    }

    [LibraryImport(Lib)] private static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static unsafe partial nint FPDF_LoadMemDocument64(byte* data, nuint size, byte* password);
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint document, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_Create(int width, int height, int alpha);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_GetBuffer(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetStride(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Lib)] public static unsafe partial int FPDFText_GetText(nint textPage, int start, int count, char* result);
}

/// <summary>Why a PDF could not be opened.</summary>
internal enum PdfOpenFailure { None, Password, Damaged }

/// <summary>
/// One PDF opened from memory (the bytes are pinned for as long as it is open). Pages are read one at a time and closed
/// at once, so memory follows one page, not the document.
/// </summary>
internal sealed unsafe class PdfDocument : IDisposable
{
    private readonly GCHandle _pin;
    private nint _doc;

    private PdfDocument(GCHandle pin, nint doc) => (_pin, _doc) = (pin, doc);

    public int PageCount { get; private init; }

    /// <summary>Opens a PDF that needs no password; null with the reason otherwise.</summary>
    public static PdfDocument? Open(byte[] bytes, out PdfOpenFailure failure)
    {
        Pdfium.EnsureInitialized();
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        lock (Pdfium.Gate)
        {
            var doc = Pdfium.FPDF_LoadMemDocument64((byte*)pin.AddrOfPinnedObject(), (nuint)bytes.Length, null);
            if (doc == 0)
            {
                failure = Pdfium.FPDF_GetLastError() is Pdfium.ErrPassword or Pdfium.ErrSecurity ? PdfOpenFailure.Password : PdfOpenFailure.Damaged;
                pin.Free();
                return null;
            }
            failure = PdfOpenFailure.None;
            return new PdfDocument(pin, doc) { PageCount = Pdfium.FPDF_GetPageCount(doc) };
        }
    }

    /// <summary>A page's size in points (1/72 inch), as it is shown (its rotation applied); null when it cannot be read.</summary>
    public (float Width, float Height)? PageSize(int index)
    {
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == 0) return null;
            try { return (Pdfium.FPDF_GetPageWidthF(page), Pdfium.FPDF_GetPageHeightF(page)); }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
    }

    /// <summary>
    /// A page drawn on white at <paramref name="width"/> × <paramref name="height"/> pixels, with its annotations, as
    /// opaque BGRA rows; null when the page cannot be read.
    /// </summary>
    public byte[]? Render(int index, int width, int height)
    {
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == 0) return null;
            var bitmap = Pdfium.FPDFBitmap_Create(width, height, 0);
            try
            {
                if (bitmap == 0) return null;
                if (Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF) == 0) return null;
                Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Pdfium.RenderAnnotations);
                var stride = Pdfium.FPDFBitmap_GetStride(bitmap);
                var buffer = (byte*)Pdfium.FPDFBitmap_GetBuffer(bitmap);
                var pixels = new byte[(long)width * height * 4];
                for (var y = 0; y < height; y++)
                {
                    var row = new ReadOnlySpan<byte>(buffer + (long)y * stride, width * 4);
                    row.CopyTo(pixels.AsSpan(y * width * 4, width * 4));
                }
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // BGRx: the fourth byte is not alpha
                return pixels;
            }
            finally
            {
                if (bitmap != 0) Pdfium.FPDFBitmap_Destroy(bitmap);
                Pdfium.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>A page's text as PDFium reads it (line breaks as CRLF), at most <paramref name="maxChars"/> characters; null when unreadable.</summary>
    public string? Text(int index, int maxChars)
    {
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == 0) return null;
            var text = Pdfium.FPDFText_LoadPage(page);
            try
            {
                if (text == 0) return null;
                var count = Math.Min(Pdfium.FPDFText_CountChars(text), maxChars);
                if (count <= 0) return "";
                var buffer = new char[count + 1];
                fixed (char* b = buffer)
                {
                    var written = Pdfium.FPDFText_GetText(text, 0, count, b);
                    return written <= 1 ? "" : new string(buffer, 0, written - 1);
                }
            }
            finally
            {
                if (text != 0) Pdfium.FPDFText_ClosePage(text);
                Pdfium.FPDF_ClosePage(page);
            }
        }
    }

    public void Dispose()
    {
        lock (Pdfium.Gate)
        {
            if (_doc == 0) return;
            Pdfium.FPDF_CloseDocument(_doc);
            _doc = 0;
        }
        _pin.Free();
    }
}
