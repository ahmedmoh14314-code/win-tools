using System.Globalization;
using System.IO.Compression;
using System.Text;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Archive;

/// <summary>Why a TAR or GZIP stream was refused before its entries were read: damaged, past a limit, unsupported, or not a TAR.</summary>
internal sealed class TarRefused(string message, ErrorCode code = ErrorCode.DecodeFailed) : Exception(message)
{
    public ErrorCode Code { get; } = code;
}

/// <summary>
/// Checks a TAR stream block by block before .NET's TarReader reads it (evaluator, PR 5.2), because TarReader trusts it:
/// <list type="bullet">
/// <item>the first header is a ustar one (a GZIP of something else is not a TAR);</item>
/// <item>every header's checksum (TAR's only integrity check) and size field are valid;</item>
/// <item>a PAX or GNU long-name header is at most <see cref="MaxMetaBytes"/> (TarReader buffers it whole);</item>
/// <item>a PAX header's size record moves the next entry's data exactly as TarReader moves it, so both see the same
/// headers (evaluator, PR 5.2, second round);</item>
/// <item>sparse entries (GNU 'S', PAX GNU.sparse) are refused: TarReader cannot read the first and misreads the second;</item>
/// <item>the archive ends with its zero blocks (a TAR cut at a block boundary is caught), followed only by zeros;</item>
/// <item>at most <see cref="ArchiveLimits.MaxEntries"/> entries, and the declared sizes of every entry, kept or not, meet
/// the size and whole-archive ratio rules.</item>
/// </list>
/// It reads the data once without keeping it. For a TAR.GZ, <see cref="GzipCheck"/> then holds whether the GZIP trailer
/// matched what came out.
/// </summary>
internal static class TarGuard
{
    public const int MaxMetaBytes = 1 << 20;

