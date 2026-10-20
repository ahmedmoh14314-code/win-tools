namespace Spike.Interaction.Core.Input;

public enum MouseButton { Left, Right, Middle, X1, X2 }

/// <summary>A key event as seen by the low-level keyboard hook. Timestamp is Stopwatch ticks.</summary>
public readonly record struct KeyEvent(int VirtualKey, bool IsDown, bool Injected, long Timestamp);

/// <summary>A mouse event as seen by the low-level mouse hook. Timestamp is Stopwatch ticks.</summary>
public readonly record struct MouseEvent(MouseButton? Button, bool IsDown, int X, int Y, bool Injected, long Timestamp)
{
    public bool IsMove => Button is null;
}
