using System.Diagnostics;

namespace WinTools.Worker.Tests;

/// <summary>
/// HEIF and AVIF test files made on the spot from a known picture (ADR-0031): the bundled FFmpeg encodes each tile as a
/// one-frame MP4 (HEVC with kvazaar, BSD; AV1 with libaom, BSD), and <see cref="HeifBuilder"/> packs them as a camera
/// would. Nothing is downloaded and no personal photo is used. The picture is four colour blocks (red top left, green
/// top right, blue bottom left, yellow bottom right) with a gentle gradient, so orientation and placement can be read
/// from a few pixels after a lossy round trip.
/// </summary>
internal static class HeifSamples
{
    public static readonly string Engine = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    public static readonly (byte R, byte G, byte B) Red = (220, 40, 40), Green = (40, 200, 60), Blue = (40, 60, 220), Yellow = (230, 220, 40);

    /// <summary>The block colour at (x, y) of a <paramref name="width"/> × <paramref name="height"/> picture.</summary>
    public static (byte R, byte G, byte B) Block(int x, int y, int width, int height) =>
        (x < width / 2, y < height / 2) switch { (true, true) => Red, (false, true) => Green, (true, false) => Blue, _ => Yellow };

    public static byte[] Picture(int width, int height) => PngWriter.Rgba(width, height, (x, y) =>
    {
        var (r, g, b) = Block(x, y, width, height);
        var shade = (x + y) % 16;
        return ((byte)Math.Min(255, r + shade), (byte)Math.Min(255, g + shade), (byte)Math.Min(255, b + shade), 255);
    });

    /// <summary>The tiles of <paramref name="png"/> (row by row), each encoded by the engine.</summary>
    public static IReadOnlyList<CodedTile> Tiles(string folder, string png, int tileWidth, int tileHeight, int columns, int rows, bool av1 = false, string format = "yuv420p")
    {
        var tiles = new List<CodedTile>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                tiles.Add(Encode(folder, png, $"crop={tileWidth}:{tileHeight}:{c * tileWidth}:{r * tileHeight},format={format}", av1));
        return tiles;
    }

    private static readonly string[] Av1 = ["-c:v", "libaom-av1", "-cpu-used", "8"], Hevc = ["-c:v", "libkvazaar"];

    /// <summary>One picture of <paramref name="png"/> through a filter, encoded as HEVC or AV1.</summary>
    public static CodedTile Encode(string folder, string png, string filter, bool av1 = false)
    {
        var mp4 = Path.Combine(folder, $"tile-{Guid.NewGuid():N}.mp4");
        Run(["-hide_banner", "-loglevel", "error", "-y", "-i", png, "-vf", filter, "-frames:v", "1",
            .. av1 ? Av1 : Hevc, "-f", "mp4", mp4]);
        var tile = CodedTile.FromMp4(mp4);
        File.Delete(mp4);
        return tile;
    }

    public static void Run(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Engine) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
    }

    /// <summary>The Windows sRGB profile (Windows ships it; other tests use it too).</summary>
    public static byte[] SrgbProfile() =>
        File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color\sRGB Color Space Profile.icm"));

    /// <summary>Whether two colours are within a lossy codec's error of each other.</summary>
    public static bool Near((int R, int G, int B, int A) actual, (byte R, byte G, byte B) expected, int tolerance = 40) =>
        Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance;
}
