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
        DecoderDiagnostics.Reset();

        string? temp = null;
        try
        {
            var input = ReadSource(source);
            hooks?.AfterSourceRead?.Invoke();
            // The format is decided by content, never by the name; the operation must apply to what the file really is.
            var actual = FileSignature.Detect(input);
            if (!op.AppliesTo(actual))
                return ConversionResult.Fail(id, ErrorCode.ContentMismatch, actual == ImageFormat.Unknown
                    ? "The file is not a supported image."
                    : $"The file content is {ImageFormats.DisplayName(actual)}; \"{op.Label}\" does not apply to it.");

            var encoded = ImagePipeline.Run(input, actual, op);

            var dirInfo = new DirectoryInfo(Path.GetDirectoryName(source)!);
            if (!dirInfo.Exists) return ConversionResult.Fail(id, ErrorCode.SourceNotFound, "The source folder no longer exists.");
            for (var d = dirInfo; d is not null; d = d.Parent)
                if (d.LinkTarget is not null) return ConversionResult.Fail(id, ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");

            var tempPath = OutputNaming.TempPath(source, id);
            WriteTemp(tempPath, encoded.Bytes); // CreateNew: an existing file with this name is never touched
            temp = tempPath;
            FaultInjection.At("after-temp");
            hooks?.AfterTempWritten?.Invoke(tempPath);

            ImagePipeline.Validate(File.ReadAllBytes(tempPath), encoded.Format, encoded.Width, encoded.Height);
            File.SetAttributes(tempPath, FileAttributes.Normal);
            hooks?.BeforeFinalize?.Invoke();

            foreach (var candidate in OutputNaming.Candidates(source, encoded.Format, op.NameSuffix))
            {
                try
                {
                    File.Move(tempPath, candidate, overwrite: false); // MoveFileEx without REPLACE_EXISTING: fails if the name is taken
                    temp = null;
                    return ConversionResult.Ok(id, candidate, Convert.ToHexString(SHA256.HashData(encoded.Bytes)));
                }
                catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists) { }
            }
            return ConversionResult.Fail(id, ErrorCode.NoFreeOutputName, $"No free output name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
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
            if (temp is not null) TryDelete(temp);
        }
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
