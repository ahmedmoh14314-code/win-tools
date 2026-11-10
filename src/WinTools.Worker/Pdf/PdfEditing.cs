using System.Runtime.InteropServices;

namespace WinTools.Worker.Pdf;

/// <summary>PDFium's page-editing and saving calls (fpdf_edit.h, fpdf_save.h), used by Watermark, Page numbers and Crop margins.</summary>
internal static partial class Pdfium
{
    [LibraryImport(Lib)] public static partial int FPDFPage_GetRotation(nint page);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetMediaBox(nint page, out float left, out float bottom, out float right, out float top);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetCropBox(nint page, out float left, out float bottom, out float right, out float top);
    [LibraryImport(Lib)] public static partial void FPDFPage_SetCropBox(nint page, float left, float bottom, float right, float top);
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_NewImageObj(nint document);
    [LibraryImport(Lib)] public static unsafe partial int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObject, nint bitmap);
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint FPDFText_LoadStandardFont(nint document, string font);
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_CreateTextObj(nint document, nint font, float fontSize);
    [LibraryImport(Lib)] public static unsafe partial int FPDFText_SetText(nint textObject, char* text);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetFillColor(nint pageObject, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Transform(nint pageObject, double a, double b, double c, double d, double e, double f);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetBounds(nint pageObject, out float left, out float bottom, out float right, out float top);
    [LibraryImport(Lib)] public static partial void FPDFPage_InsertObject(nint page, nint pageObject);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Destroy(nint pageObject);
    [LibraryImport(Lib)] public static partial int FPDFPage_GenerateContent(nint page);
    [LibraryImport(Lib)] public static unsafe partial int FPDF_SaveAsCopy(nint document, FileWrite* write, uint flags);
    [LibraryImport(Lib)] public static partial void FPDFFont_Close(nint font);

    public const int BitmapBgra = 4;
    public const uint SaveNoIncremental = 2;

    /// <summary>FPDF_FILEWRITE: a version and the function PDFium calls with each block it writes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FileWrite
    {
        public int Version;
        public delegate* unmanaged<FileWrite*, void*, nuint, int> WriteBlock;
    }
}

/// <summary>A page's unrotated box (where its content is drawn) and the clockwise rotation it is shown with.</summary>
internal readonly record struct PageFrame(float Left, float Bottom, float Right, float Top, int Rotation)
{
    public float Width => Right - Left;
    public float Height => Top - Bottom;

    /// <summary>The page's size as shown.</summary>
    public (float Width, float Height) Shown => Rotation is 90 or 270 ? (Height, Width) : (Width, Height);

    /// <summary>
    /// A point given as it is shown (from the shown page's lower-left corner) in the content's own coordinates: PDF shows a
    /// page turned clockwise by its /Rotate, so the shown bottom of a page turned 90° is its content's right edge.
    /// </summary>
    public (double X, double Y) FromShown(double u, double v) => Rotation switch
    {
        90 => (Left + Width - v, Bottom + u),
        180 => (Left + Width - u, Bottom + Height - v),
        270 => (Left + v, Bottom + Height - u),
        _ => (Left + u, Bottom + v),
    };

    /// <summary>A shown angle (counter-clockwise, degrees) in the content's coordinates.</summary>
    public double AngleFromShown(double degrees) => degrees + Rotation;
}

