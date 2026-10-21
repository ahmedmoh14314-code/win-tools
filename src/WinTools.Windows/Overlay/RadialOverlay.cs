using System.Runtime.InteropServices;
using WinTools.Core.Interaction;
using WinTools.Windows.Native;

namespace WinTools.Windows.Overlay;

/// <summary>
/// Callbacks from the overlay to its owner, on the overlay thread. Positions are offsets in physical pixels from the
/// ring centre (down is positive). The owner decides what is under the pointer and answers with
/// <see cref="RadialOverlay.SetView"/>; the return value says whether a drop there would run a command.
/// </summary>
public interface IOverlayEvents
{
    bool DragEntered(IReadOnlyList<string> paths, int dx, int dy);
    bool DragMoved(int dx, int dy);
    void DragLeft();
    /// <summary>A drop landed on the overlay. <paramref name="copyAllowed"/> is false when the source does not allow COPY. Return true when a command will run.</summary>
    bool Dropped(IReadOnlyList<string> paths, int dx, int dy, bool copyAllowed);
}

/// <summary>Where the ring was shown: its centre in screen pixels and the monitor's DPI.</summary>
public readonly record struct OverlayPlacement(int CentreX, int CentreY, uint Dpi);

/// <summary>
/// The native radial overlay (ADR-0001): a pre-created topmost, non-activating tool-window popup clipped with
/// SetWindowRgn, painted with GDI and registered as an OLE drop target. The window is the full command circle, but its
/// region is the category ring plus the arc of the open category, so only what is painted takes the drop. It paints a
/// <see cref="MenuView"/> and knows nothing about operations. Create, use and dispose on one STA thread that called
/// OleInitialize.
/// </summary>
public sealed class RadialOverlay : IDisposable
{
    private const string ClassName = "WinTools.RadialOverlay";

