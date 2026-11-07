using System.Globalization;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// HEIF (HEIC) and AVIF through the bundled FFmpeg (ADR-0031: the LGPL build of ADR-0028, its native HEVC decoder and
/// dav1d), run as a child process like the media worker runs it: arguments as a list, the file protocol only, a time
/// limit, bounded output, a private environment. Windows' own HEIF codec is not used: it needs a paid Store extension.
/// <list type="bullet">
/// <item>Decode: the file is first read by <see cref="Heif"/> (structure, primary image, limits, what would show wrong).
/// The bytes already read and checked are written to a private folder, so the engine reads exactly them; it writes a
/// lossless 8-bit RGBA PNG there, which WIC decodes into the usual <see cref="RasterImage"/>. The result must have the
/// size the structure states. A grid is composed by the engine, which also applies its rotation and mirroring; a single
/// picture is decoded as stored and its orientation goes back to the pipeline, as a JPEG's EXIF orientation does. An
/// alpha plane is joined to the picture. The colour profile is the file's own (colr), read here.</item>
/// <item>Encode (AVIF only): the pixels go to the engine as a PNG and come back as an AV1 still picture (libaom,
/// 4:4:4, full range, BT.709 signalled), with an alpha plane when the image has transparency. The result is decoded
/// again and compared with what was encoded: a file that does not show the same picture is never written.</item>
/// </list>
/// </summary>
internal static class HeifCodec
{
    /// <summary>One engine run's limit: well inside the host's per-request limit, so the worker answers before it is killed.</summary>
    private static readonly TimeSpan EngineTimeout = TimeSpan.FromSeconds(40);

    /// <summary>AV1 quality (constant rate factor, 0–63, lower is better) of "To AVIF": as close to the source as a JPEG at
    /// high quality, at a fraction of its size (measured, ADR-0031).</summary>
    public const int AvifCrf = 20;

    /// <summary>Largest average difference per channel (0–255) between the pixels encoded and the AVIF decoded again.</summary>
    internal const double MaxAvifMeanError = 4.0;

    public static (RasterImage Image, int Orientation) Decode(byte[] bytes, ImageFormat format)
    {
        var info = Heif.Read(bytes, out var why)
            ?? throw new ImagingException(why.StartsWith("a damaged", StringComparison.Ordinal) || why.Contains("past", StringComparison.Ordinal) || why.Contains("truncated", StringComparison.Ordinal)
                ? ErrorCode.DecodeFailed : ErrorCode.UnsupportedContent, $"This {ImageFormats.DisplayName(format)} file cannot be read: {why}.");
        // The limits before any pixel work, on the engine's canvas as well as on the output.
        if (info.Width > RasterImage.MaxDimension || info.Height > RasterImage.MaxDimension || (long)info.Width * info.Height > RasterImage.MaxPixels
            || info.CodedPixels > RasterImage.MaxPixels * 2)
            throw new ImagingException(ErrorCode.ImageTooLarge, $"Image is {info.Width}x{info.Height}; the limit is {RasterImage.MaxPixels:N0} pixels and {RasterImage.MaxDimension} per side.");
        if (!Ffmpeg.IsPresent) throw new ImagingException(ErrorCode.Internal, "The HEIC and AVIF engine is missing from this copy of WinTools.");

        using var scratch = Scratch.Create();
        var input = scratch.File(format == ImageFormat.Avif ? "in.avif" : "in.heic");
        var output = scratch.File("out.png");
        File.WriteAllBytes(input, bytes);
        List<string> args = ["-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file"];
        string P(int item) => "0:i:" + item.ToString(CultureInfo.InvariantCulture);
        if (info.IsGrid)
            // The engine composes the tiles and applies the grid's rotation and mirroring itself (it cannot be told not to);
            // its default choice is the primary image, and the size check below holds it to that.
            args.AddRange(["-i", Ffmpeg.FileUrl(input)]);
        else if (info.AlphaItem != 0)
            args.AddRange(["-noautorotate", "-i", Ffmpeg.FileUrl(input), "-filter_complex", $"[{P(info.PrimaryItem)}][{P(info.AlphaItem)}]alphamerge[o]", "-map", "[o]"]);
        else
            args.AddRange(["-noautorotate", "-i", Ffmpeg.FileUrl(input), "-map", P(info.PrimaryItem)]);
        args.AddRange(["-frames:v", "1", "-pix_fmt", "rgba", "-c:v", "png", "-compression_level", "1", "-f", "image2", "-update", "1", Ffmpeg.FileUrl(output)]);
        var run = Ffmpeg.Convert(args, EngineTimeout);
        if (run.TimedOut) throw new ImagingException(ErrorCode.DecodeFailed, "Decoding took too long and was stopped.");
        if (!run.Ok || !File.Exists(output)) throw new ImagingException(ErrorCode.DecodeFailed, "The file could not be decoded; it may be damaged.");

        var png = File.ReadAllBytes(output);
        var (decoded, _) = WicCodec.Decode(png, ImageFormat.Png);
        var (w, h) = info.IsGrid ? info.Shown : (info.Width, info.Height);
        if (decoded.Width != w || decoded.Height != h)
            throw new ImagingException(ErrorCode.DecodeFailed, $"The decoder produced {decoded.Width}x{decoded.Height}, not the {w}x{h} the file states.");
        // The file's own colour profile (the engine does not carry it), and no resolution: HEIF has none.
        var image = new RasterImage(decoded.Width, decoded.Height, decoded.Bgra, iccProfile: info.Icc);
        return (image, info.IsGrid ? 1 : info.Orientation);
    }

