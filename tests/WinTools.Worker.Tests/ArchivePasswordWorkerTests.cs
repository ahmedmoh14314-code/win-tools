using System.Buffers.Binary;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Archive;
using static WinTools.Worker.Tests.LockedZipBuilder;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 5.3 (ADR-0027): extracting and testing encrypted ZIPs with a password, on .NET's own AES, PBKDF2 and HMAC.
/// WinRAR's samples (<c>tests/samples/locked-*.zip</c>, an encoder independent of the worker) prove the reading; ZIPs built
/// here add what WinRAR never writes. A wrong or missing password, damage or a bomb leaves nothing in the destination;
/// the password is never in a message. Written before the code.
/// </summary>
public sealed class ArchivePasswordWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private const string Right = "right", Arabic = "سرّé1";
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

    private static string[] Tree(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p)).Order(StringComparer.Ordinal)];

    /// <summary>Everything in the test folder, hidden staging folders included.</summary>
    private string[] Everything() => [.. Directory.EnumerateFileSystemEntries(_dir.Path).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    private static void AssertSamplesTree(JobRequest request, JobResult r)
    {
        Assert.True(r.Success, r.Message);
        var folder = r.Outputs.Single().Path;
        Assert.Equal(["a.txt", "sub", @"sub\big.bin", "tiny.txt"], Tree(folder));
        Assert.Equal("hello encrypted world\n", File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Equal(Big, File.ReadAllBytes(Path.Combine(folder, "sub", "big.bin")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(folder, "tiny.txt")));
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
    }

    [Theory]
    [InlineData("locked-aes256.zip")]      // WinRAR: AE-1 entries, and AE-2 for the 1-byte file
    [InlineData("locked-zipcrypto.zip")]   // WinRAR -mezl: every entry with the data-descriptor flag
    public void WinRARs_archives_extract_with_the_right_password_and_the_host_believes_the_folder(string sample)
    {
        var zip = _dir.CopySample(sample);
        var before = Hash.Of(zip);
        var request = Request(ArchiveCapabilities.ExtractId, zip, Right);
        AssertSamplesTree(request, ArchiveJobs.Run(request));
        Assert.Equal(before, Hash.Of(zip));
    }

    [Theory]
    [InlineData("locked-aes256.zip")]
    [InlineData("locked-zipcrypto.zip")]
    public void Without_a_password_extract_and_test_say_one_is_needed_and_write_nothing(string sample)
    {
        var zip = _dir.CopySample(sample);
        Assert.Equal(ErrorCode.PasswordRequired, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip)).Error);
        Assert.Equal(ErrorCode.PasswordRequired, ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, zip)).Error);
        Assert.Equal([sample], Everything());
    }

    [Theory]
    [InlineData("locked-aes256.zip")]
    [InlineData("locked-zipcrypto.zip")]
    public void A_wrong_password_is_its_own_error_and_leaves_nothing(string sample)
    {
        var zip = _dir.CopySample(sample);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip, "wrong-pass-123"));
        Assert.Equal(ErrorCode.PasswordIncorrect, r.Error);
        Assert.DoesNotContain("wrong-pass", r.Message ?? "", StringComparison.Ordinal);
        Assert.Equal([sample], Everything());
        Assert.Equal(ErrorCode.PasswordIncorrect, ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, zip, "wrong-pass-123")).Error);
    }

    [Fact]
    public void An_arabic_password_opens_WinRARs_AES_archive()
    {
        var zip = _dir.CopySample("locked-aes256-arabic.zip");
        var request = Request(ArchiveCapabilities.ExtractId, zip, Arabic);
        AssertSamplesTree(request, ArchiveJobs.Run(request));
    }

    [Fact]
    public void An_arabic_password_opens_WinRARs_ZipCrypto_archive_through_the_ANSI_code_page()
    {
        // WinRAR encoded the password with the ANSI code page of the machine that made the sample (1252), "?" for
        // each Arabic letter. On a machine whose code page encodes it the same way it opens; elsewhere it is, rightly,
        // the wrong password. Never anything else.
        var zip = _dir.CopySample("locked-zipcrypto-arabic.zip");
        var request = Request(ArchiveCapabilities.ExtractId, zip, Arabic);
        var r = ArchiveJobs.Run(request);
        var expected = new byte[] { 0x3F, 0x3F, 0x3F, 0xE9, 0x31 };
        if (ZipPasswords.Candidates(Arabic, null).Any(c => c.AsSpan().SequenceEqual(expected))) AssertSamplesTree(request, r);
        else Assert.Equal(ErrorCode.PasswordIncorrect, r.Error);
    }

    [Fact]
    public void The_password_bytes_tried_are_UTF8_then_its_NFKC_form_then_the_ANSI_and_OEM_code_pages()
    {
        var candidates = ZipPasswords.Candidates(Arabic, "x", ansiCodePage: 1252, oemCodePage: 437);
        Assert.Equal(Encoding.UTF8.GetBytes(Arabic), candidates[0]);
        Assert.Equal("x"u8.ToArray(), candidates[1]);
        Assert.Equal(new byte[] { 0x3F, 0x3F, 0x3F, 0xE9, 0x31 }, candidates[2]);  // what WinRAR wrote on 1252
        Assert.Equal(new byte[] { 0x3F, 0x3F, 0x3F, 0x82, 0x31 }, candidates[3]);  // 437: é is 0x82
        Assert.Single(ZipPasswords.Candidates("plain", null, 1252, 437)); // one candidate when every form is the same
    }

    [Theory]
    [InlineData(Cipher.Aes128, 1, false)]
    [InlineData(Cipher.Aes192, 2, true)]
    [InlineData(Cipher.Aes256, 2, false)]
    [InlineData(Cipher.ZipCrypto, 1, false)]
    [InlineData(Cipher.ZipCrypto, 1, true)]
    public void Every_strength_and_version_reads_stored_or_deflated(Cipher cipher, int version, bool deflate)
    {
        var bytes = Build("p"u8.ToArray(), new Item("docs/", null), new Item("docs/r.txt", Big, cipher, deflate, version),
            new Item("plain.txt", "not locked"u8.ToArray(), Cipher.None));
        var zip = _dir.Write("built.zip", bytes);
        var request = Request(ArchiveCapabilities.ExtractId, zip, "p");
        var r = ArchiveJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal(Big, File.ReadAllBytes(Path.Combine(r.Outputs.Single().Path, "docs", "r.txt")));
        Assert.Equal("not locked", File.ReadAllText(Path.Combine(r.Outputs.Single().Path, "plain.txt")));
        Assert.True(ArchiveJobModule.Instance.IsBelievable(request, r));
    }

    /// <summary>The offset of a named entry's packed data, found by scanning local headers (independently of the worker).</summary>
    private static (int Data, int Packed) Locate(byte[] zip, string name)
    {
        var target = Encoding.UTF8.GetBytes(name);
        for (var i = 0; i + 30 < zip.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) != 0x04034B50) continue;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + 26)), x = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + 28));
            if (zip.AsSpan(i + 30, n).SequenceEqual(target))
                return (i + 30 + n + x, (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i + 18)));
        }
        throw new InvalidOperationException(name);
    }

    [Theory]
    [InlineData("locked-aes256.zip", "tiny.txt", -1)]        // AE-2: no CRC; only the HMAC sees a changed authentication code
    [InlineData("locked-aes256.zip", "tiny.txt", 18)]        // AE-2: the first encrypted byte
    [InlineData("locked-aes256.zip", "a.txt", -1)]           // AE-1: the HMAC, and the CRC
    [InlineData("locked-aes256.zip", "sub/big.bin", 40)]
    [InlineData("locked-zipcrypto.zip", "sub/big.bin", 40)]  // ZipCrypto: the CRC is its only check
    [InlineData("locked-zipcrypto.zip", "a.txt", 14)]
    public void A_changed_byte_is_never_a_success_and_nothing_is_kept(string sample, string entry, int at)
    {
        var bytes = File.ReadAllBytes(_dir.CopySample(sample, "source.zip"));
        var (data, packed) = Locate(bytes, entry);
        bytes[at < 0 ? data + packed - 1 : data + at] ^= 0x20;
        var zip = _dir.Write("changed.zip", bytes);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip, Right));
        Assert.False(r.Success);
        Assert.Contains(r.Error, new[] { ErrorCode.DecodeFailed, ErrorCode.PasswordIncorrect });
        Assert.Equal(["changed.zip", "source.zip"], Everything());
        Assert.False(ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, zip, Right)).Success);
    }

    [Fact]
    public void A_changed_byte_in_a_stored_ZipCrypto_entry_fails_on_its_CRC_the_only_check_it_has()
    {
        // No decompressor and no HMAC to notice: without the CRC-32 the changed file would be extracted as a success.
        var bytes = Build("p"u8.ToArray(), out var placed, new Item("a.txt", "abcdefghij"u8.ToArray(), Cipher.ZipCrypto, Deflate: false));
        bytes[placed[0].Data + 12 + 3] ^= 0x01;
        var zip = _dir.Write("changed.zip", bytes);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip, "p"));
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Equal(["changed.zip"], Everything());
    }

    [Fact]
    public void A_local_header_that_is_not_where_the_directory_says_fails_cleanly()
    {
        var bytes = Build("p"u8.ToArray(), out var placed, new Item("a.txt", "abc"u8.ToArray()), new Item("b.txt", Big));
        var record = (int)placed[1].CentralRecord;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(record + 42), (uint)placed[1].LocalHeader + 3);
        var zip = _dir.Write("moved.zip", bytes);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip, "p"));
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Equal(["moved.zip"], Everything());
    }

    [Theory]
    [InlineData(Cipher.ZipCrypto, 11)] // shorter than its 12-byte header
    [InlineData(Cipher.Aes256, 27)]    // shorter than salt, verifier and authentication code
    public void Packed_data_shorter_than_the_ciphers_own_fields_fails_cleanly(Cipher cipher, int packed)
    {
        var bytes = Build("p"u8.ToArray(), out var placed, new Item("a.txt", "abcdef"u8.ToArray(), cipher, Deflate: false));
        // Shorten the entry in both headers, as a careless or hostile writer would.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)placed[0].LocalHeader + 18), (uint)packed);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)placed[0].CentralRecord + 20), (uint)packed);
        var zip = _dir.Write("short.zip", bytes);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip, "p"));
        Assert.False(r.Success);
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Equal(["short.zip"], Everything());
    }

    [Theory]
    [InlineData(Cipher.Aes256)]
    [InlineData(Cipher.ZipCrypto)]
    public void An_encrypted_bomb_is_stopped_by_the_same_limits(Cipher cipher)
    {
        var zip = _dir.Write("bomb.zip", Build("p"u8.ToArray(), new Item("zeros.bin", new byte[64 << 20], cipher)));
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip, "p"));
        Assert.Equal(ErrorCode.LimitExceeded, r.Error);
        Assert.Equal(["bomb.zip"], Everything());
    }

    [Fact]
    public void One_archive_has_one_password_so_a_lucky_check_byte_does_not_decide_it()
    {
        // ZipCrypto's check byte lets a wrong password through 1 time in 256. Here every entry but the first refuses the
        // UTF-8 bytes, so the first entry's check alone must not choose them: the code-page form opens all three.
        var ansi = new byte[] { 0x3F, 0x3F, 0x3F, 0xE9, 0x31 };
        var utf8 = Encoding.UTF8.GetBytes(Arabic);
        byte[] bytes;
        var attempts = 0;
        do
        {
            // Build until the first entry's header happens to accept the UTF-8 bytes too (about 1 build in 256).
            bytes = Build(ansi, new Item("1.txt", "one"u8.ToArray(), Cipher.ZipCrypto, Deflate: false),
                new Item("2.txt", "two"u8.ToArray(), Cipher.ZipCrypto, Deflate: false), new Item("3.txt", "three"u8.ToArray(), Cipher.ZipCrypto, Deflate: false));
        }
        while (!(HeaderAccepts(bytes, "1.txt", "one"u8, utf8) && !HeaderAccepts(bytes, "2.txt", "two"u8, utf8)) && ++attempts < 20_000);
        Assert.True(HeaderAccepts(bytes, "1.txt", "one"u8, utf8));
        var candidates = ZipPasswords.Candidates(Arabic, null, ansiCodePage: 1252, oemCodePage: 437);
        var zip = _dir.Write("lucky.zip", bytes);
        Assert.Equal([utf8, ansi], candidates.Take(2).Select(c => c.ToArray()));
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, zip, Arabic), candidates);
        Assert.True(r.Success, r.Message);
    }

    private static bool HeaderAccepts(byte[] zip, string name, ReadOnlySpan<byte> content, byte[] password)
    {
        var (data, _) = Locate(zip, name);
        var header = ZipCryptoDecryptHeader(password, zip.AsSpan(data, 12));
        return header[11] == (byte)(WinTools.Core.Files.Crc32.Of(content) >> 24);
    }

    [Fact]
    public void Test_with_the_password_checks_every_entry()
    {
        var zip = _dir.CopySample("locked-aes256.zip");
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.TestId, zip, Right));
        Assert.True(r.Success, r.Message);
        Assert.Contains(r.Facts, f => f.Label == "Checked" && f.Value.StartsWith("3 files", StringComparison.Ordinal));
        Assert.Equal(["locked-aes256.zip"], Everything());
    }

    [Theory]
    [InlineData("locked-aes256.zip", "3 entries need a password (AES-256)")]
    [InlineData("locked-zipcrypto.zip", "3 entries need a password (ZipCrypto, a weak, old method)")]
    public void Info_names_the_method_and_needs_no_password(string sample, string expected)
    {
        var zip = _dir.CopySample(sample);
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, zip));
        Assert.True(r.Success, r.Message);
        Assert.Contains(r.Facts, f => f is { Label: "Protection", Notice: true } && f.Value == expected);
    }

    [Fact]
    public void Only_extract_and_test_take_a_password_and_it_passes_the_policy()
    {
        var zip = _dir.CopySample("locked-aes256.zip");
        Assert.Equal(ErrorCode.InvalidRequest, ArchiveJobs.Run(Request(ArchiveCapabilities.InfoId, zip, Right)).Error);
        Assert.Equal(ErrorCode.InvalidRequest, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip, "two\nlines")).Error);
        var extra = new JobRequest(Guid.NewGuid().ToString("N"), ArchiveCapabilities.ExtractId, [zip], null,
            new Dictionary<string, string> { [PasswordPolicy.ParameterName] = Right, ["more"] = "x" });
        Assert.Equal(ErrorCode.InvalidRequest, ArchiveJobs.Run(extra).Error);
        Assert.Equal(["locked-aes256.zip"], Everything());
    }

    [Fact]
    public void The_normalized_form_is_tried_when_the_typed_one_fails()
    {
        // Protected with "x2" (as an NFKC-normalizing tool would), typed as "x²".
        var zip = _dir.Write("nfkc.zip", Build("x2"u8.ToArray(), new Item("a.txt", "abc"u8.ToArray())));
        var r = ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, zip, "x²", "x2"));
        Assert.True(r.Success, r.Message);
    }

    [Fact]
    public void Strong_encryption_and_an_AES_entry_without_its_field_are_unsupported()
    {
        var strong = Build("p"u8.ToArray(), out var placed, new Item("a.txt", "abc"u8.ToArray(), Cipher.ZipCrypto));
        strong[(int)placed[0].LocalHeader + 6] |= 0x40;
        strong[(int)placed[0].CentralRecord + 8] |= 0x40;
        Assert.Equal(ErrorCode.UnsupportedContent, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, _dir.Write("strong.zip", strong), "p")).Error);
        var bare = Build("p"u8.ToArray(), out placed, new Item("a.txt", "abc"u8.ToArray(), Cipher.Aes256));
        bare[(int)placed[0].CentralRecord + 46 + 5] = 0x00; // the central record's AES field gets another tag
        Assert.Equal(ErrorCode.UnsupportedContent, ArchiveJobs.Run(Request(ArchiveCapabilities.ExtractId, _dir.Write("bare.zip", bare), "p")).Error);
        Assert.Equal(["bare.zip", "strong.zip"], Everything());
    }

    [Fact]
    public async Task The_real_worker_process_extracts_with_the_password_from_its_parameters()
    {
        var zip = _dir.CopySample("locked-zipcrypto.zip");
        var client = new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Archive.exe"), TimeSpan.FromMinutes(1), ArchiveJobModule.Instance, parallelism: 1);
        var results = await client.RunAsync([Request(ArchiveCapabilities.ExtractId, zip, Right), Request(ArchiveCapabilities.ExtractId, zip, "nope")], CancellationToken.None);
        Assert.True(results[0].Success, results[0].Message);
        Assert.Equal(ErrorCode.PasswordIncorrect, results[1].Error);
        Assert.DoesNotContain("nope", results[1].Message ?? "", StringComparison.Ordinal);
    }
}
