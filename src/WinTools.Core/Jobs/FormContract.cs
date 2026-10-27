using System.Globalization;
using System.Text;

namespace WinTools.Core.Jobs;

/// <summary>What a form field holds: a time in a file ("1:23.5"), a whole number, or a number of seconds that may be negative.</summary>
public enum FormFieldKind { Time, Count, Seconds }

/// <summary>
/// One field of a command's form: its key in the answer, the <see cref="Localization.UiText"/> key of its label (and of
/// its hint, "&lt;label&gt;.tip"), what it holds and its bounds. An optional field may be left empty.
/// </summary>
public sealed record FormField(string Key, string LabelKey, FormFieldKind Kind, string Default, double Min, double Max, bool Optional = false);

/// <summary>
/// The form dialog (Tangerine parity: Trim, Split, a custom subtitle shift): the command whose form to show and where. The
/// dialogs process builds the fields itself from <see cref="Forms"/>; like every dialog request it carries no file path.
/// </summary>
public sealed record FormRequest(string Title, string Form, int X, int Y, string Kind = DialogKinds.Form);

/// <summary>The form's answer (<see cref="Forms.Check"/>'s canonical text), or null when cancelled.</summary>
public sealed record FormResult(string? Answer);

/// <summary>The values a form answered, by field key; an optional field left empty is absent.</summary>
public sealed record FormValues(IReadOnlyDictionary<string, double> Values)
{
    public double? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
}

/// <summary>What the typed texts read as: the canonical answer, or the first problem (a UiText key) and its field's index.</summary>
public sealed record FormCheck(string? Answer, string? ProblemKey = null, int Field = -1, FormValues? Values = null);

/// <summary>
/// The forms commands ask for, and the one way their answers are read: the dialog checks what is typed with
/// <see cref="Check"/>, the host accepts only an answer <see cref="Read"/> takes, and the worker reads it again the same
/// way, so no layer trusts another's parsing. The answer is "key=value;…" in field order, values in invariant seconds.
/// </summary>
public static class Forms
{
    public const string TrimStart = "start", TrimEnd = "end", SplitParts = "parts", ShiftSeconds = "seconds";

    /// <summary>The longest time a field takes (the media module's longest file).</summary>
    public const double MaxTime = 12 * 3600;

    private static readonly Dictionary<string, IReadOnlyList<FormField>> All = new(StringComparer.Ordinal)
    {
        [Capabilities.MediaCapabilities.TrimVideoId] = Trim,
        [Capabilities.MediaCapabilities.TrimAudioId] = Trim,
        [Capabilities.MediaCapabilities.SplitVideoId] = Split,
        [Capabilities.MediaCapabilities.SplitAudioId] = Split,
        [Capabilities.SubtitleCapabilities.ShiftCustomId] = [new(ShiftSeconds, "form.shift.seconds", FormFieldKind.Seconds, "1.5", -3600, 3600)],
    };

    private static IReadOnlyList<FormField> Trim =>
        [new(TrimStart, "form.trim.start", FormFieldKind.Time, "0:00", 0, MaxTime), new(TrimEnd, "form.trim.end", FormFieldKind.Time, "", 0, MaxTime, Optional: true)];

    private static IReadOnlyList<FormField> Split => [new(SplitParts, "form.split.parts", FormFieldKind.Count, "2", 2, 20)];

    /// <summary>A command's fields, or null for a command without a form.</summary>
    public static IReadOnlyList<FormField>? For(string capabilityId) => All.TryGetValue(capabilityId, out var f) ? f : null;

