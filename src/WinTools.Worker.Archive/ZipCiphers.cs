using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Archive;

/// <summary>PKWARE's traditional encryption, "ZipCrypto" (APPNOTE §6.1): three keys that follow the plain text.</summary>
internal sealed class ZipCryptoKeys
{
    private static readonly uint[] Table = BuildTable();
    private uint _k0 = 0x12345678, _k1 = 0x23456789, _k2 = 0x34567890;

    public ZipCryptoKeys(ReadOnlySpan<byte> password)
    {
        foreach (var b in password) Update(b);
    }

    private void Update(byte plain)
    {
        _k0 = Table[(_k0 ^ plain) & 0xFF] ^ (_k0 >> 8);
        _k1 = unchecked((_k1 + (_k0 & 0xFF)) * 134775813 + 1);
        _k2 = Table[(_k2 ^ (byte)(_k1 >> 24)) & 0xFF] ^ (_k2 >> 8);
    }

    public void Decrypt(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            var t = (uint)(ushort)(_k2 | 2);
            data[i] ^= (byte)((t * (t ^ 1)) >> 8);
            Update(data[i]);
        }
    }

    /// <summary>
    /// Whether the password lets the entry's 12-byte header through: its last byte must be the CRC's high byte, or the
    /// file time's high byte when the sizes follow the data (APPNOTE §6.1.6). A wrong password passes 1 time in 256;
    /// the CRC-32 decides in the end. The keys, ready for the data, come out when it passes.
    /// </summary>
    public static bool Accepts(ReadOnlySpan<byte> password, ReadOnlySpan<byte> header, byte check, out ZipCryptoKeys keys)
    {
        keys = new ZipCryptoKeys(password);
        Span<byte> plain = stackalloc byte[12];
        header[..12].CopyTo(plain);
        keys.Decrypt(plain);
        return plain[11] == check;
    }

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}

/// <summary>ZipCrypto's plain text, read through the keys.</summary>
internal sealed class ZipCryptoStream(Stream encrypted, ZipCryptoKeys keys) : ReadOnlyStream
{
    public override int Read(Span<byte> buffer)
    {
        var n = encrypted.Read(buffer);
        keys.Decrypt(buffer[..n]);
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) encrypted.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// WinZip's AES (AE-1 and AE-2): AES-CTR with a little-endian counter from 1 over the encrypted bytes, which are also
/// authenticated with HMAC-SHA1. <b>The first 10 bytes of the HMAC are compared, in constant time, with the code after the
/// data when the data ends</b>, and a mismatch is an error: AE-2 entries carry no CRC, so this is their only check, and
/// the bytes read so far are discarded by the caller. The keys are zeroed when the stream is disposed.
/// </summary>
internal sealed class WinZipAesStream : ReadOnlyStream
{
    public const int AuthLength = 10, VerifierLength = 2, Iterations = 1000;
    private const int Blocks = 256;

    private readonly Stream _source;
    private readonly Aes _aes;
    private readonly IncrementalHash _hmac;
    private readonly byte[] _counters = new byte[Blocks * 16], _keyStream = new byte[Blocks * 16];
    private long _remaining;
    private ulong _counter = 1;
    private int _used = Blocks * 16;
    private bool _verified;

    /// <param name="source">The entry's bytes after the salt and the verifier: the encrypted data, then the code.</param>
    public WinZipAesStream(Stream source, long encryptedLength, byte[] aesKey, byte[] hmacKey)
    {
        (_source, _remaining) = (source, encryptedLength);
        _aes = Aes.Create();
        _aes.Key = aesKey;
        _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, hmacKey);
        CryptographicOperations.ZeroMemory(aesKey);
        CryptographicOperations.ZeroMemory(hmacKey);
    }

