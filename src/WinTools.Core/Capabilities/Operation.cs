using System.Globalization;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

public enum OperationKind { Convert, Resize, Compress, Rotate, StripMetadata, Info }

public enum CompressLevel { High, Balanced, Small, Lossless }

public enum ResizeMode { Percent, Width, Height, Box }

/// <summary>
/// A resize request. Percent scales both sides. Width or Height fixes one side and scales the other proportionally.
/// Box fits inside W×H (KeepAspect) or stretches each axis to W and H. With NoUpscale (the default) no axis grows, so a
/// stretch only shrinks the axes that are larger, and an image that already fits is left alone ("nothing to do").
/// </summary>
public sealed record ResizeSpec(ResizeMode Mode, int Percent = 0, int Width = 0, int Height = 0, bool KeepAspect = true, bool NoUpscale = true)
{
    /// <summary>The output size for a source of w×h, or null when nothing would change.</summary>
    public (int Width, int Height)? TargetFor(int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        double sx, sy;
        switch (Mode)
        {
            case ResizeMode.Percent: sx = sy = Percent / 100.0; break;
            case ResizeMode.Width: sx = sy = Width / (double)w; break;
            case ResizeMode.Height: sx = sy = Height / (double)h; break;
            default:
                sx = Width / (double)w; sy = Height / (double)h;
                if (KeepAspect) sx = sy = Math.Min(sx, sy);
                break;
        }
        if (NoUpscale) { sx = Math.Min(sx, 1); sy = Math.Min(sy, 1); }
        if (!(sx > 0) || !(sy > 0)) return null;
        var tw = Math.Max(1, (int)Math.Round(w * sx, MidpointRounding.AwayFromZero));
        var th = Math.Max(1, (int)Math.Round(h * sy, MidpointRounding.AwayFromZero));
        return tw == w && th == h ? null : (tw, th);
    }
}

/// <summary>
/// A declarative description of one command the radial menu can offer. Adding a format or an operation adds rows,
/// not code in the UI. Operations other than Convert keep the file's format.
/// </summary>
public sealed record Operation(string Id, string Label, OperationKind Kind, ImageFormat Target = ImageFormat.Unknown,
    ResizeSpec? Resize = null, CompressLevel Compress = CompressLevel.Balanced, int RotateDegrees = 0)
{
    /// <summary>Whether this operation is valid for a file whose content is <paramref name="source"/>.</summary>
    public bool AppliesTo(ImageFormat source)
    {
        if (!ImageFormats.CanDecode(source)) return false;
        return Kind switch
        {
            OperationKind.Convert => source != Target && ImageFormats.CanEncode(Target),
            OperationKind.Resize or OperationKind.Rotate => ImageFormats.CanEncode(source),
            OperationKind.Compress => Compress == CompressLevel.Lossless ? source == ImageFormat.Png : source is ImageFormat.Jpeg or ImageFormat.WebP,
            OperationKind.StripMetadata => source != ImageFormat.Bmp, // BMP carries none
            OperationKind.Info => true,
            _ => false,
        };
    }

    /// <summary>True for operations that report facts and write no file.</summary>
    public bool WritesFile => Kind != OperationKind.Info;

    /// <summary>The format written for a file whose content is <paramref name="source"/>.</summary>
    public ImageFormat OutputFormat(ImageFormat source) => Kind == OperationKind.Convert ? Target : source;

    /// <summary>Text added to the output name ("photo (resized 50%).jpg"); null for a plain conversion ("photo.png").</summary>
    public string? NameSuffix => Kind switch
    {
        OperationKind.Resize => Resize!.Mode switch
        {
            ResizeMode.Percent => $"resized {Resize.Percent}%",
            ResizeMode.Width => $"{Resize.Width} wide",
            ResizeMode.Height => $"{Resize.Height} high",
            _ => $"{Resize.Width}x{Resize.Height}",
        },
        OperationKind.Compress => "compressed",
        OperationKind.Rotate => RotateDegrees switch { 90 => "rotated right", 270 => "rotated left", _ => "rotated 180" },
        OperationKind.StripMetadata => "no metadata",
        _ => null,
    };
}

/// <summary>The operations this build supports, generated from the format table and the fixed menu presets.</summary>
public static class OperationCatalog
{
    public static IReadOnlyList<Operation> All { get; } = Build();

