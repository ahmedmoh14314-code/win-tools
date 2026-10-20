using Spike.Interaction.Core.Native;

namespace Spike.Interaction.Wpf;

/// <summary>Spike 002C diagnostic: class names of the foreground window and of the window the system hit-tests under the cursor.</summary>
internal static class UnderCursor
{
    public static string Foreground() => User32.ClassNameOf(User32.GetForegroundWindow());

    public static string Window()
    {
        User32.GetCursorPos(out var p);
        return User32.ClassNameOf(User32.WindowFromPoint(p));
    }
}
