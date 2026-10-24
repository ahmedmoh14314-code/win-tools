using WinTools.Core.Capabilities;

namespace WinTools.Core.Interaction;

/// <summary>What the overlay paints: one ring of labelled sectors, the one under the pointer, and a label for the hub.</summary>
/// <param name="Start">Where sector 0 begins (radians clockwise from 12 o'clock).</param>
/// <param name="Hot">Sector under the pointer, or -1.</param>
/// <param name="Armed">The hot sector is a command that a drop would run now.</param>
/// <param name="Hub">Text for the centre: the open category, or empty at the top level.</param>
/// <param name="FirstSweep">Width of sector 0 (Back on a category's ring), or 0 when all sectors are equal.</param>
/// <param name="Categories">On the categories' ring, the icon key of each sector; otherwise null.</param>
/// <param name="Open">On a category's ring, that category's icon key; otherwise null.</param>
/// <param name="EmptyText">For a ring with no commands, what it says instead of "No actions" (RadialMenu.EmptyReason).</param>
public sealed record MenuView(IReadOnlyList<string> Labels, double Start, int Hot, bool Armed, string Hub, double FirstSweep = 0,
    IReadOnlyList<string>? Categories = null, string? Open = null, string? EmptyText = null);

/// <summary>
/// Navigation of the two-level radial menu during a drag, driven only by pointer offsets from the centre of the ring
/// on screen and the time since that ring appeared. No button can be clicked while dragging, so pointing is the whole
/// interaction, and only one level is on screen at a time (a compound marking menu):
/// <list type="bullet">
/// <item>The top level shows the categories. Moving deep into one (<see cref="RingGeometry.OpenRadius96"/>) opens it:
/// the owner re-centres the ring on the pointer, which shows that category's commands plus a Back sector pointing the
/// way the stroke came. Back is at least as wide as a category, so retracing any stroke that opened the category lands
/// on it. Moving deep into Back returns to the categories, re-centred the same way.</item>
/// <item>Movement threshold: a ring reacts only after the pointer has been inside its dead zone. Each ring appears
/// centred on the pointer, so this holds at once, except near a screen edge where the ring is pushed aside; there the
/// pointer has to come to the centre first.</item>
/// <item>Minimum visible time: a drop runs a command only if the ring has been on screen for <see cref="ArmDelay"/>.
/// A drop anywhere else (a category, Back, the dead zone, outside) runs nothing.</item>
/// </list>
/// Single-threaded: every call comes from the overlay thread.
/// </summary>
public sealed class MenuNavigator
{
    /// <summary>Minimum time a ring of commands must have been on screen before a drop on it runs.</summary>
    public static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(150);

    public const string BackLabel = "Back";

    private readonly IReadOnlyList<MenuGroup> _groups;
    private readonly string[] _groupLabels;
    private readonly string[][] _levelLabels; // per category: Back, then its commands (cached so equal views compare equal)
    private readonly string[] _categories;
    private int _dead, _ring, _open;
    private int _level = -1, _hot = -1;
    private bool _hubVisited;
    private TimeSpan _shownAt;

    private readonly string? _emptyText;

    public MenuNavigator(IReadOnlyList<MenuGroup> groups, uint dpi, string? emptyText = null)
    {
        _groups = groups;
        _emptyText = groups.Count == 0 ? emptyText : null;
        _groupLabels = groups.Select(g => g.Label).ToArray();
        _levelLabels = groups.Select(g => new[] { BackLabel }.Concat(g.Commands.Select(c => c.Label)).ToArray()).ToArray();
        _categories = groups.Select(g => g.Category.Icon).ToArray();
        SetDpi(dpi);
    }

    public IReadOnlyList<MenuGroup> Groups => _groups;

    /// <summary>The open category, or null at the top level.</summary>
    public MenuGroup? Open => _level >= 0 ? _groups[_level] : null;

    /// <summary>The command under the pointer, armed or not.</summary>
    public CapabilityDescriptor? Hot => _level >= 0 && _hot >= 1 ? _groups[_level].Commands[_hot - 1] : null;

    /// <summary>
    /// The narrowest a command sector may get: V1's narrowest, tested by hand (a BMP's Resize: 7 commands beside a 90° Back, 4 categories, 38.6°).
    /// </summary>
    public const double MinCommandSweep = 38.5 * Math.PI / 180;

