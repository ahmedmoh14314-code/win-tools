using System.Buffers.Binary;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// Adds the source's ICC profile to an AVIF the engine wrote (it writes only the nclx colour box): a colr "prof" property
/// at the end of ipco, associated with the primary image in ipma; meta and its containers grow by the same bytes, and
/// every item offset into the file after meta moves by them. Only the layout FFmpeg's AVIF muxer writes is accepted
/// (ftyp, meta, mdat; iloc versions 0–1 with 4- or 8-byte offsets in the file); anything else fails the job rather than
/// leave the profile out. The result is read back: its primary image must carry exactly this profile.
/// </summary>
internal static class HeifEdit
{
    public static byte[] WithIccProfile(byte[] avif, byte[] icc)
    {
        try
        {
            var result = Rewrite(avif, icc);
            if (Heif.Read(result, out _) is not { } back || back.Icc is null || !back.Icc.AsSpan().SequenceEqual(icc))
                throw new ImagingException(ErrorCode.EncodeFailed, "The colour profile could not be added to the AVIF.");
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            throw new ImagingException(ErrorCode.EncodeFailed, "The colour profile could not be added to the AVIF.", ex);
        }
    }

    private static byte[] Rewrite(byte[] f, byte[] icc)
    {
        var primary = Heif.Read(f, out _)?.PrimaryItem ?? throw new InvalidDataException("not an AVIF");
        var top = Children(f, 0, f.Length);
        var metaIndex = top.FindIndex(b => b.Type == "meta");
        if (metaIndex < 0 || top.Take(metaIndex).Any(b => b.Type == "mdat")) throw new InvalidDataException("meta must come before the data");
        var meta = top[metaIndex];
        var colr = Box("colr", [.. "prof"u8, .. icc]);

        // The new meta body: its full-box header, then each child, iprp grown.
        var body = new List<byte>(f.AsSpan(meta.Body, 4).ToArray());
        int? ilocAt = null;
        var children = Children(f, meta.Body + 4, meta.End);
        foreach (var child in children)
        {
            if (child.Type == "iprp")
            {
                var parts = new List<byte>();
                var props = Children(f, child.Body, child.End);
                var count = props.Where(p => p.Type == "ipco").Select(p => Children(f, p.Body, p.End).Count).FirstOrDefault();
                if (count + 1 > 127) throw new InvalidDataException("too many properties");
                foreach (var p in props)
                {
                    if (p.Type == "ipco") parts.AddRange(Box("ipco", [.. f.AsSpan(p.Body, p.End - p.Body), .. colr]));
                    else if (p.Type == "ipma") parts.AddRange(Associate(f, p, primary, (byte)(count + 1)));
                    else parts.AddRange(f.AsSpan(p.Start, p.End - p.Start).ToArray());
                }
                body.AddRange(Box("iprp", [.. parts]));
            }
            else
            {
                if (child.Type == "iloc") ilocAt = body.Count + 8 + 4; // the iloc body after its header, in the new meta body
                body.AddRange(f.AsSpan(child.Start, child.End - child.Start).ToArray());
            }
        }
        var newMeta = Box("meta", [.. body]);
        var delta = newMeta.Length - (meta.End - meta.Start);
        if (ilocAt is not { } at) throw new InvalidDataException("no iloc");
        ShiftOffsets(newMeta, 8 + at, meta.End, delta);

        var output = new List<byte>(f.Length + delta);
        foreach (var b in top)
            output.AddRange(b.Type == "meta" ? newMeta : f.AsSpan(b.Start, b.End - b.Start).ToArray());
        return [.. output];
    }

    /// <summary>Every extent offset (construction 0) at or after <paramref name="after"/> moves by <paramref name="delta"/>.</summary>
    private static void ShiftOffsets(byte[] meta, int iloc, int after, int delta)
    {
        // iloc body (after the 8-byte header): version and flags, then the field sizes.
        var version = meta[iloc - 4];
        if (version > 1) throw new InvalidDataException("iloc version");
        int offsetSize = meta[iloc] >> 4, lengthSize = meta[iloc] & 15, baseSize = meta[iloc + 1] >> 4, indexSize = version == 1 ? meta[iloc + 1] & 15 : 0;
        if (offsetSize is not (4 or 8) || baseSize != 0 || indexSize != 0 || lengthSize is not (4 or 8)) throw new InvalidDataException("iloc layout");
        var at = iloc + 2;
        int count = BinaryPrimitives.ReadUInt16BigEndian(meta.AsSpan(at));
        at += 2;
        for (var i = 0; i < count; i++)
        {
            at += 2; // item id
            if (version == 1)
            {
                if ((BinaryPrimitives.ReadUInt16BigEndian(meta.AsSpan(at)) & 15) != 0) throw new InvalidDataException("construction method");
                at += 2;
            }
            at += 2; // data reference index
            int extents = BinaryPrimitives.ReadUInt16BigEndian(meta.AsSpan(at));
            at += 2;
            for (var e = 0; e < extents; e++)
            {
                var span = meta.AsSpan(at, offsetSize);
                var offset = offsetSize == 4 ? BinaryPrimitives.ReadUInt32BigEndian(span) : (long)BinaryPrimitives.ReadUInt64BigEndian(span);
                if (offset >= after)
                {
                    if (offsetSize == 4) BinaryPrimitives.WriteUInt32BigEndian(span, checked((uint)(offset + delta)));
                    else BinaryPrimitives.WriteUInt64BigEndian(span, checked((ulong)(offset + delta)));
                }
                at += offsetSize + lengthSize;
            }
        }
    }

    /// <summary>The ipma box with the property added to the primary item's associations (8-bit indexes).</summary>
    private static byte[] Associate(byte[] f, (string Type, int Start, int Body, int End) ipma, int primary, byte property)
    {
        var version = f[ipma.Body];
        if ((f[ipma.Body + 3] & 1) != 0) throw new InvalidDataException("16-bit property indexes");
        var output = new List<byte>(f.AsSpan(ipma.Body, 8).ToArray());
        var at = ipma.Body + 8;
        var count = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(ipma.Body + 4));
        var found = false;
        for (var i = 0; i < count; i++)
        {
            var idSize = version < 1 ? 2 : 4;
            var id = idSize == 2 ? BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(at)) : (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(at));
            output.AddRange(f.AsSpan(at, idSize).ToArray());
            at += idSize;
            int n = f[at++];
            var assoc = f.AsSpan(at, n).ToArray();
            at += n;
            if (id == primary)
            {
                found = true;
                output.Add((byte)(n + 1));
                output.AddRange(assoc);
                output.Add(property); // descriptive: not essential
            }
            else
            {
                output.Add((byte)n);
                output.AddRange(assoc);
            }
        }
        if (!found || at != ipma.End) throw new InvalidDataException("ipma");
        return Box("ipma", [.. output]);
    }

    private static List<(string Type, int Start, int Body, int End)> Children(byte[] f, int from, int to)
    {
        var list = new List<(string, int, int, int)>();
        for (var i = from; i < to;)
        {
            if (to - i < 8) throw new InvalidDataException("box");
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(i));
            if (size < 8 || size > to - i) throw new InvalidDataException("box size"); // the muxer writes no 64-bit or open-ended boxes before mdat
            list.Add((System.Text.Encoding.ASCII.GetString(f, i + 4, 4), i, i + 8, i + (int)size));
            i += (int)size;
        }
        return list;
    }

    private static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        body.CopyTo(box, 8);
        return box;
    }
}
