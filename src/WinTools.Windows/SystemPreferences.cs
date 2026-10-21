using WinTools.Windows.Native;

namespace WinTools.Windows;

/// <summary>User preferences and state Windows exposes that the UI follows.</summary>
public static class SystemPreferences
{
    /// <summary>
    /// True when "Animation effects" is off (Settings → Accessibility → Visual effects; SPI_GETCLIENTAREAANIMATION):
    /// the UI then shows every change at once.
    /// </summary>
    public static bool ReducedMotion() => Ui.SystemParametersInfoW(Ui.SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) && on == 0;

    /// <summary>The pointer's position in physical screen pixels.</summary>
    public static (int X, int Y) PointerPosition() => Ui.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
}
