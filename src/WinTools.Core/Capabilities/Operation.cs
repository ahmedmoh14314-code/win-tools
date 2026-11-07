using System.Globalization;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

public enum OperationKind { Convert, Resize, Compress, Rotate, StripMetadata, Info, Flip, Crop, Adjust, Collage }

/// <summary>Flip horizontal mirrors left and right; flip vertical turns the picture upside down as a mirror would.</summary>
public enum FlipAxis { Horizontal, Vertical }

/// <summary>The one-click adjustments (ADR-0031): fixed presets, no sliders.</summary>
public enum Adjustment { BlackAndWhite, Brighter, Darker, MoreContrast, MoreVivid, Sharpen, Blur }

/// <summary>A crop's shape, width to height ("16:9").</summary>
public readonly record struct AspectRatio(int Width, int Height);

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
/// not code in the UI. Operations other than Convert and Collage keep the file's format, except that an edit of a HEIF
/// photo writes JPEG (<see cref="ImageFormats.EditedAs"/>). Collage joins 2 to <see cref="OperationCatalog.MaxCollageInputs"/>
/// images into one (PNG, or JPEG when every input is a JPEG).
/// </summary>
public sealed record Operation(string Id, string Label, OperationKind Kind, ImageFormat Target = ImageFormat.Unknown,
    ResizeSpec? Resize = null, CompressLevel Compress = CompressLevel.Balanced, int RotateDegrees = 0,
    FlipAxis Flip = FlipAxis.Horizontal, AspectRatio Aspect = default, Adjustment Adjust = Adjustment.BlackAndWhite)
{
    /// <summary>Whether this operation is valid for a file whose content is <paramref name="source"/>.</summary>
    public bool AppliesTo(ImageFormat source)
    {
        if (!ImageFormats.CanDecode(source)) return false;
        return Kind switch
        {
            OperationKind.Convert => source != Target && ImageFormats.CanEncode(Target),
            OperationKind.Resize or OperationKind.Rotate or OperationKind.Flip or OperationKind.Crop or OperationKind.Adjust => ImageFormats.CanEncode(ImageFormats.EditedAs(source)),
            OperationKind.Compress => Compress == CompressLevel.Lossless ? source == ImageFormat.Png : source is ImageFormat.Jpeg or ImageFormat.WebP,
            // BMP carries none; HEIF and AVIF would need a re-encode, which removing metadata never does.
            OperationKind.StripMetadata => source is not (ImageFormat.Bmp or ImageFormat.Heif or ImageFormat.Avif),
            OperationKind.Info or OperationKind.Collage => true,
            _ => false,
        };
    }

    /// <summary>True for operations that report facts and write no file.</summary>
    public bool WritesFile => Kind != OperationKind.Info;

    /// <summary>The format written for a file whose content is <paramref name="source"/>.</summary>
    public ImageFormat OutputFormat(ImageFormat source) => Kind switch
    {
        OperationKind.Convert => Target,
        OperationKind.Collage => CollageFormat([source]),
        OperationKind.Info or OperationKind.Compress or OperationKind.StripMetadata => source,
        _ => ImageFormats.EditedAs(source),
    };

    /// <summary>What a collage of images of these formats is written as: JPEG when every one is a JPEG (photos stay
    /// photos), otherwise PNG, which keeps transparency and loses nothing.</summary>
    public static ImageFormat CollageFormat(IEnumerable<ImageFormat> sources) => sources.All(s => s == ImageFormat.Jpeg) ? ImageFormat.Jpeg : ImageFormat.Png;

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
        OperationKind.Flip => Flip == FlipAxis.Horizontal ? "flipped horizontally" : "flipped vertically",
        OperationKind.Crop => $"cropped {Aspect.Width}x{Aspect.Height}",
        OperationKind.Adjust => Adjust switch
        {
            Adjustment.BlackAndWhite => "black and white",
            Adjustment.Brighter => "brighter",
            Adjustment.Darker => "darker",
            Adjustment.MoreContrast => "more contrast",
            Adjustment.MoreVivid => "more vivid",
            Adjustment.Sharpen => "sharpened",
            _ => "blurred",
        },
        OperationKind.Collage => "collage",
        _ => null,
    };
}

/// <summary>The operations this build supports, generated from the format table and the fixed menu presets.</summary>
public static class OperationCatalog
{
    public const string CollageId = "collage";

    /// <summary>The most images one collage takes (a 4×4 grid).</summary>
    public const int MaxCollageInputs = 16;

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
        // ADR-0031: flips (beside the rotations), centred crops, one-click adjustments, and the collage of a selection.
        ops.Add(new Operation("flip.horizontal", "Flip horizontal", OperationKind.Flip, Flip: FlipAxis.Horizontal));
        ops.Add(new Operation("flip.vertical", "Flip vertical", OperationKind.Flip, Flip: FlipAxis.Vertical));
        foreach (var (w, h) in new[] { (1, 1), (4, 3), (16, 9), (9, 16) })
            ops.Add(new Operation($"crop.{w}x{h}", $"{w}:{h}", OperationKind.Crop, Aspect: new AspectRatio(w, h)));
        ops.Add(new Operation("adjust.black-and-white", "Black & white", OperationKind.Adjust, Adjust: Adjustment.BlackAndWhite));
        ops.Add(new Operation("adjust.brighter", "Brighter", OperationKind.Adjust, Adjust: Adjustment.Brighter));
        ops.Add(new Operation("adjust.darker", "Darker", OperationKind.Adjust, Adjust: Adjustment.Darker));
        ops.Add(new Operation("adjust.contrast", "More contrast", OperationKind.Adjust, Adjust: Adjustment.MoreContrast));
        ops.Add(new Operation("adjust.vivid", "More vivid", OperationKind.Adjust, Adjust: Adjustment.MoreVivid));
        ops.Add(new Operation("adjust.sharpen", "Sharpen", OperationKind.Adjust, Adjust: Adjustment.Sharpen));
        ops.Add(new Operation("adjust.blur", "Blur", OperationKind.Adjust, Adjust: Adjustment.Blur));
        ops.Add(new Operation(CollageId, "Collage", OperationKind.Collage));
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