    private readonly IOverlayEvents _events;
    private readonly WndProc _wndProc; // referenced for the window's lifetime
    private readonly FileDropTarget _dropTarget;
    private nint _hwnd;
    private bool _dropRegistered;
    private MenuView? _view;
    private int _size;
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
    /// Shows the ring centred on (x, y) — the pointer position when the trigger was pressed — clamped so the whole
    /// command circle fits that monitor's work area, without activating it.
    /// </summary>
    public OverlayPlacement ShowAt(int x, int y)
    {
        var cursor = new POINT { X = x, Y = y };
        var monitor = User32.MonitorFromPoint(cursor, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        User32.GetMonitorInfoW(monitor, ref info);
        _dpi = Shcore.GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 ? dpiX : 96;
        _size = RingGeometry.Scale(RingGeometry.CommandRadius96 * 2, _dpi);
        (_left, _top) = RingGeometry.Place(cursor.X, cursor.Y, _size, info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        _view = null;
        ApplyRegion();
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, _left, _top, _size, _size, User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        User32.InvalidateRect(_hwnd, 0, false);
        User32.UpdateWindow(_hwnd); // paint now, so the target is complete before the first DragEnter
        return new OverlayPlacement(_left + _size / 2, _top + _size / 2, _dpi);
    }

    public void Hide()
    {
        if (IsVisible) User32.ShowWindow(_hwnd, User32.SW_HIDE);
        _view = null;
    }

    /// <summary>Paints <paramref name="view"/> if it differs from what is shown; the region follows the open category.</summary>
    public void SetView(MenuView view)
    {
        if (view == _view) return;
        var regionChanged = _view?.ActiveGroup != view.ActiveGroup || _view?.Commands.Count != view.Commands.Count;
        _view = view;
        if (regionChanged) ApplyRegion();
        Repaint();
    }

    private (int Dx, int Dy) Offset(int screenX, int screenY) => (screenX - _left - _size / 2, screenY - _top - _size / 2);

    internal bool OnDragEnter(IReadOnlyList<string> paths, int x, int y)
    {
        var (dx, dy) = Offset(x, y);
        return _events.DragEntered(paths, dx, dy);
    }

    internal bool OnDragOver(int x, int y)
    {
        var (dx, dy) = Offset(x, y);
        return _events.DragMoved(dx, dy);
    }

    internal void OnDragLeave() => _events.DragLeft();

    internal bool OnDrop(IReadOnlyList<string> paths, int x, int y, bool copyAllowed)
    {
        var (dx, dy) = Offset(x, y);
        return _events.Dropped(paths, dx, dy, copyAllowed);
    }

    private void ApplyRegion()
    {
        var c = _size / 2;
        var ring = RingGeometry.Scale(RingGeometry.OuterRadius96, _dpi);
        var region = Gdi32.CreateEllipticRgn(c - ring, c - ring, c + ring + 1, c + ring + 1);
        if (_view is { ActiveGroup: >= 0, Commands.Count: > 0 } v)
        {
            var band = RingGeometry.Scale(RingGeometry.CommandRadius96, _dpi);
            var outline = ArcPolygon(c, ring - 2, band + 1, v.ArcStart, v.ArcSweep * v.Commands.Count);
            var arc = Gdi32.CreatePolygonRgn(outline, outline.Length, Gdi32.WINDING);
            if (arc != 0)
            {
                _ = Gdi32.CombineRgn(region, region, arc, Gdi32.RGN_OR);
                Gdi32.DeleteObject(arc);
            }
        }
        User32.SetWindowRgn(_hwnd, region, IsVisible); // the system owns the region from here
    }

    /// <summary>Outline of the band between two radii over an arc, in window pixels (angles clockwise from 12 o'clock).</summary>
    private static POINT[] ArcPolygon(int c, int inner, int outer, double start, double sweep)
    {
        var steps = Math.Max(2, (int)Math.Ceiling(sweep / (Math.PI / 90))); // one point every 2°
        var points = new POINT[2 * (steps + 1)];
        for (var i = 0; i <= steps; i++)
        {
            var a = start + sweep * i / steps;
            var (ox, oy) = RingGeometry.PointAt(a, outer);
            var (ix, iy) = RingGeometry.PointAt(a, inner);
            points[i] = new POINT { X = c + (int)Math.Round(ox), Y = c + (int)Math.Round(oy) };
            points[points.Length - 1 - i] = new POINT { X = c + (int)Math.Round(ix), Y = c + (int)Math.Round(iy) };
        }
        return points;
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

    /// <summary>GDI Pie draws counter-clockwise between two radials, so a clockwise sector from a0 to a1 is drawn from a1 to a0.</summary>
    private static void Sector(nint dc, int c, int r, double a0, double a1)
    {
        if (a1 - a0 >= 2 * Math.PI - 1e-9) { Gdi32.Ellipse(dc, c - r, c - r, c + r, c + r); return; } // equal radials: Pie draws nothing useful
        var (x0, y0) = RingGeometry.PointAt(a0, r);
        var (x1, y1) = RingGeometry.PointAt(a1, r);
        Gdi32.Pie(dc, c - r, c - r, c + r, c + r, c + (int)Math.Round(x1), c + (int)Math.Round(y1), c + (int)Math.Round(x0), c + (int)Math.Round(y0));
    }

    private static void Label(nint dc, string text, int c, double angle, double radius, int halfWidth, int halfHeight)
    {
        var (lx, ly) = RingGeometry.PointAt(angle, radius);
        var r = new RECT { Left = c + (int)lx - halfWidth, Top = c + (int)ly - halfHeight, Right = c + (int)lx + halfWidth, Bottom = c + (int)ly + halfHeight };
        User32.DrawTextW(dc, text, -1, ref r, User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE | User32.DT_NOPREFIX);
    }

    private void Paint(nint hwnd)
    {
        var hdc = User32.BeginPaint(hwnd, out var ps);
        try
        {
            var size = Math.Max(_size, 1);
            var c = size / 2;
            var dead = RingGeometry.Scale(RingGeometry.InnerRadius96, _dpi);
            var ring = RingGeometry.Scale(RingGeometry.OuterRadius96, _dpi);
            var band = RingGeometry.Scale(RingGeometry.CommandRadius96, _dpi);
            var view = _view ?? new MenuView([], -1, [], 0, 0, -1, false);
            var mem = Gdi32.CreateCompatibleDC(hdc);
            var bmp = Gdi32.CreateCompatibleBitmap(hdc, size, size);
            var oldBmp = Gdi32.SelectObject(mem, bmp);
            var background = Gdi32.CreateSolidBrush(Gdi32.Rgb(32, 34, 40));
            var normal = Gdi32.CreateSolidBrush(Gdi32.Rgb(52, 56, 66));
            var open = Gdi32.CreateSolidBrush(Gdi32.Rgb(62, 82, 112));     // the category whose commands are shown
            var command = Gdi32.CreateSolidBrush(Gdi32.Rgb(44, 48, 58));
            var pending = Gdi32.CreateSolidBrush(Gdi32.Rgb(58, 94, 140));  // under the pointer, not yet armed
            var armed = Gdi32.CreateSolidBrush(Gdi32.Rgb(70, 140, 220));
            var centre = Gdi32.CreateSolidBrush(Gdi32.Rgb(24, 25, 30));
            var pen = Gdi32.CreatePen(Gdi32.PS_SOLID, Math.Max(1, RingGeometry.Scale(2, _dpi)), Gdi32.Rgb(90, 94, 110));
            var font = Gdi32.CreateFontW(-RingGeometry.Scale(14, _dpi), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, "Segoe UI");
            var small = Gdi32.CreateFontW(-RingGeometry.Scale(13, _dpi), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            var oldPen = Gdi32.SelectObject(mem, pen);
            var oldFont = Gdi32.SelectObject(mem, font);
            var oldBrush = Gdi32.SelectObject(mem, normal);
            try
            {
                User32.FillRect(mem, new RECT { Right = size, Bottom = size }, background);
                _ = Gdi32.SetBkMode(mem, Gdi32.TRANSPARENT);
                var lineHalf = RingGeometry.Scale(12, _dpi);

                // Outer band first: the open category's commands, one sector each, overdrawn inside by the category ring.
                for (var i = 0; i < view.Commands.Count; i++)
                {
                    Gdi32.SelectObject(mem, i != view.HotCommand ? command : view.Armed ? armed : pending);
                    Sector(mem, c, band, view.ArcStart + i * view.ArcSweep, view.ArcStart + (i + 1) * view.ArcSweep);
                }
                var n = view.Groups.Count;
                for (var g = 0; g < n; g++)
                {
                    Gdi32.SelectObject(mem, g == view.ActiveGroup ? open : normal);
                    Sector(mem, c, ring, 2 * Math.PI * g / n, 2 * Math.PI * (g + 1) / n);
                }
                if (n == 0)
                {
                    Gdi32.SelectObject(mem, normal);
                    Gdi32.Ellipse(mem, c - ring, c - ring, c + ring, c + ring);
                }
                Gdi32.SelectObject(mem, centre);
                Gdi32.Ellipse(mem, c - dead, c - dead, c + dead, c + dead);

                _ = Gdi32.SetTextColor(mem, n == 0 ? Gdi32.Rgb(150, 152, 160) : Gdi32.Rgb(240, 240, 245));
                if (n == 0) Label(mem, "No actions", c, 0, (dead + ring) / 2.0, RingGeometry.Scale(48, _dpi), lineHalf);
                for (var g = 0; g < n; g++)
                    Label(mem, view.Groups[g], c, RingGeometry.SectorCentre(g, n), (dead + ring) / 2.0, RingGeometry.Scale(40, _dpi), lineHalf);
                Gdi32.SelectObject(mem, small);
                for (var i = 0; i < view.Commands.Count; i++)
                    Label(mem, view.Commands[i], c, RingGeometry.CommandCentre(view.ArcStart, view.ArcSweep, i), (ring + band) / 2.0, RingGeometry.Scale(56, _dpi), lineHalf);
                Gdi32.BitBlt(hdc, 0, 0, size, size, mem, 0, 0, Gdi32.SRCCOPY);
            }
            finally
            {
                Gdi32.SelectObject(mem, oldFont);
                Gdi32.SelectObject(mem, oldPen);
                Gdi32.SelectObject(mem, oldBrush);
                Gdi32.SelectObject(mem, oldBmp);
                foreach (var o in new[] { font, small, pen, background, normal, open, command, pending, armed, centre, bmp }) Gdi32.DeleteObject(o);
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