    /// <summary>The AES key, the HMAC key and the 2-byte verifier from the password and the entry's salt.</summary>
    public static (byte[] AesKey, byte[] HmacKey, byte[] Verifier) Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int keyLength)
    {
        var keys = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA1, keyLength * 2 + VerifierLength);
        var result = (keys[..keyLength], keys[keyLength..(keyLength * 2)], keys[(keyLength * 2)..]);
        CryptographicOperations.ZeroMemory(keys);
        return result;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remaining == 0)
        {
            Verify();
            return 0;
        }
        var n = _source.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        if (n == 0) throw new ZipRefused(ErrorCode.DecodeFailed, "The archive is cut short.");
        _remaining -= n;
        var data = buffer[..n];
        _hmac.AppendData(data);
        for (var i = 0; i < n;)
        {
            if (_used == _keyStream.Length) Refill();
            var take = Math.Min(n - i, _keyStream.Length - _used);
            for (var j = 0; j < take; j++) data[i + j] ^= _keyStream[_used + j];
            (i, _used) = (i + take, _used + take);
        }
        return n;
    }

    private void Refill()
    {
        for (var b = 0; b < Blocks; b++) BinaryPrimitives.WriteUInt64LittleEndian(_counters.AsSpan(b * 16), _counter++);
        _aes.EncryptEcb(_counters, _keyStream, PaddingMode.None);
        _used = 0;
    }

    private void Verify()
    {
        if (_verified) return;
        Span<byte> code = stackalloc byte[AuthLength];
        if (_source.ReadAtLeast(code, AuthLength, throwOnEndOfStream: false) != AuthLength) throw new ZipRefused(ErrorCode.DecodeFailed, "The archive is cut short.");
        Span<byte> mac = stackalloc byte[20];
        _hmac.GetHashAndReset(mac);
        if (!CryptographicOperations.FixedTimeEquals(mac[..AuthLength], code))
            throw new ZipRefused(ErrorCode.DecodeFailed, "An entry is damaged, or the password is not its own (its authentication code does not match); nothing was kept.");
        _verified = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            _hmac.Dispose();
            _source.Dispose();
            CryptographicOperations.ZeroMemory(_keyStream);
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// An entry's plain bytes, decompressed when Deflate was used. For AES, when they end, whatever the decompressor left of
/// the decrypted stream is read too, so the code is always checked before the end is reported. ZipCrypto has no code to
/// check, and its rest is never read (evaluator, PR 5.3: padding read for every record pointing at it).
/// </summary>
internal sealed class UnlockedEntry(Stream plain, Stream decrypted, bool drain) : ReadOnlyStream
{
    public override int Read(Span<byte> buffer)
    {
        var n = plain.Read(buffer);
        if (n == 0 && drain && buffer.Length > 0)
        {
            var rest = new byte[4096];
            while (decrypted.Read(rest) > 0) { }
        }
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            plain.Dispose();
            decrypted.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The read-only stream plumbing the decrypting streams share.</summary>
internal abstract class ReadOnlyStream : Stream
{
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public abstract override int Read(Span<byte> buffer);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// An encrypted ZIP opened with a typed password (V2 PR 5.3, ADR-0027), through the source's own handle: the file is
/// opened once. <b>One archive, one password</b>, in the byte form each scheme was written with: before anything is
/// extracted, every ZipCrypto entry's header narrows the ZipCrypto candidates, and the first AES entry's verifier narrows
/// the AES ones, separately, since WinRAR writes AES as UTF-8 and ZipCrypto in the code page (evaluator, PR 5.3). A wrong
/// candidate that passes one 1-in-256 check byte therefore does not decide the archive; no candidate left for a scheme the
/// archive uses is <see cref="ErrorCode.PasswordIncorrect"/>. When an entry still accepts more than one, each is proven
/// before it is used: ZipCrypto by decrypting the entry to its CRC (within the entry's size and the bomb rule), AES by its
/// authentication code. Encrypted entries may not share data. The worker's copies of the password bytes are zeroed when
/// it is done, whatever the outcome; the caller's are left alone.
/// </summary>
internal sealed class ZipLocks : IDisposable
{
    private readonly FileStream _file;
    private readonly IReadOnlyList<ZipRecord> _records;
    private readonly List<byte[]> _owned;
    private readonly List<byte[]> _zipCrypto, _aes;

    public ZipLocks(FileStream file, ZipArchive zip, IReadOnlyList<byte[]> candidates)
    {
        _file = file;
        _owned = [.. candidates.Select(c => c.ToArray())];
        (_zipCrypto, _aes) = ([.. _owned], [.. _owned]);
        try
        {
            _records = ZipDirectory.Read(_file, zip);
            var ranges = new List<(long Start, long End)>();
            bool usesZipCrypto = false, usesAes = false;
            foreach (var r in _records)
            {
                if (!r.IsEncrypted) continue;
                var start = ZipDirectory.DataStart(_file, r, out var time);
                ranges.Add((start, start + r.Packed));
                if (r.Aes is { } aes)
                {
                    if (usesAes) continue;
                    usesAes = true;
                    var head = Head(start, r, aes);
                    _aes.RemoveAll(c => !Verifies(c, head, aes));
                }
                else
                {
                    usesZipCrypto = true;
                    var header = Header(start, r);
                    _zipCrypto.RemoveAll(c => !ZipCryptoKeys.Accepts(c, header, Check(r, time), out _));
                }
            }
            // Many records on one encrypted entry would each decrypt and authenticate all of it, counted by no limit.
            ranges.Sort();
            for (var i = 1; i < ranges.Count; i++)
                if (ranges[i].Start < ranges[i - 1].End) throw new ZipRefused(ErrorCode.DecodeFailed, "The archive is damaged (encrypted entries share their data).");
            if ((usesZipCrypto && _zipCrypto.Count == 0) || (usesAes && _aes.Count == 0)) throw Wrong();
        }
        catch
        {
            Zero();
            throw;
        }
    }

    /// <summary>Whether this record's CRC is not to be checked: AE-2's is 0 by design. One that is not 0 is checked,
    /// whatever the record says (evaluator, PR 5.3: AE-1 relabelled AE-2).</summary>
    public bool SkipsCrc(int index) => _records[index] is { Aes.Version: 2, Crc: 0 };

    /// <summary>The record's method, for Info: AES and its strength, or ZipCrypto.</summary>
    public static string MethodOf(ZipRecord r) => r.Aes is { } aes ? $"AES-{64 + aes.Strength * 64}" : "ZipCrypto";

    private static ZipRefused Wrong() => new(ErrorCode.PasswordIncorrect, "The password does not open the archive; nothing was extracted.");

    private static byte Check(ZipRecord r, ushort time) => r.HasDataDescriptor ? (byte)(time >> 8) : (byte)(r.Crc >> 24);

    private byte[] Header(long start, ZipRecord r)
    {
        if (r.Packed < 12) throw new ZipRefused(ErrorCode.DecodeFailed, "The archive is damaged (an entry is shorter than its encryption header).");
        return ZipDirectory.ReadAt(_file, start, 12);
    }

    private byte[] Head(long start, ZipRecord r, AesField aes)
    {
        if (r.Packed < aes.SaltLength + WinZipAesStream.VerifierLength + WinZipAesStream.AuthLength)
            throw new ZipRefused(ErrorCode.DecodeFailed, "The archive is damaged (an entry is shorter than its encryption fields).");
        return ZipDirectory.ReadAt(_file, start, aes.SaltLength + WinZipAesStream.VerifierLength);
    }

    private static bool Verifies(byte[] password, byte[] head, AesField aes)
    {
        var (key, mac, verifier) = WinZipAesStream.Derive(password, head.AsSpan(0, aes.SaltLength), aes.KeyLength);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(mac);
        return CryptographicOperations.FixedTimeEquals(verifier, head.AsSpan(aes.SaltLength, WinZipAesStream.VerifierLength));
    }

    /// <summary>The entry's plain bytes, decrypted and decompressed. Entries are read one after another.</summary>
    public Stream Open(int index)
    {
        var r = _records[index];
        var start = ZipDirectory.DataStart(_file, r, out var time);
        if (r.Aes is { } aes)
        {
            var head = Head(start, r, aes);
            var fields = aes.SaltLength + WinZipAesStream.VerifierLength;
            var passing = _aes.Where(c => Verifies(c, head, aes)).ToList();
            if (passing.Count == 0) throw Wrong();
            // Two candidates through a 16-bit verifier: the one whose code matches the data (no decompression needed).
            var chosen = passing.Count == 1 ? passing[0] : passing.FirstOrDefault(c => Authenticates(c, start, r, aes)) ?? passing[0];
            var (key, mac, _) = WinZipAesStream.Derive(chosen, head.AsSpan(0, aes.SaltLength), aes.KeyLength);
            var decrypted = new WinZipAesStream(new Window(_file, start + fields, r.Packed - fields), r.Packed - fields - WinZipAesStream.AuthLength, key, mac);
            return Inflated(decrypted, aes.Method, drain: true);
        }
        var header = Header(start, r);
        var accepted = _zipCrypto.Where(c => ZipCryptoKeys.Accepts(c, header, Check(r, time), out _)).ToList();
        if (accepted.Count == 0) throw Wrong();
        // Two candidates through a 1-in-256 check byte: the one whose decrypted entry has its CRC (evaluator, PR 5.3).
        var password = accepted.Count == 1 ? accepted[0] : accepted.FirstOrDefault(c => Proves(c, start, r, header, time)) ?? accepted[0];
        ZipCryptoKeys.Accepts(password, header, Check(r, time), out var keys);
        return Inflated(new ZipCryptoStream(new Window(_file, start + 12, r.Packed - 12), keys), r.Method, drain: false);
    }

    /// <summary>Whether a ZipCrypto candidate decrypts the entry to its size and CRC, reading no more than its size and
    /// within the bomb rule; nothing is written.</summary>
    private bool Proves(byte[] password, long start, ZipRecord r, byte[] header, ushort time)
    {
        ZipCryptoKeys.Accepts(password, header, Check(r, time), out var keys);
        using var plain = Inflated(new ZipCryptoStream(new Window(_file, start + 12, r.Packed - 12), keys), r.Method, drain: false);
        var crc = new Crc32();
        var buffer = new byte[1 << 16];
        long total = 0;
        try
        {
            int n;
            while ((n = plain.Read(buffer)) > 0)
            {
                total += n;
                if (total > r.Size || ArchiveLimits.IsBomb(total, r.Packed)) return false;
                crc.Append(buffer.AsSpan(0, n));
            }
        }
        catch (InvalidDataException) { return false; }
        return total == r.Size && crc.Value == r.Crc;
    }

    /// <summary>Whether an AES candidate's code matches the entry's encrypted bytes.</summary>
    private bool Authenticates(byte[] password, long start, ZipRecord r, AesField aes)
    {
        var head = Head(start, r, aes);
        var fields = aes.SaltLength + WinZipAesStream.VerifierLength;
        var (key, mac, _) = WinZipAesStream.Derive(password, head.AsSpan(0, aes.SaltLength), aes.KeyLength);
        using var stream = new WinZipAesStream(new Window(_file, start + fields, r.Packed - fields), r.Packed - fields - WinZipAesStream.AuthLength, key, mac);
        var buffer = new byte[1 << 16];
        try
        {
            while (stream.Read(buffer) > 0) { }
            return true;
        }
        catch (ZipRefused) { return false; }
    }

    private static UnlockedEntry Inflated(Stream decrypted, ushort method, bool drain) =>
        new(method == 8 ? new DeflateStream(decrypted, CompressionMode.Decompress, leaveOpen: true) : decrypted, decrypted, drain);

    private void Zero()
    {
        foreach (var c in _owned) CryptographicOperations.ZeroMemory(c);
    }

    /// <summary>Zeroes the worker's copies of the password bytes; the file belongs to the source.</summary>
    public void Dispose() => Zero();
}
