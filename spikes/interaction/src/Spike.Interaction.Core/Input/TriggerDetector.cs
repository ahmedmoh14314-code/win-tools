using Spike.Interaction.Core.Native;

namespace Spike.Interaction.Core.Input;

/// <summary>
/// Pure, allocation-free decision logic that runs inside the hook callbacks.
/// Tracks modifier state and the left button so it can tell "trigger pressed while a left-button drag is in progress".
/// No Windows calls happen here; the caller supplies events and acts on the returned decision.
/// </summary>
public sealed class TriggerDetector
{
    private readonly TriggerSpec _spec;
    private readonly int _dragThresholdPx;
    private bool _ctrl, _shift, _alt;
    private bool _leftDown;
    private int _leftDownX, _leftDownY;
    private bool _dragging;
    private bool _triggerHeld;

    public TriggerDetector(TriggerSpec spec, int dragThresholdPx = 4)
    {
        _spec = spec;
        _dragThresholdPx = dragThresholdPx;
    }

    public bool LeftButtonDown => _leftDown;
    public bool IsDragging => _dragging;

    public enum Decision { None, Activate, LeftButtonReleased, Escape, DragStarted }

    public Decision OnMouse(in MouseEvent e)
    {
        if (e.Button == MouseButton.Left)
        {
            if (e.IsDown)
            {
                _leftDown = true; _leftDownX = e.X; _leftDownY = e.Y; _dragging = false;
                return Decision.None;
            }
            _leftDown = false; _dragging = false;
            return Decision.LeftButtonReleased;
        }

        if (e.IsMove)
        {
            if (_leftDown && !_dragging &&
                (Math.Abs(e.X - _leftDownX) >= _dragThresholdPx || Math.Abs(e.Y - _leftDownY) >= _dragThresholdPx))
            {
                _dragging = true;
                return Decision.DragStarted;
            }
            return Decision.None;
        }

        if (_spec.Button is { } b && e.Button == b)
        {
            if (e.IsDown && !_triggerHeld) { _triggerHeld = true; return _leftDown ? Decision.Activate : Decision.None; }
            if (!e.IsDown) _triggerHeld = false;
        }
        return Decision.None;
    }

    public Decision OnKey(in KeyEvent e)
    {
        switch (e.VirtualKey)
        {
            case VK.CONTROL or VK.LCONTROL or VK.RCONTROL: _ctrl = e.IsDown; return Decision.None;
            case VK.SHIFT or VK.LSHIFT or VK.RSHIFT: _shift = e.IsDown; return Decision.None;
            case VK.MENU or VK.LMENU or VK.RMENU: _alt = e.IsDown; return Decision.None;
            case VK.ESCAPE when e.IsDown: return Decision.Escape;
        }

        if (_spec.VirtualKey is { } vk && e.VirtualKey == vk)
        {
            if (e.IsDown)
            {
                if (_triggerHeld) return Decision.None; // key auto-repeat
                _triggerHeld = true;
                var modsOk = _ctrl == _spec.Ctrl && _shift == _spec.Shift && _alt == _spec.Alt;
                return modsOk && _leftDown ? Decision.Activate : Decision.None;
            }
            _triggerHeld = false;
        }
        return Decision.None;
    }

    /// <summary>True when this key event is the trigger key itself (used to decide whether to swallow it).</summary>
    public bool IsTriggerKey(in KeyEvent e) => _spec.VirtualKey is { } vk && e.VirtualKey == vk && _leftDown;
}
