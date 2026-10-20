using System.Runtime.InteropServices;
using Spike.Interaction.Core.DragDrop;
using Xunit;

namespace Spike.Interaction.Core.Tests;

public class HDropReaderTests
{
    [Fact]
    public void Round_trips_unicode_and_space_paths_through_a_DROPFILES_block()
    {
        string[] paths =
        [
            @"C:\temp\plain.txt",
            @"C:\temp\with spaces and (parens).png",
            @"C:\temp\ملف عربي 日本語 ✓ émoji 🚀.jpg",
            @"\\?\C:\very\long\" + new string('x', 300) + ".txt",
        ];
        var h = HDropReader.BuildDropFiles(paths);
        try
        {
            var read = HDropReader.ReadPathsFromHDrop(h);
            Assert.Equal(paths, read);
        }
        finally
        {
            Marshal.FreeHGlobal(h);
        }
    }

    [Fact]
    public void Empty_list_yields_no_paths()
    {
        var h = HDropReader.BuildDropFiles([]);
        try { Assert.Empty(HDropReader.ReadPathsFromHDrop(h)); }
        finally { Marshal.FreeHGlobal(h); }
    }
}
