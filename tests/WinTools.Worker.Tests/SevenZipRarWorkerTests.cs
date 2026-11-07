using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Writers.SevenZip;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Archive;

namespace WinTools.Worker.Tests;

/// <summary>
/// ADR-0030: 7z and RAR (RAR 4 and RAR 5) through the archive worker's checks, read with SharpCompress. The archives are
/// WinRAR's and py7zr's (<c>tests/samples</c>, independent encoders), SharpCompress's own writer, and hostile variants
/// made here: traversal names, links, bombs, a huge declared size, damage, truncation, parts of a split archive and
/// passwords. The archive is never changed; nothing lands outside the new folder; the host believes what is claimed.
/// </summary>
public sealed class SevenZipRarWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private const string Right = "right", Arabic = "سرّé1", ArabicName = "ملاحظات.txt";
    private static readonly byte[] Big = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("WinTools sample line ", 5000)));

    private static JobRequest Request(string capability, string input, string? password = null, string? normalized = null)
    {
        Dictionary<string, string>? parameters = null;
        if (password is not null)
        {
            parameters = new() { [PasswordPolicy.ParameterName] = password };
            if (normalized is not null) parameters[PasswordPolicy.NormalizedParameterName] = normalized;
        }
        return new(Guid.NewGuid().ToString("N"), capability, [input], null, parameters);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] Tree(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p)).Order(StringComparer.Ordinal)];

    /// <summary>Everything in the test folder, hidden staging folders included.</summary>
    private string[] Everything() => [.. Directory.EnumerateFileSystemEntries(_dir.Path).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    /// <summary>A 7z written by SharpCompress (not solid), with the given entries (a null content is a folder).</summary>
    private string SevenZip(string name, params (string Entry, byte[]? Content)[] entries)
    {
        var path = Path.Combine(_dir.Path, name);
        using var file = File.Create(path);
        using var writer = new SevenZipWriter(file, new SevenZipWriterOptions { CompressHeader = false });
        foreach (var (entry, content) in entries)
            if (content is null) writer.WriteDirectory(entry, null);
            else writer.Write(entry, new MemoryStream(content), new DateTime(2026, 1, 1));
        return path;
    }

    /// <summary>Extracts and checks the samples' tree (a.txt, sub\big.bin, tiny.txt and an Arabic name), believed by the host.</summary>
    private static JobResult AssertSampleTree(JobRequest request, string greeting)
    {
        var r = ArchiveJobs.Run(request);
        Assert.True(r.Success, r.Message);
        var folder = r.Outputs.Single().Path;
        Assert.Equal(["a.txt", "sub", @"sub\big.bin", "tiny.txt", ArabicName], Tree(folder));
        Assert.Equal(greeting, File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Equal(Big, File.ReadAllBytes(Path.Combine(folder, "sub", "big.bin")));
        Assert.Equal("ملاحظة", File.ReadAllText(Path.Combine(folder, ArabicName)));
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
        return r;
    }

    [Theory]
    [InlineData("rar5.rar", "RAR 5 archive", "hello rar world\n")]
    [InlineData("rar4.rar", "RAR 4 archive", "hello rar world\n")]
    [InlineData("rar5-solid.rar", "RAR 5 archive, solid", "hello rar world\n")]
    [InlineData("rar4-solid.rar", "RAR 4 archive, solid", "hello rar world\n")]
    [InlineData("7z-solid.7z", "7z archive, solid", "hello 7z world\n")]
    public void A_7z_or_rar_extracts_into_a_folder_named_after_it_and_tests_clean(string sample, string type, string greeting)
    {
        var archive = _dir.CopySample(sample);
        var before = Hash(archive);
        Assert.Equal(sample.EndsWith(".7z", StringComparison.Ordinal) ? FileKinds.SevenZip : FileKinds.Rar, FileKinds.IdentifyByName(archive));
        var r = AssertSampleTree(Request(ArchiveCapabilities.ExtractId, archive), greeting);
        Assert.Equal(Path.Combine(_dir.Path, Path.GetFileNameWithoutExtension(sample)), r.Outputs.Single().Path);
        Assert.Equal("4", r.Facts.Single(f => f.Label == "Files").Value);
        Assert.DoesNotContain(r.Facts, f => f.Notice);

        var test = ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, archive));
        Assert.True(test.Success, test.Message);
        Assert.Equal("No errors: every file's check value matches", test.Facts.Single(f => f.Label == "Result").Value);
        var info = ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, archive));
        Assert.True(info.Success, info.Message);
        Assert.Equal(type, info.Facts.Single(f => f.Label == "Type").Value);
        Assert.Equal("4", info.Facts.Single(f => f.Label == "Files").Value);
        Assert.True(JobFacts.AreShowable(info.Facts) && JobFacts.AreShowable(test.Facts));
        Assert.Equal(before, Hash(archive));
    }

    [Fact]
    public void A_7z_written_by_sharpcompress_extracts_folders_and_empty_files()
    {
        var archive = SevenZip("plain.7z", ("docs", null), ("docs/a.txt", "one"u8.ToArray()), ("empty.bin", []));
        var request = Request(ArchiveCapabilities.ExtractId, archive);
        var r = ArchiveJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal(["docs", @"docs\a.txt", "empty.bin"], Tree(r.Outputs.Single().Path));
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void Hostile_names_in_a_7z_never_reach_outside_the_folder_and_are_reported()
    {
        var archive = SevenZip("evil.7z",
            ("../escaped.txt", "x"u8.ToArray()), (@"..\up.txt", "x"u8.ToArray()), ("deep/../../out.txt", "x"u8.ToArray()),
            ("/rooted.txt", "x"u8.ToArray()), ("ok/fine.txt", "fine"u8.ToArray()), ("NUL.txt", "n"u8.ToArray()));
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive));
        Assert.True(r.Success, r.Message);
        var folder = r.Outputs.Single().Path;
        Assert.Contains(@"ok\fine.txt", Tree(folder));
        Assert.Contains("_NUL.txt", Tree(folder));
        Assert.DoesNotContain(Tree(folder), p => p.Contains("escaped", StringComparison.Ordinal) || p.Contains("up.txt", StringComparison.Ordinal) || p.Contains("out.txt", StringComparison.Ordinal));
        Assert.Contains(r.Facts, f => f is { Label: "Skipped", Notice: true });
        Assert.Contains(r.Facts, f => f is { Label: "Renamed", Notice: true });
        Assert.False(File.Exists(Path.Combine(_dir.Path, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir.Path)!, "up.txt")));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "out.txt")));
    }

    [Theory]
    [InlineData("rar5.rar")]
    [InlineData("rar4.rar")]
    [InlineData("rar4-solid.rar")]
    public void Hostile_names_in_a_rar_never_reach_outside_the_folder(string sample)
    {
        var bytes = File.ReadAllBytes(_dir.CopySample(sample));
        // RAR 5 separates folders with '/' (a '\' is part of a name there); RAR 4 with '\'.
        RarPatch.Rename(bytes, "tiny.txt", sample.StartsWith("rar5", StringComparison.Ordinal) ? "../t.txt" : @"..\t.txt");
        RarPatch.Rename(bytes, "a.txt", "C:\\aa");
        var archive = _dir.Write("evil-" + sample, bytes);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive));
        Assert.True(r.Success, r.Message);
        Assert.Equal(["sub", @"sub\big.bin", ArabicName], Tree(r.Outputs.Single().Path));
        Assert.Contains(r.Facts, f => f is { Label: "Skipped", Notice: true } && f.Value.StartsWith("2 ", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "t.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir.Path)!, "t.txt")));
    }

    [Theory]
    [InlineData("rar5-links.rar")]
    [InlineData("7z-link.7z")]
    public void Links_junctions_and_hard_links_are_never_created(string sample)
    {
        var archive = _dir.CopySample(sample);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive));
        Assert.True(r.Success, r.Message);
        var folder = r.Outputs.Single().Path;
        Assert.Equal(["ok.txt"], Tree(folder));
        Assert.Equal("fine", File.ReadAllText(Path.Combine(folder, "ok.txt")));
        Assert.Contains(r.Facts, f => f is { Label: "Skipped", Notice: true });
        Assert.All(Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories), p => Assert.Null(new FileInfo(p).LinkTarget));
        Assert.Contains(ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, archive)).Facts, f => f is { Label: "Links", Notice: true });
    }

    [Fact]
    public void A_rar_bomb_is_stopped_by_the_bytes_that_come_out()
    {
        // 4 MB of zeros packed into a few hundred bytes: past 1 MB the file is beyond 200:1 of its packed size.
        var archive = _dir.CopySample("rar5-zeros.rar");
        foreach (var capability in new[] { ArchiveCapabilities.ExtractId, ArchiveCapabilities.TestId })
            Assert.Equal(ErrorCode.LimitExceeded, ArchiveJobs.Run(Request(capability, archive)).Error);
        Assert.Equal(["rar5-zeros.rar"], Everything());
    }

    [Fact]
    public void A_7z_that_declares_far_more_than_its_size_is_refused_before_anything_is_written()
    {
        // 300 MB of zeros in a 45 KB solid 7z: refused from the declared sizes, before a byte is decompressed.
        var archive = _dir.CopySample("7z-zeros.7z");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive));
        Assert.Equal(ErrorCode.LimitExceeded, r.Error);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
        Assert.Equal(["7z-zeros.7z"], Everything());
        Assert.Contains(ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, archive)).Facts, f => f is { Label: "Ratio", Notice: true });
    }

    [Fact]
    public void A_huge_declared_size_is_refused_before_anything_is_written()
    {
        // A RAR 4 header that says 4 GB for a one-byte file.
        var bytes = File.ReadAllBytes(_dir.CopySample("rar4.rar"));
        RarPatch.SetRar4Size(bytes, "tiny.txt", 0xFFFF_FFF0);
        var archive = _dir.Write("huge.rar", bytes);
        foreach (var capability in new[] { ArchiveCapabilities.ExtractId, ArchiveCapabilities.TestId })
        {
            var r = ArchiveJobs.Run(Request(capability, archive));
            Assert.Equal(ErrorCode.LimitExceeded, r.Error);
            Assert.True(ArchiveJobModule.Instance.IsBelievable(Request(capability, archive), r));
        }
        Assert.Equal(["huge.rar", "rar4.rar"], Everything());
    }

    /// <summary>
    /// Shorter copies of a sample, so each file boundary is among them; each one fails, or says it is unsure. With
    /// encrypted names everything after the first headers comes in 16-byte blocks counted from the end, so those cuts
    /// reach every boundary, and each open derives a key, so only Test runs on them.
    /// </summary>
    private void AssertEveryCutIsCaught(string sample, string? password, bool mayLackItsEnd)
    {
        var bytes = File.ReadAllBytes(_dir.CopySample(sample));
        var blocks = sample.Contains("-names", StringComparison.Ordinal);
        for (var length = bytes.Length - (blocks ? 16 : 1); length >= 8; length -= blocks ? 16 : 1)
        {
            var cut = _dir.Write("cut.rar", bytes[..length]);
            foreach (var capability in blocks ? [ArchiveCapabilities.TestId] : new[] { ArchiveCapabilities.TestId, ArchiveCapabilities.ExtractId })
            {
                var r = ArchiveJobs.Run(Request(capability, cut, password));
                if (r.Success)
                {
                    Assert.True(mayLackItsEnd, $"{sample} cut to {length} bytes passed {capability}");
                    Assert.Contains(r.Facts, f => f is { Label: "Not checked", Notice: true, Value: EndNotConfirmed });
                    foreach (var o in r.Outputs) Directory.Delete(o.Path, recursive: true);
                }
                Assert.Equal(new[] { "cut.rar", sample }.Order(StringComparer.Ordinal), Everything());
            }
        }
    }

    private const string EndNotConfirmed = "The end of this old RAR archive could not be confirmed; files may be missing";

    [Theory]
    [InlineData("rar5.rar", null)]
    [InlineData("rar5-solid.rar", null)]
    [InlineData("rar5-locked.rar", Right)]
    [InlineData("rar5-locked-names.rar", Right)]
    public void A_rar5_cut_anywhere_fails(string sample, string? password) => AssertEveryCutIsCaught(sample, password, mayLackItsEnd: false);

    [Theory]
    [InlineData("rar4.rar", null)]
    [InlineData("rar4-solid.rar", null)]
    [InlineData("rar4-locked.rar", Right)]
    [InlineData("rar4-locked-names.rar", Right)]
    public void A_rar4_cut_anywhere_fails_or_says_its_end_is_not_confirmed(string sample, string? password) => AssertEveryCutIsCaught(sample, password, mayLackItsEnd: true);

    [Fact]
    public void Data_after_the_end_of_a_rar_is_ignored_as_rar_ignores_it()
    {
        // RAR reads nothing past its end header, so tools may append a signature there.
        foreach (var sample in new[] { "rar5.rar", "rar4.rar" })
        {
            var archive = _dir.Write("signed-" + sample, [.. File.ReadAllBytes(_dir.CopySample(sample)), .. new byte[64]]);
            var r = ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, archive));
            Assert.True(r.Success, r.Message);
            Assert.DoesNotContain(r.Facts, f => f.Notice);
        }
    }

    [Theory]
    [InlineData("7z-solid.7z", null)]
    [InlineData("7z-link.7z", null)]
    public void A_changed_byte_of_a_7z_never_passes_with_other_names_dates_or_content(string sample, string? password)
    {
        // Some bytes change nothing a user could see (an LZMA2 size field past the data, a cipher's padding): those may
        // pass. Anything that reaches a name, a date, an attribute or the content must fail.
        var bytes = File.ReadAllBytes(_dir.CopySample(sample));
        var sound = Extracted(_dir.Write("sound.7z", bytes), password);
        var failed = 0;
        for (var at = 8; at < bytes.Length; at++)
        {
            var changed = (byte[])bytes.Clone();
            changed[at] ^= 0x01;
            var archive = _dir.Write("changed.7z", changed);
            if (!ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, archive, password)).Success) { failed++; continue; }
            Assert.True(sound.SequenceEqual(Extracted(archive, password)), $"{sample}: byte {at} changed what comes out, and the test passed");
        }
        Assert.True(failed > (bytes.Length - 8) * 3 / 4, $"{sample}: only {failed} changes failed");
    }

    [Fact]
    public void A_changed_byte_in_an_encrypted_7z_directory_is_never_a_crash_and_the_content_stays_checked()
    {
        // py7zr's encrypted directory carries no CRC-32 of its own, so a change can reach a date (ADR-0030, a limit of the
        // archive). The files' CRC-32s still hold: whatever passes, no entry's content changed.
        var bytes = File.ReadAllBytes(_dir.CopySample("7z-locked-names.7z"));
        var sound = Extracted(_dir.Write("sound.7z", bytes), Right).Select(line => line.Split(' ')[^1]).Order(StringComparer.Ordinal);
        for (var at = bytes.Length - 112; at < bytes.Length; at += 8)
        {
            var changed = (byte[])bytes.Clone();
            changed[at] ^= 0x01;
            var archive = _dir.Write("changed.7z", changed);
            if (ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, archive, Right)).Success)
                Assert.Equal(sound, Extracted(archive, Right).Select(line => line.Split(' ')[^1]).Order(StringComparer.Ordinal));
        }
    }

    /// <summary>What an archive extracts to, as text: every path, date, attribute and content hash, the folder removed.</summary>
    private static string[] Extracted(string archive, string? password)
    {
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive, password));
        Assert.True(r.Success, r.Message);
        var folder = r.Outputs.Single().Path;
        string[] listing = [.. Tree(folder).Select(p =>
        {
            var path = Path.Combine(folder, p);
            return File.Exists(path) ? $"{p} {File.GetAttributes(path)} {File.GetLastWriteTimeUtc(path):O} {Hash(path)}" : p; // a folder made on the way has no date of its own
        })];
        Directory.Delete(folder, recursive: true);
        return listing;
    }

    [Fact]
    public void A_changed_name_in_a_plain_7z_header_fails_the_test()
    {
        var archive = SevenZip("names.7z", ("report.txt", "one"u8.ToArray()));
        var bytes = File.ReadAllBytes(archive);
        var at = bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("report.txt"));
        Assert.True(at > 32);
        bytes[at] ^= 0x01;
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, _dir.Write("renamed.7z", bytes)));
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
    }

    [Fact]
    public void Closing_an_entry_never_replaces_the_failure_that_stopped_it()
    {
        // A full disk or a limit stops ReadAll mid-entry; closing the half-read entry must not turn that into "damaged".
        var entry = ArchiveJobs.GuardedEntry(new ThrowsOnClose(), _ => new InvalidOperationException("translated"));
        void Stopped()
        {
            using (entry) throw new IOException("disk full");
        }
        Assert.Equal("disk full", Assert.Throws<IOException>(Stopped).Message);
    }

    private sealed class ThrowsOnClose : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new InvalidDataException("the block ends badly");
        }
    }

    [Fact]
    public void Declared_sizes_meet_both_the_total_and_the_whole_archive_rule()
    {
        // 21 GB from a 1 GB archive is only 21:1: the 20 GB cap alone refuses it. No sample that small can declare that much.
        Assert.Contains("20 GB", ArchiveJobs.DeclaredRefusal(21L << 30, 1L << 30), StringComparison.Ordinal);
        Assert.Null(ArchiveJobs.DeclaredRefusal(19L << 30, 1L << 30));
        Assert.Contains("bomb", ArchiveJobs.DeclaredRefusal(300L << 20, 1L << 20), StringComparison.Ordinal);
        Assert.Null(ArchiveJobs.DeclaredRefusal(200L << 20, 1L << 10)); // under the whole-archive rule's 256 MB floor
    }

    [Theory]
    [InlineData("rar5-solid.rar")]
    [InlineData("rar4.rar")]
    [InlineData("7z-solid.7z")]
    [InlineData("7z-locked.7z")]
    public void A_truncated_archive_fails_and_leaves_nothing(string sample)
    {
        var bytes = File.ReadAllBytes(_dir.CopySample(sample));
        var archive = _dir.Write("cut-" + sample, bytes[..(bytes.Length * 2 / 3)]);
        foreach (var capability in new[] { ArchiveCapabilities.ExtractId, ArchiveCapabilities.TestId })
        {
            var r = ArchiveJobs.Run(Request(capability, archive, sample.Contains("locked", StringComparison.Ordinal) ? Right : null));
            Assert.False(r.Success);
            Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        }
        Assert.Equal(new[] { "cut-" + sample, sample }.Order(StringComparer.Ordinal), Everything());
    }

    [Fact]
    public void A_damaged_7z_entry_fails_on_its_crc_and_leaves_nothing()
    {
        // SharpCompress does not check a 7z's CRC-32s: the worker does. The data is stored, so the changed byte decodes.
        var sound = _dir.CopySample("7z-stored.7z");
        Assert.True(ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, sound)).Success);
        var bytes = File.ReadAllBytes(sound);
        bytes[32 + 1500] ^= 0x55; // the data starts right after the 32-byte start header
        var archive = _dir.Write("stored.7z", bytes);
        Assert.Equal(ErrorCode.DecodeFailed, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive)).Error);
        Assert.Equal(ErrorCode.DecodeFailed, ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, archive)).Error);
        Assert.Equal(["7z-stored.7z", "stored.7z"], Everything());
    }

    [Fact]
    public void A_damaged_rar_entry_fails_and_leaves_nothing()
    {
        // The flipped byte is inside big.bin's packed data, past every header.
        var bytes = File.ReadAllBytes(_dir.CopySample("rar4.rar"));
        var name = Encoding.ASCII.GetBytes(@"sub\big.bin");
        var at = bytes.AsSpan().IndexOf(name) + name.Length + 20;
        bytes[at] ^= 0x55;
        var archive = _dir.Write("bad.rar", bytes);
        Assert.Equal(ErrorCode.DecodeFailed, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive)).Error);
        Assert.Equal(["bad.rar", "rar4.rar"], Everything());
    }

    [Fact]
    public void A_damaged_protected_rar4_file_fails_on_its_crc_with_the_right_password()
    {
        // SharpCompress checks no CRC of an encrypted RAR file; RAR 4 keeps the plain CRC-32, which the worker checks.
        // Stored, so the changed byte decrypts to wrong bytes that no decoder notices.
        var sound = _dir.CopySample("rar4-locked-stored.rar");
        Assert.True(ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, sound, Right)).Success);
        var bytes = File.ReadAllBytes(sound);
        bytes[bytes.Length / 2] ^= 0x55; // inside random.bin's 3,000 encrypted bytes
        var archive = _dir.Write("bad.rar", bytes);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive, Right));
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Equal(["bad.rar", "rar4-locked-stored.rar"], Everything());
    }

    [Theory]
    [InlineData("7z-locked.7z", false)]
    [InlineData("7z-locked-names.7z", true)]
    [InlineData("rar5-locked.rar", false)]
    [InlineData("rar5-locked-names.rar", true)]
    [InlineData("rar4-locked.rar", false)]
    [InlineData("rar4-locked-names.rar", true)]
    public void A_protected_archive_asks_for_its_password_and_extracts_with_it(string sample, bool namesHidden)
    {
        var archive = _dir.CopySample(sample);
        var before = Hash(archive);
        // Without the password: refused before anything is written, which is what makes the host ask (ADR-0027).
        Assert.Equal(ErrorCode.PasswordRequired, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive)).Error);
        Assert.Equal(ErrorCode.PasswordRequired, ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, archive)).Error);
        Assert.Equal([sample], Everything());
        var info = ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, archive));
        Assert.True(info.Success, info.Message);
        Assert.Contains(info.Facts, f => f is { Label: "Protection", Notice: true }
            && f.Value == (namesHidden ? "Names and contents protected with a password" : "Contents protected with a password"));
        Assert.True(JobFacts.AreShowable(info.Facts));

        var greeting = sample.EndsWith(".7z", StringComparison.Ordinal) ? "hello 7z world\n" : "hello rar world\n";
        var r = AssertSampleTree(Request(ArchiveCapabilities.ExtractId, archive, Right), greeting);
        // RAR 5 keeps a keyed form of a protected file's CRC-32 unless the names are encrypted too: said, not failed.
        var unchecked_ = sample == "rar5-locked.rar";
        Assert.Equal(unchecked_, r.Facts.Any(f => f is { Label: "Not checked", Notice: true }));
        var test = ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, archive, Right));
        Assert.True(test.Success, test.Message);
        Assert.Equal(unchecked_ ? "No errors in the files that could be checked" : "No errors: every file's check value matches", test.Facts.Single(f => f.Label == "Result").Value);
        Assert.Equal(before, Hash(archive));
    }

    [Theory]
    [InlineData("7z-locked.7z", ErrorCode.DecodeFailed)]
    [InlineData("7z-locked-names.7z", ErrorCode.DecodeFailed)]
    [InlineData("rar5-locked.rar", ErrorCode.PasswordIncorrect)]
    [InlineData("rar5-locked-names.rar", ErrorCode.PasswordIncorrect)]
    [InlineData("rar4-locked.rar", ErrorCode.DecodeFailed)]
    [InlineData("rar4-locked-names.rar", ErrorCode.DecodeFailed)]
    public void A_wrong_password_fails_and_leaves_nothing(string sample, ErrorCode error)
    {
        // RAR 5 keeps a password check value; 7z and RAR 4 cannot tell a wrong password from damage, and say both.
        var archive = _dir.CopySample(sample);
        foreach (var capability in new[] { ArchiveCapabilities.ExtractId, ArchiveCapabilities.TestId })
        {
            var r = ArchiveJobs.Run(Request(capability, archive, "wrong-pass-123"));
            Assert.Equal(error, r.Error);
            Assert.True(ArchiveJobModule.Instance.IsBelievable(Request(capability, archive, "wrong-pass-123"), r));
        }
        Assert.Equal([sample], Everything());
    }

    [Theory]
    [InlineData("7z-locked-arabic.7z", "hello 7z world\n")]
    [InlineData("rar5-locked-arabic.rar", "hello rar world\n")]
    public void An_arabic_password_opens_its_archive(string sample, string greeting) =>
        AssertSampleTree(Request(ArchiveCapabilities.ExtractId, _dir.CopySample(sample), Arabic), greeting);

    [Theory]
    [InlineData("7z-locked.7z", "hello 7z world\n")]
    [InlineData("rar5-locked.rar", "hello rar world\n")]
    [InlineData("rar4-locked.rar", "hello rar world\n")]
    public void The_password_is_tried_as_typed_then_in_its_nfkc_form(string sample, string greeting)
    {
        // Typed in full-width letters, which NFKC makes "right": the second form opens the archive (ADR-0026).
        var typed = "ｒｉｇｈｔ";
        AssertSampleTree(Request(ArchiveCapabilities.ExtractId, _dir.CopySample(sample), typed, Right), greeting);
    }

    [Fact]
    public void One_part_of_a_split_rar_is_refused_with_its_reason()
    {
        var first = _dir.CopySample("rar5-split.part1.rar");
        var second = _dir.CopySample("rar5-split.part2.rar");
        foreach (var part in new[] { first, second })
        foreach (var capability in new[] { ArchiveCapabilities.ExtractId, ArchiveCapabilities.TestId })
        {
            var r = ArchiveJobs.Run(Request(capability, part));
            Assert.Equal(ErrorCode.SplitArchive, r.Error);
            Assert.True(ArchiveJobModule.Instance.IsBelievable(Request(capability, part), r));
        }
        Assert.Equal(["rar5-split.part1.rar", "rar5-split.part2.rar"], Everything());
        var info = ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, first));
        Assert.True(info.Success, info.Message);
        Assert.Contains(info.Facts, f => f is { Label: "Parts", Notice: true });
        // The toast names the reason, in both languages.
        var extract = ArchiveCapabilities.Catalog.Single(c => c.Id == ArchiveCapabilities.ExtractId);
        Assert.Contains("split into several files", Core.Feedback.FeedbackText.Reason(ErrorCode.SplitArchive, extract.Words), StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_file_of_a_split_7z_is_cut_short_not_guessed()
    {
        var archive = _dir.CopySample("7z-split.7z.001", "split.7z");
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive));
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Contains("several files", r.Message, StringComparison.Ordinal);
        Assert.Equal(["split.7z"], Everything());
    }

    [Theory]
    [InlineData("fake.7z")]
    [InlineData("fake.rar")]
    public void A_file_named_7z_or_rar_that_is_not_one_is_refused(string name)
    {
        var path = _dir.Write(name, Encoding.ASCII.GetBytes("this is not an archive at all"));
        Assert.Equal(ErrorCode.ContentMismatch, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, path)).Error);
        Assert.Equal(ErrorCode.ContentMismatch, ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, path)).Error);
    }

    [Fact]
    public void Extract_never_overwrites_a_folder_of_the_same_name()
    {
        var archive = _dir.CopySample("rar5.rar");
        Directory.CreateDirectory(Path.Combine(_dir.Path, "rar5"));
        File.WriteAllText(Path.Combine(_dir.Path, "rar5", "mine.txt"), "keep");
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, archive));
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "rar5 (1)"), r.Outputs.Single().Path);
        Assert.Equal(["mine.txt"], Tree(Path.Combine(_dir.Path, "rar5")));
    }

    [Fact]
    public async Task Several_archives_dropped_together_each_extract_into_their_own_folder()
    {
        // Extract is a per-file command: the scheduler sends one request per archive (JobScheduler, PerFile).
        Assert.Equal(CapabilityShape.PerFile, ArchiveCapabilities.Catalog.Single(c => c.Id == ArchiveCapabilities.ExtractId).Shape);
        var archives = new[] { _dir.CopySample("rar5.rar"), _dir.CopySample("rar4-solid.rar"), _dir.CopySample("7z-solid.7z") };
        var client = new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Archive.exe"), TimeSpan.FromMinutes(1), ArchiveJobModule.Instance, parallelism: 1);
        var requests = archives.Select(a => Request(ArchiveCapabilities.ExtractId, a)).ToArray();
        var results = await client.RunAsync(requests, CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(
            [Path.Combine(_dir.Path, "rar5"), Path.Combine(_dir.Path, "rar4-solid"), Path.Combine(_dir.Path, "7z-solid")],
            results.Select(r => r.Outputs.Single().Path));
        foreach (var r in results) Assert.Equal(5, Tree(r.Outputs.Single().Path).Length);
    }
}

