using System.Runtime.CompilerServices;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Interaction;
using WinTools.Core.Settings;

namespace WinTools.Core.Tests;

/// <summary>
/// V2 PR 1.1 moves the image commands behind generic file kinds and capability descriptors with no behaviour change.
/// This pins the behaviour as it was before the move: for representative selections, every category, command id,
/// label, output format and name suffix the menu offers, written to a golden file from the V1 code. The refactor must
/// reproduce it byte for byte; a deliberate change updates the golden file in its own commit with the reason.
/// </summary>
public sealed class MenuGoldenTests
{
    private static readonly string[][] Selections =
    [
        [@"C:\p\a.png"], [@"C:\p\a.jpg"], [@"C:\p\a.jpeg"], [@"C:\p\a.JFIF"], [@"C:\p\a.webp"], [@"C:\p\a.bmp"], [@"C:\p\a.dib"],
        [@"C:\p\a.tif"], [@"C:\p\a.tiff"],
        [@"C:\p\a.png", @"C:\p\b.jpg"], [@"C:\p\a.png", @"C:\p\b.png"], [@"C:\p\a.jpg", @"C:\p\b.webp"],
        [@"C:\p\a.png", @"C:\p\b.bmp", @"C:\p\c.tif"],
        [@"C:\p\a.txt"], [@"C:\p\a.pdf"], [@"C:\p\a.png", @"C:\p\b.txt"], [@"C:\p\noext"], [],
        [@"\\server\share\a.png"], [@"C:\p\a.png:stream"], [@"C:\p\a.png."],
        // widened after the evaluator's review of PR #15: case variants, the other JPEG extension, image-like unknowns,
        // dotted folders, several dots, a name that is only an extension, Unicode and a path beyond 260 characters
        [@"C:\p\a.jpe"], [@"C:\p\a.TIF"], [@"C:\p\a.Png"], [@"C:\p\a.gif"], [@"C:\p\a.heic"], [@"C:\p.png\a.jpg"],
        [@"C:\p\a.b.png"], [@"C:\p\.png"], [@"C:\p\a.PNG "], [@"C:\صور\😀 a.webp"],
        [@"C:\p\" + new string('x', 250) + @"\a.png"],
    ];

    private static readonly string?[] CustomResizes = [null, "resize.width-800", "resize.box-1920x1080-stretch", "resize.height-600-upscale",
        "resize.box-1920x1080", "resize.box-1920x1080-upscale", "resize.width-0", "resize.width-65536", "garbage"];

    /// <summary>The output format from the name, written here so the golden file does not lean on refactored code.</summary>
    private static string Out(Operation op, string path)
    {
        var source = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => Files.ImageFormat.Png,
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => Files.ImageFormat.Jpeg,
            ".webp" => Files.ImageFormat.WebP,
            ".bmp" or ".dib" => Files.ImageFormat.Bmp,
            ".tif" or ".tiff" => Files.ImageFormat.Tiff,
            _ => Files.ImageFormat.Unknown,
        };
        return op.OutputFormat(source).ToString();
    }

    private static CapabilityResolver Resolver(string? customResizeId) => CapabilityResolver.Images(customResizeId); // V1's menu: the image module

    internal static string Render()
    {
        var text = new StringBuilder();
        foreach (var custom in CustomResizes)
        {
            var resolver = Resolver(custom);
            text.Append("## custom resize: ").AppendLine(custom ?? "none");
            foreach (var selection in Selections)
            {
                text.Append("# ").AppendLine(selection.Length == 0 ? "(empty)" : string.Join(" + ", selection.Select(p => p.Length > 80 ? p[..40] + $"…({p.Length} chars)" : p)));
                foreach (var group in RadialMenu.Build(resolver, selection))
                {
                    text.Append("  [").Append(group.Category.Label).Append("] ").AppendLine(group.Label); // V1 printed the enum name, which was the label
                    foreach (var op in group.Commands.Select(ImageCapabilities.OperationOf))
                        text.Append("    ").Append(op.Id).Append(" | ").Append(op.Label).Append(" | writes ").Append(op.WritesFile)
                            .Append(" | suffix ").Append(op.NameSuffix ?? "-").Append(" | out ")
                            .AppendLine(string.Join(",", selection.Select(p => Out(op, p))));
                }
            }
        }
        // The file limit: 1000 files get the commands of one, 1001 get none.
        foreach (var n in new[] { 1000, 1001 })
            text.Append("## ").Append(n).Append(" png files: ").AppendLine(string.Join(" ", Resolver(null).Resolve(Enumerable.Range(0, n).Select(i => $@"C:\p\{i}.png").ToArray()).Select(o => o.Id)));
        return text.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void The_menu_for_every_representative_selection_matches_the_v1_golden_file()
    {
        // A deliberate change: WINTOOLS_WRITE_GOLDEN=1 rewrites the file next to this source, to be reviewed and committed.
        // The test then fails, so a run with the variable set can never pass by comparing the output with itself.
        if (Environment.GetEnvironmentVariable("WINTOOLS_WRITE_GOLDEN") == "1")
        {
            File.WriteAllText(GoldenSource(), Render());
            Assert.Fail("The golden file was rewritten; review the diff and commit it.");
        }
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "menu-v1.txt")).Replace("\r\n", "\n");
        Assert.Equal(golden, Render());
    }

    private static string GoldenSource([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Golden", "menu-v1.txt");
}
