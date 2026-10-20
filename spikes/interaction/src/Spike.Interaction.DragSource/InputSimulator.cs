using System.Runtime.InteropServices;
using Spike.Interaction.Core.Input;
using Spike.Interaction.Core.Native;

namespace Spike.Interaction.DragSource;

/// <summary>Thin SendInput wrapper. Absolute moves use virtual-desktop coordinates so multi-monitor works.</summary>
public static class InputSimulator
{
    private static readonly int CbSize = Marshal.SizeOf<INPUT>();

    public static void MoveTo(int x, int y)
    {
        var vx = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN);
        var vy = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        var vw = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN);
        var vh = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);
        var nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        Send(Mouse(MOUSEEVENTF.MOVE | MOUSEEVENTF.ABSOLUTE | MOUSEEVENTF.VIRTUALDESK, nx, ny));
    }

    /// <summary>Move in small steps so drag loops see several WM_MOUSEMOVEs, like a real hand.</summary>
    public static void GlideTo(int x, int y, int steps = 6, int stepDelayMs = 12)
    {
        User32.GetCursorPos(out var p);
        for (var i = 1; i <= steps; i++)
        {
            MoveTo(p.X + (x - p.X) * i / steps, p.Y + (y - p.Y) * i / steps);
            Thread.Sleep(stepDelayMs);
        }
    }

    public static void LeftDown() => Send(Mouse(MOUSEEVENTF.LEFTDOWN));
    public static void LeftUp() => Send(Mouse(MOUSEEVENTF.LEFTUP));

    public static void ButtonDown(MouseButton b) => Send(Mouse(ButtonFlag(b, true), data: XData(b)));
    public static void ButtonUp(MouseButton b) => Send(Mouse(ButtonFlag(b, false), data: XData(b)));

    public static void KeyDown(int vk) => Send(Key((ushort)vk, 0));
    public static void KeyUp(int vk) => Send(Key((ushort)vk, KEYEVENTF.KEYUP));

    public static void Tap(int vk, int holdMs = 40) { KeyDown(vk); Thread.Sleep(holdMs); KeyUp(vk); }

    /// <summary>Press a trigger: modifiers down, key or button down (optionally hold), release in reverse order.</summary>
    public static void PressTrigger(TriggerSpec t, int holdMs)
    {
        if (t.Ctrl) KeyDown(VK.CONTROL);
        if (t.Shift) KeyDown(VK.SHIFT);
        if (t.Alt) KeyDown(VK.MENU);
        if (t.VirtualKey is { } vk) KeyDown(vk); else ButtonDown(t.Button!.Value);
        Thread.Sleep(holdMs);
        if (t.VirtualKey is { } vk2) KeyUp(vk2); else ButtonUp(t.Button!.Value);
        if (t.Alt) KeyUp(VK.MENU);
        if (t.Shift) KeyUp(VK.SHIFT);
        if (t.Ctrl) KeyUp(VK.CONTROL);
    }

    private static uint ButtonFlag(MouseButton b, bool down) => b switch
    {
        MouseButton.Left => down ? MOUSEEVENTF.LEFTDOWN : MOUSEEVENTF.LEFTUP,
        MouseButton.Right => down ? MOUSEEVENTF.RIGHTDOWN : MOUSEEVENTF.RIGHTUP,
        MouseButton.Middle => down ? MOUSEEVENTF.MIDDLEDOWN : MOUSEEVENTF.MIDDLEUP,
        _ => down ? MOUSEEVENTF.XDOWN : MOUSEEVENTF.XUP,
    };

    private static uint XData(MouseButton b) => b switch { MouseButton.X1 => 1, MouseButton.X2 => 2, _ => 0 };

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) => new()
    {
        type = INPUTTYPE.MOUSE,
        u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = data } },
    };

    private static INPUT Key(ushort vk, uint flags) => new()
    {
        type = INPUTTYPE.KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } },
    };

    private static void Send(INPUT input)
    {
        var sent = User32.SendInput(1, [input], CbSize);
        if (sent != 1) throw new InvalidOperationException($"SendInput failed: {Marshal.GetLastWin32Error()}");
    }
}
