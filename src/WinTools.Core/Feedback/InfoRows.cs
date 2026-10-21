using System.Globalization;
using WinTools.Core.Jobs;

namespace WinTools.Core.Feedback;

/// <summary>One line of the image info panel. <paramref name="Notice"/> marks a value worth the user's attention (a GPS location).</summary>
public sealed record InfoRow(string Label, string Value, bool Notice = false);

/// <summary>The facts "Image info" shows for one file, in reading order and in plain words.</summary>
public static class InfoRows
{
    public static IReadOnlyList<InfoRow> For(ImageInfo i)
    {
        var mp = (double)i.Width * i.Height / 1_000_000;
        var rows = new List<InfoRow>
        {
            new("Format", i.DeeperThan8Bits ? $"{i.Format} · 16-bit" : i.Format),
            new("Dimensions", string.Create(CultureInfo.InvariantCulture, $"{i.Width} × {i.Height} px") + (mp >= 0.1 ? string.Create(CultureInfo.InvariantCulture, $" · {mp:0.#} MP") : "")),
            new("File size", FeedbackText.Size(i.FileBytes)),
            new("Resolution", i.DpiX > 0 && i.DpiY > 0
                ? (Math.Abs(i.DpiX - i.DpiY) < 0.05 ? string.Create(CultureInfo.InvariantCulture, $"{i.DpiX:0.#} dpi") : string.Create(CultureInfo.InvariantCulture, $"{i.DpiX:0.#} × {i.DpiY:0.#} dpi"))
                : "Not set"),
            new("Transparency", i.HasTransparency ? "Yes" : "No"),
            new("Orientation", Orientation(i.Orientation)),
            new("Colour profile", i.HasIccProfile ? "Embedded" : "None"),
            new("EXIF", i.HasExif ? "Yes" : "None"),
            new("GPS location", i.HasGps ? "Present" : "None", Notice: i.HasGps),
        };
        var other = new List<string>();
        if (i.HasXmp) other.Add("XMP");
        if (i.HasTextOrComments) other.Add("text or comments");
        if (i.HasOtherMetadata) other.Add("other");
        if (other.Count > 0) rows.Add(new("Other metadata", string.Join(", ", other)));
        return rows;
    }

    /// <summary>The EXIF orientation tag (1–8) as the user sees it; anything else means the tag is absent.</summary>
    public static string Orientation(int tag) => tag switch
    {
        1 => "Normal",
        2 => "Mirrored",
        3 => "Rotated 180°",
        4 => "Mirrored, rotated 180°",
        5 => "Mirrored, rotated 90° left",
        6 => "Rotated 90° right",
        7 => "Mirrored, rotated 90° right",
        8 => "Rotated 90° left",
        _ => "Not set",
    };
}
