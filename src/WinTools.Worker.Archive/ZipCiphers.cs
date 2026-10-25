using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
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
/// An entry's plain bytes, decompressed when Deflate was used. When they end, whatever the decompressor left of the
/// decrypted stream is read too, so the AES code is always checked before the end is reported.
/// </summary>
internal sealed class UnlockedEntry(Stream plain, Stream decrypted) : ReadOnlyStream
{
    public override int Read(Span<byte> buffer)
    {
        var n = plain.Read(buffer);
        if (n == 0 && buffer.Length > 0)
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
/// An encrypted ZIP opened with a typed password (V2 PR 5.3, ADR-0027). <b>One archive, one password:</b> before
/// anything is extracted, every ZipCrypto entry's header and the first AES entry's verifier are checked, and only the
/// password bytes all of them accept remain, so a wrong candidate that passes one 1-in-256 check byte does not decide
/// the archive. No candidate left is <see cref="ErrorCode.PasswordIncorrect"/>. Each entry is then opened with the
/// first remaining candidate it accepts.
/// </summary>
internal sealed class ZipLocks : IDisposable
{
    private readonly FileStream _file;
    private readonly IReadOnlyList<ZipRecord> _records;
    private readonly List<byte[]> _candidates;

    public ZipLocks(string path, ZipArchive zip, IReadOnlyList<byte[]> candidates)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
        try
        {
            _records = ZipDirectory.Read(_file, zip);
            _candidates = [.. candidates];
            var aesChecked = false;
            for (var i = 0; i < _records.Count && _candidates.Count > 0; i++)
            {
                var r = _records[i];
                if (!r.IsEncrypted) continue;
                var start = ZipDirectory.DataStart(_file, r, out var time);
                if (r.Aes is { } aes)
                {
                    if (aesChecked) continue;
                    aesChecked = true;
                    var head = Head(start, r, aes);
                    _candidates.RemoveAll(c => !Verifies(c, head, aes));
                }
                else
                {
                    var header = Header(start, r);
                    _candidates.RemoveAll(c => !ZipCryptoKeys.Accepts(c, header, Check(r, time), out _));
                }
            }
            if (_candidates.Count == 0) throw Wrong();
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    /// <summary>Whether this record is AE-2, whose CRC field is 0 by design and must not be checked.</summary>
    public bool SkipsCrc(int index) => _records[index].Aes is { Version: 2 };

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

    /// <summary>The entry's plain bytes, decrypted and decompressed. Its own reader: entries are read one after another.</summary>
    public Stream Open(int index)
    {
        var r = _records[index];
        var start = ZipDirectory.DataStart(_file, r, out var time);
        Stream decrypted;
        ushort method;
        if (r.Aes is { } aes)
        {
            var head = Head(start, r, aes);
            foreach (var c in _candidates)
            {
                var (key, mac, verifier) = WinZipAesStream.Derive(c, head.AsSpan(0, aes.SaltLength), aes.KeyLength);
                if (!CryptographicOperations.FixedTimeEquals(verifier, head.AsSpan(aes.SaltLength, WinZipAesStream.VerifierLength)))
                {
                    CryptographicOperations.ZeroMemory(key);
                    CryptographicOperations.ZeroMemory(mac);
                    continue;
                }
                var fields = aes.SaltLength + WinZipAesStream.VerifierLength;
                var window = new Window(_file, start + fields, r.Packed - fields);
                decrypted = new WinZipAesStream(window, r.Packed - fields - WinZipAesStream.AuthLength, key, mac);
                return Inflated(decrypted, aes.Method);
            }
            throw Wrong();
        }
        var header = Header(start, r);
        foreach (var c in _candidates)
        {
            if (!ZipCryptoKeys.Accepts(c, header, Check(r, time), out var keys)) continue;
            decrypted = new ZipCryptoStream(new Window(_file, start + 12, r.Packed - 12), keys);
            method = r.Method;
            return Inflated(decrypted, method);
        }
        throw Wrong();
    }

    private static UnlockedEntry Inflated(Stream decrypted, ushort method) =>
        new(method == 8 ? new DeflateStream(decrypted, CompressionMode.Decompress, leaveOpen: true) : decrypted, decrypted);

    public void Dispose()
    {
        foreach (var c in _candidates) CryptographicOperations.ZeroMemory(c);
        _file.Dispose();
    }
}
