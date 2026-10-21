using System.Runtime.InteropServices;
using WinTools.Core.Interaction;
using WinTools.Windows.Native;

namespace WinTools.Windows.Overlay;

/// <summary>Callbacks from the overlay to its owner, on the overlay thread.</summary>
public interface IOverlayEvents
{
    /// <summary>A drag entered the overlay; the owner answers with <see cref="RadialOverlay.SetCommands"/>.</summary>
    void DragEntered(IReadOnlyList<string> paths);
    void DragLeft();
    /// <summary>A drop landed on <paramref name="sector"/> (-1: dead zone or outside the ring). Return true when a command will run.</summary>
    bool Dropped(IReadOnlyList<string> paths, int sector, int screenX, int screenY);
}

/// <summary>
/// The native radial overlay (ADR-0001): a pre-created topmost, non-activating tool-window popup clipped to a circle
/// with SetWindowRgn, painted with GDI and registered as an OLE drop target. It knows labels and geometry only; which
/// commands exist is decided by the owner. Create, use and dispose on one STA thread that called OleInitialize.
/// </summary>
public sealed class RadialOverlay : IDisposable
{
    private const string ClassName = "WinTools.RadialOverlay";

    private readonly IOverlayEvents _events;
    private readonly WndProc _wndProc; // referenced for the window's lifetime
    private readonly FileDropTarget _dropTarget;
    private nint _hwnd;
    private bool _dropRegistered;
    private IReadOnlyList<string> _labels = [];
    private int _size, _hot = -1;
    private uint _dpi = 96;
    private int _left, _top;

