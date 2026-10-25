using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace WinTools.Worker.Tests;

/// <summary>
/// Builds encrypted ZIPs byte by byte for the tests (V2 PR 5.3): ZipCrypto (PKWARE APPNOTE §6.1) and WinZip AES (AE-1,
/// AE-2, 128/192/256 bits), Stored or Deflate inside. Written from the specifications, and checked against WinRAR's
/// samples through the worker, so a misreading in both the worker and this builder shows up there. Tests that need
/// what WinRAR never writes (other strengths, bombs, damage in a known place) use it.
/// </summary>
public static class LockedZipBuilder
{
    public enum Cipher { None, ZipCrypto, Aes128, Aes192, Aes256 }

    /// <summary>One entry: a null content is a folder (never encrypted).</summary>
    public sealed record Item(string Name, byte[]? Content, Cipher Cipher = Cipher.Aes256, bool Deflate = true, int AesVersion = 1);

    /// <summary>Where each entry landed, for tests that damage one in a known place.</summary>
    public sealed record Placed(string Name, long LocalHeader, long Data, long Packed, long CentralRecord);

    private const ushort DosTime = (12 << 11) | (30 << 5), DosDate = ((2026 - 1980) << 9) | (10 << 5) | 25;

    public static byte[] Build(byte[] password, params Item[] items) => Build(password, out _, items);

    public static byte[] Build(byte[] password, out IReadOnlyList<Placed> placed, params Item[] items)
    {
        using var zip = new MemoryStream();
        var central = new List<(Item Item, byte[] Name, byte[] Extra, uint Crc, long Packed, long Size, long Offset, ushort Flags, ushort Method)>();
        foreach (var item in items)
        {
            var name = Encoding.UTF8.GetBytes(item.Name);
            var content = item.Content ?? [];
            var crc = WinTools.Core.Files.Crc32.Of(content);
            var inner = item.Content is not null && item.Deflate ? Deflate(content) : content;
            var cipher = item.Content is null ? Cipher.None : item.Cipher;
            var innerMethod = (ushort)(item.Content is not null && item.Deflate ? 8 : 0);
            byte[] packed, extra = [];
            ushort flags = 0x0800, method = innerMethod;
            switch (cipher)
            {
                case Cipher.None:
                    packed = inner;
                    break;
                case Cipher.ZipCrypto:
                    packed = ZipCrypto(password, inner, (byte)(crc >> 24));
                    flags |= 1;
                    break;
                default:
                    var strength = cipher switch { Cipher.Aes128 => 1, Cipher.Aes192 => 2, _ => 3 };
                    packed = WinZipAes(password, inner, strength);
                    extra = [0x01, 0x99, 7, 0, (byte)item.AesVersion, 0, (byte)'A', (byte)'E', (byte)strength, (byte)innerMethod, 0];
                    flags |= 1;
                    method = 99;
                    if (item.AesVersion == 2) crc = 0;
                    break;
            }
            var offset = zip.Position;
            var header = new byte[30];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034B50);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)(method == 99 ? 51 : 20));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), flags);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), method);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), (uint)packed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), (uint)content.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)extra.Length);
            zip.Write(header);
            zip.Write(name);
            zip.Write(extra);
            zip.Write(packed);
            central.Add((item, name, extra, crc, packed.Length, content.Length, offset, flags, method));
        }
        var cdStart = zip.Position;
        var places = new List<Placed>();
        foreach (var c in central)
        {
            places.Add(new(c.Item.Name, c.Offset, c.Offset + 30 + c.Name.Length + c.Extra.Length, c.Packed, zip.Position));
            var record = new byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014B50);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)(c.Method == 99 ? 51 : 20));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), c.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), c.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), c.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), (uint)c.Packed);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)c.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)c.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), (ushort)c.Extra.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(38), c.Item.Content is null ? 0x10u : 0x20u);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), (uint)c.Offset);
            zip.Write(record);
            zip.Write(c.Name);
            zip.Write(c.Extra);
        }
        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)central.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)central.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)(zip.Position - cdStart));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)cdStart);
        zip.Write(end);
        placed = places;
        return zip.ToArray();
    }

    private static byte[] Deflate(byte[] content)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(content);
        return output.ToArray();
    }

    /// <summary>APPNOTE §6.1.5: a 12-byte header (random, its last byte the CRC's high byte), then the data, each byte
    /// XORed with the key stream as the keys follow the plain text.</summary>
    public static byte[] ZipCrypto(byte[] password, byte[] data, byte check)
    {
        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        void Update(byte b)
        {
            k0 = Crc(k0, b);
            k1 = unchecked((k1 + (k0 & 0xFF)) * 134775813 + 1);
            k2 = Crc(k2, (byte)(k1 >> 24));
        }
        byte Stream()
        {
            var t = (ushort)(k2 | 2);
            return (byte)((t * (t ^ 1)) >> 8);
        }
        foreach (var b in password) Update(b);
        var plain = new byte[12 + data.Length];
        RandomNumberGenerator.Fill(plain.AsSpan(0, 11));
        plain[11] = check;
        data.CopyTo(plain, 12);
        var output = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
        {
            output[i] = (byte)(plain[i] ^ Stream());
            Update(plain[i]);
        }
        return output;
    }

    /// <summary>The 12-byte ZipCrypto header decrypted with a password, to see whether its check byte lets it through.</summary>
    public static byte[] ZipCryptoDecryptHeader(byte[] password, ReadOnlySpan<byte> encrypted)
    {
        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        void Update(byte b)
        {
            k0 = Crc(k0, b);
            k1 = unchecked((k1 + (k0 & 0xFF)) * 134775813 + 1);
            k2 = Crc(k2, (byte)(k1 >> 24));
        }
        foreach (var b in password) Update(b);
        var plain = new byte[encrypted.Length];
        for (var i = 0; i < encrypted.Length; i++)
        {
            var t = (ushort)(k2 | 2);
            plain[i] = (byte)(encrypted[i] ^ (byte)((t * (t ^ 1)) >> 8));
            Update(plain[i]);
        }
        return plain;
    }

    private static uint Crc(uint crc, byte b)
    {
        var c = (crc ^ b) & 0xFF;
        for (var i = 0; i < 8; i++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return (crc >> 8) ^ c;
    }

    /// <summary>WinZip's AE-x: salt, a 2-byte verifier, AES-CTR (little-endian counter from 1), then the first 10 bytes of
    /// HMAC-SHA1 over the encrypted data. The keys come from PBKDF2-HMAC-SHA1 with 1,000 iterations.</summary>
    public static byte[] WinZipAes(byte[] password, byte[] data, int strength)
    {
        var keyLength = 8 + strength * 8;
        var salt = RandomNumberGenerator.GetBytes(4 + strength * 4);
        var keys = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1000, HashAlgorithmName.SHA1, keyLength * 2 + 2);
        using var aes = Aes.Create();
        aes.Key = keys[..keyLength];
        var encrypted = new byte[data.Length];
        var counter = new byte[16];
        var stream = new byte[16];
        for (var i = 0; i < data.Length; i += 16)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(counter, (ulong)(i / 16 + 1));
            aes.EncryptEcb(counter, stream, PaddingMode.None);
            for (var j = 0; j < 16 && i + j < data.Length; j++) encrypted[i + j] = (byte)(data[i + j] ^ stream[j]);
        }
        var auth = HMACSHA1.HashData(keys[keyLength..(keyLength * 2)], encrypted)[..10];
        return [.. salt, .. keys[(keyLength * 2)..], .. encrypted, .. auth];
    }
}