    private static Operation[] Build()
    {
        var ops = new List<Operation>();
        foreach (var t in ImageFormats.Encodable)
            ops.Add(new Operation(ConvertId(t), "To " + ImageFormats.DisplayName(t), OperationKind.Convert, t));
        foreach (var p in new[] { 25, 50, 75 })
            ops.Add(new Operation($"resize.percent-{p}", $"{p}%", OperationKind.Resize, Resize: new ResizeSpec(ResizeMode.Percent, Percent: p)));
        foreach (var (w, h) in new[] { (1920, 1080), (1280, 720), (1080, 1080) })
            ops.Add(BoxResize(w, h, keepAspect: true));
        ops.Add(new Operation("compress.high", "High quality", OperationKind.Compress, Compress: CompressLevel.High));
        ops.Add(new Operation("compress.balanced", "Balanced", OperationKind.Compress, Compress: CompressLevel.Balanced));
        ops.Add(new Operation("compress.small", "Smallest", OperationKind.Compress, Compress: CompressLevel.Small));
        ops.Add(new Operation("compress.lossless", "Lossless", OperationKind.Compress, Compress: CompressLevel.Lossless));
        ops.Add(new Operation("rotate.left", "Left", OperationKind.Rotate, RotateDegrees: 270));
        ops.Add(new Operation("rotate.right", "Right", OperationKind.Rotate, RotateDegrees: 90));
        ops.Add(new Operation("rotate.180", "180°", OperationKind.Rotate, RotateDegrees: 180));
        ops.Add(new Operation("metadata.strip", "Remove metadata", OperationKind.StripMetadata));
        ops.Add(new Operation("metadata.info", "Image info", OperationKind.Info));
        return [.. ops];
    }

    private static Operation BoxResize(int w, int h, bool keepAspect, bool noUpscale = true) =>
        new($"resize.box-{w}x{h}" + (keepAspect ? "" : "-stretch") + (noUpscale ? "" : "-upscale"), $"{w}×{h}", OperationKind.Resize,
            Resize: new ResizeSpec(ResizeMode.Box, Width: w, Height: h, KeepAspect: keepAspect, NoUpscale: noUpscale));

    public static string ConvertId(ImageFormat target) => "convert.to-" + target.ToString().ToLowerInvariant();

    public static Operation Convert(ImageFormat target) => Find(ConvertId(target)) ?? throw new ArgumentOutOfRangeException(nameof(target));

    public static Operation Get(string id) => Find(id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown operation.");

    /// <summary>
    /// A catalog operation, or a parameterised resize ("resize.width-800", "resize.height-600", "resize.box-800x600",
    /// "resize.box-800x600-stretch", any of them with "-upscale" to allow enlarging) such as the settings' exact sizes
    /// produce. Sizes must be 1..65535.
    /// </summary>
    public static Operation? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var known = All.FirstOrDefault(o => o.Id == id);
        if (known is not null) return known;
        var upscale = id.EndsWith("-upscale", StringComparison.Ordinal);
        var core = upscale ? id[..^"-upscale".Length] : id;
        if (core.StartsWith("resize.width-", StringComparison.Ordinal) && Size(core["resize.width-".Length..]) is { } w)
            return new Operation(id, $"{w} wide", OperationKind.Resize, Resize: new ResizeSpec(ResizeMode.Width, Width: w, NoUpscale: !upscale));
        if (core.StartsWith("resize.height-", StringComparison.Ordinal) && Size(core["resize.height-".Length..]) is { } h)
            return new Operation(id, $"{h} high", OperationKind.Resize, Resize: new ResizeSpec(ResizeMode.Height, Height: h, NoUpscale: !upscale));
        if (core.StartsWith("resize.box-", StringComparison.Ordinal))
        {
            var spec = core["resize.box-".Length..];
            var stretch = spec.EndsWith("-stretch", StringComparison.Ordinal);
            if (stretch) spec = spec[..^"-stretch".Length];
            var parts = spec.Split('x');
            if (parts.Length == 2 && Size(parts[0]) is { } bw && Size(parts[1]) is { } bh)
                return BoxResize(bw, bh, keepAspect: !stretch, noUpscale: !upscale);
        }
        return null;
    }

    private static int? Size(string s) =>
        s.Length is > 0 and <= 5 && s[0] != '0' && s.All(char.IsAsciiDigit) && int.Parse(s, CultureInfo.InvariantCulture) is var v and <= 65_535 ? v : null;
}
