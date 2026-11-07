using System.Security.Cryptography;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;
using WinTools.Worker.Imaging;

namespace WinTools.Worker;

/// <summary>Test seams between the steps of one job (null in production).</summary>
internal sealed record JobHooks(Action? AfterSourceRead = null, Action<string>? AfterTempWritten = null, Action? BeforeFinalize = null);

/// <summary>
/// One conversion with the output-safety rules (ADR-0005): the source is opened read-only and never written; the result
/// is written to a temporary file in the destination folder, flushed, read back and fully decoded, and only then
/// renamed to the first free name with a no-overwrite move. Any failure deletes the temporary file, so a failed job
/// leaves no file that looks like a result.
/// </summary>
internal static class ConversionJob
{
    public const long MaxInputBytes = 256L * 1024 * 1024;
    private const int ErrorFileExists = unchecked((int)0x80070050), ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const int ErrorInvalidName = unchecked((int)0x8007007B), ErrorFilenameTooLong = unchecked((int)0x800700CE);
    private const int ErrorDiskFull = unchecked((int)0x80070070), ErrorHandleDiskFull = unchecked((int)0x80070027);

    public static ConversionResult Run(ConversionRequest request, JobHooks? hooks = null)
    {
        var id = request.Id;
        if (id is not { Length: > 0 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return ConversionResult.Fail("invalid", ErrorCode.InvalidRequest, "Request id must be 1-64 ASCII letters or digits.");
        var op = OperationCatalog.Find(request.OperationId); // null for a missing or unknown id
        if (op is null) return ConversionResult.Fail(id, ErrorCode.UnsupportedOperation, $"Unknown operation '{request.OperationId}'.");
        var source = request.SourcePath;
        if (!PathPolicy.IsSupported(source))
            return ConversionResult.Fail(id, ErrorCode.InvalidRequest, "Source path must be a canonical, fully qualified file path.");
        var outputDir = request.OutputDirectory;
        if (outputDir is not null && !PathPolicy.IsSupported(Path.Combine(outputDir, "x.png")))
            return ConversionResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        if (op.Kind == OperationKind.Collage) return ConversionResult.Fail(id, ErrorCode.InvalidRequest, "A collage takes 2 to 16 images.");
        DecoderDiagnostics.Reset();

        return Guarded(id, pending =>
        {
            // A folder named like a file ("holiday.png") is a folder all the same: say so, never a vague read error (UX-009).
            if (Directory.Exists(source)) return ConversionResult.Fail(id, ErrorCode.IsAFolder, "The source is a folder, not a file.");
            var input = ReadSource(source);
            var zone = MarkOfTheWeb.ZoneToCarry(source);
            FaultInjection.At("after-read", source);
            hooks?.AfterSourceRead?.Invoke();
            // The format is decided by content, never by the name; the operation must apply to what the file really is.
            var actual = FileSignature.Detect(input);
            if (!op.AppliesTo(actual))
                return ConversionResult.Fail(id, ErrorCode.ContentMismatch, actual == ImageFormat.Unknown
                    ? "The file is not a supported image."
                    : $"The file content is {ImageFormats.DisplayName(actual)}; \"{op.Label}\" does not apply to it.");

            if (op.Kind == OperationKind.Info) return ConversionResult.Facts(id, ImagePipeline.Describe(input, actual));

            var encoded = ImagePipeline.Run(input, actual, op, request.JpegQuality, request.WebPQuality);
            return Commit(id, source, encoded, op.NameSuffix, outputDir, zone, hooks, pending);
        });
    }

    /// <summary>
    /// A collage of 2 to <see cref="OperationCatalog.MaxCollageInputs"/> images (ADR-0031), in name order, named after the
    /// first ("beach (collage).jpg"), next to it or in the output folder. Each source is read, checked by content and
    /// decoded like any other; the result carries the strictest Mark of the Web of its sources and their colour profile
    /// when they all share one (otherwise none, and the user is told).
    /// </summary>
    public static ConversionResult RunCollage(string id, IReadOnlyList<string> sources, string? outputDir, int? jpegQuality, JobHooks? hooks = null)
    {
        if (id is not { Length: > 0 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return ConversionResult.Fail("invalid", ErrorCode.InvalidRequest, "Request id must be 1-64 ASCII letters or digits.");
        if (sources.Count is < 2 or > OperationCatalog.MaxCollageInputs)
            return ConversionResult.Fail(id, ErrorCode.InvalidRequest, $"A collage takes 2 to {OperationCatalog.MaxCollageInputs} images.");
        if (!sources.All(PathPolicy.IsSupported) || sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
            return ConversionResult.Fail(id, ErrorCode.InvalidRequest, "Every source must be a different canonical, fully qualified file path.");
        if (outputDir is not null && !PathPolicy.IsSupported(Path.Combine(outputDir, "x.png")))
            return ConversionResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        var op = OperationCatalog.Get(OperationCatalog.CollageId);
        var ordered = PdfCapabilities.MergeOrder(sources);

        return Guarded(id, pending =>
        {
            var (boundW, boundH) = Collage.CellBound(ordered.Count);
            var images = new List<RasterImage>();
            var sizes = new List<(int, int)>();
            var formats = new List<ImageFormat>();
            var profiles = new List<byte[]?>();
            int? zone = null;
            foreach (var source in ordered)
            {
                if (Directory.Exists(source)) return ConversionResult.Fail(id, ErrorCode.IsAFolder, "One of the sources is a folder, not a file.");
                var input = ReadSource(source);
                zone = MarkOfTheWeb.ZoneToCarry(source) is { } z ? Math.Max(z, zone ?? z) : zone;
                var actual = FileSignature.Detect(input);
                if (!op.AppliesTo(actual)) return ConversionResult.Fail(id, ErrorCode.ContentMismatch, $"{Path.GetFileName(source)} is not a supported image.");
                DecoderDiagnostics.Reset();
                var (image, orientation) = ImagePipeline.Decode(input, actual);
                image = Orientation.Apply(image, orientation);
                sizes.Add((image.Width, image.Height));
                formats.Add(actual);
                profiles.Add(image.IccProfile);
                images.Add(Collage.FitWithin(image, boundW, boundH));
            }
            hooks?.AfterSourceRead?.Invoke();
            // One colour profile for the whole picture: the sources' own when they share it, otherwise none (sRGB).
            var shared = profiles.All(p => p is not null && p.AsSpan().SequenceEqual(profiles[0])) ? profiles[0] : null;
            var mixed = shared is null && profiles.Any(p => p is not null);
            var canvas = Collage.Compose(images, sizes, shared);
            var format = Operation.CollageFormat(formats);
            var bytes = format == ImageFormat.Jpeg ? WicCodec.Encode(canvas, format, Math.Clamp(jpegQuality ?? 90, 1, 100) / 100f) : WicCodec.Encode(canvas, format);
            var result = Commit(id, ordered[0], new ImagePipeline.Encoded(bytes, canvas.Width, canvas.Height, format), op.NameSuffix, outputDir, zone, hooks, pending);
            return result.Success && mixed ? result with { Notice = "The images have different colour profiles; the collage uses standard colours (sRGB)" } : result;
        });
    }

    /// <summary>A job's temporary file, deleted when the job ends without having moved it to its final name.</summary>
    private sealed class Pending
    {
        public string? Temp;
    }

    /// <summary>Runs a job's body and turns every failure into a result; a temporary file left behind is deleted.</summary>
    private static ConversionResult Guarded(string id, Func<Pending, ConversionResult> body)
    {
        var pending = new Pending();
        try { return body(pending); }
        catch (ImagingException ex) { return ConversionResult.Fail(id, ex.Code, ex.Message); }
        catch (SourceException ex) { return ConversionResult.Fail(id, ex.Code, ex.Message); }
        catch (UnauthorizedAccessException) { return ConversionResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (PathTooLongException) { return ConversionResult.Fail(id, ErrorCode.OutputNameInvalid, "The output name would be too long."); }
        catch (IOException ex) when (ex.HResult is ErrorInvalidName or ErrorFilenameTooLong) { return ConversionResult.Fail(id, ErrorCode.OutputNameInvalid, "The output name is not valid here."); }
        catch (IOException ex) when (ex.HResult is ErrorDiskFull or ErrorHandleDiskFull) { return ConversionResult.Fail(id, ErrorCode.DiskFull, "The destination drive is full."); }
        catch (IOException ex) { return ConversionResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, $"Writing the output failed (0x{ex.HResult:X8})."); }
        catch (OutOfMemoryException) { return ConversionResult.Fail(id, ErrorCode.ImageTooLarge, "Not enough memory for this image."); }
        catch (Exception ex) { return ConversionResult.Fail(id, ErrorCode.Internal, ex.GetType().Name); }
        finally
        {
            if (pending.Temp is not null) TryDelete(pending.Temp);
        }
    }

    /// <summary>
    /// The output-safety steps: the destination checked (it exists, and neither it nor a parent is a link), the bytes
    /// written to a temporary file there, read back and decoded, then moved to the first free name without overwriting.
    /// </summary>
    private static ConversionResult Commit(string id, string namedAfter, ImagePipeline.Encoded encoded, string? suffix, string? outputDir, int? zone, JobHooks? hooks, Pending pending)
    {
        var dirInfo = new DirectoryInfo(outputDir ?? Path.GetDirectoryName(namedAfter)!);
        if (!dirInfo.Exists)
            return outputDir is null
                ? ConversionResult.Fail(id, ErrorCode.SourceNotFound, "The source folder no longer exists.")
                : ConversionResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The configured output folder does not exist.");
        for (var d = dirInfo; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) return ConversionResult.Fail(id, ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");

        var tempPath = OutputNaming.TempPath(namedAfter, id, outputDir);
        WriteTemp(tempPath, encoded.Bytes); // CreateNew: an existing file with this name is never touched
        pending.Temp = tempPath;
        if (zone is { } z) MarkOfTheWeb.Apply(tempPath, z); // a rename keeps the stream: the output is never unmarked
        FaultInjection.At("after-temp", namedAfter);
        hooks?.AfterTempWritten?.Invoke(tempPath);

        ImagePipeline.Validate(File.ReadAllBytes(tempPath), encoded.Format, encoded.Width, encoded.Height);
        File.SetAttributes(tempPath, FileAttributes.Normal);
        hooks?.BeforeFinalize?.Invoke();

        foreach (var candidate in OutputNaming.Candidates(namedAfter, encoded.Format, suffix, outputDir))
        {
            try
            {
                File.Move(tempPath, candidate, overwrite: false); // MoveFileEx without REPLACE_EXISTING: fails if the name is taken
                pending.Temp = null;
                return ConversionResult.Ok(id, candidate, Convert.ToHexString(SHA256.HashData(encoded.Bytes)));
            }
            catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists) { }
        }
        return ConversionResult.Fail(id, ErrorCode.NoFreeOutputName, $"No free output name after {OutputNaming.MaxCollisionIndex} attempts.");
    }

    private sealed class SourceException(ErrorCode code, string message) : Exception(message)
    {
        public ErrorCode Code { get; } = code;
    }

    private static byte[] ReadSource(string path)
    {
        try
        {
            // Read-only, and other processes may keep reading, writing or deleting it meanwhile.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (fs.Length > MaxInputBytes) throw new SourceException(ErrorCode.InputTooLarge, $"The file is larger than {MaxInputBytes / (1024 * 1024)} MB.");
            if (fs.Length == 0) throw new SourceException(ErrorCode.DecodeFailed, "The file is empty.");
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SourceException(ErrorCode.SourceNotFound, "The source file no longer exists.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not EndOfStreamException)
        {
            throw new SourceException(ErrorCode.SourceNotReadable, "The source file could not be read.");
        }
        catch (EndOfStreamException)
        {
            throw new SourceException(ErrorCode.SourceNotReadable, "The source file changed while it was being read.");
        }
    }

    private static void WriteTemp(string temp, byte[] bytes)
    {
        var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024); // throws if it exists
        try
        {
            using (fs)
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.SetAttributes(temp, FileAttributes.Hidden);
        }
        catch
        {
            TryDelete(temp); // we created it; a partial temp file must not stay behind
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