    /// <summary>The typed texts, one per field in order, checked: the canonical answer or the first problem.</summary>
    public static FormCheck Check(string capabilityId, IReadOnlyList<string?> texts)
    {
        if (For(capabilityId) is not { } fields || texts.Count != fields.Count) return new(null, "form.invalid");
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var text = texts[i]?.Trim() ?? "";
            if (text.Length == 0)
            {
                if (f.Optional) continue;
                return new(null, "form.required", i);
            }
            var value = f.Kind switch
            {
                FormFieldKind.Time => ReadTime(text),
                FormFieldKind.Count => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null,
                _ => ReadSeconds(text),
            };
            // Only digits, ':' and '.' (and a leading sign for seconds) reach the number parser, and only a finite value
            // counts: .NET reads "NaN" and "Infinity" whatever the styles say, and NaN passes every range test (review, #38).
            if (!Plain(text, f.Kind) || value is not { } v || !double.IsFinite(v)) return new(null, f.Kind == FormFieldKind.Time ? "form.badTime" : "form.badNumber", i);
            if (v < f.Min || v > f.Max) return new(null, "form.range", i);
            values[f.Key] = v;
        }
        // Rules across fields.
        if (values.TryGetValue(TrimEnd, out var end) && end <= values[TrimStart]) return new(null, "form.endBeforeStart", 1);
        if (values.TryGetValue(ShiftSeconds, out var by) && by == 0) return new(null, "form.zero", 0);
        var answer = new StringBuilder();
        foreach (var f in fields)
        {
            if (answer.Length > 0) answer.Append(';');
            answer.Append(f.Key).Append('=');
            if (values.TryGetValue(f.Key, out var v)) answer.Append(v.ToString("0.###", CultureInfo.InvariantCulture));
        }
        return new(answer.ToString(), Values: new FormValues(values));
    }

    /// <summary>Whether a text holds only what its kind is written with: digits, ':' and '.' for a time, digits for a count, a sign, digits and '.' for seconds.</summary>
    private static bool Plain(string text, FormFieldKind kind)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var ok = char.IsAsciiDigit(c) || kind switch
            {
                FormFieldKind.Time => c is ':' or '.',
                FormFieldKind.Seconds => c == '.' || i == 0 && c is '-' or '+',
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>An answer read back: exactly the command's keys, in order, each value valid; anything else is null.</summary>
    public static FormValues? Read(string capabilityId, string? answer)
    {
        if (answer is not { Length: > 0 and <= 200 } || For(capabilityId) is not { } fields) return null;
        var parts = answer.Split(';');
        if (parts.Length != fields.Count) return null;
        var texts = new string?[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || parts[i][..eq] != fields[i].Key) return null;
            texts[i] = parts[i][(eq + 1)..];
        }
        var check = Check(capabilityId, texts);
        return check.Answer == answer ? check.Values : null; // only the canonical form: no second spelling of one answer
    }

    /// <summary>"83", "83.5", "1:23", "1:23.5", "1:02:03": seconds, minutes and seconds, or hours too. Null otherwise.</summary>
    public static double? ReadTime(string text)
    {
        var parts = text.Split(':');
        if (parts.Length > 3) return null;
        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var p = parts[i];
            if (p.Length == 0 || p.Length > 9) return null;
            if (last)
            {
                if (!double.TryParse(p, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var s)) return null;
                if (parts.Length > 1 && s >= 60) return null; // "1:75" is a typo, not 2:15
                total = total * 60 + s;
            }
            else
            {
                if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
                if (i > 0 && n >= 60) return null;
                total = total * 60 + n;
            }
        }
        return Math.Round(total, 3);
    }

    /// <summary>A signed number of seconds ("-2.5", "+1"); null otherwise.</summary>
    public static double? ReadSeconds(string text) =>
        text.Length <= 12 && double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var s)
            ? Math.Round(s, 3) : null;

    /// <summary>A time as the form shows it: "1:23.5", "1:02:03".</summary>
    public static string ShowTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds, 3));
        var s = t.Seconds + t.Milliseconds / 1000.0;
        var sec = s.ToString(s % 1 == 0 ? "00" : "00.###", CultureInfo.InvariantCulture);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{sec}" : $"{(int)t.TotalMinutes}:{sec}";
    }
}
