using System.Text;

namespace WinTools.Core.Files;

/// <summary>
/// What the image module knows about a HEIF (HEIC) or AVIF still image from its own reading of the file, before the
/// engine sees it (ADR-0031). Sizes are the stored ones: <see cref="Orientation"/> says how to turn them for display.
/// </summary>
/// <param name="PrimaryItem">The primary image's item id (the engine's stream id when it is not a grid).</param>
/// <param name="Width">The primary image's stored width: its ispe, or a grid's output width. Not checked against the
/// product's pixel limits here: the worker refuses a large image with its own error.</param>
/// <param name="Tiles">The number of coded pictures the primary image is made of (1 when it is not a grid).</param>
/// <param name="Orientation">The primary image's irot and imir properties, in their order, as one EXIF orientation (1–8).</param>
/// <param name="Icc">The ICC profile of a colr box ("prof" or "rICC"), or null.</param>
/// <param name="Transfer">The nclx transfer characteristics (ITU-T H.273), or 0 when there is none.</param>
/// <param name="AlphaItem">The item id of an alpha plane (auxl + auxC alpha), or 0; it is always a single coded picture.</param>
/// <param name="BitDepth">Bits per channel of the coded pictures.</param>
/// <param name="Exif">The Exif item's TIFF block, or null.</param>
/// <param name="CodedPixels">The pixels the engine decodes: a grid's whole canvas of tiles, which may exceed the output.</param>
/// <param name="CodedItems">The items holding coded pictures of the primary image (its tiles, or itself).</param>
/// <param name="CodedWidth">The size each of them states (a tile's ispe, or the image's); a bitstream may claim another.</param>
public sealed record HeifImage(int PrimaryItem, bool Av1, int Width, int Height, int Tiles, int Orientation, byte[]? Icc, int Transfer, int AlphaItem,
    int BitDepth, byte[]? Exif, bool Xmp, bool OtherMetadata, long CodedPixels,
    IReadOnlyList<int> CodedItems, int CodedWidth, int CodedHeight)
{
    public bool IsGrid => Tiles > 1;

    /// <summary>The size shown: the stored size, turned when the orientation swaps the sides.</summary>
    public (int Width, int Height) Shown => Orientation >= 5 ? (Height, Width) : (Width, Height);
}

/// <summary>
/// A bounded reader of the ISO base media file format boxes a HEIF or AVIF still image is made of (ISO/IEC 23008-12, the
/// AV1 image format): ftyp, then meta with hdlr "pict", pitm, iinf, iloc, iref, iprp (ipco + ipma) and idat. It finds the
/// primary image and says whether the product can show it exactly, and refuses what it cannot:
/// <list type="bullet">
/// <item>image sequences and animations (a moov box), and files without a still image;</item>
/// <item>a primary image coded other than HEVC or AV1 (or a grid of them), a missing ispe or decoder configuration;</item>
/// <item>an essential property it does not apply (clap cropping, layered AV1, anything unknown): the image would show wrong;</item>
/// <item>an alpha plane it cannot join to the picture (a grid, another size, premultiplied); HDR transfer functions (PQ, HLG);</item>
/// <item>item data outside the file or the idat box, or in another file (a data reference); sizes beyond the limits.</item>
/// </list>
/// The engine is given only files this reader accepted, and its result is checked against the size read here.
/// </summary>
public static class Heif
{
    /// <summary>Most items a file may declare (a 48 MP iPhone photo has about 200).</summary>
    public const int MaxItems = 4096;

    /// <summary>Most tiles in a grid.</summary>
    public const int MaxTiles = 1024;

    private static readonly string[] HeifBrands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1", "mif2"];

