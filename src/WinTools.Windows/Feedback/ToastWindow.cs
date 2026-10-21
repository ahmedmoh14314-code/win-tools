using System.Diagnostics;
using System.Runtime.InteropServices;
using WinTools.Core.Feedback;
using WinTools.Windows.Native;
using WinTools.Windows.Rendering;

namespace WinTools.Windows.Feedback;

/// <summary>What a toast shows. <paramref name="Progress"/> (0–1) marks a job still running; it stays until replaced.</summary>
public sealed record ToastContent(FeedbackKind Kind, string Title, string Detail, double? Progress = null)
{
    public static ToastContent From(FeedbackMessage m) => new(m.Kind, m.Title, m.Detail);
}

/// <summary>
/// The small status card in the bottom-right corner of the work area of the monitor where the user dropped: a
/// click-through, non-activating, topmost layered window. It never takes focus, never receives a click or a drag (every
/// pixel lets input through), slides and fades in, and fades out on its own. One toast at a time; a new one replaces it.
/// Use on the UI thread.
/// </summary>
public sealed class ToastWindow : IDisposable
{
    public const string ClassName = "WinTools.Toast";
    private const nuint AnimationTimer = 1, DismissTimer = 2;

    public static readonly TimeSpan SuccessDuration = TimeSpan.FromSeconds(3.5);
    public static readonly TimeSpan FailureDuration = TimeSpan.FromSeconds(7);
    private const double FadeInMs = 140, FadeOutMs = 180;

    private readonly LayeredWindow _window;
    private readonly Surface _surface = new();
    private readonly long _epoch = Stopwatch.GetTimestamp();
    private ToastContent? _content;
    private uint _dpi = 96;
    private int _x, _y; // final top-left
    private double _shownAt, _hideAt = double.MaxValue;
    private bool _fadingOut;

    public ToastWindow() => _window = new LayeredWindow(ClassName, clickThrough: true, OnMessage);

    /// <summary>No slide and no fades (read at each show from <see cref="ReducedMotionSource"/>).</summary>
    public bool ReducedMotion { get; private set; }

    /// <summary>Where the motion preference comes from: Windows' "Animation effects" unless the owner sets another source.</summary>
    public Func<bool> ReducedMotionSource { get; set; } = SystemPreferences.ReducedMotion;

    public bool IsVisible => _window.IsVisible;

    public ToastContent? Content => _content;

    private double Now => Stopwatch.GetElapsedTime(_epoch).TotalMilliseconds;

    /// <summary>Shows <paramref name="content"/> on the monitor that contains (<paramref name="screenX"/>, <paramref name="screenY"/>).</summary>
    public void Show(ToastContent content, int screenX, int screenY)
    {
        var wasVisible = _window.IsVisible && !_fadingOut;
        ReducedMotion = ReducedMotionSource();
        _content = content;
        var monitor = User32.MonitorFromPoint(new POINT { X = screenX, Y = screenY }, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        User32.GetMonitorInfoW(monitor, ref info);
        _dpi = Shcore.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96;
        var (w, h) = ToastRenderer.WindowSize(_dpi);
        _x = info.rcWork.Right - w - ToastRenderer.Px(4, _dpi);
        _y = info.rcWork.Bottom - h - ToastRenderer.Px(4, _dpi);
        _fadingOut = false;
        if (!wasVisible) _shownAt = Now;
        _hideAt = content.Progress is not null ? double.MaxValue
            : Now + (content.Kind is FeedbackKind.Failure or FeedbackKind.Partial ? FailureDuration : SuccessDuration).TotalMilliseconds;
        Frame();
        _window.Show();
        _window.StartTimer(AnimationTimer, 10);
    }

    /// <summary>Updates a running job's progress without restarting the toast.</summary>
    public void Update(ToastContent content)
    {
        if (!_window.IsVisible || _fadingOut) return;
        _content = content;
        Frame();
    }

    public void Hide()
    {
        _window.StopTimer(AnimationTimer);
        _window.Hide();
        _content = null;
        _fadingOut = false;
    }

    private void Frame()
    {
        if (_content is null) return;
        var now = Now;
        double opacity = 1, slide = 0;
        if (!ReducedMotion)
        {
            var t = Math.Clamp((now - _shownAt) / FadeInMs, 0, 1);
            var e = 1 - Math.Pow(1 - t, 3);
            opacity = e;
            slide = (1 - e) * ToastRenderer.Px(10, _dpi);
            if (_fadingOut) opacity *= Math.Clamp(1 - (now - _hideAt) / FadeOutMs, 0, 1);
        }
        else if (_fadingOut) opacity = 0;
        var (w, h) = ToastRenderer.WindowSize(_dpi);
        using (var p = _surface.Begin(w, h, _dpi)) ToastRenderer.Draw(p, w, h, _content);
        _window.Present(_surface, _x, _y + (int)Math.Round(slide), (byte)Math.Round(opacity * 255));
    }

    private nint? OnMessage(uint msg, nuint wParam, nint lParam)
    {
        if (msg != Msg.TIMER || wParam != AnimationTimer) return null;
        var now = Now;
        if (!_fadingOut && now >= _hideAt) _fadingOut = true;
        if (_fadingOut && (ReducedMotion || now - _hideAt >= FadeOutMs)) { Hide(); return 0; }
        var animating = _fadingOut || now - _shownAt < FadeInMs;
        if (animating) Frame();
        else if (_hideAt == double.MaxValue) _window.StopTimer(AnimationTimer); // a progress toast waits for Update/Show
        return 0;
    }

    public void Dispose()
    {
        _window.Dispose();
        _surface.Dispose();
    }
}

/// <summary>Layout and painting of the toast card (window-independent, for screenshots and checks).</summary>
internal static class ToastRenderer
{
    private const double Width96 = 344, Height96 = 68, Margin96 = 14;

