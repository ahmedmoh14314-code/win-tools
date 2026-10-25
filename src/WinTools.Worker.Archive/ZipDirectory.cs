using System.Buffers.Binary;
using System.IO.Compression;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Archive;

/// <summary>A ZIP the worker refuses, with the reason the user is given (V2 PR 5.3).</summary>
internal sealed class ZipRefused(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
}

/// <summary>WinZip's AES field (0x9901): AE-1 or AE-2, strength 1-3 (128, 192, 256 bits), and the method inside.</summary>
internal sealed record AesField(int Version, int Strength, ushort Method)
{
    public int SaltLength => 4 + Strength * 4;
    public int KeyLength => 8 + Strength * 8;
}

/// <summary>One central-directory record, with what .NET's ZipArchive does not expose: where the entry's local header is,
/// its flags and its AES field. <paramref name="Name"/> is decoded as .NET decodes it (UTF-8, flag or not; measured).</summary>
internal sealed record ZipRecord(string Name, ushort Flags, ushort Method, uint Crc, long Packed, long Size, long LocalHeader, AesField? Aes)
{
    public bool IsEncrypted => (Flags & 1) != 0;
    public bool HasDataDescriptor => (Flags & 8) != 0;
}

/// <summary>
/// Reads a ZIP's central directory itself, only to find encrypted entries' data (V2 PR 5.3, ADR-0027). .NET's
/// <see cref="ZipArchive"/> stays the reader of names, sizes and check values; this one must agree with it on the entry
/// count and, entry by entry, on the name, the CRC, both sizes and the encrypted flag, or the archive is refused as
/// damaged. It also takes ZIP64's end record on the same fields .NET does. Two parsers that disagree about one file are
/// how archive checks get bypassed (PR 5.2's TAR lesson; evaluator, PR 5.3: two directories in one file).
/// </summary>
internal static class ZipDirectory
{
    private const uint EndSignature = 0x06054B50, Zip64LocatorSignature = 0x07064B50, Zip64EndSignature = 0x06064B50;
    private const uint CentralSignature = 0x02014B50, LocalSignature = 0x04034B50;

    private static ZipRefused Damaged(string why) => new(ErrorCode.DecodeFailed, $"The archive is damaged ({why}).");

    private static ZipRefused Unsupported(string why) => new(ErrorCode.UnsupportedContent, $"The archive uses {why}, which WinTools does not read.");

    /// <summary>The records in directory order, checked against the archive .NET opened.</summary>
    public static IReadOnlyList<ZipRecord> Read(Stream file, ZipArchive zip)
    {
        var records = Read(file);
        var entries = zip.Entries;
        if (records.Count != entries.Count) throw Damaged("its directory can be read two ways");
        for (var i = 0; i < records.Count; i++)
        {
            var (r, e) = (records[i], entries[i]);
            if (r.Crc != e.Crc32 || r.Packed != e.CompressedLength || r.Size != e.Length || r.IsEncrypted != e.IsEncrypted
                || !string.Equals(r.Name, e.FullName, StringComparison.Ordinal))
                throw Damaged("its directory can be read two ways");
        }
        return records;
    }

