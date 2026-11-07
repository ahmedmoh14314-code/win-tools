using System.Text;

namespace WinTools.Worker.Tests;

/// <summary>A coded picture taken from a one-frame MP4 the bundled FFmpeg wrote: its decoder configuration box (hvcC or
/// av1C) as it is, the sample entry's type, size and the sample's bytes.</summary>
internal sealed record CodedTile(byte[] Config, string ItemType, int Width, int Height, byte[] Data)
{
    public static CodedTile FromMp4(string path)
    {
        var f = File.ReadAllBytes(path);
        var stbl = HeifBuilder.Descend(f, 0, f.Length, "moov", "trak", "mdia", "minf", "stbl");
        var stsd = HeifBuilder.Find(f, stbl.Body, stbl.End, "stsd")!.Value;
        var entry = stsd.Body + 8; // version, flags, entry count
        var size = (int)HeifBuilder.BE32(f, entry);
        var type = Encoding.ASCII.GetString(f, entry + 4, 4);
        int w = f[entry + 32] << 8 | f[entry + 33], h = f[entry + 34] << 8 | f[entry + 35];
        var av1 = type == "av01";
        var cfg = HeifBuilder.Find(f, entry + 8 + 78, entry + size, av1 ? "av1C" : "hvcC")!.Value; // after the visual sample entry's fields
        var stsz = HeifBuilder.Find(f, stbl.Body, stbl.End, "stsz")!.Value;
        var sampleSize = HeifBuilder.BE32(f, stsz.Body + 4);
        if (sampleSize == 0) sampleSize = HeifBuilder.BE32(f, stsz.Body + 12);
        var stco = HeifBuilder.Find(f, stbl.Body, stbl.End, "stco")!.Value;
        var offset = HeifBuilder.BE32(f, stco.Body + 8);
        return new CodedTile(f[cfg.Start..cfg.End], av1 ? "av01" : "hvc1", w, h, f[(int)offset..(int)(offset + sampleSize)]);
    }
}

/// <summary>
/// Writes HEIF and AVIF still images the way cameras and phones do, from coded tiles: one image or a grid (ISO/IEC
/// 23008-12 §6.6.2.3), its grid descriptor in the meta box's idat as iPhones store it or in mdat, rotation (irot) and
/// mirroring (imir), an ICC profile (colr prof), a thumbnail (thmb), an auxiliary image (auxl + auxC: alpha or a gain
/// map), an Exif item (cdsc) and an image sequence's moov. Only for tests: the files the HEIF/AVIF input is verified on.
/// </summary>
internal sealed class HeifBuilder
{
    public bool Avif { get; init; }
    public bool GridInIdat { get; init; } = true;
    public int Rotation { get; init; }          // irot: anticlockwise quarter turns, 0 for none
    public int Mirror { get; init; } = -1;      // imir: 0 top-bottom, 1 left-right, -1 for none
    public byte[]? Icc { get; init; }
    public int ExifOrientation { get; init; }   // 0: no Exif item
    public CodedTile? Thumbnail { get; init; }
    public CodedTile? Auxiliary { get; init; }
    public string AuxiliaryType { get; init; } = "urn:mpeg:hevc:2015:auxid:1"; // alpha
    public bool SequenceBox { get; init; }      // a moov box as image sequences (avis, msf1) have

    private sealed record Item(int Id, string Type, bool Hidden, byte[] Data, bool InIdat, List<(int Property, bool Essential)> Props);

