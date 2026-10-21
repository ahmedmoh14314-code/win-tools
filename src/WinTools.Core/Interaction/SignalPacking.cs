namespace WinTools.Core.Interaction;

/// <summary>
/// Packs a hook signal into one pointer-sized message parameter so the hook thread can post it without allocating:
/// bits 0-7 signal, bit 8 left button down, bit 9 dragging, bits 16-31 and 32-47 the pointer position at the moment of
/// the signal (signed 16-bit each, which covers any virtual-desktop coordinate). The ring is centred on that position,
/// not on wherever the pointer is when the UI thread gets to it.
/// </summary>
public static class SignalPacking
{
    public readonly record struct Signal(InputSignal Kind, bool LeftDown, bool Dragging, int X, int Y);

    public static ulong Pack(InputSignal kind, bool leftDown, bool dragging, int x, int y) =>
        (ulong)(byte)kind | (leftDown ? 0x100UL : 0) | (dragging ? 0x200UL : 0)
        | (ulong)(ushort)(short)Math.Clamp(x, short.MinValue, short.MaxValue) << 16
        | (ulong)(ushort)(short)Math.Clamp(y, short.MinValue, short.MaxValue) << 32;

    public static Signal Unpack(ulong value) => new(
        (InputSignal)(value & 0xFF), (value & 0x100) != 0, (value & 0x200) != 0,
        (short)(ushort)(value >> 16), (short)(ushort)(value >> 32));
}