    private static List<ZipRecord> Read(Stream file)
    {
        var length = file.Length;
        var tailLength = (int)Math.Min(length, 22 + 0xFFFF);
        var tail = ReadAt(file, length - tailLength, tailLength);
        var end = -1;
        for (var i = tailLength - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndSignature) { end = i; break; }
        if (end < 0) throw Damaged("no end record");
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4));
        long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        // ZIP64 on the fields .NET's ZipArchive takes it on (this disk, the entry count, the directory's offset), so both
        // read the same directory; the names are compared as well.
        if ((disk == 0xFFFF || count == 0xFFFF || offset == 0xFFFFFFFF) && end >= 20
            && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end - 20)) == Zip64LocatorSignature)
        {
            var at = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(end - 12));
            if (at < 0 || at > length - 56) throw Damaged("a ZIP64 end record outside the file");
            var z = ReadAt(file, at, 56);
            if (BinaryPrimitives.ReadUInt32LittleEndian(z) != Zip64EndSignature) throw Damaged("a ZIP64 end record not where it is said to be");
            count = BinaryPrimitives.ReadInt64LittleEndian(z.AsSpan(32));
            size = BinaryPrimitives.ReadInt64LittleEndian(z.AsSpan(40));
            offset = BinaryPrimitives.ReadInt64LittleEndian(z.AsSpan(48));
        }
        if (count < 0 || count > ArchiveLimits.MaxEntries || offset < 0 || size < 0 || offset > length || size > length - offset)
            throw Damaged("a directory outside the file");
        var records = new List<ZipRecord>((int)count);

        using var reader = new BinaryReader(new BufferedStream(new Window(file, offset, size), 1 << 16), System.Text.Encoding.UTF8, leaveOpen: true);
        var fixedPart = new byte[46];
        for (var i = 0; i < count; i++)
        {
            if (reader.Read(fixedPart, 0, 46) != 46 || BinaryPrimitives.ReadUInt32LittleEndian(fixedPart) != CentralSignature) throw Damaged("a broken directory record");
            var f = fixedPart.AsSpan();
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(f[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(f[10..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(f[16..]);
            long packed = BinaryPrimitives.ReadUInt32LittleEndian(f[20..]);
            long unpacked = BinaryPrimitives.ReadUInt32LittleEndian(f[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(f[28..]), extraLength = BinaryPrimitives.ReadUInt16LittleEndian(f[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(f[32..]);
            long local = BinaryPrimitives.ReadUInt32LittleEndian(f[42..]);
            var nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength) throw Damaged("a broken directory record");
            var extra = reader.ReadBytes(extraLength);
            if (extra.Length != extraLength) throw Damaged("a broken directory record");
            Skip(reader, commentLength);
            for (var x = 0; x + 4 <= extra.Length;)
            {
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(x));
                int fieldLength = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(x + 2));
                if (x + 4 + fieldLength > extra.Length) break;
                var field = extra.AsSpan(x + 4, fieldLength);
                if (tag == 0x0001)
                {
                    // ZIP64: only the fields saturated in the record, in this order.
                    var at = 0;
                    if (unpacked == 0xFFFFFFFF && at + 8 <= field.Length) { unpacked = BinaryPrimitives.ReadInt64LittleEndian(field[at..]); at += 8; }
                    if (packed == 0xFFFFFFFF && at + 8 <= field.Length) { packed = BinaryPrimitives.ReadInt64LittleEndian(field[at..]); at += 8; }
                    if (local == 0xFFFFFFFF && at + 8 <= field.Length) local = BinaryPrimitives.ReadInt64LittleEndian(field[at..]);
                }
                x += 4 + fieldLength;
            }
            var record = new ZipRecord(System.Text.Encoding.UTF8.GetString(nameBytes), flags, method, crc, packed, unpacked, local, AesIn(extra));
            if (record.IsEncrypted) Check(record);
            records.Add(record);
        }
        return records;
    }

    /// <summary>WinZip's AES field among an entry's extra fields, or null.</summary>
    private static AesField? AesIn(ReadOnlySpan<byte> extra)
    {
        AesField? aes = null;
        for (var x = 0; x + 4 <= extra.Length;)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(extra[x..]);
            int fieldLength = BinaryPrimitives.ReadUInt16LittleEndian(extra[(x + 2)..]);
            if (x + 4 + fieldLength > extra.Length) break;
            var field = extra.Slice(x + 4, fieldLength);
            if (tag == 0x9901 && fieldLength == 7 && field[2] == 'A' && field[3] == 'E')
                aes = new(BinaryPrimitives.ReadUInt16LittleEndian(field), field[4], BinaryPrimitives.ReadUInt16LittleEndian(field[5..]));
            x += 4 + fieldLength;
        }
        return aes;
    }

    /// <summary>What an encrypted entry must be for this worker to read it.</summary>
    private static void Check(ZipRecord r)
    {
        if ((r.Flags & 0x40) != 0 || (r.Flags & 0x2000) != 0) throw Unsupported("PKWARE's strong encryption");
        // An AES description on an entry of another method was used and crashed the worker (evaluator, PR 5.3).
        if (r.Method != 99 && r.Aes is not null) throw Unsupported("an AES description on an entry that is not AES");
        if (r.Method == 99)
        {
            if (r.Aes is not { } aes) throw Unsupported("an AES entry without its description");
            if (aes.Version is not (1 or 2) || aes.Strength is not (1 or 2 or 3)) throw Unsupported("an AES variant");
            if (aes.Method is not (0 or 8)) throw Unsupported("a compression method inside AES");
        }
        else if (r.Method is not (0 or 8)) throw Unsupported("an encrypted compression method");
    }

    /// <summary>
    /// Where an entry's packed data starts, from its local header, which must be where the directory says, encrypted too,
    /// with the same method and the same AES description, and with the data inside the file. WinZip's AES authenticates
    /// only the encrypted bytes: a directory relabelled AE-2, Stored or shorter passed (evaluator, PR 5.3), so the two
    /// headers must agree. <paramref name="time"/> is the local header's file time (ZipCrypto's check byte with a data
    /// descriptor).
    /// </summary>
    public static long DataStart(Stream file, ZipRecord r, out ushort time)
    {
        if (r.LocalHeader < 0 || r.LocalHeader > file.Length - 30) throw Damaged("an entry outside the file");
        var header = ReadAt(file, r.LocalHeader, 30);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalSignature) throw Damaged("an entry is not where its directory says");
        if ((BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)) & 1) == 0) throw Damaged("an entry is encrypted in one header only");
        time = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)), extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        var start = r.LocalHeader + 30 + nameLength + extraLength;
        if (r.Packed < 0 || start > file.Length - r.Packed) throw Damaged("an entry is cut short");
        var extra = ReadAt(file, r.LocalHeader + 30 + nameLength, extraLength);
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) != r.Method || AesIn(extra) != r.Aes)
            throw Damaged("an entry's two headers describe it differently");
        return start;
    }

    public static byte[] ReadAt(Stream file, long at, int count)
    {
        var bytes = new byte[count];
        file.Position = at;
        if (file.ReadAtLeast(bytes, count, throwOnEndOfStream: false) != count) throw Damaged("it is cut short");
        return bytes;
    }

    private static void Skip(BinaryReader reader, int count)
    {
        if (reader.ReadBytes(count).Length != count) throw Damaged("a broken directory record");
    }
}

/// <summary>A read-only view of <paramref name="length"/> bytes of a file from <paramref name="start"/>; it seeks before each
/// read, so other readers of the same stream do not move it.</summary>
internal sealed class Window(Stream file, long start, long length) : Stream
{
    private long _position;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var take = (int)Math.Min(buffer.Length, length - _position);
        if (take <= 0) return 0;
        file.Position = start + _position;
        var n = file.Read(buffer[..take]);
        if (n == 0) throw new ZipRefused(ErrorCode.DecodeFailed, "The archive is cut short.");
        _position += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
