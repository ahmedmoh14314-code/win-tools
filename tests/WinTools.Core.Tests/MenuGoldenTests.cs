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
    ];

    private static readonly string?[] CustomResizes = [null, "resize.width-800", "resize.box-1920x1080-stretch", "resize.height-600-upscale"];

    internal static string Render()
    {
        var text = new StringBuilder();
        foreach (var custom in CustomResizes)
        {
            var resolver = new CapabilityResolver(RadialMenu.Catalog(custom));
            text.Append("## custom resize: ").AppendLine(custom ?? "none");
            foreach (var selection in Selections)
            {
                text.Append("# ").AppendLine(selection.Length == 0 ? "(empty)" : string.Join(" + ", selection));
                foreach (var group in RadialMenu.Build(resolver.Resolve(selection)))
                {
                    text.Append("  [").Append(group.Category).Append("] ").AppendLine(group.Label);
                    foreach (var op in group.Commands)
                        text.Append("    ").Append(op.Id).Append(" | ").Append(op.Label).Append(" | writes ").Append(op.WritesFile)
                            .Append(" | suffix ").Append(op.NameSuffix ?? "-").Append(" | out ")
                            .AppendLine(string.Join(",", selection.Select(p => op.OutputFormat(Files.FormatHint.FromPath(p)))));
                }
            }
        }
        return text.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void The_menu_for_every_representative_selection_matches_the_v1_golden_file()
    {
        // A deliberate change: WINTOOLS_WRITE_GOLDEN=1 rewrites the file next to this source, to be reviewed and committed.
        if (Environment.GetEnvironmentVariable("WINTOOLS_WRITE_GOLDEN") == "1") File.WriteAllText(GoldenSource(), Render());
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "menu-v1.txt")).Replace("\r\n", "\n");
        Assert.Equal(golden, Render());
    }

    private static string GoldenSource([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Golden", "menu-v1.txt");
}