    public static int Px(double v96, uint dpi) => (int)Math.Round(v96 * dpi / 96.0);

    public static (int W, int H) WindowSize(uint dpi) => (Px(Width96 + 2 * Margin96, dpi), Px(Height96 + 2 * Margin96, dpi));

    public static (Argb Accent, Glyph Glyph) Style(FeedbackKind kind, bool running) => running ? (Palette.Green, Glyph.None) : kind switch
    {
        FeedbackKind.Success => (Palette.GreenArmed, Glyph.Check),
        FeedbackKind.Partial => (Palette.Warning, Glyph.Alert),
        FeedbackKind.Failure => (Palette.Error, Glyph.Alert),
        _ => (Palette.TextSecondary, Glyph.Info),
    };

    public static void Draw(Painter p, int w, int h, ToastContent content)
    {
        var m = p.S(Margin96);
        var (x, y, cw, ch) = (m, m, w - 2 * m, h - 2 * m);
        var radius = p.S(10);
        p.Shadow(x, y, cw, ch, radius, p.S(10), 0.55);
        p.FillRoundRect(x, y, cw, ch, radius, Palette.Surface);
        p.StrokeRoundRect(x + 0.5f, y + 0.5f, cw - 1, ch - 1, radius, Palette.Edge, 1);

        var running = content.Progress is not null;
        var (accent, glyph) = Style(content.Kind, running);
        var ix = x + p.S(30);
        var iy = y + ch / 2;
        var ir = p.S(15);
        p.FillCircle(ix, iy, ir, Argb.Lerp(Palette.Surface, accent, 0.16));
        if (running)
        {
            // A progress ring: the done part in the accent over a dim track.
            p.StrokeCircle(ix, iy, ir - p.S(4), Palette.Edge, p.S(2.5));
            p.Arc(ix, iy, ir - p.S(4), 0, Math.Max(0.08, content.Progress!.Value) * 2 * Math.PI, accent, p.S(2.5));
        }
        else Icons.Draw(p, glyph, ix, iy, p.S(18), accent);

        var left = x + p.S(56);
        var right = x + cw - p.S(16);
        p.Text(content.Title, left, y + p.S(13), right, y + p.S(33), Palette.Text, Px(13.5, p.Dpi), 600, TextAlign.Left);
        p.Text(content.Detail, left, y + p.S(34), right, y + p.S(52), Palette.TextSecondary, Px(12, p.Dpi), 400, TextAlign.Left);
        if (running)
        {
            var bar = y + ch - p.S(5);
            p.Line(left, bar, right, bar, Palette.Edge, p.S(3));
            p.Line(left, bar, left + (float)((right - left) * Math.Clamp(content.Progress!.Value, 0, 1)), bar, accent, p.S(3));
        }
    }
}
