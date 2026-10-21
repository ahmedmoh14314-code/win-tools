using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>A declarative description of one command the radial menu can offer. Adding a format adds a row, not code in the UI.</summary>
public sealed record Operation(string Id, string Label, ImageFormat Source, ImageFormat Target);

/// <summary>The operations this build supports.</summary>
public static class OperationCatalog
{
    public static readonly Operation PngToJpeg = new("convert.png-to-jpeg", "To JPEG", ImageFormat.Png, ImageFormat.Jpeg);
    public static readonly Operation JpegToPng = new("convert.jpeg-to-png", "To PNG", ImageFormat.Jpeg, ImageFormat.Png);

    public static readonly IReadOnlyList<Operation> All = [PngToJpeg, JpegToPng];

    public static Operation? Find(string id) => All.FirstOrDefault(o => o.Id == id);
}