    /// <summary>A file of one image, or of a grid of <paramref name="columns"/> × <paramref name="rows"/> tiles (row by
    /// row) shown at <paramref name="width"/> × <paramref name="height"/>.</summary>
    public byte[] Build(IReadOnlyList<CodedTile> tiles, int columns, int rows, int width, int height)
    {
        var grid = tiles.Count > 1;
        var props = new List<byte[]>();
        int Prop(byte[] box) { props.Add(box); return props.Count; }
        var config = Prop(tiles[0].Config);
        var tileSize = Prop(Full("ispe", 0, 0, [.. U32(tiles[0].Width), .. U32(tiles[0].Height)]));
        var fullSize = grid ? Prop(Full("ispe", 0, 0, [.. U32(width), .. U32(height)])) : tileSize;
        var icc = Icc is null ? 0 : Prop(Box("colr", [.. A("prof"), .. Icc]));
        var rot = Rotation == 0 ? 0 : Prop(Box("irot", [(byte)Rotation]));
        var mir = Mirror < 0 ? 0 : Prop(Box("imir", [(byte)Mirror]));

        var items = new List<Item>();
        var refs = new List<(string Type, int From, int[] To)>();
        List<(int, bool)> Display(params (int, bool)[] first)
        {
            var l = first.ToList();
            if (icc > 0) l.Add((icc, false));
            if (rot > 0) l.Add((rot, true));
            if (mir > 0) l.Add((mir, true));
            return l;
        }
        for (var i = 0; i < tiles.Count; i++)
            items.Add(new(i + 1, tiles[i].ItemType, grid, tiles[i].Data, false, grid ? [(config, true), (tileSize, false)] : Display((config, true), (tileSize, false))));
        var primary = 1;
        if (grid)
        {
            primary = tiles.Count + 1;
            byte[] descriptor = [0, 0, (byte)(rows - 1), (byte)(columns - 1), (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height];
            items.Add(new(primary, "grid", false, descriptor, GridInIdat, Display((fullSize, false))));
            refs.Add(("dimg", primary, [.. Enumerable.Range(1, tiles.Count)]));
        }
        var next = items.Count + 1;
        if (Thumbnail is { } t)
        {
            var c = Prop(t.Config);
            var s = Prop(Full("ispe", 0, 0, [.. U32(t.Width), .. U32(t.Height)]));
            items.Add(new(next, t.ItemType, false, t.Data, false, Display((c, true), (s, false))));
            refs.Add(("thmb", next++, [primary]));
        }
        if (Auxiliary is { } x)
        {
            var c = Prop(x.Config);
            var s = Prop(Full("ispe", 0, 0, [.. U32(x.Width), .. U32(x.Height)]));
            var aux = Prop(Full("auxC", 0, 0, [.. A(AuxiliaryType), 0]));
            items.Add(new(next, x.ItemType, true, x.Data, false, [(c, true), (s, false), (aux, true)]));
            refs.Add(("auxl", next++, [primary]));
        }
        if (ExifOrientation > 0)
        {
            byte[] exif = [0, 0, 0, 6, .. A("Exif"), 0, 0, .. A("MM"), 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)ExifOrientation, 0, 0, 0, 0, 0, 0];
            items.Add(new(next, "Exif", false, exif, false, []));
            refs.Add(("cdsc", next++, [primary]));
        }

        var ftyp = Box("ftyp", [.. A(Avif ? (SequenceBox ? "avis" : "avif") : (SequenceBox ? "msf1" : "heic")), 0, 0, 0, 0, .. A("mif1"), .. A(Avif ? "avif" : "heic"), .. A("miaf")]);
        var hdlr = Full("hdlr", 0, 0, [0, 0, 0, 0, .. A("pict"), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var pitm = Full("pitm", 0, 0, [.. U16(primary)]);
        var iinf = Full("iinf", 0, 0, [.. U16(items.Count), .. items.SelectMany(i => Full("infe", 2, i.Hidden ? 1 : 0, [.. U16(i.Id), 0, 0, .. A(i.Type), 0]))]);
        var iref = refs.Count == 0 ? [] : Full("iref", 0, 0, [.. refs.SelectMany(r => Box(r.Type, [.. U16(r.From), .. U16(r.To.Length), .. r.To.SelectMany(U16)]))]);
        var ipma = Full("ipma", 0, 0, [.. U32(items.Count(i => i.Props.Count > 0)), .. items.Where(i => i.Props.Count > 0)
            .SelectMany(i => (byte[])[.. U16(i.Id), (byte)i.Props.Count, .. i.Props.Select(p => (byte)((p.Essential ? 0x80 : 0) | p.Property))])]);
        var iprp = Box("iprp", [.. Box("ipco", [.. props.SelectMany(p => p)]), .. ipma]);
        var idatData = items.Where(i => i.InIdat).SelectMany(i => i.Data).ToArray();
        var idat = idatData.Length == 0 ? [] : Box("idat", idatData);
        byte[] Iloc(int mdatBody)
        {
            List<byte> body = [0x44, 0x00, .. U16(items.Count)];
            int inMdat = mdatBody, inIdat = 0;
            foreach (var i in items)
            {
                body.AddRange([.. U16(i.Id), .. U16(i.InIdat ? 1 : 0), .. U16(0), .. U16(1), .. U32(i.InIdat ? inIdat : inMdat), .. U32(i.Data.Length)]);
                if (i.InIdat) inIdat += i.Data.Length; else inMdat += i.Data.Length;
            }
            return Full("iloc", 1, 0, [.. body]);
        }
        byte[] Meta(int mdatBody) => Full("meta", 0, 0, [.. hdlr, .. pitm, .. Iloc(mdatBody), .. iinf, .. iref, .. iprp, .. idat]);
        var moov = SequenceBox ? Box("moov", Box("mvhd", new byte[100])) : [];
        var mdatBody = ftyp.Length + Meta(0).Length + moov.Length + 8;
        var mdat = Box("mdat", [.. items.Where(i => !i.InIdat).SelectMany(i => i.Data)]);
        return [.. ftyp, .. Meta(mdatBody), .. moov, .. mdat];
    }

    internal readonly record struct Span(int Start, int Body, int End);

    internal static Span? Find(byte[] f, int from, int to, string type)
    {
        for (var i = from; i + 8 <= to;)
        {
            var size = (int)BE32(f, i);
            if (size < 8) return null;
            if (Encoding.ASCII.GetString(f, i + 4, 4) == type) return new Span(i, i + 8, i + size);
            i += size;
        }
        return null;
    }

    internal static Span Descend(byte[] f, int from, int to, params string[] types)
    {
        Span s = new(from, from, to);
        foreach (var t in types) s = Find(f, s.Body, s.End, t) ?? throw new InvalidDataException($"No {t} box.");
        return s;
    }

    internal static uint BE32(byte[] f, int i) => (uint)(f[i] << 24 | f[i + 1] << 16 | f[i + 2] << 8 | f[i + 3]);
    private static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];
    private static byte[] U32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] Box(string type, byte[] body) => [.. U32(body.Length + 8), .. A(type), .. body];
    private static byte[] Full(string type, int version, int flags, byte[] body) => Box(type, [(byte)version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags, .. body]);
}
