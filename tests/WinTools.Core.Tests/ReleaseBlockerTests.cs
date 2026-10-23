using WinTools.Core.Interaction;

namespace WinTools.Core.Tests;

/// <summary>
/// Release blocker 2A (owner field report, 2026-10-23): selecting files with Ctrl-click, keeping Ctrl held and starting the
/// drag, then pressing Space, did nothing. The trigger accepted Space only with no modifier down. WinTools' drop target
/// always answers COPY, so a modifier the user holds for Explorer cannot make WinTools move or link a file.
/// </summary>
public sealed class ModifierTriggerTests
{
    private const int Space = 0x20, LCtrl = 0xA2, RCtrl = 0xA3, LShift = 0xA0, LAlt = 0xA4;

    private static TriggerDetector Dragging()
    {
        var d = new TriggerDetector();
        d.OnMouseButton(MouseButton.Left, true, 100, 100);
        d.OnMouseMove(110, 100);
        return d;
    }

    [Theory]
    [InlineData(LCtrl)]
    [InlineData(RCtrl)]
    [InlineData(LShift)]
    public void Space_triggers_while_ctrl_or_shift_is_held_during_the_drag(int modifier)
    {
        var d = Dragging();
        d.OnKey(modifier, true);
        Assert.Equal(InputSignal.Trigger, d.OnKey(Space, true));
    }

    [Fact]
    public void Ctrl_held_from_a_ctrl_click_selection_through_the_drag_still_triggers()
    {
        var d = new TriggerDetector();
        d.OnKey(LCtrl, true);                                   // Ctrl-click selection: Ctrl goes down first...
        d.OnMouseButton(MouseButton.Left, true, 100, 100);      // ...and stays down through the press and the drag
        d.OnMouseMove(130, 100);
        d.Resync(leftDown: true, ctrl: true, shift: false, alt: false); // what the hook reads from the system
        Assert.Equal(InputSignal.Trigger, d.OnKey(Space, true));
    }

    [Fact]
    public void Ctrl_and_shift_together_still_trigger()
    {
        var d = Dragging();
        d.OnKey(LCtrl, true);
        d.OnKey(LShift, true);
        Assert.Equal(InputSignal.Trigger, d.OnKey(Space, true));
    }

    [Fact]
    public void Space_with_alt_is_never_silently_ignored()
    {
        // Alt+Space is Windows' window-menu shortcut, so it is not a trigger; but the user must be told, not ignored.
        var d = Dragging();
        d.OnKey(LAlt, true);
        Assert.NotEqual(InputSignal.None, d.OnKey(Space, true));
    }

    [Fact]
    public void Alt_space_is_refused_explicitly_and_only_during_a_genuine_drag()
    {
        var d = Dragging();
        d.OnKey(LAlt, true);
        Assert.Equal(InputSignal.TriggerRefused, d.OnKey(Space, true));

        var pressed = new TriggerDetector();                     // button down, but no drag yet: ordinary input
        pressed.OnMouseButton(MouseButton.Left, true, 100, 100);
        pressed.OnKey(LAlt, true);
        Assert.Equal(InputSignal.None, pressed.OnKey(Space, true));

        var typing = new TriggerDetector();                      // Alt+Space while typing opens the window menu as usual
        typing.OnKey(LAlt, true);
        Assert.Equal(InputSignal.None, typing.OnKey(Space, true));
    }

    [Fact]
    public void Releasing_alt_during_the_drag_makes_the_next_press_a_trigger()
    {
        var d = Dragging();
        d.OnKey(LAlt, true);
        Assert.Equal(InputSignal.TriggerRefused, d.OnKey(Space, true));
        d.OnKey(Space, false);
        d.OnKey(LAlt, false);
        Assert.Equal(InputSignal.Trigger, d.OnKey(Space, true));
    }

    [Theory]
    [InlineData(0xA1)] // right Shift
    [InlineData(0xA3)] // right Ctrl
    public void Right_hand_ctrl_or_shift_does_not_block_the_trigger(int modifier)
    {
        var d = Dragging();
        d.OnKey(modifier, true);
        Assert.Equal(InputSignal.Trigger, d.OnKey(Space, true));
    }

    [Fact]
    public void Right_alt_is_refused_like_left_alt_and_auto_repeat_stays_quiet()
    {
        var d = Dragging();
        d.OnKey(0xA5, true);                                     // right Alt (AltGr also sends it)
        Assert.Equal(InputSignal.TriggerRefused, d.OnKey(Space, true));
        Assert.Equal(InputSignal.None, d.OnKey(Space, true));    // auto-repeat: one refusal per press
        Assert.Equal(InputSignal.None, d.OnKey(Space, true));
    }

    [Fact]
    public void A_refusal_survives_the_hook_packing()
    {
        var s = SignalPacking.Unpack(SignalPacking.Pack(InputSignal.TriggerRefused, true, true, -1200, 845));
        Assert.Equal((InputSignal.TriggerRefused, true, true, -1200, 845), (s.Kind, s.LeftDown, s.Dragging, s.X, s.Y));
    }
}