    /// <summary>The orientation a HEIF or AVIF file states (irot, imir), for "Image info".</summary>
    public static int OrientationOf(byte[] bytes) => Heif.Read(bytes, out _)?.Orientation ?? 1;

    /// <summary>An AVIF still picture of the image, verified by decoding it again.</summary>
    public static byte[] EncodeAvif(RasterImage image, int crf = AvifCrf)
    {
        if (!Ffmpeg.IsPresent) throw new ImagingException(ErrorCode.Internal, "The AVIF engine is missing from this copy of WinTools.");
        using var scratch = Scratch.Create();
        var input = scratch.File("in.png");
        var output = scratch.File("out.avif");
        // A plain RGB(A) PNG: the colour profile is put into the AVIF below, not left to the engine.
        File.WriteAllBytes(input, WicCodec.Encode(new RasterImage(image.Width, image.Height, image.Bgra), ImageFormat.Png));
        var alpha = image.HasTransparency();
        // The colour signalled in the file (nclx) comes from the frames, so it is set on them; the alpha plane is plain grey.
        const string colour = "scale=out_color_matrix=bt709:out_range=pc,format=yuv444p,setparams=color_primaries=bt709:color_trc=iec61966-2-1:colorspace=bt709:range=pc";
        List<string> args = ["-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file", "-i", Ffmpeg.FileUrl(input)];
        if (alpha)
            args.AddRange(["-filter_complex", $"[0:v:0]format=rgba,split[c][a];[c]{colour}[pic];[a]alphaextract,format=gray,setparams=colorspace=bt709:range=pc[mask]", "-map", "[pic]", "-map", "[mask]"]);
        else
            args.AddRange(["-map", "0:v:0", "-vf", colour]);
        args.AddRange(["-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1", "-cpu-used", "6", "-row-mt", "1",
            "-crf", crf.ToString(CultureInfo.InvariantCulture), "-f", "avif", Ffmpeg.FileUrl(output)]);
        var run = Ffmpeg.Convert(args, EngineTimeout);
        if (run.TimedOut) throw new ImagingException(ErrorCode.EncodeFailed, "Encoding took too long and was stopped.");
        if (!run.Ok || !File.Exists(output)) throw new ImagingException(ErrorCode.EncodeFailed, "The AVIF encoder failed.");
        var avif = File.ReadAllBytes(output);
        if (image.IccProfile is { Length: > 0 } icc) avif = HeifEdit.WithIccProfile(avif, icc);

        // The same picture must come back: size, colour and transparency (a wrong colour matrix or range shows here).
        var (back, orientation) = Decode(avif, ImageFormat.Avif);
        if (orientation != 1 || back.Width != image.Width || back.Height != image.Height)
            throw new ImagingException(ErrorCode.OutputValidationFailed, "The AVIF does not decode to the same size.");
        var (colourError, alphaError) = MeanError(image.Bgra, back.Bgra);
        if (colourError > MaxAvifMeanError || alphaError > MaxAvifMeanError)
            throw new ImagingException(ErrorCode.OutputValidationFailed, string.Create(CultureInfo.InvariantCulture, $"The AVIF does not show the same picture (mean error {colourError:0.0}, alpha {alphaError:0.0})."));
        if (!alpha && back.HasTransparency()) throw new ImagingException(ErrorCode.OutputValidationFailed, "The AVIF gained transparency.");
        return avif;
    }

    /// <summary>Mean absolute difference of the colour channels (where visible) and of alpha, over every pixel.</summary>
    internal static (double Colour, double Alpha) MeanError(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long colour = 0, alpha = 0, visible = 0;
        for (var i = 0; i + 3 < a.Length; i += 4)
        {
            alpha += Math.Abs(a[i + 3] - b[i + 3]);
            if (a[i + 3] == 0) continue; // colour under a fully transparent pixel is not seen
            colour += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            visible++;
        }
        var pixels = Math.Max(1, a.Length / 4);
        return (visible == 0 ? 0 : colour / (3.0 * visible), alpha / (double)pixels);
    }

    /// <summary>A private folder for one engine run, deleted with everything in it afterwards.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly string _path;
        private Scratch(string path) => _path = path;

        public static Scratch Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "WinTools");
            Directory.CreateDirectory(root);
            SweepAbandoned(root);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var path = Path.Combine(root, "img-" + Guid.NewGuid().ToString("N"));
                switch (ExclusiveDirectory.Create(path))
                {
                    case CreateOutcome.Created: return new Scratch(path);
                    case CreateOutcome.NameTaken: continue;
                    default: throw new ImagingException(ErrorCode.Internal, "No private working folder could be made in the temporary folder.");
                }
            }
            throw new ImagingException(ErrorCode.Internal, "No private working folder could be made in the temporary folder.");
        }

        /// <summary>
        /// A killed worker cannot clean up, and its folder holds a copy of the user's picture: folders far older than any
        /// request's limit are removed by the next run. Plain folders of our own name only; a link is never followed.
        /// </summary>
        private static void SweepAbandoned(string root)
        {
            try
            {
                foreach (var old in new DirectoryInfo(root).EnumerateDirectories("img-*"))
                {
                    if (old.LinkTarget is not null || DateTime.UtcNow - old.LastWriteTimeUtc < TimeSpan.FromHours(1)) continue;
                    try { old.Delete(recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public string File(string name) => Path.Combine(_path, name);

        public void Dispose()
        {
            try { Directory.Delete(_path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
