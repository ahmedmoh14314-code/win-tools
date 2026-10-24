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
        ("BM"u8.ToArray(), 0, new("BMP image", "bmp")),
        ([0x49, 0x49, 0x2A, 0x00], 0, new("TIFF image", "tiff")),
        ([0x4D, 0x4D, 0x00, 0x2A], 0, new("TIFF image", "tiff")),
        ("%PDF-"u8.ToArray(), 0, new("PDF document", "pdf")),
        ([0x50, 0x4B, 0x03, 0x04], 0, new("ZIP archive or Office document", "zip")),
        ([0x50, 0x4B, 0x05, 0x06], 0, new("ZIP archive (empty)", "zip")),
        ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], 0, new("7-Zip archive", "7z")),
        ("Rar!\x1A\x07"u8.ToArray(), 0, new("RAR archive", "rar")),
        ([0x1F, 0x8B], 0, new("GZIP archive", "gz")),
        ("MZ"u8.ToArray(), 0, new("Windows program", "exe")),
        ([0x7F, 0x45, 0x4C, 0x46], 0, new("Linux program", "elf")),
        ([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], 0, new("Office 97–2003 document or installer", "cfb")),
        ("ID3"u8.ToArray(), 0, new("MP3 audio", "mp3")),
        ("ftyp"u8.ToArray(), 4, new("MP4 or QuickTime media", "mp4")),
        ("OggS"u8.ToArray(), 0, new("Ogg media", "ogg")),
        ("fLaC"u8.ToArray(), 0, new("FLAC audio", "flac")),
        ([0x1A, 0x45, 0xDF, 0xA3], 0, new("Matroska or WebM media", "mkv")),
    ];

    /// <summary>Extensions and the group their content should be in. Only these are checked for a mismatch.</summary>
    private static readonly Dictionary<string, string> Expected = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "png", [".jpg"] = "jpeg", [".jpeg"] = "jpeg", [".jpe"] = "jpeg", [".jfif"] = "jpeg", [".gif"] = "gif", [".bmp"] = "bmp",
        [".tif"] = "tiff", [".tiff"] = "tiff", [".webp"] = "webp", [".pdf"] = "pdf",
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
        foreach (var (magic, offset, kind) in Signatures)
            if (head.Length >= offset + magic.Length && head.Slice(offset, magic.Length).SequenceEqual(magic)) return kind;
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) || head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return new("Text", "text");
        return LooksLikeText(head) ? new("Text", "text") : new("Unknown (no known signature)", null);
    }

    /// <summary>Whether a definite signature disagrees with a known extension. Text is never a definite signature.</summary>
    public static bool Mismatch(Kind kind, string extension) =>
        kind.Group is { } group && group != "text" && Expected.TryGetValue(extension, out var expected) && expected != group;

    /// <summary>No NUL and mostly printable: a plain-text guess for files without a byte-order mark.</summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Contains((byte)0)) return false;
        var odd = 0;
        foreach (var b in head) if (b < 0x09 || b is > 0x0D and < 0x20) odd++;
        return odd * 50 < head.Length;
    }
}
