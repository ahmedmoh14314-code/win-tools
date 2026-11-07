using System.Diagnostics;
using Xunit.Abstractions;

namespace WinTools.Worker.Tests;

/// <summary>
/// ADR-0031's timings, on a phone-size HEIC built like an iPhone's (4032 × 3024 from 48 tiles of 512, descriptor in idat,
/// rotated, thumbnail, gain map, ICC) and on "To AVIF" of the same picture. Prints numbers; asserts nothing.
/// </summary>
public sealed class HeifMeasurement(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    [MeasurementFact]
    public void Phone_size_heic_and_avif()
    {
        var reference = _dir.Write("ref.png", WicTestPhoto(4096, 3072));
        var tiles = HeifSamples.Tiles(_dir.Path, reference, 512, 512, 8, 6);
        var heic = _dir.Write("IMG_1000.heic", new HeifBuilder
        {
            Rotation = 3, ExifOrientation = 6, Icc = HeifSamples.SrgbProfile(),
            Thumbnail = HeifSamples.Encode(_dir.Path, reference, "scale=320:240,format=yuv420p"),
            Auxiliary = HeifSamples.Encode(_dir.Path, reference, "scale=1008:752,format=gray,format=yuv420p"),
            AuxiliaryType = "urn:com:apple:photo:2020:aux:hdrgainmap",
        }.Build(tiles, 8, 6, 4032, 3024));
        output.WriteLine($"HEIC: {new FileInfo(heic).Length / 1024} KB");
        foreach (var op in new[] { "convert.to-jpeg", "resize.percent-50", "convert.to-avif" })
        {
            var watch = Stopwatch.StartNew();
            var r = ConversionJob.Run(new WinTools.Core.Jobs.ConversionRequest("m" + op.Length, op, heic));
            output.WriteLine($"{op}: {watch.Elapsed.TotalSeconds:0.00} s, {r.Success} {r.Message}, {(r.OutputPath is null ? 0 : new FileInfo(r.OutputPath).Length / 1024)} KB");
            if (r.OutputPath is not null) File.Delete(r.OutputPath);
        }
    }

    private static byte[] WicTestPhoto(int width, int height)
    {
        var photo = CompressionCorpus.Photo(seed: 3, width: width, height: height);
        return WinTools.Worker.Imaging.WicCodec.Encode(photo, WinTools.Core.Files.ImageFormat.Png);
    }
}
