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

    /// <summary>"ar" when Windows' display language for this user is Arabic (any region), otherwise "en"; the product
    /// runs with invariant globalization, so the language id comes from Windows itself.</summary>
    public static string UiLanguageTag() => (GetUserDefaultUILanguage() & 0x3FF) == 0x01 ? "ar" : "en";

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>The pointer's position in physical screen pixels.</summary>
    public static (int X, int Y) PointerPosition() => Ui.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
}
