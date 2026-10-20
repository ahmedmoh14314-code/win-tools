using System.Diagnostics;
using System.Runtime.InteropServices;
using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Telemetry;

namespace Spike.Interaction.DragSource;

/// <summary>
/// A small visible window that starts a shell drag (SHDoDragDrop with the shell's default drop source)
/// when it receives WM_LBUTTONDOWN. The script thread presses the button with SendInput.
/// </summary>
public sealed class SourceWindow : IDisposable
{
    private const string ClassName = "SpikeDragSourceWindow";
    private readonly WndProc _proc;
    private readonly SpikeLog _log;
    private IReadOnlyList<string> _paths = Array.Empty<string>();
    public nint Handle { get; private set; }
    public RECT Rect { get; private set; }

    public sealed record DragOutcome(int Hr, uint Effect, double DurationMs, long Timestamp);
    public event Action<DragOutcome>? DragFinished;

    public SourceWindow(SpikeLog log, RECT rect)
    {
        _log = log;
        _proc = WndProcImpl;
        var hInst = Kernel32.GetModuleHandleW(0);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst,
            hCursor = User32.LoadCursorW(0, User32.IDC_ARROW),
            hbrBackground = Gdi32.CreateSolidBrush(Gdi32.Rgb(200, 220, 200)),
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };
        User32.RegisterClassExW(ref wc);
        Handle = User32.CreateWindowExW(WS.EX_TOOLWINDOW, ClassName, "Spike drag source", WS.POPUP | WS.VISIBLE,
            rect.Left, rect.Top, rect.Width, rect.Height, 0, 0, hInst, 0);
        if (Handle == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        Rect = rect;
    }

    public void SetPaths(IReadOnlyList<string> paths) => _paths = paths;

    private nint WndProcImpl(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM.LBUTTONDOWN:
                StartDrag();
                return 0;
            case WM.PAINT:
            {
                User32.BeginPaint(hwnd, out var ps);
                User32.GetClientRect(hwnd, out var rc);
                Gdi32.SetBkMode(ps.hdc, Gdi32.TRANSPARENT);
                User32.DrawTextW(ps.hdc, $"drag source ({_paths.Count} files)", -1, ref rc, User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE);
                User32.EndPaint(hwnd, ref ps);
                return 0;
            }
            case WM.DESTROY:
                User32.PostQuitMessage(0);
                return 0;
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void StartDrag()
    {
        var data = ShellDataObjects.ForFiles(_paths);
        var t0 = Stopwatch.GetTimestamp();
        _log.Event("source.dragStart", new { count = _paths.Count });
        // Offer COPY|MOVE|LINK like Explorer does, so the target's returned effect is what gets exercised.
        var hr = Shell32.SHDoDragDrop(Handle, data, null, DROPEFFECT.COPY | DROPEFFECT.MOVE | DROPEFFECT.LINK, out var effect);
        var t1 = Stopwatch.GetTimestamp();
        var outcome = new DragOutcome(hr, effect, SpikeLog.MsBetween(t0, t1), t1);
        _log.Event("source.dragEnd", new { hr = $"0x{hr:X8}", hrName = HrName(hr), effect, effectName = EffectName(effect), durationMs = Math.Round(outcome.DurationMs, 1) }, t1);
        DragFinished?.Invoke(outcome);
    }

    public static string HrName(int hr) => hr switch
    {
        HRESULTS.DRAGDROP_S_DROP => "DRAGDROP_S_DROP",
        HRESULTS.DRAGDROP_S_CANCEL => "DRAGDROP_S_CANCEL",
        0 => "S_OK",
        _ => "other",
    };

    public static string EffectName(uint e) => e switch
    {
        DROPEFFECT.NONE => "NONE", DROPEFFECT.COPY => "COPY", DROPEFFECT.MOVE => "MOVE", DROPEFFECT.LINK => "LINK", _ => $"0x{e:X}",
    };

    public void Dispose()
    {
        if (Handle != 0) { User32.DestroyWindow(Handle); Handle = 0; }
    }
}
