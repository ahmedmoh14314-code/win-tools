using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Archive;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 5.1 (ADR-0023): ZIP extract, test, info and create on .NET's own System.IO.Compression, against archives built
/// here with hostile names, links, damage, bombs and protection. The archive is never changed; nothing lands outside the
/// new folder; what the worker claims is what the host's module believes.
/// </summary>
public sealed class ArchiveWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static JobRequest Request(string capability, params string[] inputs) => new(Guid.NewGuid().ToString("N"), capability, inputs);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>A ZIP with the given entries (name → content; a null content is a folder entry).</summary>
    private string Zip(string name, params (string Entry, string? Content)[] entries) => Zip(name, CompressionLevel.Optimal, null, entries);

    private string Zip(string name, CompressionLevel level, Action<ZipArchiveEntry>? tweak, params (string Entry, string? Content)[] entries)
    {
        var path = Path.Combine(_dir.Path, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (entry, content) in entries)
            {
                var e = zip.CreateEntry(entry, level);
                tweak?.Invoke(e);
                if (content is null) continue;
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        return path;
    }

    private static string[] Tree(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p)).Order(StringComparer.Ordinal)];

    [Fact]
    public void Extract_writes_a_new_folder_named_after_the_archive_and_the_host_believes_it()
    {
        var zip = Zip("photos.zip", ("2026/a.txt", "one"), ("2026/b.txt", "two"), ("empty/", null), ("صور/😀.txt", "three"));
        var before = Hash(zip);
        var request = Request(ArchiveCapabilities.ExtractId, zip);
        var r = ArchiveJobs.Run(request);
        Assert.True(r.Success, r.Message);
        var folder = r.Outputs.Single().Path;
        Assert.Equal(Path.Combine(_dir.Path, "photos"), folder);
        Assert.Equal(["2026", @"2026\a.txt", @"2026\b.txt", "empty", "صور", @"صور\😀.txt"], Tree(folder));
        Assert.Equal("two", File.ReadAllText(Path.Combine(folder, "2026", "b.txt")));
        Assert.Equal(before, Hash(zip));
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
        Assert.Equal(["photos.zip"], _dir.Names()); // the folder, and no temporary file, beside the archive
    }

    [Fact]
    public void Hostile_names_never_reach_outside_the_folder_and_are_reported()
    {
        var zip = Zip("evil.zip", ("../escaped.txt", "x"), ("..\\escaped2.txt", "x"), ("/abs.txt", "x"), ("C:/Windows/evil.txt", "x"),
            ("ok.txt", "fine"), ("a:stream.txt", "x"), ("NUL.txt", "renamed"), ("trailing. ", "renamed"));
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip));
        Assert.True(r.Success, r.Message);
        var folder = r.Outputs.Single().Path;
        Assert.Equal(["_NUL.txt", "ok.txt", "trailing"], Tree(folder));
        Assert.Contains(r.Facts, f => f is { Label: "Skipped", Notice: true } && f.Value.StartsWith("5 ", StringComparison.Ordinal));
        Assert.Contains(r.Facts, f => f is { Label: "Renamed", Notice: true });
        Assert.False(File.Exists(Path.Combine(_dir.Path, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir.Path)!, "escaped.txt")));
    }

    [Fact]
    public void A_link_entry_is_never_created()
    {
        const int symlink = unchecked((int)(0xA1FFu << 16)); // Unix mode lrwxrwxrwx
        var zip = Zip("links.zip", CompressionLevel.Optimal, e => { if (e.FullName == "link") e.ExternalAttributes = symlink; }, ("link", "C:/Windows"), ("real.txt", "r"));
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip));
        Assert.True(r.Success, r.Message);
        Assert.Equal(["real.txt"], Tree(r.Outputs.Single().Path));
        var info = ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, zip));
        Assert.Contains(info.Facts, f => f is { Label: "Links", Notice: true });
    }

    [Fact]
    public void Names_that_are_one_name_on_windows_both_survive()
    {
        var zip = Zip("case.zip", ("Read.txt", "first"), ("read.txt", "second"));
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip));
        Assert.True(r.Success, r.Message);
        Assert.Equal(["Read.txt", "read (1).txt"], Tree(r.Outputs.Single().Path));
    }

    [Fact]
    public void A_damaged_entry_fails_the_extraction_and_leaves_nothing()
    {
        var zip = Zip("damaged.zip", CompressionLevel.NoCompression, null, ("a.txt", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        var bytes = File.ReadAllBytes(zip);
        var at = bytes.AsSpan().IndexOf("AAAAAAAAAA"u8);
        bytes[at + 5] = (byte)'B'; // stored data no longer matches its CRC-32
        File.WriteAllBytes(zip, bytes);
        var extract = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip));
        Assert.Equal(ErrorCode.DecodeFailed, extract.Error);
        Assert.Equal(["damaged.zip"], _dir.Names());
        Assert.Empty(Directory.GetDirectories(_dir.Path));
        var test = ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, zip));
        Assert.Equal(ErrorCode.DecodeFailed, test.Error);
    }

    [Fact]
    public void An_archive_bomb_is_stopped_by_the_bytes_that_come_out()
    {
        var path = Path.Combine(_dir.Path, "bomb.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("zeros.bin", CompressionLevel.SmallestSize).Open())
        {
            var block = new byte[1 << 20];
            for (var i = 0; i < 64; i++) s.Write(block); // 64 MB of zeros packs to about 64 KB: 1000:1
        }
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, path));
        Assert.Equal(ErrorCode.LimitExceeded, r.Error);
        Assert.Empty(Directory.GetDirectories(_dir.Path));
        Assert.Contains(ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, path)).Facts, f => f is { Label: "Ratio", Notice: true });
    }

    [Fact]
    public void A_protected_archive_is_refused()
    {
        var zip = Zip("locked.zip", CompressionLevel.NoCompression, null, ("secret.txt", "s"));
        var bytes = File.ReadAllBytes(zip);
        // Set the "encrypted" flag (general purpose bit 0) in the local and the central headers.
        for (var i = 0; i + 4 < bytes.Length; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 3 && bytes[i + 3] == 4) bytes[i + 6] |= 1;
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 1 && bytes[i + 3] == 2) bytes[i + 8] |= 1;
        }
        File.WriteAllBytes(zip, bytes);
        Assert.Equal(ErrorCode.PasswordRequired, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip)).Error);
        Assert.Contains(ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, zip)).Facts, f => f is { Label: "Protection", Notice: true });
        Assert.Equal(["locked.zip"], _dir.Names());
    }

    [Fact]
    public void A_file_named_zip_that_is_not_one_is_refused()
    {
        var fake = _dir.Write("photos.zip", "MZ a program"u8.ToArray());
        Assert.Equal(ErrorCode.ContentMismatch, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, fake)).Error);
    }

    [Fact]
    public void Extract_never_overwrites_a_folder_of_the_same_name()
    {
        var zip = Zip("photos.zip", ("a.txt", "new"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "photos"));
        File.WriteAllText(Path.Combine(_dir.Path, "photos", "a.txt"), "mine");
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip));
        Assert.Equal(Path.Combine(_dir.Path, "photos (1)"), r.Outputs.Single().Path);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir.Path, "photos", "a.txt")));
    }

    [Fact]
    public void Test_and_info_report_without_writing()
    {
        var zip = Zip("ok.zip", ("a.txt", "one"), ("dir/b.txt", "two"));
        var test = ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, zip));
        Assert.True(test.Success, test.Message);
        Assert.Contains(test.Facts, f => f.Label == "Result" && f.Value.StartsWith("No errors", StringComparison.Ordinal));
        var info = ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, zip));
        Assert.Equal("2", info.Facts.Single(f => f.Label == "Files").Value);
        Assert.True(JobFacts.AreShowable(info.Facts) && JobFacts.AreShowable(test.Facts));
        Assert.Equal(["ok.zip"], _dir.Names());
    }

    [Fact]
    public void Add_to_zip_packs_files_and_folders_skips_links_and_the_result_extracts_back()
    {
        var folder = Path.Combine(_dir.Path, "holiday");
        Directory.CreateDirectory(Path.Combine(folder, "day 1"));
        File.WriteAllText(Path.Combine(folder, "day 1", "notes.txt"), "sunny");
        var report = _dir.Write("report.pdf", "%PDF-1.4 not really"u8.ToArray());
        var request = Request(ArchiveCapabilities.ZipId, folder + Path.DirectorySeparatorChar, report);
        var r = ArchiveJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, Path.GetFileName(_dir.Path) + ".zip"), r.OutputPath); // several items: the folder's name, as 7-Zip does
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
        using (var zip = ZipFile.OpenRead(r.OutputPath!))
            Assert.Equal(["holiday/", "holiday/day 1/", "holiday/day 1/notes.txt", "report.pdf"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        var single = ArchiveJobs.Run(Request(ArchiveCapabilities.ZipId, folder + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(_dir.Path, "holiday.zip"), single.OutputPath);
        var back = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, single.OutputPath!));
        Assert.Equal("sunny", File.ReadAllText(Path.Combine(back.Outputs.Single().Path, "holiday", "day 1", "notes.txt")));
    }

    [Fact]
    public void An_internet_archive_marks_what_it_extracts_and_a_zip_of_internet_files_is_marked()
    {
        var zip = Zip("download.zip", ("a.txt", "x"));
        File.WriteAllText(zip + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip));
        Assert.Contains("ZoneId=3", File.ReadAllText(Path.Combine(r.Outputs.Single().Path, "a.txt") + ":Zone.Identifier"), StringComparison.Ordinal);
        var downloaded = _dir.Write("setup notes.txt", "x"u8.ToArray());
        File.WriteAllText(downloaded + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var packed = ArchiveJobs.Run(Request(ArchiveCapabilities.ZipId, downloaded));
        Assert.Contains("ZoneId=3", File.ReadAllText(packed.OutputPath + ":Zone.Identifier"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_does_not_believe_an_extraction_the_disk_contradicts()
    {
        var zip = Zip("a.zip", ("x.txt", "1"));
        var request = Request(ArchiveCapabilities.ExtractId, zip);
        var r = ArchiveJobs.Run(request);
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
        File.WriteAllText(Path.Combine(r.Outputs[0].Path, "planted.exe"), "MZ");
        Assert.False(ArchiveJobModule.Instance.IsBelievable(request, r));
        Assert.False(ArchiveJobModule.Instance.IsBelievable(request, r with { Outputs = [r.Outputs[0] with { Path = _dir.Path }] }));
    }

    [Fact]
    public void Add_to_zip_packs_highly_compressible_files()
    {
        // Evaluator, PR 5.1 (reproduced): the read-back check ran the bomb rules on the user's own zeros.
        var folder = Path.Combine(_dir.Path, "disk image");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "zeros.img"), new byte[3 << 20]);
        var request = new JobRequest(Guid.NewGuid().ToString("N"), ArchiveCapabilities.ZipId, [folder + Path.DirectorySeparatorChar]);
        var r = ArchiveJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void Add_to_zip_never_leaves_out_a_file_silently()
    {
        // Evaluator, PR 5.1 (blocker): skipped files gave a "ZIP created" with files missing. A file that cannot be read
        // now fails the whole job, with nothing written.
        var folder = Path.Combine(_dir.Path, "work");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var online = Path.Combine(folder, "cloud.txt");
        File.WriteAllText(online, "b");
        File.SetAttributes(online, FileAttributes.Offline);
        var r = ArchiveJobs.Run(new JobRequest(Guid.NewGuid().ToString("N"), ArchiveCapabilities.ZipId, [folder + Path.DirectorySeparatorChar]));
        Assert.Equal(ErrorCode.OnlineOnly, r.Error);
        File.SetAttributes(online, FileAttributes.Normal);
        using (new FileStream(Path.Combine(folder, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // locked by another program
        {
            var busy = ArchiveJobs.Run(new JobRequest(Guid.NewGuid().ToString("N"), ArchiveCapabilities.ZipId, [folder + Path.DirectorySeparatorChar]));
            Assert.Equal(ErrorCode.SourceNotReadable, busy.Error);
        }
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_dir.Path), e => e.EndsWith(".zip", StringComparison.Ordinal) || Path.GetFileName(e).StartsWith("~wintools-", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_to_zip_packs_a_file_another_program_holds_open_without_writing()
    {
        // Second review, PR 5.1 (reproduced): Word keeps an open document shared for reading, open for writing.
        var folder = Path.Combine(_dir.Path, "reports");
        Directory.CreateDirectory(folder);
        var doc = Path.Combine(folder, "report.docx");
        File.WriteAllText(doc, "content");
        using var word = new FileStream(doc, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var r = ArchiveJobs.Run(new JobRequest(Guid.NewGuid().ToString("N"), ArchiveCapabilities.ZipId, [folder + Path.DirectorySeparatorChar]));
        Assert.True(r.Success, r.Message);
    }

    [Fact]
    public void Add_to_zip_takes_a_file_at_the_deepest_level_an_archive_may_hold()
    {
        var path = Path.Combine(_dir.Path, "deep");
        var leaf = path;
        for (var i = 0; i < ArchiveEntryPolicy.MaxDepth - 2; i++) leaf = Path.Combine(leaf, "d"); // "deep/d/.../x.txt": 32 parts
        Directory.CreateDirectory(leaf);
        File.WriteAllText(Path.Combine(leaf, "x.txt"), "x");
        Assert.True(ArchiveJobs.Run(new JobRequest(Guid.NewGuid().ToString("N"), ArchiveCapabilities.ZipId, [path + Path.DirectorySeparatorChar])).Success);
        File.WriteAllText(Path.Combine(leaf, "d"), "a file one level deeper would need 33 parts"); // a name, not a folder: still 32 parts
        Directory.CreateDirectory(Path.Combine(leaf, "e"));
        File.WriteAllText(Path.Combine(leaf, "e", "y.txt"), "33 parts");
        var r = ArchiveJobs.Run(new JobRequest(Guid.NewGuid().ToString("N"), ArchiveCapabilities.ZipId, [path + Path.DirectorySeparatorChar]));
        Assert.Equal(ErrorCode.LimitExceeded, r.Error); // refused while listing, before anything is compressed
    }

    [Fact]
    public void Add_to_zip_refuses_a_tree_deeper_than_an_archive_may_hold_before_writing()
    {
        var path = Path.Combine(_dir.Path, "deep");
        var leaf = path;
        for (var i = 0; i < 33; i++) leaf = Path.Combine(leaf, "d");
        Directory.CreateDirectory(leaf);
        File.WriteAllText(Path.Combine(leaf, "x.txt"), "x");
        var r = ArchiveJobs.Run(new JobRequest(Guid.NewGuid().ToString("N"), ArchiveCapabilities.ZipId, [path + Path.DirectorySeparatorChar]));
        Assert.Equal(ErrorCode.LimitExceeded, r.Error);
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir.Path), e => e.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new[] { "a", "a/b" }, new[] { "a" })]
    [InlineData(new[] { "a/b", "a" }, new[] { "a", @"a\b" })]
    public void A_file_and_a_folder_of_one_name_keep_the_first_and_say_so(string[] entries, string[] expected)
    {
        var zip = Zip("clash.zip", [.. entries.Select(e => (e, (string?)"x"))]);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip));
        Assert.True(r.Success, r.Message);
        Assert.Equal(expected, Tree(r.Outputs.Single().Path));
        Assert.Contains(r.Facts, f => f is { Label: "Skipped", Notice: true });
    }

    [Fact]
    public async Task The_real_worker_process_runs_a_job_the_host_believes()
    {
        var zip = Zip("a.zip", ("x.txt", "1"));
        var client = new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Archive.exe"), TimeSpan.FromMinutes(1), ArchiveJobModule.Instance, parallelism: 1);
        var results = await client.RunAsync([Request(ArchiveCapabilities.ExtractId, zip), Request(ArchiveCapabilities.InfoId, zip)], CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(Path.Combine(_dir.Path, "a"), results[0].Outputs.Single().Path);
    }
}
