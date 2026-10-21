namespace WinTools.Core.Feedback;

/// <summary>The UI sounds. Each one is a short tone synthesised by <see cref="SoundBank"/>.</summary>
public enum UiSound { Tick, Level, Success, Failure, Nothing }

/// <summary>
/// Decides which navigation sounds the radial menu plays, from what the pointer is over. Fed with every view the
/// overlay shows (the open level and the sector under the pointer):
/// <list type="bullet">
/// <item>A <see cref="UiSound.Tick"/> when the pointer comes onto a sector other than the last one it was on, on the
/// same ring. Leaving to the dead zone or outside the ring and coming back to the same sector is not a change.</item>
/// <item>A <see cref="UiSound.Level"/> when the menu opens a category or goes back to the categories; the sector
/// memory starts again on the new ring.</item>
/// <item>Ticks are at least <see cref="MinTickInterval"/> apart, and none follows a level sound within
/// <see cref="QuietAfterLevel"/>: a fast sweep across the ring gives a few ticks, not one per sector crossed. A tick
/// that is held back is dropped, never queued.</item>
/// </list>
/// When the sounds are off nothing is ever returned. Single-threaded: the overlay thread owns it.
/// </summary>
public sealed class NavigationSounds(bool enabled)
{
    public static readonly TimeSpan MinTickInterval = TimeSpan.FromMilliseconds(45);
    public static readonly TimeSpan QuietAfterLevel = TimeSpan.FromMilliseconds(80);

    private int _level = int.MinValue; // nothing seen yet
    private int _lastSector = -1;
    private TimeSpan _lastSound = TimeSpan.MinValue;
    private bool _lastWasLevel;

    public bool Enabled { get; set; } = enabled;

    /// <summary>Sectors entered on this ring or earlier ones in the session, counted with or without a sound.</summary>
    public int SectorChanges { get; private set; }
    public int LevelChanges { get; private set; }
    public int Ticks { get; private set; }
    public int LevelSounds { get; private set; }

    /// <summary>Forgets the session: the next view is the first of a new overlay session and plays nothing.</summary>
    public void Reset()
    {
        _level = int.MinValue;
        _lastSector = -1;
        _lastSound = TimeSpan.MinValue;
        _lastWasLevel = false;
        SectorChanges = LevelChanges = Ticks = LevelSounds = 0;
    }

    /// <param name="level">The open category's index, or -1 for the categories' ring.</param>
    /// <param name="hot">The sector under the pointer, or -1.</param>
    /// <param name="now">Time on a monotonic clock.</param>
    public UiSound? OnView(int level, int hot, TimeSpan now)
    {
        if (_level == int.MinValue) { _level = level; _lastSector = hot; return null; } // the ring just appeared
        if (level != _level)
        {
            _level = level;
            _lastSector = hot;
            LevelChanges++;
            if (!Enabled) return null;
            LevelSounds++;
            _lastSound = now;
            _lastWasLevel = true;
            return UiSound.Level;
        }
        if (hot < 0 || hot == _lastSector) return null;
        _lastSector = hot;
        SectorChanges++;
        if (!Enabled) return null;
        var quiet = _lastWasLevel ? QuietAfterLevel : MinTickInterval;
        if (_lastSound != TimeSpan.MinValue && now - _lastSound < quiet) return null;
        Ticks++;
        _lastSound = now;
        _lastWasLevel = false;
        return UiSound.Tick;
    }
}
