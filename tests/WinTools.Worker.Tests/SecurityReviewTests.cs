using System.Diagnostics;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;
using WinTools.Host.Jobs;
using WinTools.Windows.Settings;

namespace WinTools.Worker.Tests;

/// <summary>Findings of the TASK 5 security review, each pinned by a test.</summary>
public sealed class SecurityReviewTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static ConversionResult Run(string op, string source) => ConversionJob.Run(new ConversionRequest("s1", op, source));

    /// <summary>M2: a small TIFF whose strip table repeats one compressed blob must not keep the worker busy.</summary>
    [Fact]
    public void Tiff_with_more_strips_than_the_image_needs_is_refused_at_once()
    {
        var tiff = TiffWriter.Rgb(40, 30, rowsPerStrip: 30, compression: 8, (x, y) => ((byte)(x * 6), (byte)(y * 8), 90));
        Assert.True(Run("convert.to-png", _dir.Write("ok.tif", tiff)).Success);

        var bomb = _dir.Write("bomb.tif", WithRepeatedStrip(tiff, 500_000));
        var clock = Stopwatch.StartNew();
        var r = Run("metadata.info", bomb);
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Contains("strip table", r.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
    }

    /// <summary>m1: an EXIF IFD0 offset near int.MaxValue must not wrap past the bounds check.</summary>
    [Theory]
    [InlineData("metadata.info")]
    [InlineData("metadata.strip")]
    public void Exif_offset_at_the_end_of_the_range_is_handled_not_a_crash(string op)
    {
        var jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.jpg"));
        byte[] payload = [.. "Exif"u8, 0, 0, .. "II*"u8, 0, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] app1 = [0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];
        var src = _dir.Write("exif.jpg", [.. jpeg[..2], .. app1, .. jpeg[2..]]);
        var r = Run(op, src);
        Assert.NotEqual(ErrorCode.Internal, r.Error);
        Assert.True(r.Success, $"{r.Error}: {r.Message}");
    }

    /// <summary>m7 (not reproduced): the no-overwrite move reports a folder with the output name as taken, so the next free name is used.</summary>
    [Fact]
    public void A_folder_with_the_output_name_is_skipped()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        Directory.CreateDirectory(Path.Combine(_dir.Path, "photo.jpg"));
        var r = Run("convert.to-jpeg", src);
        Assert.True(r.Success, $"{r.Error}: {r.Message}");
        Assert.Equal("photo (1).jpg", Path.GetFileName(r.OutputPath));
    }

    /// <summary>m3: a worker line is never read past the protocol limit; normal lines are unaffected.</summary>
    [Fact]
    public async Task Worker_lines_are_bounded()
    {
        using var huge = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', WorkerProtocol.MaxLineLength + 10))));
        var tooLong = await WorkerClient.ReadLineAsync(huge, CancellationToken.None);
        Assert.NotNull(tooLong);
        Assert.True(tooLong.Length < 10);

        using var normal = new StreamReader(new MemoryStream("{\"a\":1}\r\nnext\n"u8.ToArray()));
        Assert.Equal("{\"a\":1}", await WorkerClient.ReadLineAsync(normal, CancellationToken.None));
        Assert.Equal("next", await WorkerClient.ReadLineAsync(normal, CancellationToken.None));
        Assert.Null(await WorkerClient.ReadLineAsync(normal, CancellationToken.None));
    }

    /// <summary>m3: a success claim naming a file outside the naming policy is refused, even with the right hash.</summary>
    [Fact]
    public void A_claim_outside_the_naming_policy_is_refused_even_with_the_right_hash()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        var other = _dir.CopySample("quadrants-64x48.jpg", "elsewhere.jpg");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(other)));
        Assert.False(WorkerClient.IsBelievable(new ConversionRequest("c1", "convert.to-jpeg", src), ConversionResult.Ok("c1", other, sha)));
    }

    /// <summary>m8: an oversized settings file is not parsed, moved or overwritten.</summary>
    [Fact]
    public void An_oversized_settings_file_gives_defaults_and_is_left_untouched()
    {
        var path = Path.Combine(_dir.Path, "settings.json");
        var text = "{\"schema\":1,\"triggerKey\":\"F9\"" + new string(' ', SettingsStore.MaxFileBytes) + "}";
        File.WriteAllText(path, text);
        var store = new SettingsStore(path);
        Assert.Equal(new AppSettings(), store.Load());
        Assert.True(store.ReadOnly, store.LastLoad);
        Assert.False(store.Save(new AppSettings()));
        Assert.Equal(text, File.ReadAllText(path));
    }

    /// <summary>Points StripOffsets/StripByteCounts at <paramref name="copies"/> entries that all repeat the first strip.</summary>
    private static byte[] WithRepeatedStrip(byte[] tiff, int copies)
    {
        Assert.True(tiff[0] == 'I' && tiff[1] == 'I');
        var ifd = BitConverter.ToInt32(tiff, 4);
        var entries = BitConverter.ToUInt16(tiff, ifd);
        var output = new List<byte>(tiff);
        while (output.Count % 4 != 0) output.Add(0);
        var result = output.ToArray();
        var extra = new List<byte>();
        foreach (var tag in new[] { 273, 279 })
        {
            var e = Enumerable.Range(0, entries).Select(i => ifd + 2 + i * 12).Single(p => BitConverter.ToUInt16(tiff, p) == tag);
            var type = BitConverter.ToUInt16(tiff, e + 2);
            var value = type == 3 ? BitConverter.ToUInt16(tiff, e + 8) : BitConverter.ToUInt32(tiff, e + 8);
            var at = result.Length + extra.Count;
            for (var k = 0; k < copies; k++) extra.AddRange(BitConverter.GetBytes((uint)value));
            BitConverter.TryWriteBytes(result.AsSpan(e + 2), (ushort)4);
            BitConverter.TryWriteBytes(result.AsSpan(e + 4), copies);
            BitConverter.TryWriteBytes(result.AsSpan(e + 8), at);
        }
        return [.. result, .. extra];
    }
}
