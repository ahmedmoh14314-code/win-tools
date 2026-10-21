namespace WinTools.Core.Files;

/// <summary>PNG chunk surgery for lossless re-encoding: carry the source's metadata into a freshly encoded PNG.</summary>
public static class PngChunks
{
    /// <summary>
    /// Ancillary chunks that describe how to show or identify the image and do not depend on its pixel layout. Chunks
    /// such as tRNS, bKGD, sBIT, hIST and sPLT refer to the old pixel format or palette and are not copied.
    /// </summary>
    private static readonly string[] Carried = ["gAMA", "cHRM", "sRGB", "iCCP", "pHYs", "tEXt", "zTXt", "iTXt", "eXIf", "tIME"];

    /// <summary>
    /// Returns <paramref name="encoded"/> with the source's carried chunks inserted right after IHDR, in source order.
    /// Any chunk of those types that the encoder wrote itself is dropped, so none appears twice.
    /// </summary>
    public static byte[] CopyAncillary(ReadOnlySpan<byte> source, ReadOnlySpan<byte> encoded)
    {
        var copied = Chunks(source).Where(c => Carried.Contains(c.Type)).ToList();
        if (copied.Count == 0) return encoded.ToArray();
        var ownTypes = copied.Select(c => c.Type).ToHashSet();
        var output = new MemoryStream(encoded.Length + copied.Sum(c => c.Length));
        output.Write(encoded[..8]);
        foreach (var (type, offset, length) in Chunks(encoded))
        {
            if (ownTypes.Contains(type)) continue;
            output.Write(encoded.Slice(offset, length));
            if (type != "IHDR") continue;
            foreach (var c in copied) output.Write(source.Slice(c.Offset, c.Length));
        }
        return output.ToArray();
    }

    /// <summary>Each chunk's type and its full extent (length, type, data, CRC). Stops at the first malformed chunk.</summary>
    private static List<(string Type, int Offset, int Length)> Chunks(ReadOnlySpan<byte> png)
    {
        var list = new List<(string, int, int)>();
        if (FileSignature.Detect(png) != ImageFormat.Png) return list;
        for (var i = 8; png.Length - i >= 12;)
        {
            var length = (uint)(png[i] << 24 | png[i + 1] << 16 | png[i + 2] << 8 | png[i + 3]);
            if (length > (uint)(png.Length - i - 12)) break;
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(i + 4, 4));
            list.Add((type, i, 12 + (int)length));
            i += 12 + (int)length;
            if (type == "IEND") break;
        }
        return list;
    }
}
