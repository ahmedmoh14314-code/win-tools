using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Worker.Text;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 1.4: the text worker's File info and SHA-256. It reads, never writes; every answer stays within the bounds the
/// host checks; and a file whose content disagrees with its name is shown as what it is.
/// </summary>
public sealed class TextWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static JobResult Run(string capability, string path, IReadOnlyDictionary<string, string>? parameters = null) =>
        FileJobs.Run(new JobRequest(Guid.NewGuid().ToString("N"), capability, [path], null, parameters));

    private static string Fact(JobResult r, string label) => r.Facts.Single(f => f.Label == label).Value;

    [Fact]
    public void Sha256_of_known_bytes_matches_the_standard_value()
    {
        var path = _dir.Write("abc.txt", "abc"u8.ToArray());
        var r = Run(FilesCapabilities.Sha256Id, path);
        Assert.True(r.Success, r.Message);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Fact(r, "SHA-256")); // FIPS 180-2, "abc"
        Assert.True(JobFacts.AreShowable(r.Facts));
        Assert.DoesNotContain(r.Facts, f => f.Notice);
    }

    [Fact]
    public void Info_names_the_real_type()
    {
        // A program named like a document: A_real_program_header_is_still_recognised (a bare "MZ" is not one).
        var png = _dir.CopySample("quadrants-64x48.png", "a.png");
        var info = Run(FilesCapabilities.InfoId, png);
        Assert.True(info.Success, info.Message);
        Assert.Equal("PNG image", Fact(info, "Type"));
        Assert.True(JobFacts.AreShowable(info.Facts));
    }

    [Fact]
    public void Text_and_unknown_content_are_never_called_a_mismatch()
    {
        var notes = _dir.Write("notes.pdf", Encoding.UTF8.GetBytes("just some words\r\n"));
        Assert.False(Run(FilesCapabilities.InfoId, notes).Facts.Single(f => f.Label == "Type").Notice);
        var blob = _dir.Write("data.bin", [0, 1, 2, 3, 0xFE, 0x00]);
        Assert.Equal("Unknown (no known signature)", Fact(Run(FilesCapabilities.InfoId, blob), "Type"));
    }

    [Theory]
    [InlineData("MZ notes from the meeting\r\n", "notes.txt")]          // "MZ" without a PE header
    [InlineData("BMW,320i,2019\r\nAudi,A4,2020\r\n", "cars.csv")]     // "BM" without a BMP header
    [InlineData("ID3 tags explained\r\n", "id3.txt")]                   // "ID3" without a tag header
    public void Text_that_starts_like_a_signature_is_still_text(string text, string name)
    {
        var path = _dir.Write(name, Encoding.UTF8.GetBytes(text));
        var type = Run(FilesCapabilities.InfoId, path).Facts.Single(f => f.Label == "Type");
        Assert.Equal("Text", type.Value);
        Assert.False(type.Notice);
    }

    [Fact]
    public void A_real_program_header_is_still_recognised()
    {
        var pe = new byte[0x100];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
        "PE\0\0"u8.CopyTo(pe.AsSpan(0x80));
        var type = Run(FilesCapabilities.InfoId, _dir.Write("invoice.pdf", pe)).Facts.Single(f => f.Label == "Type");
        Assert.Equal("Windows program, but named .pdf", type.Value);
        Assert.True(type.Notice);
    }

    [Fact]
    public void An_online_only_file_is_never_read()
    {
        // Reading a cloud placeholder downloads it (evaluator, PR #18). The offline attribute stands in for one here.
        var path = _dir.Write("cloud.txt", "abc"u8.ToArray());
        File.SetAttributes(path, FileAttributes.Offline);
        try
        {
            Assert.Equal("Online only (not downloaded)", Fact(Run(FilesCapabilities.InfoId, path), "Type"));
            Assert.Equal(ErrorCode.OnlineOnly, Run(FilesCapabilities.Sha256Id, path).Error);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void A_folder_of_exactly_the_limit_is_counted_not_called_more()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir.Path, "many"));
        for (var i = 0; i < FileJobs.MaxEntries; i++) File.WriteAllBytes(Path.Combine(folder.FullName, $"{i}.t"), []);
        Assert.Equal("10,000 files", FileJobs.Contents(folder));
        File.WriteAllBytes(Path.Combine(folder.FullName, "one more.t"), []);
        Assert.Equal("More than 10,000 items", FileJobs.Contents(folder));
    }

    [Fact]
    public void Info_of_a_folder_counts_its_own_entries()
    {
        var folder = Path.Combine(_dir.Path, "box");
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "b");
        var r = Run(FilesCapabilities.InfoId, folder + Path.DirectorySeparatorChar);
        Assert.True(r.Success, r.Message);
        Assert.Equal("Folder", Fact(r, "Type"));
        Assert.Equal("2 files, 1 folder", Fact(r, "Contains"));
        Assert.True(JobFacts.AreShowable(r.Facts));
    }

    [Fact]
    public void Hashing_a_folder_or_a_missing_file_fails_with_its_own_reason()
    {
        var folder = Path.Combine(_dir.Path, "box");
        Directory.CreateDirectory(folder);
        Assert.Equal(ErrorCode.IsAFolder, Run(FilesCapabilities.Sha256Id, folder + Path.DirectorySeparatorChar).Error);
        Assert.Equal(ErrorCode.SourceNotFound, Run(FilesCapabilities.Sha256Id, Path.Combine(_dir.Path, "gone.txt")).Error);
        Assert.Equal(ErrorCode.SourceNotFound, Run(FilesCapabilities.InfoId, Path.Combine(_dir.Path, "gone.txt")).Error);
    }

    [Fact]
    public void Unknown_commands_parameters_and_paths_are_refused()
    {
        var path = _dir.Write("a.txt", "a"u8.ToArray());
        Assert.Equal(ErrorCode.UnsupportedOperation, Run("convert.to-png", path).Error);
        Assert.Equal(ErrorCode.InvalidRequest, Run(FilesCapabilities.InfoId, path, new Dictionary<string, string> { ["x"] = "1" }).Error);
        Assert.Equal(ErrorCode.InvalidRequest, Run(FilesCapabilities.InfoId, path + "::$DATA").Error);
    }

    [Fact]
    public void Reading_a_file_leaves_it_as_it_was()
    {
        var path = _dir.CopySample("quadrants-64x48.png", "keep.png");
        var (bytes, written) = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
        Run(FilesCapabilities.InfoId, path);
        Run(FilesCapabilities.Sha256Id, path);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.EnumerateFileSystemEntries(_dir.Path)); // nothing written next to it
    }

    [Fact]
    public async Task The_real_worker_process_answers_over_the_pipe_and_the_host_believes_it()
    {
        var path = _dir.Write("abc.txt", "abc"u8.ToArray());
        var module = WinTools.Host.Jobs.FilesJobModule.Instance;
        var client = new WinTools.Host.Jobs.WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Text.exe"), TimeSpan.FromSeconds(30), module);
        var request = new JobRequest("p1", FilesCapabilities.Sha256Id, [path]);
        var results = await client.RunAsync([request], CancellationToken.None);
        var r = Assert.Single(results);
        Assert.True(r.Success, r.Message);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Fact(r, "SHA-256"));
        Assert.True(module.IsBelievable(request, r));
    }
}