    /// <summary>The image format an ftyp box names, from the first bytes of a file: AVIF, HEIF or neither.</summary>
    public static ImageFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header.Slice(4, 4).SequenceEqual("ftyp"u8)) return ImageFormat.Unknown;
        var size = (int)Math.Min(header.Length, BE32(header, 0));
        if (size < 16) return ImageFormat.Unknown;
        var brands = new List<string> { Encoding.ASCII.GetString(header.Slice(8, 4)) };
        for (var i = 16; i + 4 <= size; i += 4) brands.Add(Encoding.ASCII.GetString(header.Slice(i, 4)));
        if (brands.Any(b => b is "avif" or "avis")) return ImageFormat.Avif;
        return brands.Any(b => HeifBrands.Contains(b)) ? ImageFormat.Heif : ImageFormat.Unknown;
    }

    /// <summary>The primary image, or null with the reason it cannot be shown exactly.</summary>
    public static HeifImage? Read(ReadOnlySpan<byte> d, out string reason)
    {
        try { return ReadCore(d, out reason); }
        catch (FormatException ex) { reason = ex.Message; return null; }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException) { reason = "a damaged structure"; return null; }
    }

    private sealed class Item
    {
        public uint Id;
        public string Type = "";
        public string? ContentType;
        public List<(int Index, bool Essential)> Props = [];
        public List<(long Offset, long Length)>? Extents;
        public int Construction;
    }

    private static HeifImage? ReadCore(ReadOnlySpan<byte> d, out string reason)
    {
        reason = "";
        if (Detect(d) == ImageFormat.Unknown) return Fail("not a HEIF or AVIF file", out reason);
        Box? meta = null;
        foreach (var box in Boxes(d, 0, d.Length))
        {
            if (box.Type == "moov") return Fail("an image sequence or animation", out reason);
            if (box.Type == "meta") meta ??= box;
        }
        if (meta is not { } m) return Fail("no still image", out reason);
        var metaBody = m.Body + 4; // FullBox
        if (metaBody > m.End) return Fail("damaged meta box", out reason);

        var items = new Dictionary<uint, Item>();
        uint primary = 0;
        var handler = "";
        var properties = new List<Box>();
        var refs = new List<(string Type, uint From, uint[] To)>();
        Box? idat = null;
        var sawIloc = false;
        // Item information first: the other boxes refer to items, and writers order them differently.
        var children = Boxes(d, metaBody, m.End);
        foreach (var box in children.Where(b => b.Type == "iinf").Concat(children.Where(b => b.Type != "iinf")))
        {
            switch (box.Type)
            {
                case "hdlr":
                    Need(d, box, 16);
                    handler = Encoding.ASCII.GetString(d.Slice(box.Body + 8, 4));
                    break;
                case "pitm":
                    Need(d, box, 4);
                    Need(d, box, d[box.Body] == 0 ? 6 : 8);
                    primary = d[box.Body] == 0 ? BE16(d, box.Body + 4) : BE32(d, box.Body + 4);
                    break;
                case "iinf":
                    ReadItemInfo(d, box, items);
                    break;
                case "iloc":
                    ReadLocations(d, box, items);
                    sawIloc = true;
                    break;
                case "iref":
                    ReadReferences(d, box, refs);
                    break;
                case "iprp":
                    ReadProperties(d, box, items, properties);
                    break;
                case "idat":
                    idat ??= box;
                    break;
            }
        }
        if (handler != "pict") return Fail("no picture handler", out reason);
        if (!sawIloc || !items.TryGetValue(primary, out var main)) return Fail("no primary image", out reason);

        // Every item's data must be in this file or its idat box: an extent outside them is damage, not something to guess.
        foreach (var item in items.Values)
        {
            if (item.Extents is null) continue;
            var (start, end) = item.Construction == 1 && idat is { } id ? (id.Body, id.End) : (0, d.Length);
            if (item.Construction == 1 && idat is null) return Fail("item data refers to a missing idat box", out reason);
            foreach (var (offset, length) in item.Extents)
                if (offset > end || length > end || start + offset + length > end) return Fail("item data runs past the end of the file", out reason);
        }

        Box? Prop(Item item, string type) =>
            item.Props.Select(p => p.Index >= 1 && p.Index <= properties.Count ? properties[p.Index - 1] : (Box?)null)
                .FirstOrDefault(b => b?.Type == type);

        // Essential properties the product does not apply would make the picture show wrong: refused, as the format asks.
        static bool Understood(string type) => type is "hvcC" or "av1C" or "ispe" or "irot" or "imir" or "colr" or "pixi" or "pasp" or "auxC";
        string? UnsupportedEssential(Item item)
        {
            foreach (var (index, essential) in item.Props)
            {
                if (index < 1 || index > properties.Count) throw new FormatException("a property index outside the property list");
                var type = properties[index - 1].Type;
                if (type == "clap") return "a cropped image (clap)";
                if (type is "lsel" or "a1op" or "a1lx") return "a layered image";
                if (essential && !Understood(type)) return $"an image feature WinTools does not read ({type})";
            }
            return null;
        }

        int width, height, tiles;
        long codedPixels = 0;
        int codedWidth, codedHeight;
        uint[] codedItems;
        Item coded;
        if (main.Type == "grid")
        {
            var descriptor = Data(d, main, idat) ?? throw new FormatException("the grid has no data");
            if (descriptor.Length < 8 || descriptor[0] != 0) throw new FormatException("an unknown grid version");
            var wide = (descriptor[1] & 1) != 0;
            if (descriptor.Length < (wide ? 12 : 8)) throw new FormatException("a short grid descriptor");
            int rows = descriptor[2] + 1, columns = descriptor[3] + 1;
            width = (int)Math.Min(int.MaxValue, wide ? BE32(descriptor, 4) : BE16(descriptor, 4));
            height = (int)Math.Min(int.MaxValue, wide ? BE32(descriptor, 8) : BE16(descriptor, 6));
            var dimg = refs.Where(r => r.Type == "dimg" && r.From == main.Id).SelectMany(r => r.To).ToArray();
            tiles = dimg.Length;
            if (tiles != rows * columns) return Fail("the grid's tiles do not match its rows and columns", out reason);
            if (tiles > MaxTiles) return Fail("too many tiles", out reason);
            var tileItems = new List<Item>();
            foreach (var t in dimg)
                tileItems.Add(items.TryGetValue(t, out var tile) ? tile : throw new FormatException("a grid tile is missing"));
            coded = tileItems[0];
            if (tileItems.Any(t => t.Type != coded.Type || t.Extents is null)) return Fail("the grid mixes kinds of tiles", out reason);
            foreach (var t in tileItems)
                if (UnsupportedEssential(t) is { } why) return Fail(why, out reason);
            if (Size(d, Prop(coded, "ispe")) is not { } tileSize) return Fail("a tile without its size", out reason);
            // The tiles must cover the output, and the canvas the engine composes them on stays bounded.
            if ((long)tileSize.W * columns < width || (long)tileSize.H * rows < height) return Fail("the grid's tiles do not cover the image", out reason);
            codedPixels = (long)tileSize.W * columns * tileSize.H * rows;
            (codedWidth, codedHeight, codedItems) = (tileSize.W, tileSize.H, dimg);
        }
        else
        {
            coded = main;
            tiles = 1;
            if (main.Extents is null) return Fail("the image has no data", out reason);
            if (Size(d, Prop(main, "ispe")) is not { } size) return Fail("the image does not state its size", out reason);
            (width, height) = (size.W, size.H);
            (codedWidth, codedHeight, codedItems) = (size.W, size.H, [main.Id]);
        }
        if (coded.Type is not ("hvc1" or "av01")) return Fail($"an image coded as {coded.Type.Trim()}, which WinTools does not read", out reason);
        var config = Prop(coded, coded.Type == "hvc1" ? "hvcC" : "av1C");
        if (config is null) return Fail("an image without its decoder configuration", out reason);
        if (UnsupportedEssential(main) is { } unsupported) return Fail(unsupported, out reason);
        if (width <= 0 || height <= 0) return Fail("the image has no size", out reason);

        // Display transforms of the primary image, in the order listed.
        var orientation = 1;
        foreach (var (index, _) in main.Props)
        {
            var p = properties[index - 1];
            if (p.Type == "irot") { Need(d, p, 1); orientation = Compose(orientation, Rotations[d[p.Body] & 3]); }
            else if (p.Type == "imir") { Need(d, p, 1); orientation = Compose(orientation, (d[p.Body] & 1) == 0 ? 4 : 2); }
        }

        byte[]? icc = null;
        var transfer = 0;
        // The colour of the image as shown: the primary item's, or else its tiles' (a writer may put it on either).
        var colours = main.Props.Select(p => properties[p.Index - 1]).Where(b => b.Type == "colr").ToList();
        if (colours.Count == 0 && coded != main) colours = [.. coded.Props.Where(p => p.Index >= 1 && p.Index <= properties.Count).Select(p => properties[p.Index - 1]).Where(b => b.Type == "colr")];
        foreach (var colr in colours)
        {
            Need(d, colr, 4);
            var kind = Encoding.ASCII.GetString(d.Slice(colr.Body, 4));
            if (kind is "prof" or "rICC" && colr.End - colr.Body - 4 <= IccProfile.MaxBytes) icc ??= d[(colr.Body + 4)..colr.End].ToArray();
            else if (kind == "nclx") { Need(d, colr, 10); transfer = BE16(d, colr.Body + 6); }
        }
        if (transfer is 16 or 18) return Fail("an HDR image (PQ or HLG), which WinTools cannot show exactly yet", out reason);

        var depth = Depth(d, coded.Type, config.Value, Prop(main, "pixi") ?? Prop(coded, "pixi"));

        // An alpha plane: joined to the picture only when it is one coded picture of the same size.
        uint alpha = 0;
        foreach (var r in refs.Where(r => r.Type == "auxl" && r.To.Contains(main.Id)))
        {
            if (!items.TryGetValue(r.From, out var aux)) continue;
            var auxType = Prop(aux, "auxC") is { } a && a.End - a.Body > 4 ? Encoding.ASCII.GetString(d[(a.Body + 4)..a.End]).TrimEnd('\0') : "";
            if (auxType is not ("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha" or "urn:mpeg:hevc:2015:auxid:1")) continue; // depth, gain maps: not part of the picture
            if (main.Type == "grid" || aux.Type is not ("hvc1" or "av01") || aux.Extents is null) return Fail("transparency in a tiled image, which WinTools cannot read yet", out reason);
            if (Size(d, Prop(aux, "ispe")) is not { } s || s.W != width || s.H != height) return Fail("transparency of another size than the picture", out reason);
            if (refs.Any(p => p.Type == "prem" && p.From == main.Id)) return Fail("premultiplied transparency", out reason);
            if (UnsupportedEssential(aux) is { } why) return Fail(why, out reason);
            alpha = aux.Id;
        }
        if (alpha > int.MaxValue) return Fail("an item id out of range", out reason);

        // Metadata items that describe the primary image (cdsc).
        var described = refs.Where(r => r.Type == "cdsc" && r.To.Contains(main.Id)).Select(r => r.From).ToHashSet();
        byte[]? exif = null;
        bool xmp = false, other = false;
        foreach (var item in items.Values.Where(i => described.Contains(i.Id)))
        {
            if (item.Type == "Exif")
            {
                var data = Data(d, item, idat);
                if (data is { Length: > 4 } && BE32(data, 0) is var skip && skip <= data.Length - 4) exif ??= data[(4 + (int)skip)..];
            }
            else if (item.Type == "mime" && item.ContentType == "application/rdf+xml") xmp = true;
            else other = true;
        }
        if (main.Id > int.MaxValue || codedItems.Any(i => i > int.MaxValue)) return Fail("an item id out of range", out reason);
        return new HeifImage((int)main.Id, coded.Type == "av01", width, height, tiles, orientation, icc, transfer, (int)alpha, depth, exif, xmp, other,
            Math.Max(codedPixels, (long)width * height), [.. codedItems.Select(i => (int)i)], codedWidth, codedHeight);
    }

    private static HeifImage? Fail(string why, out string reason) { reason = why; return null; }

    /// <summary>irot's anticlockwise quarter turns as EXIF orientations.</summary>
    private static readonly int[] Rotations = [1, 8, 3, 6];

    /// <summary>Each EXIF orientation (1–8) as the map from stored to displayed coordinates (x right, y down): x' = ax + by, y' = cx + dy.</summary>
    private static readonly (int A, int B, int C, int D)[] Matrices =
        [(1, 0, 0, 1), (1, 0, 0, 1), (-1, 0, 0, 1), (-1, 0, 0, -1), (1, 0, 0, -1), (0, 1, 1, 0), (0, -1, 1, 0), (0, -1, -1, 0), (0, 1, -1, 0)];

    /// <summary>The orientation that shows <paramref name="first"/> and then <paramref name="then"/> applied.</summary>
    public static int Compose(int first, int then)
    {
        var (a, b, c, e) = Matrices[first];
        var (p, q, r, s) = Matrices[then];
        var product = (p * a + q * c, p * b + q * e, r * a + s * c, r * b + s * e);
        for (var o = 1; o <= 8; o++) if (Matrices[o] == product) return o;
        throw new InvalidOperationException("Not an orientation.");
    }

    private static int Depth(ReadOnlySpan<byte> d, string type, Box config, Box? pixi)
    {
        if (pixi is { } p && p.End - p.Body >= 6 && d[p.Body + 4] > 0) return d[p.Body + 5];
        var c = d[config.Body..config.End];
        if (type == "av01" && c.Length >= 3) return (c[2] & 0x40) == 0 ? 8 : (c[2] & 0x20) != 0 ? 12 : 10;
        if (type == "hvc1" && c.Length >= 18) return (c[17] & 7) + 8;
        return 8;
    }

    private static (int W, int H)? Size(ReadOnlySpan<byte> d, Box? ispe)
    {
        if (ispe is not { } b || b.End - b.Body < 12) return null;
        var (w, h) = (BE32(d, b.Body + 4), BE32(d, b.Body + 8));
        return w is 0 or > int.MaxValue || h is 0 or > int.MaxValue ? null : ((int)w, (int)h);
    }

    /// <summary>An item's bytes (its extents joined), bounded: metadata and grid descriptors are small.</summary>
    private static byte[]? Data(ReadOnlySpan<byte> d, Item item, Box? idat)
    {
        if (item.Extents is null || item.Extents.Sum(e => e.Length) > 4 * 1024 * 1024) return null;
        var start = item.Construction == 1 && idat is { } i ? i.Body : 0;
        var output = new List<byte>();
        foreach (var (offset, length) in item.Extents) output.AddRange(d.Slice((int)(start + offset), (int)length));
        return [.. output];
    }

    private static void ReadItemInfo(ReadOnlySpan<byte> d, Box iinf, Dictionary<uint, Item> items)
    {
        Need(d, iinf, 6);
        var version = d[iinf.Body];
        var at = iinf.Body + (version == 0 ? 6 : 8);
        foreach (var infe in Boxes(d, at, iinf.End).Where(b => b.Type == "infe"))
        {
            Need(d, infe, 4);
            var v = d[infe.Body];
            if (v < 2) continue; // versions 0 and 1 describe files, not images
            Need(d, infe, v == 2 ? 12 : 14);
            var id = v == 2 ? BE16(d, infe.Body + 4) : BE32(d, infe.Body + 4);
            var typeAt = infe.Body + (v == 2 ? 8 : 10);
            var item = new Item { Id = id, Type = Encoding.ASCII.GetString(d.Slice(typeAt, 4)) };
            if (item.Type == "mime")
            {
                var rest = d[(typeAt + 4)..infe.End];
                var nameEnd = rest.IndexOf((byte)0);
                if (nameEnd >= 0 && rest[(nameEnd + 1)..].IndexOf((byte)0) is var typeEnd and >= 0)
                    item.ContentType = Encoding.ASCII.GetString(rest.Slice(nameEnd + 1, typeEnd));
            }
            if (items.Count >= MaxItems) throw new FormatException("too many items");
            if (!items.TryAdd(id, item)) throw new FormatException("an item declared twice");
        }
    }

    private static void ReadLocations(ReadOnlySpan<byte> d, Box iloc, Dictionary<uint, Item> items)
    {
        Need(d, iloc, 6);
        var version = d[iloc.Body];
        if (version > 2) throw new FormatException("an unknown iloc version");
        var at = iloc.Body + 4;
        int offsetSize = d[at] >> 4, lengthSize = d[at] & 15, baseSize = d[at + 1] >> 4, indexSize = version > 0 ? d[at + 1] & 15 : 0;
        if (new[] { offsetSize, lengthSize, baseSize, indexSize }.Any(s => s is not (0 or 4 or 8))) throw new FormatException("an unknown iloc field size");
        at += 2;
        long count = version < 2 ? Read(d, ref at, 2, iloc.End) : Read(d, ref at, 4, iloc.End);
        if (count > MaxItems) throw new FormatException("too many items");
        for (var i = 0; i < count; i++)
        {
            var id = (uint)Read(d, ref at, version < 2 ? 2 : 4, iloc.End);
            var construction = version > 0 ? (int)(Read(d, ref at, 2, iloc.End) & 15) : 0;
            var reference = Read(d, ref at, 2, iloc.End);
            var baseOffset = Read(d, ref at, baseSize, iloc.End);
            var extents = Read(d, ref at, 2, iloc.End);
            if (extents is 0 or > 4096) throw new FormatException("an item without data or with too many pieces");
            var list = new List<(long, long)>();
            for (var e = 0; e < extents; e++)
            {
                Read(d, ref at, indexSize, iloc.End);
                var offset = Read(d, ref at, offsetSize, iloc.End);
                var length = Read(d, ref at, lengthSize, iloc.End);
                if (length == 0) throw new FormatException("an item piece of unknown length");
                if (baseOffset > int.MaxValue || offset > int.MaxValue || length > int.MaxValue) throw new FormatException("item data runs past the end of the file");
                list.Add((baseOffset + offset, length));
            }
            // Data in another file (a data reference) or built from other items is never followed.
            if (reference != 0) throw new FormatException("item data in another file");
            if (construction > 1) throw new FormatException("item data built from other items");
            if (items.TryGetValue(id, out var item)) { item.Extents = list; item.Construction = construction; }
        }
    }

    private static void ReadReferences(ReadOnlySpan<byte> d, Box iref, List<(string, uint, uint[])> refs)
    {
        Need(d, iref, 4);
        var wide = d[iref.Body] != 0;
        foreach (var r in Boxes(d, iref.Body + 4, iref.End))
        {
            var at = r.Body;
            var from = (uint)Read(d, ref at, wide ? 4 : 2, r.End);
            var count = Read(d, ref at, 2, r.End);
            var to = new uint[count];
            for (var i = 0; i < count; i++) to[i] = (uint)Read(d, ref at, wide ? 4 : 2, r.End);
            if (refs.Count > MaxItems * 4) throw new FormatException("too many references");
            refs.Add((r.Type, from, to));
        }
    }

    private static void ReadProperties(ReadOnlySpan<byte> d, Box iprp, Dictionary<uint, Item> items, List<Box> properties)
    {
        foreach (var child in Boxes(d, iprp.Body, iprp.End))
        {
            if (child.Type == "ipco")
            {
                foreach (var p in Boxes(d, child.Body, child.End))
                {
                    if (properties.Count >= 32_767) throw new FormatException("too many properties");
                    properties.Add(p);
                }
            }
            else if (child.Type == "ipma")
            {
                Need(d, child, 8);
                var version = d[child.Body];
                var wideIndex = (d[child.Body + 3] & 1) != 0;
                var at = child.Body + 4;
                var count = Read(d, ref at, 4, child.End);
                if (count > MaxItems) throw new FormatException("too many items");
                for (var i = 0; i < count; i++)
                {
                    var id = (uint)Read(d, ref at, version < 1 ? 2 : 4, child.End);
                    var n = Read(d, ref at, 1, child.End);
                    var list = new List<(int, bool)>();
                    for (var j = 0; j < n; j++)
                    {
                        var v = (int)Read(d, ref at, wideIndex ? 2 : 1, child.End);
                        list.Add(wideIndex ? (v & 0x7FFF, (v & 0x8000) != 0) : (v & 0x7F, (v & 0x80) != 0));
                    }
                    if (items.TryGetValue(id, out var item)) item.Props.AddRange(list);
                }
            }
        }
    }

    // ---- Boxes ----

    private readonly record struct Box(string Type, int Body, int End);

    /// <summary>The boxes between two offsets; a box that does not fit is damage.</summary>
    private static List<Box> Boxes(ReadOnlySpan<byte> d, int from, int to)
    {
        var list = new List<Box>();
        var i = from;
        while (i < to)
        {
            if (to - i < 8) throw new FormatException("a truncated box");
            long size = BE32(d, i);
            var body = i + 8;
            if (size == 1)
            {
                if (to - i < 16) throw new FormatException("a truncated box");
                size = (long)((ulong)BE32(d, i + 8) << 32 | BE32(d, i + 12));
                body = i + 16;
            }
            else if (size == 0) size = to - i;
            if (size < body - i || size > to - i) throw new FormatException("a box runs past its container");
            list.Add(new Box(Encoding.ASCII.GetString(d.Slice(i + 4, 4)), body, (int)(i + size)));
            if (list.Count > 100_000) throw new FormatException("too many boxes");
            i += (int)size;
        }
        return list;
    }

    private static void Need(ReadOnlySpan<byte> d, Box b, int bytes)
    {
        if (b.End - b.Body < bytes || b.End > d.Length) throw new FormatException($"a short {b.Type} box");
    }

    private static long Read(ReadOnlySpan<byte> d, ref int at, int size, int end)
    {
        if (size == 0) return 0;
        if (end - at < size) throw new FormatException("a truncated box");
        long v = 0;
        for (var i = 0; i < size; i++) v = v << 8 | d[at + i];
        at += size;
        if (v < 0) throw new FormatException("a value out of range");
        return v;
    }

    private static ushort BE16(ReadOnlySpan<byte> d, int i) => (ushort)(d[i] << 8 | d[i + 1]);
    private static uint BE32(ReadOnlySpan<byte> d, int i) => (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
}
