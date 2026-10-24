namespace WinTools.Core.Settings;

/// <summary>Whether a key may be the trigger, and why not when it may not.</summary>
public readonly record struct KeyVerdict(bool Allowed, string? Reason);

/// <summary>
/// Which keys may open the radial menu during a drag (V2 PR 1.5, plan D8). The hook passes every key on, so a key that
/// Explorer acts on would also do that during a drag: a key is allowed only when it is known to do nothing there. F2–F7
/// and letters are refused until the typing-interference spike shows Explorer ignores them during a drag; the refusal
/// says so, which is the settings window's conflict detection.
/// </summary>
public static class TriggerKeyPolicy
{
    /// <summary>The allowed keys and their virtual-key codes.</summary>
    public static IReadOnlyDictionary<string, int> Allowed { get; } = new Dictionary<string, int>
    {
        ["Space"] = 0x20, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
    };

    private const string Unproven = "is refused until a test shows Explorer ignores it during a drag.";

    public static KeyVerdict Judge(string? key) => key switch
    {
        not null when Allowed.ContainsKey(key) => new(true, null),
        "F1" => new(false, "F1 opens Help in Explorer and most programs."),
        "F2" => new(false, $"F2 renames the selected file in Explorer; it {Unproven}"),
        "F3" => new(false, $"F3 opens Explorer's search; it {Unproven}"),
        "F4" => new(false, $"F4 opens Explorer's address bar; it {Unproven}"),
        "F5" => new(false, $"F5 refreshes Explorer's view; it {Unproven}"),
        "F6" => new(false, $"F6 moves the focus between Explorer's panes; it {Unproven}"),
        "F7" => new(false, $"F7 {Unproven}"),
        "Ctrl" or "Shift" or "Alt" or "Win" => new(false, "Modifier keys change what Explorer does with the drop (copy, move, link)."),
        { Length: 1 } k when char.IsAsciiLetterOrDigit(k[0]) =>
            new(false, "Letters and digits select files by name in Explorer while you type; they are refused until the typing test passes."),
        _ => new(false, "Not a key WinTools can use as the trigger."),
    };
}
