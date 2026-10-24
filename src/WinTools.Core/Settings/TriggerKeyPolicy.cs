namespace WinTools.Core.Settings;

/// <summary>Whether a key may be the trigger, and why not when it may not.</summary>
public readonly record struct KeyVerdict(bool Allowed, string? Reason);

/// <summary>
/// Which keys may open the radial menu during a drag (V2 PR 1.5, plan D8). The hook passes every key on, so whatever a
/// key does in Explorer it may also do during a drag.
/// <list type="bullet">
/// <item><b>Space</b> is the only key measured during a drag (every Explorer end-to-end run).</item>
/// <item><b>F8–F12</b> are kept from V1 so existing settings keep working, but they are <b>not measured</b> either:
/// Explorer uses F10 (menu bar) and F11 (full screen). The typing-interference spike measures all of F2–F12.</item>
/// <item><b>F2–F7, letters and digits</b> are not offered until that spike shows Explorer ignores them during a drag.</item>
/// </list>
/// A refused key in the settings file is reported in the load note with its reason, and the settings window says why
/// other keys are not offered: that is the plan's conflict detection, as far as it goes before the spike.
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
        "F7" => new(false, $"F7 is not measured during a drag; it {Unproven}"),
        "Ctrl" or "Shift" or "Alt" or "Win" => new(false, "Modifier keys change what Explorer does with the drop (copy, move, link)."),
        { Length: 1 } k when char.IsAsciiLetterOrDigit(k[0]) =>
            new(false, "Letters and digits select files by name in Explorer while you type; they are refused until the typing test passes."),
        _ => new(false, "Not a key WinTools can use as the trigger."),
    };
}