    public RadialOverlay(IOverlayEvents events)
    {
        _events = events;
        _wndProc = WndProcImpl;
        _dropTarget = new FileDropTarget(this);
        var instance = Kernel32.GetModuleHandleW(0);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            hCursor = User32.LoadCursorW(0, 32512 /* IDC_ARROW */),
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };
        if (User32.RegisterClassExW(wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
        _hwnd = User32.CreateWindowExW(User32.WS_EX_TOPMOST | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW, ClassName, "WinTools", User32.WS_POPUP, 0, 0, 1, 1, 0, 0, instance, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        var hr = Ole32.RegisterDragDrop(_hwnd, _dropTarget);
        if (hr != 0) throw new InvalidOperationException($"RegisterDragDrop failed (0x{hr:X8}); OleInitialize must run on this thread first.");
        _dropRegistered = true;
    }

    public bool IsVisible => _hwnd != 0 && User32.IsWindowVisible(_hwnd);

    /// <summary>
    /// Shows the ring centred on (x, y) — the pointer position when the trigger was pressed — clamped to that monitor's
    /// work area, without activating it. Returns the ring centre actually used.
    /// </summary>
    public (int X, int Y) ShowAt(int x, int y)
    {
        var cursor = new POINT { X = x, Y = y };
        var monitor = User32.MonitorFromPoint(cursor, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        User32.GetMonitorInfoW(monitor, ref info);
        _dpi = Shcore.GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 ? dpiX : 96;
        _size = RingGeometry.Scale(RingGeometry.OuterRadius96 * 2, _dpi);
        (_left, _top) = RingGeometry.Place(cursor.X, cursor.Y, _size, info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        _labels = [];
        _hot = -1;
        User32.SetWindowRgn(_hwnd, Gdi32.CreateEllipticRgn(0, 0, _size + 1, _size + 1), false); // the system owns the region
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, _left, _top, _size, _size, User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        User32.InvalidateRect(_hwnd, 0, false);
        User32.UpdateWindow(_hwnd); // paint now, so the target is complete before the first DragEnter
        return (_left + _size / 2, _top + _size / 2);
    }

    public void Hide()
    {
        if (IsVisible) User32.ShowWindow(_hwnd, User32.SW_HIDE);
        _labels = [];
        _hot = -1;
    }

    public void SetCommands(IReadOnlyList<string> labels)
    {
        _labels = labels;
        Repaint();
    }

    internal bool IsCommandAt(int screenX, int screenY) => SectorAt(screenX, screenY) >= 0;

    internal int SectorAt(int screenX, int screenY)
    {
        var c = _size / 2;
        return RingGeometry.HitSector(screenX - _left - c, screenY - _top - c, RingGeometry.Scale(RingGeometry.InnerRadius96, _dpi), c, _labels.Count);
    }

    internal void OnDragEnter(IReadOnlyList<string> paths, int x, int y)
    {
        _events.DragEntered(paths);
        SetHot(SectorAt(x, y));
    }

    internal void OnDragOver(int x, int y) => SetHot(SectorAt(x, y));

    internal void OnDragLeave()
    {
        SetHot(-1);
        _events.DragLeft();
    }

    internal bool OnDrop(IReadOnlyList<string> paths, int x, int y, bool copyAllowed) =>
        _events.Dropped(paths, copyAllowed ? SectorAt(x, y) : -1, x, y);

    private void SetHot(int sector)
    {
        if (sector == _hot) return;
        _hot = sector;
        Repaint();
    }

    private void Repaint()
    {
        if (!IsVisible) return;
        User32.InvalidateRect(_hwnd, 0, false);
        User32.UpdateWindow(_hwnd);
    }

    private nint WndProcImpl(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Msg.MOUSEACTIVATE: return User32.MA_NOACTIVATE;
            case Msg.ERASEBKGND: return 1;
            case Msg.PAINT: Paint(hwnd); return 0;
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Paint(nint hwnd)
    {
        var hdc = User32.BeginPaint(hwnd, out var ps);
        try
        {
            var size = Math.Max(_size, 1);
            var c = size / 2;
            var inner = RingGeometry.Scale(RingGeometry.InnerRadius96, _dpi);
            var mem = Gdi32.CreateCompatibleDC(hdc);
            var bmp = Gdi32.CreateCompatibleBitmap(hdc, size, size);
            var oldBmp = Gdi32.SelectObject(mem, bmp);
            var background = Gdi32.CreateSolidBrush(Gdi32.Rgb(32, 34, 40));
            var normal = Gdi32.CreateSolidBrush(Gdi32.Rgb(52, 56, 66));
            var hot = Gdi32.CreateSolidBrush(Gdi32.Rgb(70, 140, 220));
            var centre = Gdi32.CreateSolidBrush(Gdi32.Rgb(24, 25, 30));
            var pen = Gdi32.CreatePen(Gdi32.PS_SOLID, Math.Max(1, RingGeometry.Scale(2, _dpi)), Gdi32.Rgb(90, 94, 110));
            var font = Gdi32.CreateFontW(-RingGeometry.Scale(14, _dpi), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, "Segoe UI");
            var oldPen = Gdi32.SelectObject(mem, pen);
            var oldFont = Gdi32.SelectObject(mem, font);
            var oldBrush = Gdi32.SelectObject(mem, normal);
            try
            {
                User32.FillRect(mem, new RECT { Right = size, Bottom = size }, background);
                var n = _labels.Count;
                for (var i = 0; i < n; i++)
                {
                    // Sector i spans clockwise from angle a0 to a1 (0 = 12 o'clock); Pie draws counter-clockwise from (x1,y1) to (x2,y2).
                    var a0 = 2 * Math.PI * i / n - Math.PI / 2;
                    var a1 = 2 * Math.PI * (i + 1) / n - Math.PI / 2;
                    Gdi32.SelectObject(mem, i == _hot ? hot : normal);
                    Gdi32.Pie(mem, 0, 0, size, size, c + (int)(c * Math.Cos(a1)), c + (int)(c * Math.Sin(a1)), c + (int)(c * Math.Cos(a0)), c + (int)(c * Math.Sin(a0)));
                }
                Gdi32.SelectObject(mem, centre);
                Gdi32.Ellipse(mem, c - inner, c - inner, c + inner, c + inner);

                _ = Gdi32.SetBkMode(mem, Gdi32.TRANSPARENT);
                _ = Gdi32.SetTextColor(mem, n == 0 ? Gdi32.Rgb(150, 152, 160) : Gdi32.Rgb(240, 240, 245));
                var half = RingGeometry.Scale(48, _dpi);
                var lineHalf = RingGeometry.Scale(12, _dpi);
                if (n == 0)
                {
                    var r = new RECT { Left = c - half, Top = c - inner - 3 * lineHalf, Right = c + half, Bottom = c - inner - lineHalf };
                    User32.DrawTextW(mem, "No actions", -1, ref r, User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE | User32.DT_NOPREFIX);
                }
                for (var i = 0; i < n; i++)
                {
                    var am = n == 1 ? -Math.PI / 2 : 2 * Math.PI * (i + 0.5) / n - Math.PI / 2;
                    var radius = (inner + c) / 2.0;
                    var (lx, ly) = (c + (int)(radius * Math.Cos(am)), c + (int)(radius * Math.Sin(am)));
                    var r = new RECT { Left = lx - half, Top = ly - lineHalf, Right = lx + half, Bottom = ly + lineHalf };
                    User32.DrawTextW(mem, _labels[i], -1, ref r, User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE | User32.DT_NOPREFIX);
                }
                Gdi32.BitBlt(hdc, 0, 0, size, size, mem, 0, 0, Gdi32.SRCCOPY);
            }
            finally
            {
                Gdi32.SelectObject(mem, oldFont);
                Gdi32.SelectObject(mem, oldPen);
                Gdi32.SelectObject(mem, oldBrush);
                Gdi32.SelectObject(mem, oldBmp);
                foreach (var o in new[] { font, pen, background, normal, hot, centre, bmp }) Gdi32.DeleteObject(o);
                Gdi32.DeleteDC(mem);
            }
        }
        finally
        {
            User32.EndPaint(hwnd, ps);
        }
    }

    public void Dispose()
    {
        if (_hwnd == 0) return;
        if (_dropRegistered) { _ = Ole32.RevokeDragDrop(_hwnd); _dropRegistered = false; }
        User32.DestroyWindow(_hwnd);
        _hwnd = 0;
    }
}