/// <summary>
/// Test inputs only: rewrites a field of one RAR file header in place and fixes that header's CRC, to make hostile RARs
/// from WinRAR's samples (no RAR encoder writes them). RAR 4 headers carry the low 16 bits of a CRC-32 from their type
/// byte on; RAR 5 headers a CRC-32 from their size field on.
/// </summary>
internal static class RarPatch
{
    /// <summary>Replaces a file name with another of the same length in bytes.</summary>
    public static void Rename(byte[] rar, string from, string to)
    {
        var (a, b) = (Encoding.UTF8.GetBytes(from), Encoding.UTF8.GetBytes(to));
        Assert.Equal(a.Length, b.Length);
        var at = rar.AsSpan().IndexOf(a);
        Assert.True(at > 0, $"{from} not found");
        var header = HeaderAround(rar, at);
        b.CopyTo(rar, at);
        Seal(rar, header);
    }

    /// <summary>Sets the unpacked size a RAR 4 file header declares.</summary>
    public static void SetRar4Size(byte[] rar, string name, uint size)
    {
        var at = rar.AsSpan().IndexOf(Encoding.ASCII.GetBytes(name));
        var header = HeaderAround(rar, at);
        Assert.False(header.Rar5);
        BinaryPrimitives.WriteUInt32LittleEndian(rar.AsSpan(header.Start + 11), size);
        Seal(rar, header);
    }

