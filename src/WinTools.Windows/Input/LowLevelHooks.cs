using System.Diagnostics;
using System.Runtime.InteropServices;
using WinTools.Core.Interaction;
using WinTools.Windows.Native;

namespace WinTools.Windows.Input;

/// <summary>
/// WH_KEYBOARD_LL and WH_MOUSE_LL on a dedicated thread that does nothing but pump messages (ADR-0003), so a busy UI
/// thread can never delay the callbacks past the system's hook timeout. Each callback reads the event struct in place,
/// runs the allocation-free <see cref="TriggerDetector"/>, posts a compact message to the target window when there is
/// something to report, and returns. Nothing is swallowed. Both hooks are installed or neither.
/// </summary>
public sealed class LowLevelHooks : IDisposable
{
    /// <summary>Posted message: wParam = <see cref="SignalPacking"/>, lParam = Stopwatch timestamp of the callback.</summary>
    public const uint SignalMessage = Msg.APP + 1;

    private readonly nint _target;
    private readonly TriggerDetector _detector;
    private readonly HookProc _keyboardProc, _mouseProc; // referenced for the hooks' lifetime
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private nint _keyboardHook, _mouseHook;
    private uint _threadId;
    private int _installError;

    /// <param name="triggerVirtualKey">The trigger key from the settings (Space by default).</param>
    public LowLevelHooks(nint targetWindow, int triggerVirtualKey = TriggerDetector.VkSpace)
    {
        _target = targetWindow;
        _detector = new TriggerDetector(triggerVirtualKey);
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _thread = new Thread(Run) { Name = "low-level hooks", IsBackground = true, Priority = ThreadPriority.AboveNormal };
    }

    public static SignalPacking.Signal Unpack(nuint wParam) => SignalPacking.Unpack(wParam);

    /// <summary>Installs both hooks; throws if either cannot be installed.</summary>
    public void Start()
    {
        _thread.Start();
        _ready.Wait();
        if (_installError != 0) throw new InvalidOperationException($"SetWindowsHookEx failed (Win32 error {_installError}).");
    }

    private void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        var module = Kernel32.GetModuleHandleW(0);
        _keyboardHook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(_keyboardProc), module, 0);
        _mouseHook = _keyboardHook == 0 ? 0 : User32.SetWindowsHookExW(User32.WH_MOUSE_LL, Marshal.GetFunctionPointerForDelegate(_mouseProc), module, 0);
        if (_keyboardHook == 0 || _mouseHook == 0)
        {
            _installError = Marshal.GetLastPInvokeError();
            if (_installError == 0) _installError = -1;
            if (_keyboardHook != 0) User32.UnhookWindowsHookEx(_keyboardHook);
            _ready.Set();
            return;
        }
        _ready.Set();
        while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(msg);
            User32.DispatchMessageW(msg);
        }
        User32.UnhookWindowsHookEx(_keyboardHook);
        User32.UnhookWindowsHookEx(_mouseHook);
    }

    private unsafe nint KeyboardProc(int code, nuint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                var k = (KBDLLHOOKSTRUCT*)lParam;
                var msg = (uint)wParam;
                var isDown = msg is Msg.KEYDOWN or Msg.SYSKEYDOWN;
                if (isDown && _detector.IsTriggerKey((int)k->vkCode))
                {
                    var leftVk = User32.GetSystemMetrics(User32.SM_SWAPBUTTON) != 0 ? 0x02 : 0x01; // VK_RBUTTON when buttons are swapped
                    _detector.Resync(User32.GetAsyncKeyState(leftVk) < 0, User32.GetAsyncKeyState(0x11) < 0, User32.GetAsyncKeyState(0x10) < 0, User32.GetAsyncKeyState(0x12) < 0);
                }
                var signal = _detector.OnKey((int)k->vkCode, isDown);
                if (signal != InputSignal.None) Post(signal);
            }
            catch (Exception) { /* nothing may escape a hook callback */ }
        }
        return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private unsafe nint MouseProc(int code, nuint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                var m = (MSLLHOOKSTRUCT*)lParam;
                switch ((uint)wParam)
                {
                    case Msg.MOUSEMOVE: _detector.OnMouseMove(m->pt.X, m->pt.Y); break;
                    case Msg.LBUTTONDOWN: _detector.OnMouseButton(MouseButton.Left, true, m->pt.X, m->pt.Y); break;
                    case Msg.LBUTTONUP:
                        if (_detector.OnMouseButton(MouseButton.Left, false, m->pt.X, m->pt.Y) is var s && s != InputSignal.None) Post(s);
                        break;
                }
            }
            catch (Exception) { }
        }
        return User32.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void Post(InputSignal signal)
    {
        var (x, y) = _detector.Position;
        var w = (nuint)SignalPacking.Pack(signal, _detector.LeftButtonDown, _detector.IsDragging, x, y);
        User32.PostMessageW(_target, SignalMessage, w, (nint)Stopwatch.GetTimestamp());
    }

    public void Dispose()
    {
        if (_threadId != 0 && _thread.IsAlive)
        {
            User32.PostThreadMessageW(_threadId, Msg.QUIT, 0, 0);
            _thread.Join(2000);
        }
        _ready.Dispose();
    }
}