internal sealed unsafe partial class PdfDocument
{
    /// <summary>A page's box and rotation; null when the page cannot be read.</summary>
    public PageFrame? Frame(int index)
    {
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == 0) return null;
            try { return FrameOf(page); }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
    }

    private static PageFrame FrameOf(nint page)
    {
        float l, b, r, t;
        if (Pdfium.FPDFPage_GetCropBox(page, out l, out b, out r, out t) == 0 && Pdfium.FPDFPage_GetMediaBox(page, out l, out b, out r, out t) == 0)
            (l, b, r, t) = (0, 0, 612, 792); // PDF's default page
        if (r < l) (l, r) = (r, l);
        if (t < b) (b, t) = (t, b);
        var rotation = Pdfium.FPDFPage_GetRotation(page) switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
        return new PageFrame(l, b, r, t, rotation);
    }

    /// <summary>
    /// Writes text in a standard font (Helvetica: digits and Latin letters) on a page, centred on a shown point and turned to
    /// read upright as the page is shown. False when the page cannot be read or changed.
    /// </summary>
    public bool StampText(int index, string text, float size, Func<PageFrame, (double U, double V)> where, (byte R, byte G, byte B) color)
    {
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == 0) return false;
            var font = Pdfium.FPDFText_LoadStandardFont(_doc, "Helvetica");
            try
            {
                if (font == 0) return false;
                var obj = Pdfium.FPDFPageObj_CreateTextObj(_doc, font, size);
                if (obj == 0) return false;
                fixed (char* s = text + "\0")
                    if (Pdfium.FPDFText_SetText(obj, s) == 0) { Pdfium.FPDFPageObj_Destroy(obj); return false; }
                _ = Pdfium.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, 255);
                Pdfium.FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t);
                var frame = FrameOf(page);
                var (u, v) = where(frame);
                Place(obj, frame, u, v, 0, -(l + r) / 2, -(b + t) / 2);
                Pdfium.FPDFPage_InsertObject(page, obj);
                return Pdfium.FPDFPage_GenerateContent(page) != 0;
            }
            finally
            {
                if (font != 0) Pdfium.FPDFFont_Close(font);
                Pdfium.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Places a picture (BGRA rows, not premultiplied) on a page, <paramref name="widthPoints"/> wide, centred on a shown
    /// point and turned by <paramref name="shownAngle"/> as the page is shown.
    /// </summary>
    public bool StampImage(int index, byte[] bgra, int pixelWidth, int pixelHeight, Func<PageFrame, (double U, double V, double Width, double Angle)> where)
    {
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == 0) return false;
            nint bitmap = 0;
            try
            {
                var obj = Pdfium.FPDFPageObj_NewImageObj(_doc);
                if (obj == 0) return false;
                fixed (byte* p = bgra)
                {
                    bitmap = Pdfium.FPDFBitmap_CreateEx(pixelWidth, pixelHeight, Pdfium.BitmapBgra, (nint)p, pixelWidth * 4);
                    if (bitmap == 0 || Pdfium.FPDFImageObj_SetBitmap(&page, 1, obj, bitmap) == 0) { Pdfium.FPDFPageObj_Destroy(obj); return false; }
                }
                var frame = FrameOf(page);
                var (u, v, width, angle) = where(frame);
                var height = width * pixelHeight / pixelWidth;
                Pdfium.FPDFPageObj_Transform(obj, width, 0, 0, height, 0, 0); // the image is the unit square until scaled
                Place(obj, frame, u, v, angle, -width / 2, -height / 2);
                Pdfium.FPDFPage_InsertObject(page, obj);
                return Pdfium.FPDFPage_GenerateContent(page) != 0;
            }
            finally
            {
                if (bitmap != 0) Pdfium.FPDFBitmap_Destroy(bitmap);
                Pdfium.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Moves an object by (dx, dy), turns it to the shown angle, and puts that centre on the shown point.</summary>
    private static void Place(nint obj, PageFrame frame, double u, double v, double shownAngle, double dx, double dy)
    {
        Pdfium.FPDFPageObj_Transform(obj, 1, 0, 0, 1, dx, dy);
        var radians = frame.AngleFromShown(shownAngle) * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        Pdfium.FPDFPageObj_Transform(obj, cos, sin, -sin, cos, 0, 0);
        var (x, y) = frame.FromShown(u, v);
        Pdfium.FPDFPageObj_Transform(obj, 1, 0, 0, 1, x, y);
    }

    /// <summary>Sets a page's crop box (in its content's coordinates). False when the page cannot be read.</summary>
    public bool SetCropBox(int index, float left, float bottom, float right, float top)
    {
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == 0) return false;
            try { Pdfium.FPDFPage_SetCropBox(page, left, bottom, right, top); return true; }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
    }

    [ThreadStatic] private static MemoryStream? _saving;

    [UnmanagedCallersOnly]
    private static int WriteBlock(Pdfium.FileWrite* self, void* data, nuint size)
    {
        try
        {
            _saving!.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch (Exception ex) when (ex is IOException or OverflowException or NotSupportedException or OutOfMemoryException) { return 0; }
    }

    /// <summary>The document as it now is, written whole (not as an incremental update); null when PDFium fails.</summary>
    public byte[]? Save()
    {
        lock (Pdfium.Gate)
        {
            _saving = new MemoryStream();
            try
            {
                var write = new Pdfium.FileWrite { Version = 1, WriteBlock = &WriteBlock };
                return Pdfium.FPDF_SaveAsCopy(_doc, &write, Pdfium.SaveNoIncremental) != 0 ? _saving.ToArray() : null;
            }
            finally { _saving = null; }
        }
    }
}
