using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace WinTools.Worker.Pdf;

/// <summary>
/// A PowerPoint deck (.pptx) of pictures, one slide each, written from scratch (PDF to PowerPoint): the smallest package
/// PowerPoint opens without repair, namely one master, one blank layout, one theme, and a slide per picture, centred on the
/// slide at its own proportions. The slide size is the first page's, within PowerPoint's limits. No macro, link,
/// external relationship or embedded object is ever written: only XML of fixed shape and the JPEG pictures.
/// </summary>
internal static class SlideDeckWriter
{
    /// <summary>English Metric Units in a point (PowerPoint measures in EMU: 914,400 to the inch).</summary>
    private const long EmuPerPoint = 12_700;

    /// <summary>PowerPoint's limits for the slide size: 1 inch to 56 inches a side.</summary>
    public const long MinSide = 914_400, MaxSide = 51_206_400;

    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    /// <summary>One slide: its picture (JPEG bytes) and the page's size in points.</summary>
    public readonly record struct Slide(byte[] Jpeg, double WidthPoints, double HeightPoints);

    /// <summary>The slide size for a page of that size in points, kept in proportion within PowerPoint's limits.</summary>
    public static (long Cx, long Cy) SlideSize(double widthPoints, double heightPoints)
    {
        double cx = Math.Max(1, widthPoints) * EmuPerPoint, cy = Math.Max(1, heightPoints) * EmuPerPoint;
        var down = Math.Min(1, Math.Min(MaxSide / cx, MaxSide / cy));
        cx *= down; cy *= down;
        var up = Math.Max(1, Math.Max(MinSide / cx, MinSide / cy));
        cx *= up; cy *= up;
        return (Math.Clamp((long)Math.Round(cx), MinSide, MaxSide), Math.Clamp((long)Math.Round(cy), MinSide, MaxSide));
    }

    /// <summary>A picture of that size fitted into the slide, centred: offset and extent in EMU.</summary>
    public static (long X, long Y, long Cx, long Cy) Fit(long slideCx, long slideCy, double widthPoints, double heightPoints)
    {
        var scale = Math.Min(slideCx / Math.Max(1, widthPoints), slideCy / Math.Max(1, heightPoints));
        var cx = Math.Clamp((long)Math.Round(widthPoints * scale), 1, slideCx);
        var cy = Math.Clamp((long)Math.Round(heightPoints * scale), 1, slideCy);
        return ((slideCx - cx) / 2, (slideCy - cy) / 2, cx, cy);
    }

    public static byte[] Write(IReadOnlyList<Slide> slides)
    {
        if (slides.Count == 0) throw new ArgumentException("A deck needs at least one slide.", nameof(slides));
        var (cx, cy) = SlideSize(slides[0].WidthPoints, slides[0].HeightPoints);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Text(zip, "[Content_Types].xml", ContentTypes(slides.Count));
            Text(zip, "_rels/.rels", Rels(("rId1", Rel + "/officeDocument", "ppt/presentation.xml")));
            Text(zip, "ppt/presentation.xml", Presentation(slides.Count, cx, cy));
            var presentationRels = new List<(string, string, string)>
            {
                ("rId1", Rel + "/slideMaster", "slideMasters/slideMaster1.xml"),
                ("rId2", Rel + "/theme", "theme/theme1.xml"),
            };
            for (var i = 0; i < slides.Count; i++) presentationRels.Add(($"rId{i + 3}", Rel + "/slide", $"slides/slide{i + 1}.xml"));
            Text(zip, "ppt/_rels/presentation.xml.rels", Rels([.. presentationRels]));
            Text(zip, "ppt/slideMasters/slideMaster1.xml", Master);
            Text(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels(("rId1", Rel + "/slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", Rel + "/theme", "../theme/theme1.xml")));
            Text(zip, "ppt/slideLayouts/slideLayout1.xml", Layout);
            Text(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels(("rId1", Rel + "/slideMaster", "../slideMasters/slideMaster1.xml")));
            Text(zip, "ppt/theme/theme1.xml", Theme);
            for (var i = 0; i < slides.Count; i++)
            {
                var n = (i + 1).ToString(CultureInfo.InvariantCulture);
                var (x, y, w, h) = Fit(cx, cy, slides[i].WidthPoints, slides[i].HeightPoints);
                Text(zip, $"ppt/slides/slide{n}.xml", SlideXml(i + 1, x, y, w, h));
                Text(zip, $"ppt/slides/_rels/slide{n}.xml.rels", Rels(("rId1", Rel + "/slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", Rel + "/image", $"../media/image{n}.jpeg")));
                var entry = zip.CreateEntry($"ppt/media/image{n}.jpeg", CompressionLevel.NoCompression); // JPEG does not shrink further
                using var s = entry.Open();
                s.Write(slides[i].Jpeg);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>The part names a deck of that many slides holds, for the check after writing.</summary>
    public static IReadOnlySet<string> PartNames(int slides)
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "[Content_Types].xml", "_rels/.rels", "ppt/presentation.xml", "ppt/_rels/presentation.xml.rels",
            "ppt/slideMasters/slideMaster1.xml", "ppt/slideMasters/_rels/slideMaster1.xml.rels",
            "ppt/slideLayouts/slideLayout1.xml", "ppt/slideLayouts/_rels/slideLayout1.xml.rels", "ppt/theme/theme1.xml",
        };
        for (var i = 1; i <= slides; i++)
        {
            names.Add($"ppt/slides/slide{i}.xml");
            names.Add($"ppt/slides/_rels/slide{i}.xml.rels");
            names.Add($"ppt/media/image{i}.jpeg");
        }
        return names;
    }

