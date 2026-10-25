namespace WinTools.Core.Settings;

/// <summary>Whether a key may be the trigger, and why not when it may not.</summary>
public readonly record struct KeyVerdict(bool Allowed, string? Reason);

/// <summary>
/// Which keys may open the radial menu during a drag (V2 PR 1.5, plan D8, ADR-0021). The hook passes every key on, so
/// whatever a key does in Explorer it could also do during a drag. The typing-interference spike measured that
/// (docs/research/2026-10-25-typing-interference.md):
/// <list type="bullet">
/// <item><b>Space, F2–F4 and F6–F12</b> are offered. Explorer ignored each of them during a drag, while the control
/// without a drag showed that the probe sees it when Explorer does act (F2 rename, F3 search, F4 address bar, F6 focus,
/// F10 menu, F11 full screen).</item>
/// <item><b>F5</b> is refused: a refresh changes nothing the probe can see, so "ignored" is not proven.</item>
/// <item><b>F1</b> is refused: it opens Help, and was not measured.</item>
/// <item><b>Letters and digits</b> passed in Explorer but are refused: the ring opens on any left-button drag, not only on
/// a file drag, so a letter pressed while holding the mouse in another program (a game, a drawing program, a text
/// selection) would open it there. They need a file-drag-only condition first.</item>
/// </list>
/// A refused key in the settings file is reported in the load note with its reason, and the settings window says why
/// other keys are not offered.
/// </summary>
public static class TriggerKeyPolicy
{
    /// <summary>The allowed keys and their virtual-key codes, in the order the settings window lists them.</summary>
    public static IReadOnlyDictionary<string, int> Allowed { get; } = new Dictionary<string, int>
    {
        ["Space"] = 0x20, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F6"] = 0x75, ["F7"] = 0x76,
        ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
    };

    public static KeyVerdict Judge(string? key) => key switch
    {
        not null when Allowed.ContainsKey(key) => new(true, null),
        "F1" => new(false, "F1 opens Help in Explorer and most programs."),
        "F5" => new(false, "F5 refreshes Explorer's view; the test cannot see a refresh, so it is not offered."),
        "Ctrl" or "Shift" or "Alt" or "Win" => new(false, "Modifier keys change what Explorer does with the drop (copy, move, link)."),
        { Length: 1 } k when char.IsAsciiLetterOrDigit(k[0]) =>
            new(false, "Letters and digits are typed while holding the mouse in other programs (games, drawing); they would open the menu there."),
        _ => new(false, "Not a key WinTools can use as the trigger."),
    };
}
