using System.Diagnostics;
using System.Runtime.InteropServices;
using WinTools.Core.Interaction;
using WinTools.Windows.Native;
using WinTools.Windows.Rendering;

namespace WinTools.Windows.Overlay;

/// <summary>
/// Callbacks from the overlay to its owner, on the overlay thread. Positions are offsets in physical pixels from the
/// ring centre (down is positive). The owner decides what is under the pointer and answers with
/// <see cref="RadialOverlay.SetView"/>; the return value says whether a command is under the pointer (drop effect COPY).
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

/// <summary>Paint statistics of one overlay session, for the log.</summary>
public readonly record struct OverlayFrames(int Frames, int AnimationFrames, double MaxFrameMs);

/// <summary>
/// The native radial overlay (ADR-0001, ADR-0016): a pre-created topmost, non-activating tool-window popup with
/// per-pixel alpha (UpdateLayeredWindow), registered as an OLE drop target. The disc is opaque in every frame, so the
/// whole disc, including the dead zone, receives the drag (alpha 0 would let OLE through); only the corners outside the
/// circle are transparent, as with the earlier window region. It shows one ring at a time and is moved by its owner when
/// the menu changes level. Short animations (appear, hover, armed) are driven by a timer while they run and never delay
/// a drop: what a drop does depends on the navigator, not on the frame. Create, use and dispose on one STA thread that
/// called OleInitialize.
/// </summary>
public sealed class RadialOverlay : IDisposable
{
    public const string ClassName = "WinTools.RadialOverlay";
    private const nuint AnimationTimer = 1;
    private const uint FrameMs = 10; // the system rounds timers to its tick (about 15.6 ms)

    private readonly IOverlayEvents _events;
    private readonly FileDropTarget _dropTarget;
    private readonly LayeredWindow _window;
    private readonly Surface _surface = new();
    private readonly RadialRenderer _renderer = new();
    private readonly RingAnimation _animation;
    private readonly long _epoch = Stopwatch.GetTimestamp();
    private bool _dropRegistered;
    private MenuView? _view;
    private int _size;
    private uint _dpi = 96;
    private int _left, _top;
    private int _frames, _animationFrames;
    private double _maxFrameMs;

    public RadialOverlay(IOverlayEvents events, bool reducedMotion = false)
    {
        _events = events;
        _animation = new RingAnimation(reducedMotion);
        _dropTarget = new FileDropTarget(this);
        _window = new LayeredWindow(ClassName, clickThrough: false, OnMessage);
        var hr = Ole32.RegisterDragDrop(_window.Handle, _dropTarget);
        if (hr != 0) throw new InvalidOperationException($"RegisterDragDrop failed (0x{hr:X8}); OleInitialize must run on this thread first.");
        _dropRegistered = true;
        WarmUp();
    }

    /// <summary>
    /// Renders two frames off screen at start-up (nothing is shown): the first frame otherwise pays for JIT compilation,
    /// font creation and GDI+ set-up inside the trigger-to-overlay latency.
    /// </summary>
    private void WarmUp()
    {
        var dpi = Ui.GetDpiForSystem();
        var size = RadialRenderer.WindowSize(dpi);
        var anim = new RingAnimation(reducedMotion: false);
        var sample = new MenuView(["Convert", "Compress", "Resize", "Rotate", "Tools"], 0, 0, false, "", 0,
            [MenuCategory.Convert, MenuCategory.Compress, MenuCategory.Resize, MenuCategory.Rotate, MenuCategory.Tools]);
        foreach (var view in new[] { RadialRenderer.Waiting, sample })
        {
            anim.Start(view.Labels.Count, TimeSpan.Zero);
            anim.Target(view.Hot, false, TimeSpan.Zero);
            using var p = _surface.Begin(size, size, dpi);
            _renderer.Draw(p, size, view, anim);
        }
    }

    public bool IsVisible => _window.IsVisible;

    /// <summary>Windows' "animation effects" setting off, or the owner's choice: changes are shown at once.</summary>
    public bool ReducedMotion { get => _animation.ReducedMotion; set => _animation.ReducedMotion = value; }

    private TimeSpan Now => Stopwatch.GetElapsedTime(_epoch);

