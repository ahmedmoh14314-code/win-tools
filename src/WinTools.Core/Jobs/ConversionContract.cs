using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTools.Core.Jobs;

/// <summary>
/// One job sent from the host to the worker. The output goes next to the source unless <paramref name="OutputDirectory"/>
/// (from the settings) names a folder. Qualities (1–100) override the encoder defaults for conversions to JPEG and WebP.
/// </summary>
public sealed record ConversionRequest(string Id, string OperationId, string SourcePath,
    string? OutputDirectory = null, int? JpegQuality = null, int? WebPQuality = null);

/// <summary>What "Image info" reports. Facts read from the file; nothing is written.</summary>
public sealed record ImageInfo(string Format, int Width, int Height, long FileBytes, double DpiX, double DpiY, bool HasTransparency,
    int Orientation, bool HasExif, bool HasGps, bool HasXmp, bool HasIccProfile, bool HasTextOrComments, bool DeeperThan8Bits, bool HasOtherMetadata = false);

/// <summary>
/// Why a job did not produce an output. Stable names: they cross the process boundary and appear in logs.
/// <see cref="NothingToDo"/> is not a failure: the image already fits, or compression would not make it smaller.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ErrorCode>))]
public enum ErrorCode
{
    None,
    InvalidRequest,
    UnsupportedOperation,
    SourceNotFound,
    SourceNotReadable,
    InputTooLarge,
    ContentMismatch,
    UnsupportedContent,
    NothingToDo,
    DecodeFailed,
    ImageTooLarge,
    EncodeFailed,
    OutputDirectoryNotWritable,
    OutputDirectoryIsLink,
    OutputNameInvalid,
    DiskFull,
    NoFreeOutputName,
    OutputValidationFailed,
    WorkerCrashed,
    WorkerTimeout,
    WorkerProtocolError,
    Cancelled,
    Internal,
}

/// <summary>The worker's answer. Success carries the final output path; failure carries a code and a short message.</summary>
/// <param name="OutputSha256">Hex SHA-256 of the output the worker validated, so the host can check the file it finds is that one.</param>
public sealed record ConversionResult(string Id, bool Success, ErrorCode Error, string? Message, string? OutputPath, string? OutputSha256 = null,
    ImageInfo? Info = null)
{
    /// <summary>Success without a file: "Image info" reports facts only.</summary>
    public static ConversionResult Facts(string id, ImageInfo info) => new(id, true, ErrorCode.None, null, null, null, info);

    public static ConversionResult Ok(string id, string outputPath, string sha256) => new(id, true, ErrorCode.None, null, outputPath, sha256);
    public static ConversionResult Fail(string id, ErrorCode error, string message) => new(id, false, error, message, null);
}

/// <summary>
/// Line-delimited JSON framing shared by both ends. Non-ASCII is escaped, so the pipe carries ASCII only; paths must be
/// well-formed UTF-16 (<see cref="Files.PathPolicy"/>), since a lone surrogate would not survive the encoding.
/// Lines longer than <see cref="MaxLineLength"/> are rejected after they are read.
/// </summary>
public static class WorkerProtocol
{
    /// <summary>Longest line either side accepts (a request holds at most one long path).</summary>
    public const int MaxLineLength = 64 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Parses one line; null for anything malformed or oversized.</summary>
    public static T? Deserialize<T>(string? line) where T : class
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength) return null;
        try { return JsonSerializer.Deserialize<T>(line, Options); }
        catch (JsonException) { return null; }
    }
}
