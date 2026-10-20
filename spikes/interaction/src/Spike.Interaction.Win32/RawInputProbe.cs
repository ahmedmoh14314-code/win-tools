using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Spike.Interaction.Core.Telemetry;

namespace Spike.Interaction.Win32;

/// <summary>
/// Spike 002B, experiment 2: evidence-only Raw Input listener (not a subsystem). Registers keyboard and mouse
/// with RIDEV_INPUTSINK on the host's message window, so WM_INPUT arrives while another process is in the
/// foreground (e.g. Explorer running DoDragDrop). Logs trigger/Esc key events with a QPC timestamp so they can
/// be compared with the low-level hook's entry timestamp for the same key press.
/// </summary>
public sealed partial class RawInputProbe
{
    public const uint WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100, RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    private const int HeaderSize = 24; // RAWINPUTHEADER on x64: dwType, dwSize, hDevice, wParam

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort UsagePage; public ushort Usage; public uint Flags; public nint Target; }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    private static partial uint GetRawInputData(nint hRawInput, uint command, nint data, ref uint size, uint headerSize);

    private readonly SpikeLog _log;
    private readonly int _triggerVk;
    private readonly byte[] _buf = new byte[256];
    private long _kb, _kbInjected, _mouseButtons, _mouseMoves, _sink, _foreground;

    public bool Registered { get; }

    public RawInputProbe(SpikeLog log, nint hwnd, int triggerVk)
    {
        _log = log;
        _triggerVk = triggerVk;
        var devices = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, Target = hwnd }, // keyboard
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Target = hwnd }, // mouse
        };
        Registered = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        log.Event("raw.registered", new { ok = Registered, error = Registered ? 0 : Marshal.GetLastWin32Error(), hwnd = hwnd.ToString("X") });
    }

    public unsafe void Handle(nuint wParam, nint lParam)
    {
        var ts = Stopwatch.GetTimestamp();
        if (wParam == 1) _sink++; else _foreground++;
        var size = (uint)_buf.Length;
        uint n;
        fixed (byte* p = _buf) n = GetRawInputData(lParam, RID_INPUT, (nint)p, ref size, HeaderSize);
        if (n == uint.MaxValue || n < HeaderSize) return;
        var span = _buf.AsSpan(0, (int)n);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var hDevice = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
        if (type == RIM_TYPEKEYBOARD && n >= HeaderSize + 16)
        {
            _kb++;
            var injected = hDevice == 0; // SendInput events carry no device handle
            if (injected) _kbInjected++;
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[(HeaderSize + 2)..]);
            var vk = BinaryPrimitives.ReadUInt16LittleEndian(span[(HeaderSize + 6)..]);
            if (vk == _triggerVk || vk == 0x1B)
                _log.Event("raw.key", new { vk, down = (flags & 1) == 0, injected, sink = wParam == 1 }, ts);
        }
        else if (type == RIM_TYPEMOUSE && n >= HeaderSize + 8)
        {
            var buttonFlags = BinaryPrimitives.ReadUInt16LittleEndian(span[(HeaderSize + 4)..]);
            if (buttonFlags != 0) _mouseButtons++; else _mouseMoves++;
        }
    }

    public object Summary() => new { registered = Registered, kb = _kb, kbInjected = _kbInjected, mouseButtons = _mouseButtons, mouseMoves = _mouseMoves, sinkMsgs = _sink, foregroundMsgs = _foreground };
}