    /// <summary>
    /// Width of Back on a category's ring: an equal share, widened to a category sector's width so the reverse of any
    /// stroke that opened the category (which stayed inside that sector) points into Back, but never so far that a
    /// command gets narrower than <see cref="MinCommandSweep"/>. With one category there is no other sector to come back
    /// from: Back takes an equal share (evaluator, PR #17: it took the whole circle).
    /// </summary>
    public static double BackSweep(int groupCount, int commandCount)
    {
        var equal = 2 * Math.PI / (commandCount + 1);
        var category = groupCount > 1 ? 2 * Math.PI / groupCount : 0;
        return Math.Max(equal, Math.Min(category, 2 * Math.PI - commandCount * MinCommandSweep));
    }

    /// <summary>
    /// Where sector 0 (Back) of category <paramref name="group"/>'s ring begins: Back is centred on the direction
    /// opposite to the category. Commands follow clockwise and share the rest of the circle.
    /// </summary>
    public static double LevelStart(int group, int groupCount, int commandCount) =>
        RingGeometry.Normalize(RingGeometry.SectorCentre(group, groupCount) + Math.PI - BackSweep(groupCount, commandCount) / 2);

    private int SectorCount => _level < 0 ? _groups.Count : _groups[_level].Commands.Count + 1;

    private double Start => _level < 0 ? 0 : LevelStart(_level, _groups.Count, _groups[_level].Commands.Count);

    private double FirstSweep => _level < 0 ? 0 : BackSweep(_groups.Count, _groups[_level].Commands.Count);

    /// <summary>
    /// Feeds the pointer offset from the centre of the ring on screen at <paramref name="now"/>. Returns true when the
    /// level changed: the owner must re-centre the ring on the pointer and call <see cref="Shown"/>.
    /// </summary>
    public bool Move(int dx, int dy, TimeSpan now)
    {
        var d2 = (double)dx * dx + (double)dy * dy;
        if (d2 < (double)_dead * _dead) { _hubVisited = true; _hot = -1; return false; }
        if (!_hubVisited) { _hot = -1; return false; } // pushed aside near an edge: come to the centre first
        _hot = RingGeometry.HitSector(dx, dy, _dead, _ring, SectorCount, Start, FirstSweep);
        if (_hot < 0 || d2 < (double)_open * _open) return false;
        if (_level < 0) _level = _hot;          // into a category
        else if (_hot == 0) _level = -1;        // into Back
        else return false;                      // deep on a command: just stay there
        _hot = -1;
        _hubVisited = false;
        _shownAt = now;
        return true;
    }

    /// <summary>
    /// The new ring is on screen: <paramref name="dx"/>/<paramref name="dy"/> is the pointer's offset from its centre
    /// (zero unless it was pushed aside near an edge), measured on a monitor of <paramref name="dpi"/>.
    /// </summary>
    public void Shown(int dx, int dy, TimeSpan now, uint dpi)
    {
        SetDpi(dpi);
        _shownAt = now;
        _hot = -1;
        _hubVisited = (double)dx * dx + (double)dy * dy < (double)_dead * _dead;
    }

    /// <summary>The command a drop at this moment would run, or null.</summary>
    public CapabilityDescriptor? Armed(TimeSpan now) => Hot is { } op && now - _shownAt >= ArmDelay ? op : null;

    /// <summary>How long the current ring has been on screen.</summary>
    public TimeSpan ShownFor(TimeSpan now) => now - _shownAt;

    public MenuView View(TimeSpan now) => _level < 0
        ? new MenuView(_groupLabels, 0, _hot, false, "", 0, _categories, EmptyText: _emptyText)
        : new MenuView(_levelLabels[_level], Start, _hot, Armed(now) is not null, _groups[_level].Label, FirstSweep, null, _groups[_level].Category.Icon);

    private void SetDpi(uint dpi)
    {
        _dead = RingGeometry.Scale(RingGeometry.InnerRadius96, dpi);
        _ring = RingGeometry.Scale(RingGeometry.OuterRadius96, dpi);
        _open = RingGeometry.Scale(RingGeometry.OpenRadius96, dpi);
    }
}
