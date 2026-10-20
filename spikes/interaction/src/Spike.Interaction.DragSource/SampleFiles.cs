using System.IO;
namespace Spike.Interaction.DragSource;

/// <summary>Creates throwaway sample files with awkward names in a temp folder. Content is irrelevant to the spike.</summary>
public static class SampleFiles
{
    public static readonly string[] Names =
    [
        "plain.txt",
        "with spaces and (parens).png",
        "ملف عربي 日本語 ✓ émoji 🚀.jpg",
        "quote'apostrophe & ampersand.webp",
        "trailing.dots.and.long.name.ABCDEFGHIJKLMNOPQRSTUVWXYZ.0123456789.txt",
    ];

    public static (string Dir, IReadOnlyList<string> Paths) Create(string? baseDir = null, int count = 3)
    {
        var dir = Path.Combine(baseDir ?? Path.GetTempPath(), "spike-interaction-samples");
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        foreach (var name in Names.Take(Math.Clamp(count, 1, Names.Length)))
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) File.WriteAllBytes(p, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 });
            paths.Add(p);
        }
        return (dir, paths);
    }

    private static readonly byte[] Content = [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0];

    /// <summary>
    /// Post-run integrity check: every expected sample file still exists with the exact bytes written at creation,
    /// and the folder contains nothing else (no "- Copy", no moved-in files). The harness logs the result so a
    /// run cannot claim "Explorer unaffected" without it.
    /// </summary>
    public static (bool Ok, string Detail) Verify(string dir, IReadOnlyList<string> expected)
    {
        var problems = new List<string>();
        foreach (var p in expected)
        {
            if (!File.Exists(p)) { problems.Add($"missing: {Path.GetFileName(p)}"); continue; }
            var bytes = File.ReadAllBytes(p);
            if (!bytes.AsSpan().SequenceEqual(Content)) problems.Add($"content changed: {Path.GetFileName(p)} ({bytes.Length} bytes)");
        }
        var known = new HashSet<string>(Names, StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFileSystemEntries(dir))
        {
            if (!known.Contains(Path.GetFileName(f))) problems.Add($"unexpected entry: {Path.GetFileName(f)}");
        }
        return (problems.Count == 0, problems.Count == 0 ? $"{expected.Count} files intact, no extra entries" : string.Join("; ", problems));
    }
}