    /// <summary>
    /// Shows the ring centred on (x, y), the pointer when the trigger was pressed, clamped to that monitor's work area,
    /// without activating it. The first frame is complete before the window appears, so the target is whole before the
    /// first DragEnter.
    /// </summary>
    public OverlayPlacement ShowAt(int x, int y)
    {
        Place(x, y);
        _view = RadialRenderer.Waiting; // until the first DragEnter says what the files allow
        _frames = _animationFrames = 0;
        _maxFrameMs = 0;
        _animation.Start(0, Now);
        if (Render()) _window.Show(); // a frame that failed is never shown (its holes would let the drag through)
        return new OverlayPlacement(_left + _size / 2, _top + _size / 2, _dpi);
    }

    /// <summary>
    /// Moves the visible ring to be centred on (x, y), clamped like <see cref="ShowAt"/>. Nothing is drawn until the
    /// owner's next <see cref="SetView"/>, which paints the new level at the new place in one update (no intermediate frame).
    /// </summary>
    public OverlayPlacement MoveTo(int x, int y)
    {
        Place(x, y);
        _view = null; // the next SetView always paints, and starts the new ring's appearance
        return new OverlayPlacement(_left + _size / 2, _top + _size / 2, _dpi);
    }

    /// <summary>Session paint statistics since the last <see cref="ShowAt"/>.</summary>
    public OverlayFrames Frames => new(_frames, _animationFrames, Math.Round(_maxFrameMs, 3));

    /// <summary>Puts the visible ring back above other topmost windows (a toast that appeared during the drag).</summary>
    public void BringToFront()
    {
        if (_window.IsVisible) _window.Show();
    }

    public void Hide()
    {
        _window.StopTimer(AnimationTimer);
        _window.Hide();
        _view = null;
    }

    /// <summary>Paints <paramref name="view"/> if it differs from what is shown.</summary>
    public void SetView(MenuView view)
    {
        if (view == _view) return;
        var newRing = _view is null || _view.Labels.Count != view.Labels.Count || _view.Start != view.Start || _view.Open != view.Open
            || !_view.Labels.SequenceEqual(view.Labels);
        _view = view;
        if (newRing) _animation.Start(view.Labels.Count, Now);
        _animation.Target(view.Hot, view.Armed, Now);
        if (!_window.IsVisible) return;
        if (!Render()) { Hide(); return; }
        if (!_animation.Settled) _window.StartTimer(AnimationTimer, FrameMs);
    }

    private void Place(int x, int y)
    {
        var cursor = new POINT { X = x, Y = y };
        var monitor = User32.MonitorFromPoint(cursor, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        User32.GetMonitorInfoW(monitor, ref info);
        _dpi = Shcore.GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 ? dpiX : 96;
        _size = RadialRenderer.WindowSize(_dpi);
        (_left, _top) = RingGeometry.Place(cursor.X, cursor.Y, _size, info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
    }

    /// <summary>
    /// Draws and presents one frame. Returns false, presenting nothing, when drawing failed or the frame is not opaque
    /// where the disc must be (checked at the centre and just inside the rim), or when the window refused it.
    /// </summary>
    private bool Render()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using (var p = _surface.Begin(_size, _size, _dpi))
                _renderer.Draw(p, _size, _view ?? RadialRenderer.Waiting, _animation);
        }
        catch (InvalidOperationException) { return false; } // GDI or GDI+ out of resources
        if (!DiscIsOpaque() || !_window.Present(_surface, _left, _top)) return false;
        _frames++;
        _maxFrameMs = Math.Max(_maxFrameMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return true;
    }

    private bool DiscIsOpaque()
    {
        var c = _size / 2;
        var r = c - Math.Max(2, _size / 100);
        return _surface.AlphaAt(c, c) == 255 && _surface.AlphaAt(c - r, c) == 255 && _surface.AlphaAt(c + r - 1, c) == 255
            && _surface.AlphaAt(c, c - r) == 255 && _surface.AlphaAt(c, c + r - 1) == 255;
    }

    private nint? OnMessage(uint msg, nuint wParam, nint lParam)
    {
        if (msg != Msg.TIMER || wParam != AnimationTimer) return null;
        _animation.Advance(Now);
        if (_window.IsVisible && _view is not null)
        {
            if (Render()) _animationFrames++;
            else Hide();
        }
        if (_animation.Settled || !_window.IsVisible) _window.StopTimer(AnimationTimer);
        return 0;
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

    public void Dispose()
    {
        if (_window.Handle == 0) return;
        _window.StopTimer(AnimationTimer);
        if (_dropRegistered) { _ = Ole32.RevokeDragDrop(_window.Handle); _dropRegistered = false; }
        _window.Dispose();
        _surface.Dispose();
    }
}