    private readonly record struct Header(int Start, int End, bool Rar5);

    private static Header HeaderAround(byte[] rar, int at)
    {
        for (var p = at - 1; p >= Math.Max(0, at - 300); p--)
        {
            if (p + 5 < rar.Length && Vint(rar, p + 4) is var (size, n) && p + 4 + n + size is var end5 && end5 > at && end5 <= rar.Length
                && Crc(rar.AsSpan(p + 4, (int)(end5 - p - 4))) == BinaryPrimitives.ReadUInt32LittleEndian(rar.AsSpan(p)))
                return new(p, (int)end5, true);
            if (p + 7 <= rar.Length && rar[p + 2] == 0x74 && BinaryPrimitives.ReadUInt16LittleEndian(rar.AsSpan(p + 5)) + p is var end4 && end4 > at && end4 <= rar.Length
                && (ushort)Crc(rar.AsSpan(p + 2, end4 - p - 2)) == BinaryPrimitives.ReadUInt16LittleEndian(rar.AsSpan(p)))
                return new(p, end4, false);
        }
        throw new InvalidOperationException("No RAR file header around the name.");
    }

    private static void Seal(byte[] rar, Header h)
    {
        if (h.Rar5) BinaryPrimitives.WriteUInt32LittleEndian(rar.AsSpan(h.Start), Crc(rar.AsSpan(h.Start + 4, h.End - h.Start - 4)));
        else BinaryPrimitives.WriteUInt16LittleEndian(rar.AsSpan(h.Start), (ushort)Crc(rar.AsSpan(h.Start + 2, h.End - h.Start - 2)));
    }

    private static (long Value, int Length) Vint(byte[] b, int at)
    {
        long value = 0;
        for (var i = 0; i < 10 && at + i < b.Length; i++)
        {
            value |= (long)(b[at + i] & 0x7F) << (7 * i);
            if ((b[at + i] & 0x80) == 0) return (value, i + 1);
        }
        return (long.MaxValue / 2, 1);
    }

    private static uint Crc(ReadOnlySpan<byte> data) => Crc32.Of(data);
}
