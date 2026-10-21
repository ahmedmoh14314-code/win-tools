namespace WinTools.Core.Interaction;

/// <summary>
/// The visual state of the ring between frames: how far the ring has appeared, how lit each sector is, and how far the
/// sector under the pointer has turned into the armed colour. It only decides how things look. What a drop does is
/// decided by <see cref="MenuNavigator"/>, from the time the ring has been on screen, whatever this shows.
/// <list type="bullet">
/// <item>Appear: <see cref="AppearDuration"/> from <see cref="AppearFrom"/>, never from nothing, so the first frame is already readable.</item>
/// <item>Hover: each sector moves towards lit or unlit over <see cref="HoverDuration"/>.</item>
/// <item>Armed: the hot sector turns armed over <see cref="ArmDuration"/>; a new hot sector starts unarmed.</item>
/// <item>Reduced motion: every change is immediate.</item>
/// </list>
/// Single-threaded: the overlay thread owns it.
/// </summary>
public sealed class RingAnimation(bool reducedMotion)
{
    public static readonly TimeSpan AppearDuration = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan HoverDuration = TimeSpan.FromMilliseconds(90);
    public static readonly TimeSpan ArmDuration = TimeSpan.FromMilliseconds(70);
    public const double AppearFrom = 0.35;

    private double[] _hover = [];
    private double _appear = 1, _arm;
    private int _hot = -1;
    private bool _armed;
    private TimeSpan _last;

    public bool ReducedMotion { get; set; } = reducedMotion;

    /// <summary>A new ring is on screen (the overlay appeared or the menu changed level).</summary>
    public void Start(int sectorCount, TimeSpan now)
    {
        _hover = new double[sectorCount];
        _appear = ReducedMotion ? 1 : AppearFrom;
        _hot = -1;
        _armed = false;
        _arm = 0;
        _last = now;
    }

    /// <summary>What the ring should look like now: the sector under the pointer and whether a drop would run.</summary>
    public void Target(int hot, bool armed, TimeSpan now)
    {
        Advance(now);
        if (hot != _hot) { _hot = hot; _arm = 0; }
        _armed = armed && hot >= 0;
        if (ReducedMotion) Settle();
    }

    /// <summary>Moves every value towards its target by the time passed since the last call.</summary>
    public void Advance(TimeSpan now)
    {
        var dt = Math.Max(0, (now - _last).TotalMilliseconds);
        _last = now;
        if (ReducedMotion) { Settle(); return; }
        _appear = Step(_appear, 1, dt / AppearDuration.TotalMilliseconds * (1 - AppearFrom));
        for (var i = 0; i < _hover.Length; i++) _hover[i] = Step(_hover[i], i == _hot ? 1 : 0, dt / HoverDuration.TotalMilliseconds);
        _arm = Step(_arm, _armed ? 1 : 0, dt / ArmDuration.TotalMilliseconds);
    }

    private void Settle()
    {
        _appear = 1;
        for (var i = 0; i < _hover.Length; i++) _hover[i] = i == _hot ? 1 : 0;
        _arm = _armed ? 1 : 0;
    }

    private static double Step(double value, double target, double by) =>
        value < target ? Math.Min(target, value + by) : Math.Max(target, value - by);

    /// <summary>Whether every value has reached its target (no more frames are needed).</summary>
    public bool Settled =>
        _appear >= 1 && _arm == (_armed ? 1 : 0) && _hover.Select((h, i) => h == (i == _hot ? 1 : 0)).All(x => x);

    /// <summary>Appearance progress, eased (ease-out cubic), from <see cref="AppearFrom"/> to 1.</summary>
    public double Appear => Ease(_appear);

    /// <summary>How lit sector <paramref name="i"/> is, eased, 0 to 1.</summary>
    public double Hover(int i) => i >= 0 && i < _hover.Length ? Ease(_hover[i]) : 0;

    /// <summary>How far the hot sector has turned armed, eased, 0 to 1.</summary>
    public double Arm => Ease(_arm);

    public int Hot => _hot;

    private static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
}
