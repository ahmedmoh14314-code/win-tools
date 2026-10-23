namespace WinTools.Core.Interaction;

public enum MouseButton { Left, Right, Middle, X1, X2 }

/// <summary>
/// What the hook thread forwards to the UI thread. <see cref="TriggerRefused"/>: the trigger key was pressed during a
/// genuine drag while Alt was held; nothing opens, and the user is told why instead of being silently ignored.
/// </summary>
public enum InputSignal { None, Trigger, LeftButtonReleased, Escape, TriggerRefused }

/// <summary>
/// Allocation-free decision logic that runs inside the low-level hook callbacks: it tracks the left button and the
/// drag threshold and recognises the trigger key (key auto-repeat ignored). Modifier policy (ADR-0003, amended
/// 2026-10-23 after an owner field report):
/// <list type="bullet">
/// <item>Ctrl and Shift do not block the trigger. Users select with Ctrl-click or Shift-click and keep the key held
/// into the drag; WinTools' drop target only ever answers COPY, so a modifier meant for Explorer cannot make WinTools
/// move or link anything.</item>
/// <item>Alt does: Alt+Space is Windows' window-menu shortcut. During a genuine drag the press is reported as
/// <see cref="InputSignal.TriggerRefused"/> so the user gets feedback; outside a drag it is ordinary typing.</item>
/// </list>
/// Not thread-safe: the hook thread owns it, except <see cref="TriggerVirtualKey"/>, which the settings may change from
/// another thread (a single atomic field).
/// </summary>
public sealed class TriggerDetector(int triggerVirtualKey = TriggerDetector.VkSpace, int dragThresholdPx = 4)
{
    private int _triggerVk = triggerVirtualKey;

    /// <summary>The trigger key; changing it takes effect from the next key event.</summary>
    public int TriggerVirtualKey { get => Volatile.Read(ref _triggerVk); set => Volatile.Write(ref _triggerVk, value); }

    public const int VkSpace = 0x20;
    private const int VkEscape = 0x1B, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    private const int VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;

    private bool _ctrl, _shift, _alt, _leftDown, _dragging, _triggerHeld;
    private int _downX, _downY, _x, _y;

    public bool LeftButtonDown => _leftDown;
    public bool IsDragging => _dragging;

    /// <summary>Last pointer position seen by the mouse hook, in physical screen pixels.</summary>
    public (int X, int Y) Position => (_x, _y);

    public InputSignal OnMouseButton(MouseButton button, bool isDown, int x, int y)
    {
        _x = x; _y = y;
        if (button != MouseButton.Left) return InputSignal.None;
        if (isDown)
        {
            _leftDown = true; _dragging = false; _downX = x; _downY = y;
            return InputSignal.None;
        }
        var wasDown = _leftDown;
        _leftDown = false; _dragging = false;
        return wasDown ? InputSignal.LeftButtonReleased : InputSignal.None;
    }

    public void OnMouseMove(int x, int y)
    {
        _x = x; _y = y;
        if (_leftDown && !_dragging && (Math.Abs(x - _downX) >= dragThresholdPx || Math.Abs(y - _downY) >= dragThresholdPx))
            _dragging = true;
    }

    /// <summary>
    /// Corrects tracked state from the system's own key state. Key-ups and button-ups can be missed (secure desktop,
    /// release over an elevated window), which would leave a modifier or the button "stuck". The hook calls this just
    /// before a trigger key-down is evaluated.
    /// </summary>
    public void Resync(bool leftDown, bool ctrl, bool shift, bool alt)
    {
        (_ctrl, _shift, _alt) = (ctrl, shift, alt);
        if (!leftDown) { _leftDown = false; _dragging = false; }
    }

    public bool IsTriggerKey(int virtualKey) => virtualKey == TriggerVirtualKey;

    public InputSignal OnKey(int virtualKey, bool isDown)
    {
        switch (virtualKey)
        {
            case VkControl or VkLControl or VkRControl: _ctrl = isDown; return InputSignal.None;
            case VkShift or VkLShift or VkRShift: _shift = isDown; return InputSignal.None;
            case VkMenu or VkLMenu or VkRMenu: _alt = isDown; return InputSignal.None;
            case VkEscape: return isDown ? InputSignal.Escape : InputSignal.None;
        }
        if (virtualKey != TriggerVirtualKey) return InputSignal.None;
        if (!isDown) { _triggerHeld = false; return InputSignal.None; }
        if (_triggerHeld) return InputSignal.None; // auto-repeat
        _triggerHeld = true;
        if (!_leftDown) return InputSignal.None;
        if (_alt) return _dragging ? InputSignal.TriggerRefused : InputSignal.None;
        return InputSignal.Trigger;
    }
}
