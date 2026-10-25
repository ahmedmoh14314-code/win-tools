namespace WinTools.Core.Files;

/// <summary>
/// CRC-32 (ISO-HDLC: the PNG, ZIP and GZIP check value), table-driven and incremental; .NET's own lives in a separate
/// package.
/// </summary>
public sealed class Crc32
{
    private static readonly uint[] Table = Build();
    private uint _crc = 0xFFFFFFFF;

    public uint Value => ~_crc;

    public void Append(ReadOnlySpan<byte> data)
    {
        var crc = _crc;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    public static uint Of(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Append(data);
        return crc.Value;
    }

    private static uint[] Build()
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
