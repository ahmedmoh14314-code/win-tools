using System.Formats.Tar;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Archive;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 5.2: TAR, TAR.GZ and GZIP through the same checks as ZIP (names, links, devices, budget, limits), on archives
/// built here. TAR keeps no check values; GZIP checks its own at the end.
/// </summary>
public sealed class TarWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static JobRequest Request(string capability, string path) => new(Guid.NewGuid().ToString("N"), capability, [path]);

    private string Tar(string name, bool gzip, params TarEntry[] entries)
    {
        var path = Path.Combine(_dir.Path, name);
        using var file = File.Create(path);
        using Stream target = gzip ? new GZipStream(file, CompressionLevel.Optimal) : file;
        using var writer = new TarWriter(target, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var e in entries) writer.WriteEntry(e);
        return path;
    }

    private static PaxTarEntry Text(string name, string text) => new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) };

    private static string[] Tree(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p)).Order(StringComparer.Ordinal)];

    [Theory]
    [InlineData("logs.tar", false)]
    [InlineData("logs.tar.gz", true)]
    [InlineData("logs.tgz", true)]
    public void A_tar_extracts_into_a_folder_named_after_it(string name, bool gzip)
    {
        var tar = Tar(name, gzip, new PaxTarEntry(TarEntryType.Directory, "2026"), Text("2026/a.log", "one"), Text("notes.txt", "two"));
        Assert.Equal(gzip ? FileKinds.TarGz : FileKinds.Tar, FileKinds.IdentifyByName(tar));
        var request = Request(ArchiveCapabilities.ExtractId, tar);
        var r = ArchiveJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "logs"), r.Outputs.Single().Path);
        Assert.Equal(["2026", @"2026\a.log", "notes.txt"], Tree(r.Outputs.Single().Path));
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
        var info = ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, tar));
        Assert.Equal(gzip ? "TAR.GZ archive" : "TAR archive", info.Facts.Single(f => f.Label == "Type").Value);
        Assert.True(ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, tar)).Success);
    }

    [Fact]
    public void Links_devices_and_hostile_names_in_a_tar_never_reach_the_disk()
    {
        var tar = Tar("evil.tar.gz", true,
            Text("../escaped.txt", "x"), Text("/etc/passwd", "x"), Text("ok.txt", "fine"),
            new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "C:/Windows" },
            new PaxTarEntry(TarEntryType.HardLink, "hard") { LinkName = "ok.txt" },
            new PaxTarEntry(TarEntryType.CharacterDevice, "null-device"));
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, tar));
        Assert.True(r.Success, r.Message);
        Assert.Equal(["ok.txt"], Tree(r.Outputs.Single().Path));
        Assert.Contains(r.Facts, f => f is { Label: "Skipped", Notice: true } && f.Value.StartsWith("5 ", StringComparison.Ordinal));
        Assert.False(System.IO.File.Exists(Path.Combine(_dir.Path, "escaped.txt")));
    }

    [Fact]
    public void A_damaged_tar_gz_fails_and_leaves_nothing()
    {
        var tar = Tar("broken.tgz", true, Text("a.txt", new string('a', 5000)));
        var bytes = System.IO.File.ReadAllBytes(tar);
        bytes[^6] ^= 0xFF; // inside GZIP's CRC-32 trailer
        System.IO.File.WriteAllBytes(tar, bytes);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, tar));
        Assert.False(r.Success);
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Empty(Directory.GetDirectories(_dir.Path));
    }

    /// <summary>A stream of zeros of a set length that holds no buffer (a 300 MB entry in a test).</summary>
    private sealed class Zeros(long length) : Stream
    {
        private long _at;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _at; set => _at = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _at);
            Array.Clear(buffer, offset, n);
            _at += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _at = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _at + offset, _ => length + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void A_tar_bomb_is_stopped_by_the_whole_archive_ratio()
    {
        // TAR keeps no per-entry packed size, so the whole-archive rule stops it once past its 256 MB floor.
        var path = Path.Combine(_dir.Path, "bomb.tgz");
        using (var file = System.IO.File.Create(path))
        using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
        using (var writer = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "zeros.bin") { DataStream = new Zeros(300L << 20) });
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, path));
        Assert.Equal(ErrorCode.LimitExceeded, r.Error);
        Assert.Empty(Directory.GetDirectories(_dir.Path));
    }

    [Fact]
    public void A_gzip_decompresses_next_to_itself_and_never_overwrites()
    {
        var gz = Path.Combine(_dir.Path, "notes.txt.gz");
        using (var file = System.IO.File.Create(gz))
        using (var stream = new GZipStream(file, CompressionLevel.Optimal))
            stream.Write("hello from gzip"u8);
        _dir.Write("notes.txt", "mine"u8.ToArray());
        var request = Request(ArchiveCapabilities.DecompressId, gz);
        var r = ArchiveJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "notes (1).txt"), r.OutputPath);
        Assert.Equal("hello from gzip", System.IO.File.ReadAllText(r.OutputPath!));
        Assert.Equal("mine", System.IO.File.ReadAllText(Path.Combine(_dir.Path, "notes.txt")));
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
        var fake = _dir.Write("fake.gz", "not compressed"u8.ToArray());
        Assert.Equal(ErrorCode.ContentMismatch, ArchiveJobs.Run(Request(ArchiveCapabilities.DecompressId, fake)).Error);
    }

    // Evaluator, PR 5.2: each finding as its failing input.

    [Fact]
    public void A_gzip_cut_short_is_damaged_never_a_partial_success()
    {
        var full = Path.Combine(_dir.Path, "full.gz");
        using (var file = System.IO.File.Create(full))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            gz.Write(RandomNumberGenerator.GetBytes(200_000));
        var bytes = System.IO.File.ReadAllBytes(full);
        var cut = _dir.Write("cut.txt.gz", bytes[..(bytes.Length / 2)]);
        var trailerless = _dir.Write("notrailer.txt.gz", bytes[..^8]);
        foreach (var path in new[] { cut, trailerless })
        {
            var r = ArchiveJobs.Run(Request(ArchiveCapabilities.DecompressId, path));
            Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        }
        Assert.DoesNotContain(Directory.GetFiles(_dir.Path), f => f.EndsWith(".txt", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pax_number_that_overflows_is_damage_not_a_crash()
    {
        // TarWriter writes uid from the entry itself, so the record goes in under another key and is renamed in the bytes
        // (the PAX records are entry data: no header checksum covers them).
        var tar = Tar("pax.tar", false, new PaxTarEntry(TarEntryType.RegularFile, "a.txt", new Dictionary<string, string> { ["zzz"] = "99999999999999999999999" })
            { DataStream = new MemoryStream("x"u8.ToArray()) });
        var bytes = System.IO.File.ReadAllBytes(tar);
        var at = bytes.AsSpan().IndexOf("zzz="u8);
        Assert.True(at > 0);
        "uid="u8.CopyTo(bytes.AsSpan(at));
        System.IO.File.WriteAllBytes(tar, bytes);
        Assert.Equal(ErrorCode.DecodeFailed, ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, tar)).Error);
    }

    [Fact]
    public void A_huge_extended_header_is_refused_before_it_is_buffered()
    {
        var tar = Tar("huge.tgz", true, new PaxTarEntry(TarEntryType.RegularFile, "a.txt", new Dictionary<string, string> { ["comment"] = new string('z', 2 << 20) })
            { DataStream = new MemoryStream("x"u8.ToArray()) });
        Assert.Equal(ErrorCode.LimitExceeded, ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, tar)).Error);
    }

    [Fact]
    public void A_tar_cut_at_a_block_boundary_or_with_a_bad_checksum_fails_the_test()
    {
        var tar = Tar("three.tar", false, Text("a.txt", "one"), Text("b.txt", "two"), Text("c.txt", "three"));
        var bytes = System.IO.File.ReadAllBytes(tar);
        var pax = bytes.AsSpan(156, 1)[0] == (byte)'x'; // a PAX writer may put an extended header first
        var cut = _dir.Write("cut.tar", bytes[..(pax ? 3072 : 1024)]);
        Assert.Equal(ErrorCode.DecodeFailed, ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, cut)).Error);
        bytes[1] ^= 0x01; // the first header's name: its checksum no longer matches
        var damaged = _dir.Write("damaged.tar", bytes);
        Assert.Equal(ErrorCode.DecodeFailed, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, damaged)).Error);
        Assert.Empty(Directory.GetDirectories(_dir.Path));
    }

    [Fact]
    public void A_file_named_tar_that_is_not_one_is_refused()
    {
        var fake = _dir.Write("backup.tar", new byte[1024]);
        Assert.Equal(ErrorCode.ContentMismatch, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, fake)).Error);
    }
}
