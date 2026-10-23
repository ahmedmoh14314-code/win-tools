using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WinTools.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
public struct POINTL { public int X, Y; }

/// <summary>OLE drop target (oleidl.h). Implemented in managed code; RegisterDragDrop wraps it in a COM-callable wrapper
/// (public, so the runtime will expose it to COM).</summary>
[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject dataObject, uint keyState, POINTL pt, ref uint effect);
    [PreserveSig] int DragOver(uint keyState, POINTL pt, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject dataObject, uint keyState, POINTL pt, ref uint effect);
}

internal static class Ole32
{
    public const uint DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1;
    public const short CF_HDROP = 15;

    [DllImport("ole32.dll")] public static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] public static extern void OleUninitialize();
    [DllImport("ole32.dll")] public static extern int RegisterDragDrop(nint hwnd, [MarshalAs(UnmanagedType.Interface)] IDropTarget target);
    [DllImport("ole32.dll")] public static extern int RevokeDragDrop(nint hwnd);
    [DllImport("ole32.dll")] public static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
