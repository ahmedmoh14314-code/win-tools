using System.Globalization;
using System.Text;
using WinTools.Core.Jobs;

namespace WinTools.Host;

/// <summary>Plain-text rendering of "Image info" results (functional V1 surface; the UI phase replaces it).</summary>
internal static class InfoText
{
    public static string Format(IReadOnlyList<ImageInfo> items)
    {
        var sb = new StringBuilder();
        foreach (var i in items.Take(10))
        {
            if (sb.Length > 0) sb.AppendLine().AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i.Format}  {i.Width} × {i.Height} px  ({Size(i.FileBytes)})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Resolution: {i.DpiX:0.#} × {i.DpiY:0.#} dpi");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Transparency: {YesNo(i.HasTransparency)}    More than 8 bits per channel: {YesNo(i.DeeperThan8Bits)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Orientation tag: {i.Orientation}    Colour profile (ICC): {YesNo(i.HasIccProfile)}");
            sb.Append(CultureInfo.InvariantCulture, $"EXIF: {YesNo(i.HasExif)}    GPS location: {YesNo(i.HasGps)}    XMP: {YesNo(i.HasXmp)}    Text/comments: {YesNo(i.HasTextOrComments)}");
        }
        if (items.Count > 10) sb.AppendLine().AppendLine().Append(CultureInfo.InvariantCulture, $"… and {items.Count - 10} more files.");
        return sb.ToString();
    }

    private static string YesNo(bool b) => b ? "yes" : "no";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.##} MB"),
    };
}
