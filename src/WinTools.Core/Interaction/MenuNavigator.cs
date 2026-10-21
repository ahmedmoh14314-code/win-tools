using WinTools.Core.Capabilities;

namespace WinTools.Core.Interaction;

/// <summary>What the overlay paints: one ring of labelled sectors, the one under the pointer, and a label for the hub.</summary>
/// <param name="Start">Where sector 0 begins (radians clockwise from 12 o'clock).</param>
/// <param name="Hot">Sector under the pointer, or -1.</param>
/// <param name="Armed">The hot sector is a command that a drop would run now.</param>
/// <param name="Hub">Text for the centre: the open category, or empty at the top level.</param>
public sealed record MenuView(IReadOnlyList<string> Labels, double Start, int Hot, bool Armed, string Hub);

/// <summary>
/// Navigation of the two-level radial menu during a drag, driven only by pointer offsets from the centre of the ring
/// on screen and the time since that ring appeared. No button can be clicked while dragging, so pointing is the whole
/// interaction, and only one level is on screen at a time (a compound marking menu):
/// <list type="bullet">
/// <item>The top level shows the categories. Moving deep into one (<see cref="RingGeometry.OpenRadius96"/>) opens it:
/// the owner re-centres the ring on the pointer, which shows that category's commands plus a Back sector pointing the
/// way the stroke came. Moving deep into Back returns to the categories, re-centred the same way.</item>
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
    private int _dead, _ring, _open;
    private int _level = -1, _hot = -1;
    private bool _hubVisited;
    private TimeSpan _shownAt;

    public MenuNavigator(IReadOnlyList<MenuGroup> groups, uint dpi)
    {
        _groups = groups;
        _groupLabels = groups.Select(g => g.Label).ToArray();
        _levelLabels = groups.Select(g => new[] { BackLabel }.Concat(g.Commands.Select(c => c.Label)).ToArray()).ToArray();
        SetDpi(dpi);
    }

    public IReadOnlyList<MenuGroup> Groups => _groups;

    /// <summary>The open category, or null at the top level.</summary>
    public MenuGroup? Open => _level >= 0 ? _groups[_level] : null;

    /// <summary>The command under the pointer, armed or not.</summary>
    public Operation? Hot => _level >= 0 && _hot >= 1 ? _groups[_level].Commands[_hot - 1] : null;

    /// <summary>
    /// Where sector 0 (Back) of category <paramref name="group"/>'s ring begins: Back is centred on the direction
    /// opposite to the category, so retracing the stroke goes back. Commands follow clockwise.
    /// </summary>
    public static double LevelStart(int group, int groupCount, int commandCount) =>
        RingGeometry.Normalize(RingGeometry.SectorCentre(group, groupCount) + Math.PI - Math.PI / (commandCount + 1));

    private int SectorCount => _level < 0 ? _groups.Count : _groups[_level].Commands.Count + 1;

    private double Start => _level < 0 ? 0 : LevelStart(_level, _groups.Count, _groups[_level].Commands.Count);

    /// <summary>
    /// Feeds the pointer offset from the centre of the ring on screen at <paramref name="now"/>. Returns true when the
    /// level changed: the owner must re-centre the ring on the pointer and call <see cref="Shown"/>.
    /// </summary>
    public bool Move(int dx, int dy, TimeSpan now)
    {
        var d2 = (double)dx * dx + (double)dy * dy;
        if (d2 < (double)_dead * _dead) { _hubVisited = true; _hot = -1; return false; }
        if (!_hubVisited) { _hot = -1; return false; } // pushed aside near an edge: come to the centre first
        _hot = RingGeometry.HitSector(dx, dy, _dead, _ring, SectorCount, Start);
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
    public Operation? Armed(TimeSpan now) => Hot is { } op && now - _shownAt >= ArmDelay ? op : null;

    /// <summary>How long the current ring has been on screen.</summary>
    public TimeSpan ShownFor(TimeSpan now) => now - _shownAt;

    public MenuView View(TimeSpan now) => _level < 0
        ? new MenuView(_groupLabels, 0, _hot, false, "")
        : new MenuView(_levelLabels[_level], Start, _hot, Armed(now) is not null, _groups[_level].Label);

    private void SetDpi(uint dpi)
    {
        _dead = RingGeometry.Scale(RingGeometry.InnerRadius96, dpi);
        _ring = RingGeometry.Scale(RingGeometry.OuterRadius96, dpi);
        _open = RingGeometry.Scale(RingGeometry.OpenRadius96, dpi);
    }
}
