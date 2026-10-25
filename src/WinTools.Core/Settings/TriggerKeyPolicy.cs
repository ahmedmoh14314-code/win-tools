namespace WinTools.Core.Settings;

/// <summary>Whether a key may be the trigger, and why not when it may not.</summary>
public readonly record struct KeyVerdict(bool Allowed, string? Reason);

/// <summary>
/// Which keys may open the radial menu during a drag (V2 PR 1.5, plan D8, ADR-0021). The hook passes every key on, so
/// whatever a key does in Explorer it could also do. The typing-interference spike
/// (docs/research/2026-10-25-typing-interference.md) showed that Explorer ignores F2–F12, letters and digits while a
/// file is being dragged. It did not cover two ways a key still reaches Explorer: a key held past the drop repeats into
/// Explorer once the drag has ended, and with Ctrl, Shift or Alt held it is another shortcut (Alt+F4, Shift+F10). So:
/// <list type="bullet">
/// <item><b>Space, F7, F8, F9 and F12</b> are offered: they do nothing in Explorer, so a stray press is harmless.</item>
/// <item><b>F2, F3, F4, F6, F10 and F11</b> are refused: a stray press renames, searches, opens the address bar, moves the
/// focus, opens the menu bar or goes full screen.</item>
/// <item><b>F5</b> refreshes Explorer's view, and <b>F1</b> opens Help.</item>
/// <item><b>Letters and digits</b> are refused: the ring opens on any left-button drag, not only on a file drag, so a
/// letter pressed while holding the mouse in another program (a game, a drawing program) would open it there.</item>
/// </list>
/// A refused key in the settings file is reported in the load note with its reason, and the settings window says why
/// other keys are not offered.
/// </summary>
public static class TriggerKeyPolicy
{
    /// <summary>The allowed keys in the order the settings window lists them.</summary>
    public static IReadOnlyList<string> Order { get; } = ["Space", "F7", "F8", "F9", "F12"];

    /// <summary>The allowed keys and their virtual-key codes.</summary>
    public static IReadOnlyDictionary<string, int> Allowed { get; } = new Dictionary<string, int>
    {
        ["Space"] = 0x20, ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F12"] = 0x7B,
    };

    private const string Stray = "a press that reaches Explorer, such as the key held past the drop, would";

    public static KeyVerdict Judge(string? key) => key switch
    {
        not null when Allowed.ContainsKey(key) => new(true, null),
        "F1" => new(false, "F1 opens Help in Explorer and most programs."),
        "F2" => new(false, $"F2 renames in Explorer; {Stray} start renaming the file."),
        "F3" => new(false, $"F3 opens Explorer's search; {Stray} open it."),
        "F4" => new(false, $"F4 opens Explorer's address bar, and Alt+F4 closes windows; {Stray} act on it."),
        "F5" => new(false, $"F5 refreshes Explorer's view; {Stray} refresh it."),
        "F6" => new(false, $"F6 moves the focus between Explorer's panes; {Stray} move it."),
        "F10" => new(false, $"F10 opens the menu bar, and Shift+F10 the context menu; {Stray} open them."),
        "F11" => new(false, $"F11 switches Explorer to full screen; {Stray} switch it."),
        "Ctrl" or "Shift" or "Alt" or "Win" => new(false, "Modifier keys change what Explorer does with the drop (copy, move, link)."),
        { Length: 1 } k when char.IsAsciiLetterOrDigit(k[0]) =>
            new(false, "Letters and digits are typed while holding the mouse in other programs (games, drawing); they would open the menu there."),
        _ => new(false, "Not a key WinTools can use as the trigger."),
    };
}
