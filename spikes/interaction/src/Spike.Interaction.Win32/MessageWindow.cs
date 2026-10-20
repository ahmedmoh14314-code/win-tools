using System.Runtime.InteropServices;
using Spike.Interaction.Core.Native;

namespace Spike.Interaction.Win32;

/// <summary>A hidden message-only window that receives posted work on the UI thread.</summary>
public sealed class MessageWindow : IDisposable
{
    private const string ClassName = "SpikeInteractionMessageWindow";
    private static readonly nint HWND_MESSAGE = new(-3);
    private readonly WndProc _proc;
    private readonly WndProc _user;

    public nint Handle { get; private set; }

    public MessageWindow(WndProc handler)
    {
        _user = handler;
        _proc = (h, m, w, l) => _user(h, m, w, l);
        var hInst = Kernel32.GetModuleHandleW(0);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst,
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };
        User32.RegisterClassExW(ref wc);
        Handle = User32.CreateWindowExW(0, ClassName, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, hInst, 0);
        if (Handle == 0) throw new InvalidOperationException($"Message window creation failed: {Marshal.GetLastWin32Error()}");
    }

    public void Dispose()
    {
        if (Handle != 0) { User32.DestroyWindow(Handle); Handle = 0; }
    }
}
