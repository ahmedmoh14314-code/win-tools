using System.Diagnostics;
using System.Runtime.InteropServices;
using Spike.Interaction.Core.DragDrop;
using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Overlay;
using Spike.Interaction.Core.Telemetry;

namespace Spike.Interaction.Win32;

/// <summary>
/// Raw Win32 overlay: a topmost, non-activating, tool-window popup clipped to a circle via SetWindowRgn,
/// painted with GDI, registered as an OLE drop target. Only window/paint/drop concerns live here.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "SpikeInteractionOverlay";
    private const int MA_NOACTIVATE = 3;

    private readonly WndProc _wndProc; // kept alive for the lifetime of the window class
    private readonly SpikeLog _log;
    private readonly int _radius96;
    private readonly int _sectors;
    private readonly FileDropTarget _dropTarget = new();
    private nint _hwnd;
    private int _sizePx;
    private uint _dpi = 96;
    private int _hotSector = -1;
    private bool _dropRegistered;

    // Measurement state for the current show cycle.
    private long _activateTs, _showTs;
    private bool _awaitingPaint, _awaitingEnter;
    private int _movesSinceShow;
    private int _cycle;

    public event Action<DropResult, int>? Dropped;   // paths, sector
    public event Action? Hidden;
    public event Action? DragLeft;                   // OLE DragLeave received
    public event Action? DragEntered;                // OLE DragEnter received

    public nint Handle => _hwnd;
    public bool IsVisible => _hwnd != 0 && User32.IsWindowVisible(_hwnd);

    public OverlayWindow(SpikeLog log, int radius96, int sectors)
    {
        _log = log;
        _radius96 = radius96;
        _sectors = sectors;
        _wndProc = WndProcImpl;
        Create();
        _dropTarget.Entered += OnDragEnter;
        _dropTarget.Moved += OnDragOver;
        _dropTarget.Left += OnDragLeave;
        _dropTarget.Dropped += OnDrop;
    }

    private void Create()
    {
        var hInst = Kernel32.GetModuleHandleW(0);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInst,
            hCursor = User32.LoadCursorW(0, User32.IDC_ARROW),
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };
        if (User32.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        _hwnd = User32.CreateWindowExW(
            WS.EX_TOPMOST | WS.EX_NOACTIVATE | WS.EX_TOOLWINDOW,
            ClassName, "Spike overlay", WS.POPUP,
            0, 0, 10, 10, 0, 0, hInst, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        var hr = Ole32.RegisterDragDrop(_hwnd, _dropTarget);
        if (hr != 0) throw new InvalidOperationException($"RegisterDragDrop failed: 0x{hr:X8} (OleInitialize must have been called on this thread)");
        _dropRegistered = true;
        _log.Event("overlay.created", new { hwnd = _hwnd.ToString("X") });
    }

    /// <summary>Show the overlay centred on the cursor. activateTs is the hook timestamp of the trigger.</summary>
    public void ShowAtCursor(long activateTs)
    {
        _cycle++;
        _activateTs = activateTs;
        User32.GetCursorPos(out var cursor);
        var (work, bounds, dpi, device) = MonitorInfo.ForPoint(cursor);
        _dpi = dpi;
        _sizePx = OverlayPlacement.Scale(_radius96 * 2, dpi);
        var rect = OverlayPlacement.Place(cursor, work, _sizePx, _sizePx);

        var fgBefore = User32.GetForegroundWindow();
        _hotSector = -1;
        _movesSinceShow = 0;
        _awaitingPaint = true;
        _awaitingEnter = true;

        var rgn = Gdi32.CreateEllipticRgn(0, 0, _sizePx, _sizePx);
        User32.SetWindowRgn(_hwnd, rgn, false); // the system owns rgn after this call
        User32.SetWindowPos(_hwnd, SWP.HWND_TOPMOST, rect.Left, rect.Top, _sizePx, _sizePx, SWP.NOACTIVATE | SWP.SHOWWINDOW);
        _showTs = Stopwatch.GetTimestamp();
        User32.InvalidateRect(_hwnd, 0, true);
        User32.UpdateWindow(_hwnd); // paint synchronously so "visible" is measurable right here

        var fgAfter = User32.GetForegroundWindow();
        _log.Event("overlay.show", new
        {
            cycle = _cycle,
            cursor = cursor.ToString(),
            monitor = device,
            dpi,
            work = work.ToString(),
            rect = rect.ToString(),
            clamped = rect.Left != cursor.X - _sizePx / 2 || rect.Top != cursor.Y - _sizePx / 2,
            hookToShowMs = Math.Round(SpikeLog.MsBetween(activateTs, _showTs), 3),
            fgBefore = User32.ClassNameOf(fgBefore),
            fgAfter = User32.ClassNameOf(fgAfter),
            focusStolen = fgBefore != fgAfter,
        }, _showTs);
    }

    public void Hide(string reason)
    {
        if (!IsVisible) return;
        User32.ShowWindow(_hwnd, SW.HIDE);
        _log.Event("overlay.hide", new { cycle = _cycle, reason, enterReceived = !_awaitingEnter });
        Hidden?.Invoke();
    }

    private void OnDragEnter(DragPosition p)
    {
        if (_awaitingEnter)
        {
            _awaitingEnter = false;
            _log.Event("drag.enter", new
            {
                cycle = _cycle,
                hookToEnterMs = Math.Round(SpikeLog.MsBetween(_activateTs, p.Timestamp), 3),
                showToEnterMs = Math.Round(SpikeLog.MsBetween(_showTs, p.Timestamp), 3),
                movesSinceShow = _movesSinceShow,
                keyState = p.KeyState,
            }, p.Timestamp);
        }
        DragEntered?.Invoke();
        UpdateHotSector(p.ScreenX, p.ScreenY);
    }

    private void OnDragOver(DragPosition p)
    {
        _movesSinceShow++;
        UpdateHotSector(p.ScreenX, p.ScreenY);
    }

    private void OnDragLeave()
    {
        _log.Event("drag.leave", new { cycle = _cycle });
        _hotSector = -1;
        User32.InvalidateRect(_hwnd, 0, false);
        DragLeft?.Invoke();
    }

    private void OnDrop(DropResult r)
    {
        var sector = HitSector(r.ScreenX, r.ScreenY);
        _log.Event("drop", new
        {
            cycle = _cycle,
            sector,
            count = r.Paths.Count,
            paths = r.Paths,
            keyState = r.KeyState,
            hookToDropMs = Math.Round(SpikeLog.MsBetween(_activateTs, r.Timestamp), 3),
        }, r.Timestamp);
        Hide("drop");
        Dropped?.Invoke(r, sector);
    }

    private int HitSector(int screenX, int screenY)
    {
        User32.GetWindowRect(_hwnd, out var wr);
        var c = _sizePx / 2;
        var inner = OverlayPlacement.Scale(28, _dpi);
        return SectorHitTest.Hit(screenX - wr.Left, screenY - wr.Top, c, c, inner, c, _sectors);
    }

    private void UpdateHotSector(int screenX, int screenY)
    {
        var s = HitSector(screenX, screenY);
        if (s != _hotSector)
        {
            _hotSector = s;
            User32.InvalidateRect(_hwnd, 0, false);
        }
    }

    /// <summary>Called from the hook thread via PostMessage; tracks raw mouse moves while shown (diagnostic only).</summary>
    public void NoteMouseMove() { if (_awaitingEnter) _movesSinceShow++; }

    private nint WndProcImpl(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM.MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM.ERASEBKGND:
                return 1;
            case WM.PAINT:
                Paint(hwnd);
                if (_awaitingPaint)
                {
                    _awaitingPaint = false;
                    var now = Stopwatch.GetTimestamp();
                    _log.Event("overlay.painted", new
                    {
                        cycle = _cycle,
                        hookToPaintMs = Math.Round(SpikeLog.MsBetween(_activateTs, now), 3),
                        showToPaintMs = Math.Round(SpikeLog.MsBetween(_showTs, now), 3),
                    }, now);
                }
                return 0;
            case WM.DESTROY:
                if (_dropRegistered) { Ole32.RevokeDragDrop(hwnd); _dropRegistered = false; }
                return 0;
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Paint(nint hwnd)
    {
        var hdc = User32.BeginPaint(hwnd, out var ps);
        try
        {
            User32.GetClientRect(hwnd, out var rc);
            var size = rc.Width;
            var c = size / 2;
            var inner = OverlayPlacement.Scale(28, _dpi);

            // Double buffer to avoid flicker when the hot sector changes.
            var mem = Gdi32.CreateCompatibleDC(hdc);
            var bmp = Gdi32.CreateCompatibleBitmap(hdc, size, size);
            var oldBmp = Gdi32.SelectObject(mem, bmp);

            var bg = Gdi32.CreateSolidBrush(Gdi32.Rgb(32, 34, 40));
            User32.FillRect(mem, ref rc, bg);
            Gdi32.DeleteObject(bg);

            var pen = Gdi32.CreatePen(Gdi32.PS_SOLID, Math.Max(1, OverlayPlacement.Scale(2, _dpi)), Gdi32.Rgb(90, 94, 110));
            var oldPen = Gdi32.SelectObject(mem, pen);
            var normal = Gdi32.CreateSolidBrush(Gdi32.Rgb(52, 56, 66));
            var hot = Gdi32.CreateSolidBrush(Gdi32.Rgb(70, 140, 220));

            for (var i = 0; i < _sectors; i++)
            {
                // Pie draws counter-clockwise from start to end; compute endpoints on the circle.
                var a0 = 2 * Math.PI * i / _sectors - Math.PI / 2;        // sector start (clockwise from 12 o'clock)
                var a1 = 2 * Math.PI * (i + 1) / _sectors - Math.PI / 2;
                var (x0, y0) = (c + (int)(c * Math.Cos(a0)), c + (int)(c * Math.Sin(a0)));
                var (x1, y1) = (c + (int)(c * Math.Cos(a1)), c + (int)(c * Math.Sin(a1)));
                Gdi32.SelectObject(mem, i == _hotSector ? hot : normal);
                Gdi32.Pie(mem, 0, 0, size, size, x1, y1, x0, y0);
            }

            // Dead zone in the centre.
            var centre = Gdi32.CreateSolidBrush(Gdi32.Rgb(24, 25, 30));
            Gdi32.SelectObject(mem, centre);
            Gdi32.Ellipse(mem, c - inner, c - inner, c + inner, c + inner);
            Gdi32.DeleteObject(centre);

            // Labels.
            Gdi32.SetBkMode(mem, Gdi32.TRANSPARENT);
            Gdi32.SetTextColor(mem, Gdi32.Rgb(235, 235, 240));
            var oldFont = Gdi32.SelectObject(mem, Gdi32.GetStockObject(17 /* DEFAULT_GUI_FONT */));
            for (var i = 0; i < _sectors; i++)
            {
                var am = 2 * Math.PI * (i + 0.5) / _sectors - Math.PI / 2;
                var r = (inner + c) / 2.0;
                var (lx, ly) = (c + (int)(r * Math.Cos(am)), c + (int)(r * Math.Sin(am)));
                var lr = new RECT(lx - 30, ly - 10, lx + 30, ly + 10);
                User32.DrawTextW(mem, ((char)('A' + i)).ToString(), -1, ref lr, User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE);
            }
            Gdi32.SelectObject(mem, oldFont);

            Gdi32.BitBlt(hdc, 0, 0, size, size, mem, 0, 0, Gdi32.SRCCOPY);

            Gdi32.SelectObject(mem, oldPen);
            Gdi32.SelectObject(mem, oldBmp);
            Gdi32.DeleteObject(pen); Gdi32.DeleteObject(normal); Gdi32.DeleteObject(hot);
            Gdi32.DeleteObject(bmp);
            Gdi32.DeleteDC(mem);
        }
        finally
        {
            User32.EndPaint(hwnd, ref ps);
        }
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            User32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