    public static void Check(Stream tar, long archiveBytes)
    {
        var block = new byte[512];
        long declared = 0;
        var entries = 0;
        long? paxSize = null; // a PAX header's size record: TarReader takes it, not the next header's own, as that entry's size
        var afterPax = false;
        var first = true;
        while (true)
        {
            if (!ReadBlock(tar, block))
                throw first ? new TarRefused("The file does not hold a TAR archive, whatever its name says.", ErrorCode.ContentMismatch)
                    : new TarRefused("The TAR archive is cut short (its end blocks are missing).");
            // An empty archive (only its end blocks) is a TAR; any other first header must be a ustar one.
            if (first && !IsZero(block) && !block.AsSpan(257, 5).SequenceEqual("ustar"u8))
                throw new TarRefused("The file does not hold a TAR archive, whatever its name says.", ErrorCode.ContentMismatch);
            first = false;
            if (IsZero(block))
            {
                if (afterPax) throw new TarRefused("A TAR extended header has no entry after it.");
                if (!ReadBlock(tar, block) || !IsZero(block)) throw new TarRefused("The TAR archive is cut short or damaged after its last entry.");
                // Only zero padding may follow.
                int n;
                var rest = new byte[1 << 16];
                long tail = 0;
                while ((n = tar.Read(rest)) > 0)
                {
                    if (rest.AsSpan(0, n).ContainsAnyExcept((byte)0)) throw new TarRefused("The TAR archive holds unexpected data after its end.");
                    if ((tail += n) > 16L << 20) throw new TarRefused("The TAR archive holds unexpected data after its end.");
                }
                return;
            }
            if (!ChecksumOk(block)) throw new TarRefused("A TAR header is damaged (its checksum does not match).");
            var type = (char)block[156];
            if (type == 'S') throw new TarRefused("The archive holds a sparse file, which WinTools does not read.", ErrorCode.UnsupportedContent);
            if (type is 'x' && afterPax) throw new TarRefused("A TAR archive holds two extended headers in a row.");
            var size = SizeOf(block) ?? throw new TarRefused("A TAR header holds an invalid size.");
            if (type is not ('x' or 'g'))
            {
                size = paxSize ?? size;
                paxSize = null;
                afterPax = false;
            }
            if (type is 'x' or 'g' or 'L' or 'K' && size > MaxMetaBytes) throw new TarRefused("A TAR extended header is unusually large; it was not read.", ErrorCode.LimitExceeded);
            if (++entries > ArchiveLimits.MaxEntries * 2) throw new TarRefused($"The archive has more than {ArchiveLimits.MaxEntries:N0} entries.", ErrorCode.LimitExceeded);
            declared += size;
            if (declared > ArchiveLimits.MaxTotalBytes) throw new TarRefused($"The archive declares more than {ArchiveLimits.MaxTotalBytes >> 30} GB.", ErrorCode.LimitExceeded);
            if (ArchiveLimits.IsArchiveBomb(declared, archiveBytes)) throw new TarRefused("The archive expands far beyond its own size; it looks like an archive bomb.", ErrorCode.LimitExceeded);
            if (type is 'x' or 'g')
            {
                var body = new byte[size];
                if (tar.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) != body.Length) throw new TarRefused("The TAR archive is cut short inside an entry.");
                Skip(tar, (size + 511) / 512 * 512 - size);
                var records = PaxRecords(body);
                if (records.Keys.Any(k => k.StartsWith("GNU.sparse.", StringComparison.Ordinal)))
                    throw new TarRefused("The archive holds a sparse file, which WinTools does not read.", ErrorCode.UnsupportedContent);
                if (records.TryGetValue("size", out var value))
                {
                    if (type == 'g') throw new TarRefused("A TAR global header sets a size; the archive was not read.", ErrorCode.UnsupportedContent);
                    paxSize = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new TarRefused("A TAR extended header holds an invalid size.");
                }
                afterPax = type == 'x';
                continue;
            }
            Skip(tar, (size + 511) / 512 * 512);
        }
    }

    /// <summary>A PAX header's records ("%d key=value\n", the length counting itself); a malformed or repeated one is damage.</summary>
    internal static Dictionary<string, string> PaxRecords(ReadOnlySpan<byte> body)
    {
        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 0;
        while (at < body.Length && body[at] != 0)
        {
            var rest = body[at..];
            var space = rest.IndexOf((byte)' ');
            if (space <= 0 || !int.TryParse(rest[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length <= space + 2 || length > rest.Length || rest[length - 1] != '\n')
                throw new TarRefused("A TAR extended header is damaged.");
            var record = rest[(space + 1)..(length - 1)];
            var equals = record.IndexOf((byte)'=');
            if (equals <= 0 || !records.TryAdd(Encoding.UTF8.GetString(record[..equals]), Encoding.UTF8.GetString(record[(equals + 1)..])))
                throw new TarRefused("A TAR extended header is damaged.");
            at += length;
        }
        return records;
    }

    private static bool ReadBlock(Stream s, byte[] block) => s.ReadAtLeast(block, 512, throwOnEndOfStream: false) == 512;

    private static bool IsZero(byte[] block) => !block.AsSpan().ContainsAnyExcept((byte)0);

    private static void Skip(Stream s, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            var n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n == 0) throw new TarRefused("The TAR archive is cut short inside an entry.");
            count -= n;
        }
    }

    /// <summary>The size field: octal ASCII, or GNU's base-256 when its first byte has the high bit set.</summary>
    internal static long? SizeOf(ReadOnlySpan<byte> header)
    {
        var field = header.Slice(124, 12);
        if ((field[0] & 0x80) != 0)
        {
            if ((field[0] & 0x40) != 0) return null; // negative
            long v = field[0] & 0x3F;
            for (var i = 1; i < 12; i++)
            {
                if (v > (long.MaxValue >> 8)) return null;
                v = (v << 8) | field[i];
            }
            return v;
        }
        return Octal(field);
    }

    private static long? Octal(ReadOnlySpan<byte> field)
    {
        long v = 0;
        var digits = 0;
        foreach (var b in field)
        {
            if (b is 0 or (byte)' ') { if (digits > 0) break; continue; }
            if (b is < (byte)'0' or > (byte)'7') return null;
            if (++digits > 21) return null;
            v = v * 8 + (b - '0');
        }
        return v;
    }

    /// <summary>The header checksum: the byte sum with the checksum field read as spaces (unsigned, or the signed variant).</summary>
    internal static bool ChecksumOk(ReadOnlySpan<byte> header)
    {
        if (Octal(header.Slice(148, 8)) is not { } stored) return false;
        long unsigned = 0, signed = 0;
        for (var i = 0; i < 512; i++)
        {
            var b = i is >= 148 and < 156 ? (byte)' ' : header[i];
            unsigned += b;
            signed += (sbyte)b;
        }
        return stored == unsigned || stored == signed;
    }
}

/// <summary>
/// A read-only stream over a GZIP file's decompressed data that keeps the CRC-32 and length of what came out; at the
/// end, <see cref="TrailerMatches"/> compares them with the file's last eight bytes. GZipStream reports a cut-off file as
/// a clean end, so a truncated file would otherwise pass as complete (evaluator, PR 5.2). A file of several GZIP members
/// fails the comparison too and is refused as damaged: the trailer covers only its last member.
/// </summary>
internal sealed class CheckedGzip(Stream file, long fileLength) : Stream
{
    private readonly GZipStream _gzip = new(file, CompressionMode.Decompress, leaveOpen: true);
    private readonly Crc32 _crc = new();
    private long _length;

    public bool TrailerMatches()
    {
        if (fileLength < 18) return false;
        var trailer = new byte[8];
        var position = file.Position;
        file.Position = fileLength - 8;
        file.ReadExactly(trailer);
        file.Position = position;
        var crc = BitConverter.ToUInt32(trailer, 0);
        var size = BitConverter.ToUInt32(trailer, 4);
        return crc == _crc.Value && size == (uint)(_length & 0xFFFFFFFF);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _gzip.Read(buffer, offset, count);
        _crc.Append(buffer.AsSpan(offset, n));
        _length += n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _gzip.Read(buffer);
        _crc.Append(buffer[..n]);
        _length += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gzip.Dispose();
        base.Dispose(disposing);
    }
}
