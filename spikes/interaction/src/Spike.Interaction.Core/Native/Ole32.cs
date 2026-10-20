using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Spike.Interaction.Core.Native;

/// <summary>
/// OLE drop target interface. Implemented by managed classes and handed to RegisterDragDrop,
/// which creates a COM-callable wrapper. PreserveSig keeps HRESULT handling explicit.
/// </summary>
[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);
    [PreserveSig] int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);
}

[ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool fEscapePressed, uint grfKeyState);
    [PreserveSig] int GiveFeedback(uint dwEffect);
}

public static class Ole32
{
    private const string Lib = "ole32.dll";

    [DllImport(Lib)] public static extern int OleInitialize(nint pvReserved);
    [DllImport(Lib)] public static extern void OleUninitialize();
    [DllImport(Lib)] public static extern int RegisterDragDrop(nint hwnd, [MarshalAs(UnmanagedType.Interface)] IDropTarget pDropTarget);
    [DllImport(Lib)] public static extern int RevokeDragDrop(nint hwnd);
    [DllImport(Lib)] public static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);
    [DllImport(Lib)] public static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj, [MarshalAs(UnmanagedType.Interface)] IDropSource pDropSource, uint dwOKEffects, out uint pdwEffect);
}

public static class Shell32
{
    private const string Lib = "shell32.dll";

    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern uint DragQueryFileW(nint hDrop, uint iFile, [Out] char[]? lpszFile, uint cch);

    /// <summary>Starts a shell drag using the shell's own default drop source when pdsrc is null (same behaviour Explorer uses).</summary>
    [DllImport(Lib)] public static extern int SHDoDragDrop(nint hwnd, [MarshalAs(UnmanagedType.Interface)] IDataObject pdata, [MarshalAs(UnmanagedType.Interface)] IDropSource? pdsrc, uint dwEffect, out uint pdwEffect);

    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern int SHParseDisplayName(string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);
    [DllImport(Lib)] public static extern int SHCreateShellItemArrayFromIDLists(uint cidl, nint[] rgpidl, out IShellItemArray ppsiItemArray);
    [DllImport(Lib)] public static extern void ILFree(nint pidl);
}

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    [PreserveSig] int BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvOut);
    // Remaining methods are not used by the spike; the vtable order up to BindToHandler is what matters.
}

public static class ShellIds
{
    public static Guid BHID_DataObject = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");
    public static Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");
}

public static partial class Kernel32
{
    private const string Lib = "kernel32.dll";
    [LibraryImport(Lib)] public static partial nint GetModuleHandleW(nint lpModuleName);
    [LibraryImport(Lib)] public static partial nint GlobalLock(nint hMem);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GlobalUnlock(nint hMem);
    [LibraryImport(Lib)] public static partial nuint GlobalSize(nint hMem);
    [LibraryImport(Lib)] public static partial nint GetCurrentProcess();
    [LibraryImport(Lib)] public static partial uint GetCurrentThreadId();
}
