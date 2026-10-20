using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Overlay;
using Spike.Interaction.Core.Telemetry;

namespace Spike.Interaction.Wpf;

/// <summary>
/// WPF overlay: a transparent, topmost, non-activating window with a ring of sector paths.
/// Drop handling uses WPF's own AllowDrop/DragOver/Drop (WPF registers the OLE drop target internally).
/// The only native interop left is the window ex-style tweak to stop activation.
/// </summary>
public sealed class WpfOverlayWindow : Window
{
    private readonly SpikeLog _log;
    private readonly int _radius96;
    private readonly int _sectors;
    private readonly Canvas _canvas = new();
    private readonly List<Path> _sectorPaths = new();
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(52, 56, 66));
    private static readonly Brush Hot = new SolidColorBrush(Color.FromRgb(70, 140, 220));
    private int _hotSector = -1;
    private int _cycle;
    private long _activateTs, _showTs;
    private bool _awaitingFrame, _awaitingEnter;
    private int _movesSinceShow;
    private double _scale = 1;

    public event Action<IReadOnlyList<string>, int>? Dropped;
    public event Action? DragLeft;
    public event Action? DragEntered;

    public WpfOverlayWindow(SpikeLog log, int radius96, int sectors)
    {
        _log = log; _radius96 = radius96; _sectors = sectors;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        AllowDrop = true;
        Focusable = false;
        Width = radius96 * 2; Height = radius96 * 2;
        Content = _canvas;
        BuildRing();
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => { _log.Event("drag.leave", new { cycle = _cycle }); SetHot(-1); DragLeft?.Invoke(); };
        Drop += OnDrop;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
            User32.SetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE, ex | (nint)(WS.EX_NOACTIVATE | WS.EX_TOOLWINDOW));
            _log.Event("overlay.created", new { hwnd = hwnd.ToString("X") });
        };
    }

    private void BuildRing()
    {
        _canvas.Children.Clear(); _sectorPaths.Clear();
        double r = _radius96, c = _radius96, inner = 28;
        for (var i = 0; i < _sectors; i++)
        {
            var a0 = 2 * Math.PI * i / _sectors - Math.PI / 2;
            var a1 = 2 * Math.PI * (i + 1) / _sectors - Math.PI / 2;
            var p0 = new Point(c + r * Math.Cos(a0), c + r * Math.Sin(a0));
            var p1 = new Point(c + r * Math.Cos(a1), c + r * Math.Sin(a1));
            var q0 = new Point(c + inner * Math.Cos(a0), c + inner * Math.Sin(a0));
            var q1 = new Point(c + inner * Math.Cos(a1), c + inner * Math.Sin(a1));
            var fig = new PathFigure { StartPoint = q0, IsClosed = true };
            fig.Segments.Add(new LineSegment(p0, true));
            fig.Segments.Add(new ArcSegment(p1, new Size(r, r), 0, _sectors == 1, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(q1, true));
            fig.Segments.Add(new ArcSegment(q0, new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true));
            var path = new Path { Data = new PathGeometry([fig]), Fill = Normal, Stroke = new SolidColorBrush(Color.FromRgb(90, 94, 110)), StrokeThickness = 2 };
            _sectorPaths.Add(path);
            _canvas.Children.Add(path);
            var am = 2 * Math.PI * (i + 0.5) / _sectors - Math.PI / 2;
            var lr = (inner + r) / 2;
            var label = new TextBlock { Text = ((char)('A' + i)).ToString(), Foreground = Brushes.White, FontSize = 14, IsHitTestVisible = false };
            Canvas.SetLeft(label, c + lr * Math.Cos(am) - 5); Canvas.SetTop(label, c + lr * Math.Sin(am) - 9);
            _canvas.Children.Add(label);
        }
    }

    public void ShowAtCursor(long activateTs)
    {
        _cycle++;
        _activateTs = activateTs;
        User32.GetCursorPos(out var cursor);
        var (work, _, dpi, device) = MonitorInfo.ForPoint(cursor);
        _scale = dpi / 96.0;
        var sizePx = OverlayPlacement.Scale(_radius96 * 2, dpi);
        var rect = OverlayPlacement.Place(cursor, work, sizePx, sizePx);
        var fgBefore = User32.GetForegroundWindow();
        _hotSector = -1; _movesSinceShow = 0; _awaitingFrame = true; _awaitingEnter = true;
        SetHot(-1);

        // WPF positions in DIPs relative to the primary monitor's DPI at startup; set via SetWindowPos in pixels instead.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) { Show(); hwnd = new WindowInteropHelper(this).Handle; }
        User32.SetWindowPos(hwnd, SWP.HWND_TOPMOST, rect.Left, rect.Top, sizePx, sizePx, SWP.NOACTIVATE | SWP.SHOWWINDOW);
        if (!IsVisible) Show();
        _showTs = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnFirstFrame;

        var fgAfter = User32.GetForegroundWindow();
        _log.Event("overlay.show", new
        {
            cycle = _cycle, cursor = cursor.ToString(), monitor = device, dpi, work = work.ToString(), rect = rect.ToString(),
            hookToShowMs = Math.Round(SpikeLog.MsBetween(activateTs, _showTs), 3),
            fgBefore = User32.ClassNameOf(fgBefore), fgAfter = User32.ClassNameOf(fgAfter), focusStolen = fgBefore != fgAfter,
        }, _showTs);
    }

    private void OnFirstFrame(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnFirstFrame;
        if (!_awaitingFrame) return;
        _awaitingFrame = false;
        var now = Stopwatch.GetTimestamp();
        _log.Event("overlay.painted", new { cycle = _cycle, hookToPaintMs = Math.Round(SpikeLog.MsBetween(_activateTs, now), 3), showToPaintMs = Math.Round(SpikeLog.MsBetween(_showTs, now), 3) }, now);
    }

    public void HideOverlay(string reason)
    {
        if (!IsVisible) return;
        Hide();
        _log.Event("overlay.hide", new { cycle = _cycle, reason, enterReceived = !_awaitingEnter });
    }

    public void NoteMouseMove() { if (_awaitingEnter) _movesSinceShow++; }

    private Point _lastDragPos;

    private int HitSector(DragEventArgs e)
    {
        var p = e.GetPosition(this);
        _lastDragPos = p;
        var c = _radius96;
        return SectorHitTest.Hit((int)p.X, (int)p.Y, c, c, 28, c, _sectors);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (_awaitingEnter)
        {
            _awaitingEnter = false;
            var now = Stopwatch.GetTimestamp();
            _log.Event("drag.enter", new { cycle = _cycle, hookToEnterMs = Math.Round(SpikeLog.MsBetween(_activateTs, now), 3), showToEnterMs = Math.Round(SpikeLog.MsBetween(_showTs, now), 3), movesSinceShow = _movesSinceShow }, now);
        }
        DragEntered?.Invoke();
        SetHot(HitSector(e));
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        _movesSinceShow++;
        SetHot(HitSector(e));
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var sector = HitSector(e);
        e.Effects = paths.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None; // never Move
        e.Handled = true;
        var now = Stopwatch.GetTimestamp();
        User32.GetCursorPos(out var cur);
        _log.Event("drop", new { cycle = _cycle, sector, posDip = $"({_lastDragPos.X:0},{_lastDragPos.Y:0})", cursorPx = $"({cur.X},{cur.Y})", windowLeftTopDip = $"({Left:0},{Top:0})", count = paths.Length, paths, hookToDropMs = Math.Round(SpikeLog.MsBetween(_activateTs, now), 3) }, now);
        HideOverlay("drop");
        Dropped?.Invoke(paths, sector);
    }

    private void SetHot(int s)
    {
        if (s == _hotSector) return;
        _hotSector = s;
        for (var i = 0; i < _sectorPaths.Count; i++) _sectorPaths[i].Fill = i == s ? Hot : Normal;
    }
}
