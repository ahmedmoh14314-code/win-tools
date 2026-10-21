using WinTools.Core.Capabilities;

namespace WinTools.Core.Interaction;

/// <summary>What the overlay paints: category labels, the active one, its command arc, and the command under the pointer.</summary>
public sealed record MenuView(
    IReadOnlyList<string> Groups,
    int ActiveGroup,
    IReadOnlyList<string> Commands,
    double ArcStart,
    double ArcSweep,
    int HotCommand,
    bool Armed);

/// <summary>
/// Navigation of the two-level radial menu during a drag, driven only by pointer offsets from the ring centre and the
/// time since the overlay appeared. No button can be clicked while dragging, so pointing is the whole interaction:
/// <list type="bullet">
/// <item>Entering a category in the inner ring makes it active and shows its commands in the outer band. It stays
/// active while the pointer is in the dead zone or the outer band. A command arc can be wider than its category, so
/// the way out to it may cross a neighbouring category: that neighbour opens only near the hub (inside
/// <see cref="SwitchFraction"/> of the ring) or in a direction outside the open arc, never on the way to it.</item>
/// <item>A drop runs a command only if the pointer is on a command of the active category and that category has been
/// visible for at least <see cref="ArmDelay"/>. A drop on a category, the dead zone or a gap runs nothing.</item>
/// <item>The commands appear outside the pointer, never under it, so reaching one always takes a deliberate outward
/// movement.</item>
/// </list>
/// Single-threaded: every call comes from the overlay thread.
/// </summary>
public sealed class MenuNavigator
{
    /// <summary>
    /// Minimum time the command arc must have been visible before a drop on it runs. It rejects a release that
    /// happens while the pointer is still sweeping outward through a category that just opened.
    /// </summary>
    public static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>While a category is open, another one inside the open arc's directions opens only this far out into the ring (0 = hub edge, 1 = ring edge).</summary>
    public const double SwitchFraction = 0.4;

    private readonly IReadOnlyList<MenuGroup> _groups;
    private readonly string[] _groupLabels;
    private readonly string[][] _commandLabels; // cached, so equal views compare equal and repaint only on change
    private readonly int _dead, _ring, _band, _switch;
    private int _active = -1, _hot = -1;
    private TimeSpan _activeSince;

    public MenuNavigator(IReadOnlyList<MenuGroup> groups, uint dpi)
    {
        _groups = groups;
        _groupLabels = groups.Select(g => g.Label).ToArray();
        _commandLabels = groups.Select(g => g.Commands.Select(c => c.Label).ToArray()).ToArray();
        _dead = RingGeometry.Scale(RingGeometry.InnerRadius96, dpi);
        _ring = RingGeometry.Scale(RingGeometry.OuterRadius96, dpi);
        _band = RingGeometry.Scale(RingGeometry.CommandRadius96, dpi);
        _switch = _dead + (int)Math.Round((_ring - _dead) * SwitchFraction);
    }

    public IReadOnlyList<MenuGroup> Groups => _groups;

    /// <summary>The active category, or null before the pointer has entered one.</summary>
    public MenuGroup? Active => _active >= 0 ? _groups[_active] : null;

    /// <summary>The command under the pointer, armed or not.</summary>
    public Operation? Hot => _active >= 0 && _hot >= 0 ? _groups[_active].Commands[_hot] : null;

    /// <summary>Feeds the pointer offset from the ring centre at <paramref name="now"/> (time since the overlay appeared).</summary>
    public void Move(int dx, int dy, TimeSpan now)
    {
        var group = RingGeometry.HitSector(dx, dy, _dead, _ring, _groups.Count);
        if (group >= 0)
        {
            if (group != _active && (_active < 0 || NearHub(dx, dy) || !InOpenArc(dx, dy))) { _active = group; _activeSince = now; }
            _hot = -1;
            return;
        }
        if (_active < 0) { _hot = -1; return; }
        var (start, sweep) = Arc();
        _hot = RingGeometry.HitCommand(dx, dy, _ring, _band, start, sweep, _groups[_active].Commands.Count);
    }

    /// <summary>The command a drop at this moment would run, or null.</summary>
    public Operation? Armed(TimeSpan now) => Hot is { } op && now - _activeSince >= ArmDelay ? op : null;

    /// <summary>How long the active category has been open (zero when none is).</summary>
    public TimeSpan ActiveFor(TimeSpan now) => _active >= 0 ? now - _activeSince : TimeSpan.Zero;

    public MenuView View(TimeSpan now)
    {
        if (_active < 0) return new MenuView(_groupLabels, -1, [], 0, 0, -1, false);
        var (start, sweep) = Arc();
        return new MenuView(_groupLabels, _active, _commandLabels[_active], start, sweep, _hot, Armed(now) is not null);
    }

    private bool NearHub(int dx, int dy) => (double)dx * dx + (double)dy * dy < (double)_switch * _switch;

    private bool InOpenArc(int dx, int dy)
    {
        var (start, sweep) = Arc();
        var span = sweep * _groups[_active].Commands.Count;
        var rel = (RingGeometry.Angle(dx, dy) - start) % (2 * Math.PI);
        return (rel < 0 ? rel + 2 * Math.PI : rel) < span;
    }

    private (double Start, double Sweep) Arc() => RingGeometry.CommandArc(_active, _groups.Count, _groups[_active].Commands.Count);
}