    private static void Text(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(new UTF8Encoding(false).GetBytes(xml));
    }

    private static string Rels(params (string Id, string Type, string Target)[] rels)
    {
        var sb = new StringBuilder(Head).Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        foreach (var (id, type, target) in rels) sb.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{target}\"/>");
        return sb.Append("</Relationships>").ToString();
    }

    private static string ContentTypes(int slides)
    {
        const string Pml = "application/vnd.openxmlformats-officedocument.presentationml.";
        var sb = new StringBuilder(Head).Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
            .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
            .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
            .Append("<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>")
            .Append("<Override PartName=\"/ppt/presentation.xml\" ContentType=\"" + Pml + "presentation.main+xml\"/>")
            .Append("<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"" + Pml + "slideMaster+xml\"/>")
            .Append("<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"" + Pml + "slideLayout+xml\"/>")
            .Append("<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
        for (var i = 1; i <= slides; i++) sb.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/ppt/slides/slide{i}.xml\" ContentType=\"{Pml}slide+xml\"/>");
        return sb.Append("</Types>").ToString();
    }

    private static string Presentation(int slides, long cx, long cy)
    {
        var sb = new StringBuilder(Head).Append(CultureInfo.InvariantCulture, $"<p:presentation xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\">")
            .Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>");
        for (var i = 0; i < slides; i++) sb.Append(CultureInfo.InvariantCulture, $"<p:sldId id=\"{256 + i}\" r:id=\"rId{i + 3}\"/>");
        return sb.Append(CultureInfo.InvariantCulture, $"</p:sldIdLst><p:sldSz cx=\"{cx}\" cy=\"{cy}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>").ToString();
    }

    private const string EmptyTree = "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
        + "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static string SlideXml(int number, long x, long y, long cx, long cy) => Head
        + $"<p:sld xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld>" + EmptyTree
        + string.Create(CultureInfo.InvariantCulture, $"<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"Page {number}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>")
        + "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>"
        + string.Create(CultureInfo.InvariantCulture, $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>")
        + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";

    private static readonly string Master = Head
        + $"<p:sldMaster xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>"
        + EmptyTree + "</p:spTree></p:cSld>"
        + "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
        + "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>";

    private static readonly string Layout = Head
        + $"<p:sldLayout xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\" type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\">"
        + EmptyTree + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string Solid(string inner) => $"<a:solidFill>{inner}</a:solidFill>";

    private static readonly string Theme = Head
        + $"<a:theme xmlns:a=\"{A}\" name=\"WinTools\"><a:themeElements>"
        + "<a:clrScheme name=\"WinTools\">"
        + "<a:dk1><a:srgbClr val=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1>"
        + "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>"
        + "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2>"
        + "<a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>"
        + "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>"
        + "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>"
        + "<a:fontScheme name=\"WinTools\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>"
        + "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>"
        + "<a:fmtScheme name=\"WinTools\"><a:fillStyleLst>"
        + Solid("<a:schemeClr val=\"phClr\"/>") + Solid("<a:schemeClr val=\"phClr\"/>") + Solid("<a:schemeClr val=\"phClr\"/>")
        + "</a:fillStyleLst><a:lnStyleLst>"
        + "<a:ln w=\"6350\">" + Solid("<a:schemeClr val=\"phClr\"/>") + "</a:ln>"
        + "<a:ln w=\"12700\">" + Solid("<a:schemeClr val=\"phClr\"/>") + "</a:ln>"
        + "<a:ln w=\"19050\">" + Solid("<a:schemeClr val=\"phClr\"/>") + "</a:ln>"
        + "</a:lnStyleLst><a:effectStyleLst>"
        + "<a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle>"
        + "</a:effectStyleLst><a:bgFillStyleLst>"
        + Solid("<a:schemeClr val=\"phClr\"/>") + Solid("<a:schemeClr val=\"phClr\"/>") + Solid("<a:schemeClr val=\"phClr\"/>")
        + "</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>";
}
