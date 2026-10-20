using Spike.Interaction.Core.Native;

namespace Spike.Interaction.Core.Input;

/// <summary>
/// Describes an activation trigger: either a key (optionally with modifiers) or a mouse button.
/// Parsed from strings such as "space", "ctrl+space", "mbutton", "xbutton1", "f8".
/// </summary>
public sealed record TriggerSpec(string Name, int? VirtualKey, MouseButton? Button, bool Ctrl, bool Shift, bool Alt)
{
    public bool IsKey => VirtualKey is not null;

    public static TriggerSpec Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Trigger text is empty.", nameof(text));
        var parts = text.Trim().ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false, alt = false;
        int? vk = null;
        MouseButton? button = null;

        foreach (var part in parts)
        {
            switch (part)
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "space": vk = VK.SPACE; break;
                case "tab": vk = VK.TAB; break;
                case "enter" or "return": vk = VK.RETURN; break;
                case "mbutton" or "middle": button = MouseButton.Middle; break;
                case "rbutton" or "right": button = MouseButton.Right; break;
                case "xbutton1" or "x1": button = MouseButton.X1; break;
                case "xbutton2" or "x2": button = MouseButton.X2; break;
                default:
                    if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) { vk = char.ToUpperInvariant(part[0]); break; }
                    if (part.Length is 2 or 3 && part[0] == 'f' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24) { vk = VK.F1 + f - 1; break; }
                    throw new ArgumentException($"Unknown trigger token '{part}' in '{text}'.", nameof(text));
            }
        }

        if (vk is null && button is null) throw new ArgumentException($"Trigger '{text}' has no key or button.", nameof(text));
        if (vk is not null && button is not null) throw new ArgumentException($"Trigger '{text}' mixes a key and a mouse button.", nameof(text));
        if (button is not null && (ctrl || shift || alt)) throw new ArgumentException("Mouse-button triggers with modifiers are not supported in the spike.", nameof(text));
        return new TriggerSpec(text.Trim().ToLowerInvariant(), vk, button, ctrl, shift, alt);
    }

    public override string ToString() => Name;
}
