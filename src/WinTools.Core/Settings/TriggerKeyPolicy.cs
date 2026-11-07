namespace WinTools.Core.Settings;

/// <summary>Whether a key may be the trigger, and why not when it may not.</summary>
public readonly record struct KeyVerdict(bool Allowed, string? Reason);

/// <summary>
/// Which keys may open the radial menu during a drag (V2 PR 1.5, plan D8, ADR-0021). The hook passes every key on, so
/// whatever a key does in Explorer it could also do. The typing-interference spike
/// (docs/research/2026-10-25-typing-interference.md) showed that Explorer ignores F2–F4, F6–F12, letters and digits
/// while a file is being dragged (F5's refresh is invisible to it). It did not cover two ways a key still reaches a
/// program: a key held past the drop repeats into whatever is in front once the drag has ended, and with Ctrl, Shift or
/// Alt held it is another shortcut (Alt+F4, Shift+F10). So only keys whose stray press is harmless are offered:
/// <list type="bullet">
/// <item><b>Space, F7, F9 and F12</b>: Space only selects in Explorer; F7, F9 and F12 do nothing there.</item>
/// <item><b>F8</b> is refused: it deletes in Total Commander and Far (Shift+F8 without the Recycle Bin).</item>
/// <item><b>F2, F3, F4, F6, F10 and F11</b> are refused: a stray press renames, searches, opens the address bar, moves the
/// focus, opens the menu bar or goes full screen.</item>
/// <item><b>F5</b> refreshes Explorer's view, and <b>F1</b> opens Help.</item>
/// <item><b>Letters A to Z</b> (ADR-0021 amendment, 2026-11-07) work only during a file drag, recognised by the shell's
/// drag-image window: then the key is swallowed, so it is typed nowhere, even held past the drop. At any other time the
/// letter is ordinary typing, and a letter pressed while holding the mouse in a game or a drawing program does nothing here.
/// A drag from a program that shows no shell drag image cannot use a letter; Space still works there.</item>
/// <item><b>Digits</b> are refused: nothing asked for them, and they select in games while the mouse is held.</item>
/// </list>
/// A refused key in the settings file is reported in the load note with its reason, and the settings window says why
/// other keys are not offered.
/// </summary>
public static class TriggerKeyPolicy
{
    /// <summary>The allowed keys in the order the settings window lists them.</summary>
    public static IReadOnlyList<string> Order { get; } = ["Space", "F7", "F9", "F12", .. Letters()];

    /// <summary>The allowed keys and their virtual-key codes.</summary>
    public static IReadOnlyDictionary<string, int> Allowed { get; } = new Dictionary<string, int>(Letters().Select(l => KeyValuePair.Create(l, (int)l[0])))
    {
        ["Space"] = 0x20, ["F7"] = 0x76, ["F9"] = 0x78, ["F12"] = 0x7B,
    };

    private static IEnumerable<string> Letters() => Enumerable.Range('A', 26).Select(c => ((char)c).ToString());

    /// <summary>Whether a trigger key works only during a file drag, and is then swallowed: the letters (their virtual keys are their capitals).</summary>
    public static bool NeedsFileDrag(int virtualKey) => virtualKey is >= 'A' and <= 'Z';

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
        "F8" => new(false, "F8 deletes files in Total Commander and Far; a press that reaches them, such as the key held past the drop, would ask to delete."),
        "F10" => new(false, $"F10 opens the menu bar, and Shift+F10 the context menu; {Stray} open them."),
        "F11" => new(false, $"F11 switches Explorer to full screen; {Stray} switch it."),
        "Ctrl" or "Shift" or "Alt" or "Win" => new(false, "Modifier keys change what Explorer does with the drop (copy, move, link)."),
        { Length: 1 } k when char.IsAsciiLetterLower(k[0]) => new(false, "Letters are named in capitals: A to Z."),
        { Length: 1 } k when char.IsAsciiDigit(k[0]) => new(false, "Digits are typed while holding the mouse in other programs (games, drawing)."),
        _ => new(false, "Not a key WinTools can use as the trigger."),
    };
}
