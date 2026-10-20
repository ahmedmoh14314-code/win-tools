using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Spike.Interaction.Core.Native;

namespace Spike.Interaction.DragSource;

/// <summary>
/// Builds the same kind of data object Explorer uses for file-system items (CF_HDROP + CFSTR_SHELLIDLIST ...)
/// by asking the shell for it, so the harness drags are as close to Explorer's as possible without Explorer.
/// </summary>
public static class ShellDataObjects
{
    public static IDataObject ForFiles(IReadOnlyList<string> paths)
    {
        var pidls = new nint[paths.Count];
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var hr = Shell32.SHParseDisplayName(paths[i], 0, out pidls[i], 0, out _);
                if (hr != 0) throw new InvalidOperationException($"SHParseDisplayName('{paths[i]}') failed: 0x{hr:X8}");
            }
            var hr2 = Shell32.SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array);
            if (hr2 != 0) throw new InvalidOperationException($"SHCreateShellItemArrayFromIDLists failed: 0x{hr2:X8}");
            var bhid = ShellIds.BHID_DataObject;
            var iid = ShellIds.IID_IDataObject;
            var hr3 = array.BindToHandler(0, ref bhid, ref iid, out var obj);
            if (hr3 != 0) throw new InvalidOperationException($"BindToHandler(BHID_DataObject) failed: 0x{hr3:X8}");
            return (IDataObject)obj;
        }
        finally
        {
            foreach (var p in pidls) if (p != 0) Shell32.ILFree(p);
        }
    }
}
