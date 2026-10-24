namespace WinTools.Worker.Text;

/// <summary>
/// What a file really is, from its first bytes, for File info (V2 PR 1.4). The name says what it claims; when a
/// definite signature disagrees with a known extension, the info says so ("Windows program, but named .pdf"), the one
/// place a user can see that a file is not what its name says. Signatures only: nothing is parsed.
/// </summary>
internal static class ContentType
{
    public const int HeaderLength = 4096;

    /// <summary>A detected type: its display name and the group extensions are matched against (null: no definite signature).</summary>
    public readonly record struct Kind(string Name, string? Group);

    private static readonly (byte[] Magic, int Offset, Kind Kind)[] Signatures =
    [
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0, new("PNG image", "png")),
        ([0xFF, 0xD8, 0xFF], 0, new("JPEG image", "jpeg")),
        ("GIF87a"u8.ToArray(), 0, new("GIF image", "gif")),
        ("GIF89a"u8.ToArray(), 0, new("GIF image", "gif")),
        ([0x49, 0x49, 0x2A, 0x00], 0, new("TIFF image", "tiff")),
        ([0x4D, 0x4D, 0x00, 0x2A], 0, new("TIFF image", "tiff")),
        ("%PDF-"u8.ToArray(), 0, new("PDF document", "pdf")),
        ([0x50, 0x4B, 0x03, 0x04], 0, new("ZIP archive or Office document", "zip")),
        ([0x50, 0x4B, 0x05, 0x06], 0, new("ZIP archive (empty)", "zip")),
        ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], 0, new("7-Zip archive", "7z")),
        ("Rar!\x1A\x07"u8.ToArray(), 0, new("RAR archive", "rar")),
        ([0x1F, 0x8B], 0, new("GZIP archive", "gz")),
        ([0x7F, 0x45, 0x4C, 0x46], 0, new("Linux program", "elf")),
        ([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], 0, new("Office 97–2003 document or installer", "cfb")),

        ("OggS"u8.ToArray(), 0, new("Ogg media", "ogg")),
        ("fLaC"u8.ToArray(), 0, new("FLAC audio", "flac")),
        ([0x1A, 0x45, 0xDF, 0xA3], 0, new("Matroska or WebM media", "mkv")),
    ];

    /// <summary>Extensions and the group their content should be in. Only these are checked for a mismatch.</summary>
    private static readonly Dictionary<string, string> Expected = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "png", [".jpg"] = "jpeg", [".jpeg"] = "jpeg", [".jpe"] = "jpeg", [".jfif"] = "jpeg", [".gif"] = "gif", [".bmp"] = "bmp",
        [".tif"] = "tiff", [".tiff"] = "tiff", [".webp"] = "webp", [".heic"] = "heif", [".heif"] = "heif", [".avif"] = "avif", [".pdf"] = "pdf",
        [".zip"] = "zip", [".docx"] = "zip", [".xlsx"] = "zip", [".pptx"] = "zip", [".odt"] = "zip", [".ods"] = "zip", [".odp"] = "zip", [".epub"] = "zip", [".jar"] = "zip", [".apk"] = "zip",
        [".7z"] = "7z", [".rar"] = "rar", [".gz"] = "gz", [".tgz"] = "gz",
        [".exe"] = "exe", [".dll"] = "exe", [".scr"] = "exe", [".sys"] = "exe", [".com"] = "exe",
        [".doc"] = "cfb", [".xls"] = "cfb", [".ppt"] = "cfb", [".msi"] = "cfb", [".msg"] = "cfb",
        [".mp3"] = "mp3", [".mp4"] = "mp4", [".m4a"] = "mp4", [".m4v"] = "mp4", [".mov"] = "mp4", [".ogg"] = "ogg", [".opus"] = "ogg", [".flac"] = "flac", [".mkv"] = "mkv", [".webm"] = "mkv",
        [".txt"] = "text", [".csv"] = "text", [".md"] = "text", [".json"] = "text", [".xml"] = "text", [".srt"] = "text", [".log"] = "text", [".ini"] = "text",
    };

    public static Kind Detect(ReadOnlySpan<byte> head, long length)
    {
        if (length == 0) return new("Empty file", null);
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return new("WebP image", "webp");
        // Short magic numbers ("MZ", "BM", "ID3") also start ordinary text ("MZ notes", "BMW,..."): each counts only with
        // the structure behind it (evaluator, PR #18: plain text was called a disguised program).
        if (IsPortableExecutable(head)) return new("Windows program", "exe");
        if (head.Length >= 14 && head.StartsWith("BM"u8) && BitConverter.ToUInt32(head[2..6]) == length && BitConverter.ToUInt32(head[6..10]) == 0)
            return new("BMP image", "bmp");
        if (head.Length >= 10 && head.StartsWith("ID3"u8) && head[3] is >= 2 and <= 4 && head[4] != 0xFF && head[6..10].IndexOfAnyInRange((byte)0x80, (byte)0xFF) < 0)
            return new("MP3 audio", "mp3");
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            // The major brand, then the compatible ones (an AVIF may say "mif1" first and "avif" after).
            var size = (int)Math.Min(head.Length, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[..4]));
            var brands = new List<string> { System.Text.Encoding.ASCII.GetString(head[8..12]) };
            for (var at = 16; at + 4 <= size; at += 4) brands.Add(System.Text.Encoding.ASCII.GetString(head.Slice(at, 4)));
            if (brands.Any(b => b is "avif" or "avis")) return new("AVIF image", "avif");
            if (brands.Any(b => b is "heic" or "heix" or "hevc" or "heim" or "heis" or "mif1" or "msf1")) return new("HEIF image", "heif");
            return new("MP4 or QuickTime media", "mp4");
        }
        foreach (var (magic, offset, kind) in Signatures)
            if (head.Length >= offset + magic.Length && head.Slice(offset, magic.Length).SequenceEqual(magic)) return kind;
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) || head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return new("Text", "text");
        return LooksLikeText(head) ? new("Text", "text") : new("Unknown (no known signature)", null);
    }

    /// <summary>Whether a definite signature disagrees with a known extension. Text is never a definite signature.</summary>
    public static bool Mismatch(Kind kind, string extension) =>
        kind.Group is { } group && group != "text" && Expected.TryGetValue(extension, out var expected) && expected != group;

    /// <summary>"MZ" with a PE header where e_lfanew points (inside the 4 KB read): a real Windows program, not "MZ..." text.</summary>
    private static bool IsPortableExecutable(ReadOnlySpan<byte> head)
    {
        if (head.Length < 0x40 || !head.StartsWith("MZ"u8)) return false;
        var pe = BitConverter.ToInt32(head[0x3C..0x40]);
        return pe >= 0x40 && pe <= head.Length - 4 && head.Slice(pe, 4).SequenceEqual("PE\0\0"u8);
    }

    /// <summary>No NUL and mostly printable: a plain-text guess for files without a byte-order mark.</summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Contains((byte)0)) return false;
        var odd = 0;
        foreach (var b in head) if (b < 0x09 || b is > 0x0D and < 0x20) odd++;
        return odd * 50 < head.Length;
    }
}
